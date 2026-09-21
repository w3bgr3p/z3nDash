// ══════════════════════════════════════════════════════════════════════════════
// ZpDebugService.cs — сессия отладки шаблона: живой контекст, браузер с окном и
// очередь команд.
//
// Команда кладётся в очередь и подтверждается сразу, а исполняется потоком
// сессии: BranchExecutor синхронный, и ветка с WaitElementTime=60 думает минуту.
// Держать на ней HTTP-запрос нельзя — кнопка «пауза» перестала бы нажиматься.
//
// Поток при этом только исполняет команды. Где стоит исполнение, помнит
// PlaySession, а не стек вызова, — поэтому «до выбранной ветки» и точки
// останова оказываются проверкой перед шагом, а не правкой правил перехода.
//
// Сессия одна на приложение: браузер с окном, и две параллельные отладки — это
// два окна и гонка за каталог профиля.
// ══════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using z3nDash.Browser;
using z3nDash.Xml;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash;

public enum DebugState { Idle, Running, Paused, Finished, Failed }

public enum DebugCommand { Step, Run, Pause, RunTo, Stop }

internal static class ZpDebugService
{
    private static readonly object _gate = new();

    private static PlaySession?   _session;
    private static BrowserSession? _browser;
    private static bool            _browserTaken;

    private static CancellationTokenSource? _cts;
    private static Thread?                  _thread;
    private static BlockingCollection<DebugCommand>? _commands;

    private static DebugState _state = DebugState.Idle;
    private static volatile bool _pauseRequested;
    private static (string stepId, string branchId)? _runTo;

    /// <summary>Сколько веток прошло с момента возобновления. Нужно точкам останова.</summary>
    private static int _ranSinceResume;

    private static DateTime _lastActivity = DateTime.UtcNow;
    private static bool     _idleWarned;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    // ── Наружу ────────────────────────────────────────────────────────────────

    public static object CurrentState() => Snapshot(null);

    /// <summary>
    /// Осмотреть страницу сессии: список документов и выполнение скрипта в
    /// любом из них. Нужно, чтобы разбирать отказы по тому, что на странице
    /// есть на самом деле, а не по догадкам о ней.
    /// </summary>
    public static object Inspect(int frame, string script)
    {
        var session = _session;
        if (session is null) return new { ok = false, error = "сессии нет" };

        // Instance — обёртка ZP над браузером; сам браузер лежит в Browser.
        if (session.Instance.IsVoid ||
            session.Instance.Browser is not z3nDash.Browser.PlaywrightInstance page)
            return new { ok = false, error = "у сессии нет браузера" };

        try
        {
            var frames = page.FrameUrls();
            if (string.IsNullOrWhiteSpace(script))
                return new { ok = true, frames, result = "" };

            return new { ok = true, frames, result = page.EvaluateInFrame(frame, script) };
        }
        catch (Exception ex)
        {
            // Дословно: что спросили и что ответил браузер.
            return new { ok = false, error = $"{ex.GetType().Name}: {Cut(ex.Message, 400)}" };
        }
    }

    /// <summary>Положить команду в очередь. false — сессии нет.</summary>
    public static bool Enqueue(DebugCommand cmd)
    {
        lock (_gate)
        {
            if (_commands is null) return false;
            _lastActivity = DateTime.UtcNow;
            _idleWarned   = false;

            // Пауза не ставится в очередь: очередь разбирается между ветками, а
            // остановить надо ту, что уже идёт.
            if (cmd == DebugCommand.Pause) { _pauseRequested = true; return true; }

            _commands.Add(cmd);
            return true;
        }
    }

    public static void SetRunTarget(string stepId, string branchId)
        => _runTo = (stepId, branchId);

    /// <summary>
    /// Собрать сессию. Дешёвые проверки идут до браузера: окно, открытое ради
    /// того, чтобы сразу закрыться на несобравшемся коде, — потерянные полминуты
    /// на каждой попытке.
    /// </summary>
    public static async Task<(bool ok, string error)> StartAsync(
        string xml, string projectDir, bool headless)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return (false, $"каталог проекта не найден: {projectDir}");

        XmlTemplate tpl;
        try
        {
            tpl = XmlTemplate.Parse(XDocument.Parse(xml), "debug.xml");
        }
        catch (Exception ex)
        {
            return (false, $"шаблон не разобран | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
        }

        if (tpl.Start.IsNone) return (false, "в шаблоне нет <Start> — это не проект ZennoPoster");

        await StopAsync("вытеснена новой сессией");

        var project = new StubProject
        {
            // Имя проекта — файл шаблона, как в ZennoPoster: шаблоны разбирают
            // его на части, а Constantes.ProjectName ищет по нему файл.
            Name  = "debug.xml",
            Path  = projectDir,
            OnLog = Log
        };

        var profileDir = Path.Combine(Path.GetTempPath(), "z3nDash-xml", "debug-profile");
        Log($"[dbg] браузер{(headless ? " (без окна)" : " (с окном)")}, профиль {profileDir}");

        // Chrome помечает профиль как «Crashed» и при следующем запуске
        // восстанавливает вкладки прошлой сессии. В отладке это хуже, чем
        // просто лишнее окно: на экране оказывается страница прошлого прогона,
        // и по ней кажется, что текущий шаг прошёл, хотя он упал.
        Safe("снятие признака аварийного выхода", () => ClearCrashFlag(profileDir));

        try
        {
            _browser      = await BrowserSession.LaunchAsync(profileDir, headless, proxy: null, log: Log);
            _browserTaken = true;
        }
        catch (Exception ex)
        {
            return (false, $"браузер не поднялся | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
        }

        try
        {
            _session = new PlaySession(tpl, projectDir, project, _browser.Instance, Log);
        }
        catch (PlaySetupException ex)
        {
            await Release($"окружение не собрано: {Cut(ex.Message, 200)}");
            return (false, $"окружение шаблона не собрано | {Cut(ex.Message, 300)}");
        }
        catch (Exception ex)
        {
            await Release("непредвиденная ошибка сборки контекста");
            return (false, $"контекст не собран | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
        }

        lock (_gate)
        {
            _state          = DebugState.Paused;
            _pauseRequested = false;
            _runTo          = null;
            _ranSinceResume = 0;
            _lastActivity   = DateTime.UtcNow;
            _idleWarned     = false;
            _cts            = new CancellationTokenSource();
            _commands       = new BlockingCollection<DebugCommand>();
            _thread = new Thread(LoopGuarded) { IsBackground = true, Name = "zp-debug" };
            _thread.Start();
        }

        Publish(null);
        return (true, "");
    }

    /// <summary>Остановить сессию и освободить браузер.</summary>
    public static async Task StopAsync(string why)
    {
        Thread? thread;
        lock (_gate)
        {
            if (_commands is null && !_browserTaken) return;
            _cts?.Cancel();
            _commands?.CompleteAdding();
            thread    = _thread;
            _thread   = null;
            _commands = null;
        }

        // Ждём поток недолго: он может стоять внутри ветки, которая думает
        // минуту, и держать на этом HTTP-запрос незачем.
        if (thread is not null && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(3));

        await Release(why);

        lock (_gate)
        {
            _session = null;
            _state   = DebugState.Idle;
        }
    }

    // ── Поток сессии ──────────────────────────────────────────────────────────

    private static void LoopGuarded()
    {
        try
        {
            Loop();
        }
        catch (OperationCanceledException) { /* штатная остановка */ }
        catch (Exception ex)
        {
            _state = DebugState.Failed;
            // Шаг и дословный текст, без трактовки: догадка, записанная рядом
            // с фактом, через день читается как факт.
            Log($"[dbg] непредвиденная ошибка сессии | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
            Safe("запись трейсбека", () => DumpTrace(ex));
            Publish(null);
        }
        finally
        {
            // Любой непредусмотренный путь тоже закрывает окно.
            Release("поток сессии завершён").GetAwaiter().GetResult();
        }
    }

    private static void Loop()
    {
        var commands = _commands;
        var ct       = _cts?.Token ?? CancellationToken.None;
        if (commands is null) return;

        while (!ct.IsCancellationRequested)
        {
            if (!commands.TryTake(out var cmd, 1000)) { CheckIdle(); continue; }

            switch (cmd)
            {
                case DebugCommand.Step:  DoStep(ct);  break;
                case DebugCommand.Run:   DoRun(ct);   break;
                case DebugCommand.RunTo: DoRunTo(ct); break;
                case DebugCommand.Stop:  return;
            }
        }
    }

    /// <summary>
    /// Один шаг. Упавшая ветка оставляет сессию в Paused: в бою обрыв маршрута
    /// правилен, но в отладке закрыть браузер на ошибке — значит лишить себя
    /// возможности посмотреть, почему упало.
    /// </summary>
    private static void DoStep(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return;

        StepResult r;
        try { r = session.StepOnce(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _state = DebugState.Paused;
            Log($"[dbg] шаг не выполнен | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
            Publish(null);
            return;
        }

        _ranSinceResume++;
        _state = r.Outcome switch
        {
            StepOutcome.Finished => DebugState.Finished,
            _                    => DebugState.Paused
        };

        if (r.Outcome == StepOutcome.Failed)
            Log($"[dbg] ветка упала и уходить некуда: {Cut(r.Message, 300)}");

        Publish(r);

        if (_state == DebugState.Finished)
            Release("маршрут закончен").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Идти до конца, точки останова или паузы. Всё, что решает «стоять или
    /// идти», проверяется здесь — до вызова шага, а не внутри правил перехода.
    /// </summary>
    private static void DoRun(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return;

        _state          = DebugState.Running;
        _ranSinceResume = 0;
        PublishState();

        while (!ct.IsCancellationRequested)
        {
            if (_pauseRequested)
            {
                _pauseRequested = false;
                _state = DebugState.Paused;
                Log("[dbg] пауза");
                Publish(null);
                return;
            }

            // Счётчик нужен, чтобы run с ветки, на которой уже стоим, не вставал
            // на ней же сразу: иначе продолжить с точки останова было бы нельзя.
            if (_ranSinceResume > 0 && session.Current is { HasBreakPoint: true } next)
            {
                _state = DebugState.Paused;
                Log($"[dbg] точка останова: {next}");
                Publish(null);
                return;
            }

            if (!StepInsideRun(session, ct)) return;
        }
    }

    /// <summary>Общий шаг для run и runTo. false — прогон закончился.</summary>
    private static bool StepInsideRun(PlaySession session, CancellationToken ct)
    {
        StepResult r;
        try { r = session.StepOnce(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _state = DebugState.Paused;
            Log($"[dbg] шаг не выполнен | {ex.GetType().Name}: {Cut(ex.Message, 300)}");
            Publish(null);
            return false;
        }

        _ranSinceResume++;
        if (r.Outcome == StepOutcome.Moved) return true;

        _state = r.Outcome == StepOutcome.Finished ? DebugState.Finished : DebugState.Paused;
        if (r.Outcome == StepOutcome.Failed)
            Log($"[dbg] ветка упала и уходить некуда: {Cut(r.Message, 300)}");

        Publish(r);
        if (_state == DebugState.Finished)
            Release("маршрут закончен").GetAwaiter().GetResult();
        return false;
    }

    /// <summary>
    /// Идти до указанной ветки. Предел тот же, что у обычного прогона: цель
    /// может лежать в недостижимой части графа, и без предела это вечный цикл.
    /// </summary>
    private static void DoRunTo(CancellationToken ct)
    {
        var session = _session;
        var target  = _runTo;
        if (session is null || target is null) return;

        const int maxBranches = 10_000;
        _state          = DebugState.Running;
        _ranSinceResume = 0;
        PublishState();

        for (var guard = 0; guard < maxBranches && !ct.IsCancellationRequested; guard++)
        {
            if (_pauseRequested)
            {
                _pauseRequested = false;
                _state = DebugState.Paused;
                Log("[dbg] пауза");
                Publish(null);
                return;
            }

            if (session.Current is { } at
                && at.StepId == target.Value.stepId && at.Id == target.Value.branchId)
            {
                _state = DebugState.Paused;
                Log($"[dbg] дошли до {at}");
                Publish(null);
                return;
            }

            if (!StepInsideRun(session, ct)) return;
        }

        // Не дошли — это не ошибка: ветка могла быть в мёртвой части графа.
        _state = DebugState.Paused;
        Log("[dbg] до указанной ветки маршрут не дошёл");
        Publish(null);
    }

    // ── Ресурс ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Освободить браузер. Флаг снимается сразу после закрытия, иначе страховка
    /// в finally закроет второй раз.
    /// </summary>
    private static async Task Release(string why)
    {
        BrowserSession? browser;
        lock (_gate)
        {
            if (!_browserTaken) return;
            _browserTaken = false;
            browser  = _browser;
            _browser = null;
        }

        Log($"[dbg] сессия закрыта: {why}");
        // Закрывается браузер. PlaySession — обычный объект без ресурсов, он
        // уходит вместе со ссылкой.
        if (browser is not null)
            await SafeAsync("закрытие браузера", async () => await browser.DisposeAsync());
    }

    /// <summary>
    /// Вкладку со страницей закрыли — сессия осталась висеть с открытым окном.
    /// Бездействие — это отсутствие команд И отсутствие живых подписчиков
    /// одновременно: пока страница открыта, сессия ждёт сколько угодно.
    /// </summary>
    private static void CheckIdle()
    {
        if (SseHub.HasSubscribers(Channel)) { _lastActivity = DateTime.UtcNow; _idleWarned = false; return; }

        var idle = DateTime.UtcNow - _lastActivity;
        if (!_idleWarned && idle > IdleLimit - TimeSpan.FromMinutes(1))
        {
            _idleWarned = true;
            Log("[dbg] нет страницы и команд — сессия закроется через минуту");
        }
        if (idle > IdleLimit)
        {
            Log("[dbg] таймаут бездействия");
            _commands?.Add(DebugCommand.Stop);
        }
    }

    // ── Снимок и события ──────────────────────────────────────────────────────

    private const string Channel = "zp-debug";

    /// <summary>
    /// Снимок для остановки. Всё побочное идёт через Safe: если скриншот не
    /// снялся, остановка всё равно происходит и состояние показывается — просто
    /// без картинки. Обратный порядок означал бы, что упавшая диагностика
    /// убивает то, что диагностирует.
    /// </summary>
    private static object Snapshot(StepResult? last)
    {
        var session = _session;

        return new
        {
            state   = _state.ToString().ToLowerInvariant(),
            current = session?.Current is { } c ? new { stepId = c.StepId, branchId = c.Id } : null,
            ran     = session?.BranchesRun ?? 0,
            last    = last is { } r
                ? new { message = r.Message, failed = r.FailedAt?.ToString(), outcome = r.Outcome.ToString() }
                : null,
            vars    = Safe("сбор переменных", () => CollectVariables(session)) ?? [],
            profile = Safe("профиль", () => session is null ? null : new
            {
                name  = session.Project.Profile.Name + " " + session.Project.Profile.Surname,
                email = session.Project.Profile.Email,
                login = session.Project.Profile.Login
            })
        };
    }

    private static object[] CollectVariables(PlaySession? session)
    {
        if (session is null) return [];
        var list = new List<object>();
        foreach (var name in session.Template.Variables.Keys)
            list.Add(new { name, value = Cut(session.Project.Variables[name].Value ?? "", 400) });
        return list.ToArray();
    }

    private static void Publish(StepResult? last)
        => Safe("отправка события", () =>
               SseHub.BroadcastOutput(JsonSerializer.Serialize(Snapshot(last)), Channel));

    private static void PublishState()
        => Safe("отправка состояния", () =>
               SseHub.BroadcastOutput(
                   JsonSerializer.Serialize(new { state = _state.ToString().ToLowerInvariant() }), Channel));

    private static void Log(string line)
        => Safe("лог", () => SseHub.BroadcastOutput(JsonSerializer.Serialize(new { log = line }), Channel));

    // ── Мелочи ────────────────────────────────────────────────────────────────

    private static T? Safe<T>(string label, Func<T> fn)
    {
        try { return fn(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[dbg] {label} не выполнено: {Cut(ex.Message, 200)}");
            return default;
        }
    }

    private static void Safe(string label, Action fn)
    {
        try { fn(); }
        catch (Exception ex) { Console.WriteLine($"[dbg] {label} не выполнено: {Cut(ex.Message, 200)}"); }
    }

    private static async Task SafeAsync(string label, Func<Task> fn)
    {
        try { await fn(); }
        catch (Exception ex) { Console.WriteLine($"[dbg] {label} не выполнено: {Cut(ex.Message, 200)}"); }
    }

    /// <summary>
    /// Снять «Crashed» из Preferences профиля. Правим только два поля, остальное
    /// не трогаем: в файле лежат настройки браузера, и переписывать его целиком
    /// значит рисковать ими ради мелочи.
    /// </summary>
    private static void ClearCrashFlag(string profileDir)
    {
        foreach (var rel in new[] { Path.Combine("Default", "Preferences"), "Preferences" })
        {
            var file = Path.Combine(profileDir, rel);
            if (!File.Exists(file)) continue;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            var profile = node?["profile"];
            if (profile is null) continue;

            profile["exit_type"]      = "Normal";
            profile["exited_cleanly"] = true;
            File.WriteAllText(file, node!.ToJsonString());
            Log("[dbg] профиль: снят признак аварийного выхода, вкладки прошлой сессии не восстановятся");
        }
    }

    /// <summary>Полный стек — в файл: в панели он нечитаем, но при разборе нужен.</summary>
    private static void DumpTrace(Exception ex)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"zp-debug-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), ex.ToString());
    }

    private static string Cut(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
