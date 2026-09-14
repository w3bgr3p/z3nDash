using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cronos;
using NBitcoin.Protocol;
using z3nDash;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash;

public sealed partial class SchedulerService : IDisposable
{
    private readonly DbConnectionService _dbService;
    private readonly System.Threading.Timer _timer;
    internal readonly ConcurrentDictionary<string, RunningProcess> _running = new();
    private readonly Logger? _log;

    /// <summary>Нити, уже занятые запуском, но ещё не зарегистрированные в _running.</summary>
    private readonly ConcurrentDictionary<string, int> _reserved = new();

    /// <summary>Когда по каждой задаче в последний раз стартовала нить.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastLaunch = new();

    /// <summary>Минимальный зазор между доливами нитей одной задачи.</summary>
    private static readonly TimeSpan RefillFloor = TimeSpan.FromSeconds(2);

    /// <summary>Границы паузы между стартами соседних нитей одной пачки.</summary>
    private const int StaggerMinMs = 1000;
    private const int StaggerMaxMs = 5000;
    private static string Table => DbSchema.Schedules.Name;
    private static string QueueTable => DbSchema.ScheduleQueue.Name;

    public SchedulerService(DbConnectionService dbService, Logger? log = null)
    {
        _dbService = dbService;
        _log       = log;
        _timer = new System.Threading.Timer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Init()
    {
        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
            return;
        }
        db.PrepareTable(DbSchema.Schedules.Columns, Table);
        db.PrepareTable(DbSchema.ScheduleQueue.Columns, QueueTable);
        RepairShiftedScheduleColumns(db);
        RestoreRunningProcesses(db);
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private void RepairShiftedScheduleColumns(Db db)
    {
        var repaired = db.Query($"""
            UPDATE "{Table}"
            SET
                "max_threads"   = "last_run_id",
                "status"        = "max_threads",
                "last_run"      = "status",
                "last_exit"     = "last_run",
                "last_output"   = "last_exit",
                "payload_schema" = "last_output",
                "payload_values" = "payload_schema",
                "runs_total"    = "payload_values",
                "runs_success"  = "runs_total",
                "schedule_tag"  = "runs_success",
                "last_run_id"   = "schedule_tag"
            WHERE "max_threads" IN ('idle', 'running', 'error')
              AND "status" NOT IN ('idle', 'running', 'error')
            """);

        if (repaired != "0")
            _log?.Info($"[SchedulerService] Repaired {repaired} shifted schedule rows");
    }

    private void RestoreRunningProcesses(Db db)
    {
        var columns = RowColumns(db);
        if (columns.Count == 0) return;

        var rows = db.GetLines(string.Join(",", columns), Table, where: "\"status\" = 'running' AND \"enabled\" = 'true'");

        if (rows.Count == 0) return;

        _log?.Info($"[SchedulerService] Found {rows.Count} tasks with status=running, restarting...");

        var restoreStagger = TimeSpan.Zero;
        foreach (var row in rows)
        {
            var record = ParseRow(row, columns);
            var id     = record.GetValueOrDefault("id", "");
            var name   = record.GetValueOrDefault("name", id);

            // Сбросить статус в idle и очистить last_run_id
            db.Query($"UPDATE \"{Table}\" SET \"status\" = 'idle', \"last_run_id\" = '' WHERE \"id\" = '{id}'");

            _log?.Info($"[SchedulerService] Restarting task: {name} (id={id})");

            // Перезапустить процесс — тоже вразнос, иначе после падения всё
            // недоделанное поднимется одной пачкой.
            StartInstance(db, record, DateTime.UtcNow, delay: restoreStagger);
            restoreStagger += NextStaggerGap();
        }
    }

    private void Tick(object? _)
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;

        var columns = RowColumns(db);
        if (columns.Count == 0) return;

        var rows = db.GetLines(string.Join(",", columns), Table, where: "\"enabled\" = 'true'");
        var now  = DateTime.UtcNow;

        foreach (var row in rows)
        {
            var record = ParseRow(row, columns);
            var id     = record.GetValueOrDefault("id", "");
            FireSchedule(db, record, id, now);
        }

        DrainQueue(db, now);
    }

    /// <summary>
    /// Одно срабатывание расписания для конкретной задачи. Threads — это ёмкость:
    /// сколько инстансов задачи держать одновременно. Расписание говорит, сколько
    /// запусков заказать; сколько из них стартует прямо сейчас, решает свободная
    /// ёмкость, а что делать с хвостом — on_overlap.
    /// </summary>
    private void FireSchedule(Db db, Dictionary<string, string> record, string id, DateTime now)
    {
        lock (_controlGate)
        {
            if (!AutomaticLaunchAllowed(db, id)) return;
            FireScheduleCore(db, record, id, now);
        }
    }

    private void FireScheduleCore(Db db, Dictionary<string, string> record, string id, DateTime now)
    {
        var maxThreads = ReadThreads(record);
        var active     = CountActiveInstances(id);

        var decision = Decide(record, now, active, maxThreads);
        if (!decision.Fire) return;

        var overlap = record.GetValueOrDefault("on_overlap", "skip");

        // kill&restart освобождает все нити разом — свободные считаем уже после.
        if (overlap == "kill_restart" && active > 0)
        {
            KillAllInstances(id);
            RemoveInstancesFromRunning(id);
            active = 0;
        }

        var free = Math.Max(0, maxThreads - active);
        var want = Math.Max(1, decision.Attempts);
        var take = Math.Min(want, free);

        // Нити заняты: parallel копит очередь, skip и kill_restart пропускают заход.
        if (take == 0)
        {
            if (overlap != "parallel") return;
            for (var i = 0; i < want; i++) EnqueueItem(db, id, record, priority: 10);
            NoteScheduledFire(db, id, record, want);
            return;
        }

        if (take > 1)
            _log?.Info($"[{record.GetValueOrDefault("name", id)}] наливаю {take} нитей с разносом {StaggerMinMs / 1000}–{StaggerMaxMs / 1000} с");

        var stagger = TimeSpan.Zero;
        for (var i = 0; i < take; i++)
        {
            StartInstance(db, record, now, delay: stagger);
            stagger += NextStaggerGap();
        }

        // Хвост сверх ёмкости имеет смысл копить только в режиме очереди.
        var queued = 0;
        if (overlap == "parallel")
            for (var i = take; i < want; i++) { EnqueueItem(db, id, record, priority: 10); queued++; }

        NoteScheduledFire(db, id, record, take + queued);
    }

    /// <summary>
    /// Прогнать расписание одной задачи прямо сейчас, не дожидаясь минутного тика:
    /// «Начать сразу» в ZP означает сразу, а не «в течение ближайшей минуты».
    /// </summary>
    public void EvaluateNow(string id, Db db)
    {
        var cols = RowColumns(db);
        if (cols.Count == 0) return;

        var rows = db.GetLines(string.Join(",", cols), Table, where: $"\"id\" = '{id}'");
        if (rows.Count == 0) return;

        var record = ParseRow(rows[0], cols);
        if (record.GetValueOrDefault("enabled", "") != "true") return;

        FireSchedule(db, record, id, DateTime.UtcNow);
    }

    /// <summary>Ёмкость задачи в нитях. Пустое или битое значение — одна нить.</summary>
    private static int ReadThreads(Dictionary<string, string> record)
        => int.TryParse(record.GetValueOrDefault("max_threads", "1"), out var mt) && mt > 0 ? mt : 1;

    /// <summary>
    /// Занятые нити: живые инстансы плюс зарезервированные, но ещё не
    /// зарегистрированные слоты. Без резерва пачка запусков в одном тике увидела
    /// бы ёмкость свободной столько раз, сколько нитей запрашивает.
    /// </summary>
    private int CountActiveInstances(string scheduleId)
        => _running.Count(kv => kv.Key.StartsWith(scheduleId + ":") && !kv.Value.HasExited)
         + _reserved.GetValueOrDefault(scheduleId, 0);

    private void RemoveInstancesFromRunning(string scheduleId)
    {
        foreach (var key in _running.Keys.Where(k => k.StartsWith(scheduleId + ":")).ToList())
            _running.TryRemove(key, out _);
    }

    private void KillAllInstances(string scheduleId)
    {
        foreach (var kv in _running.Where(kv => kv.Key.StartsWith(scheduleId + ":")))
            kv.Value.Kill();
    }

    private void EnqueueItem(Db db, string scheduleId, Dictionary<string, string> record, int priority)
    {
        db.InsertDic(new Dictionary<string, string>
        {
            { "uuid",        Guid.NewGuid().ToString() },
            { "schedule_id", scheduleId },
            { "queued_at",   DateTime.UtcNow.ToString("o") },
            { "status",      "pending" },
            { "priority",    priority.ToString() },
            { "run_id",      "" },
            { "args_b64",    record.GetValueOrDefault("args", "") },
        }, QueueTable);
    }

    /// <summary>Общий проход по очереди: каждой задаче доливаем её свободные нити.</summary>
    private void DrainQueue(Db db, DateTime now)
    {
        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return;

        var raw = db.Query($"SELECT \"schedule_id\" FROM \"{QueueTable}\" WHERE \"status\" = 'pending' ORDER BY \"priority\" ASC, \"queued_at\" ASC");
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var scheduleId in raw.Split('·')
                                      .Select(s => s.Trim())
                                      .Where(s => s.Length > 0)
                                      .Distinct())
            DrainQueueFor(db, scheduleId);
    }

    /// <summary>Разобрать очередь одной задачи ровно до заполнения её нитей.</summary>
    private void DrainQueueFor(Db db, string scheduleId)
    {
        lock (_controlGate)
        {
            if (!AutomaticLaunchAllowed(db, scheduleId)) return;
            DrainQueueForCore(db, scheduleId);
        }
    }

    private void DrainQueueForCore(Db db, string scheduleId)
    {
        var schedCols = RowColumns(db);
        if (schedCols.Count == 0) return;

        var schedRows = db.GetLines(string.Join(",", schedCols), Table, where: $"\"id\" = '{scheduleId}'");
        if (schedRows.Count == 0) return;

        var record     = ParseRow(schedRows[0], schedCols);
        var maxThreads = ReadThreads(record);

        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return;
        var colsSql = string.Join(", ", qCols.Select(c => $"\"{c}\""));

        var stagger = TimeSpan.Zero;
        while (CountActiveInstances(scheduleId) < maxThreads)
        {
            var raw = db.Query($"SELECT {colsSql} FROM \"{QueueTable}\" WHERE \"status\" = 'pending' AND \"schedule_id\" = '{scheduleId}' ORDER BY \"priority\" ASC, \"queued_at\" ASC LIMIT 1");
            if (string.IsNullOrWhiteSpace(raw)) return;

            var qRecord = ParseRow(raw.Split('·')[0], qCols);
            var qUuid   = qRecord.GetValueOrDefault("uuid", "");
            if (string.IsNullOrEmpty(qUuid)) return;

            var argsB64 = qRecord.GetValueOrDefault("args_b64", "");
            db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = 'running' WHERE \"uuid\" = '{qUuid}'");

            var own = new Dictionary<string, string>(record);
            if (!string.IsNullOrWhiteSpace(argsB64)) own["args"] = argsB64;
            StartInstance(db, own, DateTime.UtcNow, qUuid, delay: stagger);
            stagger += NextStaggerGap();
        }
    }

    /// <summary>
    /// Нить освободилась: сперва добираем очередь, потом — если расписание просит
    /// держать потоки занятыми («Подряд», «Подряд с паузой») — доливаем из него.
    /// </summary>
    private void TopUp(Db db, string scheduleId)
    {
        DrainQueueFor(db, scheduleId);

        var schedCols = RowColumns(db);
        if (schedCols.Count == 0) return;

        var schedRows = db.GetLines(string.Join(",", schedCols), Table, where: $"\"id\" = '{scheduleId}'");
        if (schedRows.Count == 0) return;

        var record = ParseRow(schedRows[0], schedCols);
        if (record.GetValueOrDefault("enabled", "") != "true") return;
        if (CountActiveInstances(scheduleId) >= ReadThreads(record)) return;

        // Скрипт, падающий за миллисекунду, в режиме «Подряд» иначе крутился бы вхолостую.
        var since = DateTime.UtcNow - _lastLaunch.GetValueOrDefault(scheduleId, DateTime.MinValue);
        if (since < RefillFloor)
        {
            _ = Task.Delay(RefillFloor - since)
                    .ContinueWith(_ => { try { TopUp(db, scheduleId); } catch { } });
            return;
        }

        FireSchedule(db, record, scheduleId, DateTime.UtcNow);
    }

    private static void FinishQueueEntry(Db db, string? queueUuid, string status, string runId)
    {
        if (string.IsNullOrEmpty(queueUuid)) return;
        db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = '{status}', \"run_id\" = '{runId}' WHERE \"uuid\" = '{queueUuid}'");
    }

    /// <summary>
    /// Занять нить и запустить инстанс. Слот резервируется синхронно: иначе пачка
    /// запусков из одного тика посчитала бы ёмкость свободной столько раз, сколько
    /// нитей запрашивает, — регистрация в _running происходит уже после await.
    /// </summary>
    private void StartInstance(Db db, Dictionary<string, string> record, DateTime firedAt, string? queueUuid = null, TimeSpan delay = default, bool automatic = true)
    {
        var id   = record.GetValueOrDefault("id", "");
        var slot = Reserve(id);
        _lastLaunch[id] = DateTime.UtcNow;

        // Своя копия записи на каждую нить: запуск дописывает в неё служебные поля
        // прогона, и общий словарь пять нитей растащили бы.
        var own = new Dictionary<string, string>(record);

        _ = Task.Run(async () =>
        {
            TaskRunContext? runContext = null;
            try
            {
                // Слот занят до паузы: иначе за время разноса тик или TopUp увидели бы
                // ёмкость свободной и подняли лишние нити сверх Threads.
                if (delay > TimeSpan.Zero) await Task.Delay(delay);

                lock (_controlGate)
                {
                    if (automatic && !AutomaticLaunchAllowed(db, id))
                    {
                        if (queueUuid != null)
                            db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = 'pending' WHERE \"uuid\" = '{SqlValue(queueUuid)}'");
                        return;
                    }
                    runContext = RegisterRun(id);
                }

                // Время старта, а не время решения: иначе uptime нити врал бы на разнос.
                var startedAt = delay > TimeSpan.Zero ? DateTime.UtcNow : firedAt;
                using (TaskRunContext.Enter(runContext))
                    await LaunchFromQueue(db, own, startedAt, queueUuid, runContext.RunId, slot);
            }
            catch (Exception ex) { _log?.Error($"[scheduler] launch failed id={id}: {ex.Message}"); }
            finally
            {
                if (runContext != null) _runContexts.TryRemove(runContext.Token, out _);
                slot.Dispose();
            }
        });
    }

    /// <summary>
    /// Пауза до следующей нити пачки — 1–5 секунд. Смещение копит вызывающий:
    /// считать его заново на каждую нить нельзя, иначе соседние старты случайно
    /// сходятся в один момент. Пять браузеров, поднятых разом, дерутся за CPU и
    /// выглядят синхронной пачкой — это бьёт по результату прогона.
    /// </summary>
    private static TimeSpan NextStaggerGap()
        => TimeSpan.FromMilliseconds(Random.Shared.Next(StaggerMinMs, StaggerMaxMs + 1));

    private IDisposable Reserve(string id)
    {
        _reserved.AddOrUpdate(id, 1, (_, v) => v + 1);
        return new Slot(this, id);
    }

    /// <summary>Резерв нити. Освобождается один раз — при регистрации инстанса.</summary>
    private sealed class Slot : IDisposable
    {
        private SchedulerService? _owner;
        private readonly string   _id;

        public Slot(SchedulerService owner, string id) { _owner = owner; _id = id; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?._reserved.AddOrUpdate(_id, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    private async Task LaunchFromQueue(Db db, Dictionary<string, string> record, DateTime firedAt, string? queueUuid, string runId, IDisposable? threadSlot = null)
    {
        var id         = record.GetValueOrDefault("id", "");
        var name       = record.GetValueOrDefault("name", id);
        var executor   = record.GetValueOrDefault("executor", "python");
        var scriptPath = record.GetValueOrDefault("script_path", "");
        var args       = record.GetValueOrDefault("args", "");
        var timeoutSec = ReadTimeoutSeconds(record);

        _log?.Info($"[{name}] executor='{executor}' (len={executor.Length}) scriptPath='{scriptPath}'");

        // Экзекуторы, которых больше нет. Без явной проверки такая строка ушла бы
        // в ветку по умолчанию и пыталась запустить имя задачи как python-скрипт.
        if (executor is "internal" or "csx-internal")
        {
            var gone = executor == "internal"
                ? $"[ERR] экзекутор internal удалён; {scriptPath} вызывается через POST /config/client-bundle или /config/update-templates"
                : "[ERR] экзекутор csx-internal удалён; используйте xml или внешний csx";
            _log?.Error(gone);
            SseHub.BroadcastOutput(JsonSerializer.Serialize(new { line = gone, level = "ERROR" }), id);
            db.Upd(
                $"last_output = '{gone.Replace("'", "''")}', last_exit = '1', last_run = '{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}', status = 'error'",
                Table, where: $"\"id\" = '{id}'");
            return;
        }

        if (executor != "cmd" && executor != "npm" && !File.Exists(scriptPath))
        {
            var errMsg = $"[ERR] no script file found at {scriptPath}";
            _log.Error(errMsg);
            SseHub.BroadcastOutput(
                JsonSerializer.Serialize(new { line = errMsg, level = "ERROR" }), id);
            db.Upd(
                $"last_output = '{errMsg.Replace("'","''")}', last_exit = '1', last_run = '{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}'",
                SchedulerService.Table,
                where: $"\"id\" = '{id}'");
            return;
        }
        

        // ── уникальный id прогона ──────────────────────────────────────────────
        var instanceKey = $"{id}:{runId}";
        var scheduleTag = BuildScheduleTag(name);

        var payloadValues = record.GetValueOrDefault("payload_values", "");
        if (!string.IsNullOrWhiteSpace(payloadValues))
        {
            var projectName = Path.GetFileName(scriptPath).Split('.')[0];
            var table       = $"__{projectName}";
            var nowIso      = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadValues)
                          ?? new Dictionary<string, object>();

            var condition = payload.TryGetValue("condition", out var cond) ? cond?.ToString() ?? "" : "";
            condition     = condition.Replace("NOW", $"'{nowIso}'");

            if (!string.IsNullOrWhiteSpace(condition))
            {
                var cols    = db.GetTableColumns(table);
                var colsSql = string.Join(", ", cols.Select(c => $"\"{c}\""));
                var rawRow  = db.Query($"SELECT {colsSql} FROM \"{table}\" WHERE {condition} LIMIT 1");

                if (!string.IsNullOrWhiteSpace(rawRow))
                {
                    var values = rawRow.Split('¦');
                    for (int i = 0; i < cols.Count && i < values.Length; i++)
                        payload[cols[i]] = values[i];
                    _log?.Info($"[{name}] account selected from {table}");
                }
                else
                {
                    _log?.Warn($"[{name}] no account found in {table} by condition: {condition}");
                }
            }

            args = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        }
        
        // run в событии — чтобы UI мог развести по цветам логи параллельных нитей.
        Action<string> broadcast = line =>
            SseHub.BroadcastOutput(JsonSerializer.Serialize(new
            {
                line,
                run   = runId,
                ts    = DateTime.UtcNow.ToString("o"),
                level = line.StartsWith("[ERR]") ? "ERROR" : "INFO",
            }), id);

        if (executor == "xml")
        {
            // Проигрывание шаблона ZennoPoster: граф из XML исполняет
            // встроенный рантайм поверх слоя z3n7, без конвертации в csx.
            if (!File.Exists(scriptPath))
            {
                _log?.Error($"[{name}] xml template not found: {scriptPath}");
                UpdateStatus(db, id, "error", firedAt, "-1", $"template not found: {scriptPath}", runId);
                return;
            }

            UpdateStatus(db, id, "running", firedAt, "", "", runId);
            var cts = new CancellationTokenSource();
            var rp  = new RunningProcess(null, firedAt, cts, broadcast);
            _running[instanceKey] = rp;
            threadSlot?.Dispose();

            using var limit = new RunTimeout(timeoutSec, () =>
            {
                rp.AddLine($"[ERR] лимит времени {timeoutSec}s исчерпан — прогон обрывается");
                rp.Kill();
            });

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE] xml started id={id} name={name} run={runId} template={Path.GetFileName(scriptPath)}");
            Console.ResetColor();

            z3nDash.Browser.BrowserSession? session = null;
            // Профиль антика, который мы обязаны закрыть за собой; null — не наш.
            string? externalProfile = null;
            // Аккаунт — эксклюзивный ресурс: ссылка живёт снаружи try, иначе
            // освободить его на исключении или на снятии по таймауту нечем.
            Dictionary<string, string>? account = null;
            var accountTable  = "";
            var accountStatus = "fail";
            var brConfig = BrowserConfig.Parse(record.GetValueOrDefault("browser_json", ""));
            BrowserProvider.ApplyGlobalConfig(brConfig, Config.BrowsersApi);
            try
            {
                var project = new StubProject
                {
                    // Имя проекта — файл шаблона, как в ZennoPoster: шаблоны
                    // разбирают его на части (Name.Split('.')[1] — сервис), а
                    // Constantes.ProjectName ищет по нему файл в каталоге.
                    Name = Path.GetFileName(scriptPath),
                    OnLog = rp.AddLine,
                };
                project.Variables["dbSource"].Value = db.Source;

                var tpl = z3nDash.Xml.XmlTemplate.Load(scriptPath);

                // Прокси задаётся при запуске браузера и на живом инстансе не
                // меняется, поэтому берём его до старта — из поля задачи, а иначе
                // из переменной самого шаблона.
                var payload = string.IsNullOrWhiteSpace(args)
                    ? record
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(
                        Encoding.UTF8.GetString(Convert.FromBase64String(args))) ?? record;

                // Значения payload — это InputSettings задачи, и в ZennoPoster они
                // ложатся в переменные проекта до старта. Без этого шаблон работал
                // на своих значениях по умолчанию: в настройках стояла страна GH,
                // а прокси собирался с той, что зашита в сам шаблон.
                //
                // Кладём только то, что пришло из args: при пустом args payload
                // подменяется строкой задачи из БД, и её колонки (name, executor,
                // script_path) переменными проекта быть не должны.
                //
                // SeedVariables в XmlPlayer заполняет лишь пустые переменные,
                // поэтому заданное здесь шаблон не перетрёт.
                if (!string.IsNullOrWhiteSpace(args))
                {
                    var applied = 0;
                    foreach (var (key, value) in payload)
                    {
                        if (key.StartsWith("__") || key == "condition") continue;
                        project.Variables[key].Value = value ?? "";
                        applied++;
                    }
                    if (applied > 0) rp.AddLine($"[xml] из payload в переменные: {applied}");
                }

                // Аккаунт берём только когда шаблон действительно с ним работает:
                // лок эксклюзивный, а по аккаунтам ходит меньшая часть шаблонов.
                // Явно заданный acc0 — это выбор руками, резервировать нечего.
                accountTable = payload.GetValueOrDefault("accountTable", "__" + name.Split('.')[0]);
                var wantsAccount = string.IsNullOrWhiteSpace(payload.GetValueOrDefault("acc0", ""))
                                   && (TemplateUsesAccount(tpl, scriptPath)
                                       || payload.ContainsKey("accountTable")
                                       || payload.ContainsKey("condition"));

                if (wantsAccount)
                {
                    // Нет таблицы — это ошибка настройки, а не «аккаунты кончились».
                    // ReserveAccount на оба случая отдаёт null, и без этой проверки
                    // неверно настроенная задача молча отчитывалась бы успехом.
                    if (db.GetTableColumns(accountTable).Count == 0)
                    {
                        rp.AddLine($"[ERR] таблица аккаунтов {accountTable} не найдена");
                        SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true, run = runId }), id);
                        _running.TryRemove(instanceKey, out _);
                        UpdateStatus(db, id, "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
                        FinishQueueEntry(db, queueUuid, "error", runId);
                        return;
                    }

                    var condition = payload.GetValueOrDefault("condition", "1=1")
                        .Replace("NOW", $"'{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}'");

                    account = await ReserveAccount(db, accountTable, condition, RunLogger(scheduleTag, runId));

                    if (account == null)
                    {
                        rp.AddLine($"[xml] свободных аккаунтов нет: таблица {accountTable}, условие [{condition}]");
                        SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true, run = runId }), id);
                        _running.TryRemove(instanceKey, out _);
                        // Пустой код возврата — счётчики не трогаются: прогона не было,
                        // и записывать его успехом значит портить статистику.
                        UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle",
                                     DateTime.UtcNow, "", rp.Snapshot(), runId);
                        FinishQueueEntry(db, queueUuid, "done", runId);
                        return;
                    }

                    SeedAccount(db, project, payload, account);
                    rp.AddLine($"[xml] аккаунт {account.GetValueOrDefault("id", "")} из {accountTable}");
                }

                var rawProxy = payload.GetValueOrDefault("proxy", "");
                if (string.IsNullOrWhiteSpace(rawProxy))
                    rawProxy = tpl.Variables.GetValueOrDefault("proxy", "");
                var proxy = z3nDash.Browser.BrowserSession.NormalizeProxy(rawProxy);

                // Чем поднимать браузер — настройка задачи; какой именно профиль
                // открывать — данные запуска, поэтому payload перекрывает настройку.
                var profile = payload.GetValueOrDefault("browser_profile", "");
                if (string.IsNullOrWhiteSpace(profile)) profile = payload.GetValueOrDefault("zb_id", "");
                if (string.IsNullOrWhiteSpace(profile)) profile = brConfig.Profile;

                var mode = brConfig.Mode;
                // Профиль ZennoBrowser в payload включает свою ветку и при
                // настройке по умолчанию — так задачи вели себя до появления выбора.
                if (mode == "patchright" && !string.IsNullOrWhiteSpace(payload.GetValueOrDefault("zb_id", "")))
                    mode = "zennobrowser";

                if (mode == "zennobrowser" && !string.IsNullOrWhiteSpace(profile))
                {
                    // Боевой путь: профиль ZennoBrowser со своим отпечатком.
                    var ws = await new ZB(
                        Config.BrowsersApi.ZennoBrowser.Token,
                        Config.BrowsersApi.ZennoBrowser.Host).RunProfile(profile);
                    if (!string.IsNullOrWhiteSpace(ws))
                        session = await z3nDash.Browser.BrowserSession.AttachAsync(ws);
                    else
                        rp.AddLine($"[br] ZennoBrowser не отдал эндпоинт для профиля {profile}");
                }
                else if (mode == "cdp")
                {
                    if (string.IsNullOrWhiteSpace(brConfig.Cdp))
                        rp.AddLine("[br] режим CDP выбран, но эндпоинт не задан");
                    else
                    {
                        rp.AddLine($"[br] подключаюсь к {brConfig.Cdp}");
                        session = await z3nDash.Browser.BrowserSession.AttachAsync(brConfig.Cdp);
                    }
                }
                else if (BrowserProvider.UsesProviderApi(mode))
                {
                    var ws = await BrowserProvider.StartAsync(brConfig, profile, rp.AddLine);
                    if (!string.IsNullOrWhiteSpace(ws))
                    {
                        session = await z3nDash.Browser.BrowserSession.AttachAsync(ws);
                        externalProfile = brConfig.CloseAfterRun ? profile : null;
                    }
                }

                // Внешний браузер не поднялся — это не повод молча уйти на
                // Patchright: у профиля другой отпечаток и другой прокси.
                if (session is null && mode != "patchright")
                {
                    rp.AddLine("[ERR] внешний браузер не подключён, запуск отменён");
                    UpdateStatus(db, id, "error", firedAt, "-1", rp.Snapshot(), runId);
                    _running.TryRemove(instanceKey, out _);
                    FinishQueueEntry(db, queueUuid, "error", runId);
                    return;
                }

                if (session is null)
                {
                    // Профиль на запуск, а не на задачу. Persistent context в
                    // Playwright занимает каталог монопольно: два потока одного
                    // шаблона на общем профиле либо не поднимутся, либо испортят
                    // друг другу куки. Поэтому в путь идёт ещё и runId, а при
                    // работе по аккаунтам — сам аккаунт, чтобы его сессия
                    // возвращалась в свой каталог.
                    var acc  = payload.GetValueOrDefault("acc0", "");
                    var slot = acc.Length > 0 ? acc : runId;
                    var safe = string.Concat((name + "-" + slot).Select(
                        c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var profileDir = Path.Combine(Path.GetTempPath(), "z3nDash-xml", "profile-" + safe);
                    rp.AddLine($"[br] Patchright{(brConfig.Headless ? " (без окна)" : " (с окном)")}, профиль {profileDir}"
                               + (proxy.Length > 0 ? $", прокси {proxy}" : ", без прокси"));
                    session = await z3nDash.Browser.BrowserSession.LaunchAsync(
                        profileDir, headless: brConfig.Headless, proxy: proxy.Length > 0 ? proxy : null,
                        log: rp.AddLine);
                }

                var player = new z3nDash.Xml.XmlPlayer(project, session.Instance, rp.AddLine);
                var res    = player.Play(tpl, Path.GetDirectoryName(Path.GetFullPath(scriptPath))!, cts.Token);

                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true, run = runId }), id);

                if (!res.Success)
                {
                    rp.AddLine($"[ERR] прервано на ветке {res.FailedAt}: {res.Message}");
                    RunLogger(scheduleTag, runId)?.Error($"[{name}] xml failed: {res.Message}");
                    rp.Result = res.Message;
                    _running.TryRemove(instanceKey, out _);
                    UpdateStatus(db, id, "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
                    FinishQueueEntry(db, queueUuid, "error", runId);
                    return;
                }

                rp.Result = "ok";
                accountStatus = "idle";
                RunLogger(scheduleTag, runId)?.Info($"[{name}] xml done run={runId}, веток {res.BranchesRun}");
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle", DateTime.UtcNow, "0", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "done", runId);
            }
            catch (Exception ex)
            {
                rp.AddLine("[ERR] " + ex.Message);
                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true, run = runId }), id);
                RunLogger(scheduleTag, runId)?.Error($"[{name}] xml failed: {ex.Message}");
                rp.Result = ex.Message;
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "error", runId);
            }
            finally
            {
                // Ресурс освобождаем первым и так, чтобы его освобождение не
                // могло уронить уже полученный результат прогона.
                if (account != null)
                {
                    try { ReleaseAccount(db, accountTable, account, accountStatus, RunLogger(scheduleTag, runId)); }
                    catch (Exception ex) { rp.AddLine($"[warn] аккаунт не освобождён: {ex.Message}"); }
                    account = null;
                }
                if (session is not null) await session.DisposeAsync();
                // Профиль антика закрываем только если сами его открывали:
                // при прогоне по аккаунтам иначе накопятся десятки открытых окон.
                if (externalProfile is not null)
                    await BrowserProvider.StopAsync(brConfig, externalProfile, rp.AddLine);
                cts.Dispose();
                TopUp(db, id);
            }

            return;
        }
        var useVenv = record.GetValueOrDefault("use_venv", "false") == "true";
        // venv создаётся лениво: чекбокс можно поставить до того, как каталог появится.
        if (useVenv && executor == "python") PythonEnv.Ensure(scriptPath, line => _log?.Info($"[{name}] {line}"));
        var (fileName, arguments) = BuildCommand(executor, scriptPath, args, useVenv);

        _log?.Info($"[{name}] launch → {fileName} {Path.GetFileName(scriptPath)} run={runId}");
        UpdateStatus(db, id, "running", firedAt, "", "", runId);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = Directory.Exists(Path.GetDirectoryName(scriptPath) ?? "")
                                        ? Path.GetDirectoryName(scriptPath)!
                                        : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        TaskRunContext.Current.ApplyEnvironment(psi.Environment);
        var sdkPath = Path.Combine(AppContext.BaseDirectory, "sdk", "python");
        psi.Environment["PYTHONPATH"] = sdkPath + (psi.Environment.TryGetValue("PYTHONPATH", out var pythonPath)
            && !string.IsNullOrEmpty(pythonPath) ? Path.PathSeparator + pythonPath : "");

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var rp2     = new RunningProcess(process, firedAt, null, broadcast);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[LIVE] process task starting id={id} name={name} run={runId} key={instanceKey} exe={fileName}");
        Console.ResetColor();

        using var guard = new RunTimeout(timeoutSec, () =>
        {
            rp2.AddLine($"[ERR] лимит времени {timeoutSec}s исчерпан — процесс снимается");
            rp2.Kill();
        });

        try
        {
            process.Start();

            _running[instanceKey] = rp2;
            threadSlot?.Dispose();

            var stdoutTask = ReadStreamAsync(process.StandardOutput.BaseStream, rp2, prefix: "");
            var stderrTask = ReadStreamAsync(process.StandardError.BaseStream,  rp2, prefix: "");

            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true, run = runId }), id);

            string output = rp2.Snapshot();
            _log?.Info($"[{name}] exit {process.ExitCode} run={runId}");
            _running.TryRemove(instanceKey, out _);

            // Снятый по лимиту процесс — это провал прогона, а не его исход:
            // код возврата у убитого дерева случайный и мог бы совпасть с нулём.
            if (guard.Fired)
            {
                _log?.Warn($"[{name}] timeout {timeoutSec}s run={runId}");
                RunLogger(scheduleTag, runId)?.Error($"[{name}] timeout {timeoutSec}s run={runId}");
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", output, runId);
                FinishQueueEntry(db, queueUuid, "error", runId);
            }
            else
            {
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle", DateTime.UtcNow, process.ExitCode.ToString(), output, runId);
                FinishQueueEntry(db, queueUuid, "done", runId);
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"[{name}] launch failed: {ex.Message}");
            _running.TryRemove(instanceKey, out _);
            UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", ex.Message, runId);
            FinishQueueEntry(db, queueUuid, "error", runId);
        }
        finally
        {
            process.Dispose();
            TopUp(db, id);
        }
    }

    private static async Task ReadStreamAsync(Stream stream, RunningProcess rp, string prefix)
    {
        var buffer = new byte[1024];
        var sb     = new StringBuilder();
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

            int idx;
            while ((idx = sb.ToString().IndexOfAny(['\n', '\r'])) >= 0)
            {
                var line = sb.ToString(0, idx).Trim();
                sb.Remove(0, idx + 1);
                if (line.Length > 0)
                    rp.AddLine(prefix + line);
            }
        }

        var tail = sb.ToString().Trim();
        if (tail.Length > 0)
            rp.AddLine(prefix + tail);
    }

    // ── schedule_tag: имя задачи без пробелов, lowercase ─────────────────────
    private Logger? RunLogger(string scheduleTag, string runId)
    {
        if (_log == null) return null;
        return new Logger(
            taskId:  scheduleTag,
            session: runId);
    }

    private static string BuildScheduleTag(string name)
        => string.IsNullOrEmpty(name) ? "unknown" : name.Trim().Replace(" ", "_");

    private void UpdateStatus(Db db, string id, string status, DateTime lastRun, string exitCode, string output, string runId = "")
    {
        var safe = output
            .Replace("'", "''")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "");

        string incrSql = "";
        if (exitCode != "")
        {
            bool isPostgre = db.Mode == dbMode.Postgre;
            string castTotal   = isPostgre
                ? "COALESCE(NULLIF(\"runs_total\",''),'0')::int + 1"
                : "CAST(COALESCE(NULLIF(\"runs_total\",''),'0') AS INTEGER) + 1";
            string castSuccess = isPostgre
                ? "COALESCE(NULLIF(\"runs_success\",''),'0')::int + 1"
                : "CAST(COALESCE(NULLIF(\"runs_success\",''),'0') AS INTEGER) + 1";

            incrSql = isPostgre
                ? $", runs_total = ({castTotal})::text"
                : $", runs_total = CAST({castTotal} AS TEXT)";

            if (exitCode == "0")
                incrSql += isPostgre
                    ? $", runs_success = ({castSuccess})::text"
                    : $", runs_success = CAST({castSuccess} AS TEXT)";
        }

        string runIdSql = !string.IsNullOrEmpty(runId) ? $", last_run_id = '{runId}'" : "";

        db.Upd(
            $"status = '{status}', last_run = '{lastRun:yyyy-MM-dd HH:mm:ss}', last_exit = '{exitCode}', last_output = '{safe}'{incrSql}{runIdSql}",
            Table,
            where: $"\"id\" = '{id}'"
        );
    }

    /// <summary>
    /// Пора ли запускать задачу. Режим берётся из schedule_mode: off — только
    /// вручную, cron — выражение из колонки cron, zp — модель планировщика
    /// ZennoPoster из schedule_json.
    /// </summary>
    private static ZpSchedule.Decision Decide(Dictionary<string, string> r, DateTime now, int active, int maxThreads)
        => IsHeld(r, now) ? ZpSchedule.Decision.No : r.GetValueOrDefault("schedule_mode", "off") switch
        {
            "cron" => CronFires(r.GetValueOrDefault("cron", ""), now)
                          ? new ZpSchedule.Decision(true, 1)
                          : ZpSchedule.Decision.No,
            "zp"   => ZpSchedule.ShouldFire(ZpSchedule.Parse(r.GetValueOrDefault("schedule_json", "")),
                                            ReadState(r, active, maxThreads), now),
            _      => ZpSchedule.Decision.No,
        };

    private static bool CronFires(string cron, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        try
        {
            var expr = CronExpression.Parse(cron);
            var prev = expr.GetNextOccurrence(now.AddMinutes(-1), TimeZoneInfo.Utc);
            return prev.HasValue && prev.Value >= now.AddMinutes(-1) && prev.Value <= now;
        }
        catch { return false; }
    }

    private static ZpSchedule.State ReadState(Dictionary<string, string> r, int active, int maxThreads)
    {
        DateTime? lastRun = DateTime.TryParse(r.GetValueOrDefault("last_run", ""), out var lr) ? lr : null;
        DateTime? started = DateTime.TryParse(r.GetValueOrDefault("sched_started_at", ""), out var sa) ? sa : null;
        var runs = int.TryParse(r.GetValueOrDefault("sched_runs", "0"), out var n) ? n : 0;
        return new ZpSchedule.State(lastRun, runs, started, active, maxThreads);
    }

    /// <summary>
    /// Счётчик запусков расписания. Отдельно от runs_total, потому что тот
    /// считает и ручные запуски, а условие «Завершить после N повторений»
    /// должно видеть только запуски самого расписания.
    /// </summary>
    private void NoteScheduledFire(Db db, string id, Dictionary<string, string> record, int attempts)
    {
        var runs = int.TryParse(record.GetValueOrDefault("sched_runs", "0"), out var n) ? n : 0;
        runs += Math.Max(1, attempts);
        record["sched_runs"] = runs.ToString();
        db.Upd($"sched_runs = '{runs}'", Table, where: $"\"id\" = '{id}'");
    }

    private static string ResolveGitBash()
    {
        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            @"C:\Git\bin\bash.exe",
        ];
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return "bash"; // PATH fallback
    }

    private static string ResolvePython(string scriptPath, bool useVenv)
        => PythonEnv.Resolve(scriptPath, useVenv);

    private static (string fileName, string arguments) BuildCommand(string executor, string scriptPath, string args, bool useVenv = false)
    {
        static string TsNodeArgs(string path, string extraArgs)
        {
            string dir      = Path.GetDirectoryName(path) ?? ".";
            string tsconfig = Path.Combine(dir, "tsconfig.json");
            string project  = File.Exists(tsconfig) ? $"--project \"{tsconfig}\" " : "";
            return $"/c npx ts-node {project}\"{path}\" {extraArgs}".Trim();
        }

        return executor.ToLower() switch
        {
            "python"  => (ResolvePython(scriptPath, useVenv), $"\"{scriptPath}\" {args}".Trim()),
            "node"    => ("node",     $"\"{scriptPath}\" {args}".Trim()),
            "ts-node" => ("cmd.exe",  TsNodeArgs(scriptPath, args)),
            "npm"     => ("cmd.exe",  $"/c cd /d \"{Path.GetDirectoryName(scriptPath)}\" && npm {args}".Trim()),
            "csx"     => ("cmd.exe",  $"/c dotnet-script \"{scriptPath}\" {args}".Trim()),
            "exe"     => (scriptPath, args),
            "cmd"     => ("cmd.exe",  $"/c {scriptPath} {args}".Trim()),
            "bat"     => ("cmd.exe",  $"/c \"{scriptPath}\" {args}".Trim()),
            "bash" => (ResolveGitBash(), $"\"{scriptPath}\" {args}".Trim()),
            "ps1" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}".Trim()),
            _         => (ResolvePython(scriptPath, useVenv), $"\"{scriptPath}\" {args}".Trim()),
        };
    }

    /// <summary>
    /// Колонки строки расписания для чтения через GetLines. last_output исключён
    /// намеренно: GetLines склеивает колонки разделителем '¦', а вывод шаблона
    /// содержит его как есть — DbGetLine печатает результат тем же разделителем.
    /// Одна такая строка в last_output сдвигала все колонки вправо, и запуск
    /// падал на разборе payload_values, не оставляя следа в выводе задачи.
    /// </summary>
    public static List<string> RowColumns(Db db)
        => db.GetTableColumns(Table).Where(c => c != "last_output").ToList();

    private static Dictionary<string, string> ParseRow(string row, List<string> columns)
    {
        var values = row.Split('¦');
        var dict   = new Dictionary<string, string>();
        for (int i = 0; i < columns.Count && i < values.Length; i++)
            dict[columns[i]] = values[i];
        return dict;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRunning(string id)
        => _running.Any(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited);

    public (int pid, long uptimeSec, long memoryMB, bool running) GetProcessInfo(string id)
    {
        var inst = _running
            .Where(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        if (inst == null) return (-1, 0, 0, false);
        return (inst.Pid, inst.UptimeSec, inst.MemoryMB, true);
    }

    // runId == null → все инстансы; задан → только этот
    public string GetLiveOutput(string id, string? runId = null)
    {
        var keys = _running.Keys
            .Where(k => k.StartsWith(id + ":"))
            .Where(k => runId == null || k == $"{id}:{runId}")
            .ToList();

        if (keys.Count == 0) return "";
        if (keys.Count == 1)
            return _running.TryGetValue(keys[0], out var single) ? single.Snapshot() : "";

        return string.Join("\n\n", keys
            .Where(k => _running.ContainsKey(k))
            .Select(k => $"── {k[(id.Length + 1)..]} ──\n{_running[k].Snapshot()}"));
    }

    /// <summary>
    /// Снимок живого вывода как готовые SSE-события: строка, время и id нити.
    /// Догруженная при переподписке история раскрашивается так же, как поток.
    /// </summary>
    public List<string> GetLiveEvents(string id)
    {
        return _running
            .Where(kv => kv.Key.StartsWith(id + ":"))
            .SelectMany(kv => kv.Value.Lines().Select(l => (l.At, l.Text, Run: kv.Key[(id.Length + 1)..])))
            .OrderBy(e => e.At)
            .Select(e => JsonSerializer.Serialize(new
            {
                line  = e.Text,
                run   = e.Run,
                ts    = e.At.ToString("o"),
                level = e.Text.StartsWith("[ERR]") ? "ERROR" : "INFO",
            }))
            .ToList();
    }

    public string GetResult(string id)
        => _running
            .Where(kv => kv.Key.StartsWith(id + ":"))
            .Select(kv => kv.Value.Result)
            .FirstOrDefault() ?? "";

    public void ClearLiveOutput(string id)
    {
        foreach (var kv in _running.Where(kv => kv.Key.StartsWith(id + ":")))
            kv.Value.Clear();
    }

    public List<Dictionary<string, string>> GetInstances(string id)
        => _running
            .Where(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited)
            .Select(kv => new Dictionary<string, string>
            {
                { "runId",     kv.Key[(id.Length + 1)..] },
                { "uptimeSec", kv.Value.UptimeSec.ToString() },
                { "pid",       kv.Value.Pid.ToString() },
                { "memoryMB",  kv.Value.MemoryMB.ToString() },
            })
            .ToList();

    public List<Dictionary<string, string>> GetQueueItems(Db db, string scheduleId)
    {
        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return new();
        var colsSql = string.Join(", ", qCols.Select(c => $"\"{c}\""));
        var raw = db.Query($"SELECT {colsSql} FROM \"{QueueTable}\" WHERE \"schedule_id\" = '{scheduleId}' ORDER BY \"priority\" ASC, \"queued_at\" ASC");
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split('·').Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => ParseRow(r, qCols)).ToList();
    }

    public void ClearQueue(Db db, string scheduleId)
        => db.Query($"DELETE FROM \"{QueueTable}\" WHERE \"schedule_id\" = '{scheduleId}' AND \"status\" = 'pending'");

    private readonly SemaphoreSlim _reserveLock = new(1, 1);

    public async Task<Dictionary<string, string>?> ReserveAccount(
        Db db, string table, string condition, Logger? log = null)
    {
        await _reserveLock.WaitAsync();
        try
        {
            var cols    = db.GetTableColumns(table);
            if (cols.Count == 0)
            {
                LoggerExt.Debug($"table not found: {table}");
                return null;
            }
            var colsSql = string.Join(", ", cols.Select(c => $"\"{c}\""));
            var query   = $"SELECT {colsSql}, COUNT(*) OVER() AS __total FROM \"{table}\" WHERE ({condition}) AND \"status\" != 'busy' ORDER BY RANDOM() LIMIT 1";
            var rawRow  = db.Query(query, thrw: false);

            if (string.IsNullOrWhiteSpace(rawRow))
            {
                LoggerExt.Debug($"no accs by query [{query}]");
                return null;
            }

            var values = rawRow.Split('¦');

            // __total — последнее поле, за пределами cols
            var total = values.Length > cols.Count ? values[cols.Count].Trim() : "?";

            var record = new Dictionary<string, string>();
            for (int i = 0; i < cols.Count && i < values.Length; i++)
                record[cols[i]] = values[i];

            var keyCol = "id";
            var id     = record.GetValueOrDefault(keyCol, "");
            if (string.IsNullOrEmpty(id)) return null;

            db.Query($"UPDATE \"{table}\" SET \"status\" = 'busy' WHERE \"{keyCol}\" = '{id}'");
        
            // ── сохраняем метаданные лока ──────────────────────────────
            record["__keyCol"]   = keyCol;
            record["__table"]    = table;
            record["__lockedAt"] = DateTime.UtcNow.ToString("o");  // ISO timestamp
        
            // ── логируем какой аккаунт залочен ────────────────────────
            var acctLabel = record.GetValueOrDefault("address",
                record.GetValueOrDefault("login",
                    record.GetValueOrDefault("name", id)));
            log?.Info($"account locked  table={table} id={id} label={acctLabel} queue={total}");
        
            return record;
        }
        finally
        {
            _reserveLock.Release();
        }
    }

    public void ReleaseAccount(
        Db db, string table, Dictionary<string, string> account, 
        string status, Logger? log = null)
    {
        var keyCol = account.GetValueOrDefault("__keyCol", "id");
        var id     = account.GetValueOrDefault(keyCol, "");
        if (string.IsNullOrEmpty(id)) return;

        db.Query($"UPDATE \"{table}\" SET \"status\" = '{status}' WHERE \"{keyCol}\" = '{id}'");

        // ── логируем длительность лока ────────────────────────────────
        var acctLabel = account.GetValueOrDefault("address",
            account.GetValueOrDefault("login",
                account.GetValueOrDefault("name", id)));

        var lockedSec = "";
        if (account.TryGetValue("__lockedAt", out var lockedAt) &&
            DateTime.TryParse(lockedAt, out var lockedTime))
        {
            var elapsed = DateTime.UtcNow - lockedTime;
            lockedSec = $" locked={elapsed.TotalSeconds:F1}s";
        }

        log?.Info($"account released table={table} id={id} label={acctLabel} status={status}{lockedSec}");
    }

    public void Kill(string id)
    {
        KillAllInstances(id);
        RemoveInstancesFromRunning(id);
    }

    public void KillInstance(string id, string runId)
    {
        var key = $"{id}:{runId}";
        if (!_running.TryGetValue(key, out var rp)) return;
        rp.Kill();
        _running.TryRemove(key, out _);
    }

    /// <summary>
    /// Ручной запуск. Наливает все свободные нити задачи: если в настройках стоит
    /// пять потоков, кнопка Run поднимает пять инстансов, а не один.
    /// </summary>
    public void FireNow(string id, Dictionary<string, string> record, Db db)
    {
        var overlap    = record.GetValueOrDefault("on_overlap", "skip");
        var maxThreads = ReadThreads(record);
        var active     = CountActiveInstances(id);

        if (overlap == "kill_restart" && active > 0)
        {
            KillAllInstances(id);
            RemoveInstancesFromRunning(id);
            active = 0;
        }

        var free = Math.Max(0, maxThreads - active);
        if (free == 0)
        {
            // Свободных нитей нет: очередь копит только режим parallel.
            if (overlap == "parallel") EnqueueItem(db, id, record, priority: 0);
            return;
        }

        var stagger = TimeSpan.Zero;
        for (var i = 0; i < free; i++)
        {
            StartInstance(db, record, DateTime.UtcNow, delay: stagger, automatic: false);
            stagger += NextStaggerGap();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var rp in _running.Values)
            rp.Kill();
    }

    // ── Аккаунт под прогон шаблона ────────────────────────────────────────────

    /// <summary>
    /// Шаблон работает по аккаунту: переменная acc0 объявлена в блоке Variables
    /// и упомянута где-то ещё — в шаге или в собственном коде.
    ///
    /// Одного объявления мало. В шаблоны ZennoPoster переменные переезжают из
    /// проекта-донора пачкой, и часть из них не читает никто. Проверено на
    /// xml_example/simroute_test.xml: acc0 там объявлена, а во всём файле
    /// встречается ровно один раз — в самом объявлении.
    /// </summary>
    private static bool TemplateUsesAccount(z3nDash.Xml.XmlTemplate tpl, string templatePath)
    {
        if (!tpl.Variables.ContainsKey("acc0")) return false;

        string text;
        try { text = File.ReadAllText(templatePath); } catch { return false; }

        // Блок объявлений вырезаем: имя переменной есть в нём всегда.
        var start = text.IndexOf("<Variables", StringComparison.Ordinal);
        var end   = text.IndexOf("</Variables>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
            text = text.Remove(start, end - start + "</Variables>".Length);

        return MentionsName(text, "acc0");
    }

    /// <summary>Имя целиком, а не куском длинного слова: acc0 — это не acc01.</summary>
    private static bool MentionsName(string text, string name)
    {
        for (var i = text.IndexOf(name, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 ? ' ' : text[i - 1];
            var after  = i + name.Length >= text.Length ? ' ' : text[i + name.Length];
            if (!char.IsLetterOrDigit(before) && before != '_' &&
                !char.IsLetterOrDigit(after)  && after  != '_')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Данные аккаунта — в переменные проекта и в payload запуска: колонки самого
    /// аккаунта, acc0, строки из instance и folder_profile. payload заполняется
    /// тоже, потому что именно из него ветка xml берёт прокси и профиль браузера,
    /// а у аккаунта они свои — и должны перекрыть значения из настроек задачи.
    /// </summary>
    private static void SeedAccount(Db db, StubProject project,
                                    Dictionary<string, string> payload,
                                    Dictionary<string, string> account)
    {
        var accId = account.GetValueOrDefault("id", "");

        void Put(string key, string value, bool toPayload = true)
        {
            if (string.IsNullOrEmpty(key) || key.StartsWith("__")) return;
            project.Variables[key].Value = value ?? "";
            // id аккаунта в payload не кладём: там это идентификатор задачи.
            if (toPayload && key != "id") payload[key] = value ?? "";
        }

        foreach (var (key, value) in account) Put(key, value);

        var instanceCols = db.GetTableColumns(DbSchema.Instance.Name);
        if (instanceCols.Count > 0)
            foreach (var (key, value) in db.GetColumns(string.Join(",", instanceCols),
                         DbSchema.Instance.Name, where: $"\"id\" = '{accId}'"))
                Put(key, value);

        var profileCols = db.GetTableColumns("folder_profile");
        if (profileCols.Count > 0)
        {
            var profile = db.GetColumns(string.Join(",", profileCols), "folder_profile",
                                        where: $"\"id\" = '{accId}'");
            foreach (var (key, value) in profile) Put(key, value);
            if (profile.TryGetValue("UserAgent", out var ua) && !string.IsNullOrEmpty(ua))
                project.Profile.UserAgent = ua;
        }

        Put("acc0", accId);
    }

    // ── Ограничение времени прогона ───────────────────────────────────────────

    /// <summary>
    /// Лимит времени одного прогона в секундах. 0 или мусор — лимита нет.
    /// </summary>
    private static int ReadTimeoutSeconds(Dictionary<string, string> record)
        => int.TryParse(record.GetValueOrDefault("timeout_seconds", "0"), out var v) && v > 0 ? v : 0;

    /// <summary>
    /// Сторож времени прогона. По истечении лимита один раз вызывает обрыв.
    /// Внешний процесс при этом убивается деревом — это остановка по факту;
    /// внутренние задачи (internal, csx, xml) получают отмену токена, а она
    /// кооперативная: код, который токен не смотрит, ею не прервётся.
    /// </summary>
    private sealed class RunTimeout : IDisposable
    {
        private readonly System.Threading.Timer? _timer;
        private int _fired;

        public RunTimeout(int seconds, Action onTimeout)
        {
            if (seconds <= 0) return;
            _timer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref _fired, 1) != 0) return;
                try { onTimeout(); } catch { }
            }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        }

        /// <summary>Лимит уже сработал — прогон оборван сторожем, а не собой.</summary>
        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Dispose() => _timer?.Dispose();
    }

    // ── RunningProcess ────────────────────────────────────────────────────────

    internal sealed class RunningProcess
    {
        private readonly System.Diagnostics.Process? _process;
        private readonly CancellationTokenSource?    _cts;
        private readonly List<(DateTime At, string Text)> _lines = new();
        private readonly object                      _lock  = new();
        private readonly Action<string>?             _broadcast;

        public string   Result    { get; set; } = "";
        public DateTime StartedAt { get; }

        public RunningProcess(System.Diagnostics.Process? process, DateTime startedAt, CancellationTokenSource? cts, Action<string>? broadcast = null)
        {
            _process   = process;
            StartedAt  = startedAt;
            _cts       = cts;
            _broadcast = broadcast;
        }


        public int Pid =>
            _process == null ? -1 : (_process.HasExited ? -1 : _process.Id);

        public long MemoryMB
        {
            get
            {
                try
                {
                    var target = _process is { HasExited: false } ? _process : System.Diagnostics.Process.GetCurrentProcess();
                    target.Refresh();
                    return target.WorkingSet64 / 1024 / 1024;
                }
                catch { return 0; }
            }
        }

        public long UptimeSec => (long)(DateTime.UtcNow - StartedAt).TotalSeconds;

        public bool HasExited =>
            _process == null ? (_cts?.IsCancellationRequested ?? true) : _process.HasExited;

        public void Kill()
        {
            if (_process != null)
                try { _process.Kill(entireProcessTree: true); } catch { }
            _cts?.Cancel();
        }

        public void AddLine(string line)
        {
            lock (_lock) { _lines.Add((DateTime.UtcNow, line)); if (_lines.Count > 2000) _lines.RemoveAt(0); }
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE+] {line}");
            Console.ResetColor();
            _broadcast?.Invoke(line);
        }

        public string Snapshot()
        {
            lock (_lock) { return string.Join("\n", _lines.Select(l => l.Text)); }
        }

        /// <summary>Строки со временем — для восстановления цветного лога при переподписке.</summary>
        public List<(DateTime At, string Text)> Lines()
        {
            lock (_lock) { return new List<(DateTime, string)>(_lines); }
        }

        public void Clear()
        {
            lock (_lock) { _lines.Clear(); }
        }
    }
}
