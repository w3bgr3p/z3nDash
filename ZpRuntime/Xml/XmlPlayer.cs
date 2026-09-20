// ══════════════════════════════════════════════════════════════════════════════
// XmlPlayer.cs — обход графа шаблона.
//
// Правила перехода сняты с формата, а не выдуманы:
//   успех + OnSuccess задан  → прыжок по адресу;
//   успех + OnSuccess пуст   → следующая ветка того же Step;
//   ветка была последней     → маршрут закончен;
//   ошибка + OnError задан   → прыжок по адресу;
//   ошибка + OnError пуст    → маршрут падает, если ветка не IsNotNecessarily.
//   IsDisable="True"         → ветка не исполняется, маршрут идёт по OnSuccess.
//   ветка выбрала переход сама (Logic/Switch) → её выбор старше OnSuccess.
//
// Отдельно считается число шагов: шаблон с циклом, у которого сломано условие
// выхода, иначе крутится вечно. Предел настраивается, по умолчанию щедрый.
// ══════════════════════════════════════════════════════════════════════════════

using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash.Xml;

public sealed record PlayResult(
    bool       Success,
    string     Message,
    int        BranchesRun,
    Branch?    FailedAt,
    Exception? Exception)
{
    public static PlayResult Ok(int run)  => new(true, "ok", run, null, null);
}

public sealed class XmlPlayerOptions
{
    /// <summary>Предохранитель от вечного цикла.</summary>
    public int  MaxBranches { get; init; } = 10_000;
    /// <summary>Писать в лог каждую ветку. Шумно, но без этого шаблон непрозрачен.</summary>
    public bool Trace       { get; init; } = true;
}

public sealed class XmlPlayer
{
    private readonly IZennoPosterProjectModel _project;
    private readonly Instance                 _instance;
    private readonly Action<string>           _log;
    private readonly XmlPlayerOptions         _opt;

    public XmlPlayer(IZennoPosterProjectModel project, Instance instance,
                     Action<string>? log = null, XmlPlayerOptions? options = null)
    {
        _project  = project;
        _instance = instance;
        _log      = log ?? Console.WriteLine;
        _opt      = options ?? new XmlPlayerOptions();
    }

    public PlayResult Play(string templatePath, CancellationToken ct = default)
    {
        // Имя — файл шаблона: по нему Constantes.ProjectName ищет проект в каталоге.
        if (_project is StubProject stub && string.IsNullOrWhiteSpace(stub.Name))
            stub.Name = System.IO.Path.GetFileName(templatePath);

        return Play(XmlTemplate.Load(templatePath),
                    System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(templatePath))!,
                    ct);
    }

    public PlayResult Play(XmlTemplate tpl, string templateDir, CancellationToken ct = default)
    {
        if (tpl.Start.IsNone)
            return new PlayResult(false, "в шаблоне нет <Start> — это не проект ZennoPoster", 0, null, null);

        if (tpl.Locate(tpl.Start) is not { } entry)
            return new PlayResult(false, $"<Start> ведёт в никуда: {tpl.Start}", 0, null, null);

        var dead = tpl.UnreachableSteps();
        if (dead.Count > 0)
            _log($"[xml] недостижимо узлов: {dead.Count} — артефакты разработки, в маршрут не входят");

        // Каталог проекта — папка шаблона, имя — сам файл. Так их разбирает и
        // ZP, и перенесённый код: ReadEnv ищет .env в project.Path, а
        // Constantes.ProjectName — файл с именем project.Name внутри него. Пока
        // это не проставлено, .env искался рядом с приложением, и шаблон падал
        // на «… is not set in .env», хотя файл лежит рядом с ним.
        if (_project is StubProject stub)
        {
            stub.Path = templateDir;

            // Имя проекта задаёт вызывающий — именем файла шаблона, как в ZP.
            // Раньше здесь оно перетиралось атрибутом Name из самого XML, а там
            // у сохранённых из ProjectMaker шаблонов остаётся «Template.xml».
            // Шаблоны на это имя опираются: simroute.bolt.lgn берёт из него
            // название сервиса через Name.Split('.')[1], и вместо «bolt»
            // получалось «xml» — выборка из базы уходила пустой.
            if (string.IsNullOrWhiteSpace(stub.Name) && !string.IsNullOrWhiteSpace(tpl.Name))
                stub.Name = tpl.Name;
        }

        SeedVariables(tpl);

        // Личность нужна до первой ветки: {-Profile.Name-} встречается уже в
        // SetAttribute, а ветки присваивают Profile.Password напрямую.
        ProfileGenerator.Fill(_project, tpl.Profile);
        _log($"[xml] профиль: {_project.Profile.Name} {_project.Profile.Surname}, " +
             $"{_project.Profile.Gender}, {_project.Profile.BirthDate}, login {_project.Profile.Login}");

        XmlCodeRunner code;
        try
        {
            code = new XmlCodeRunner(
                new XmlCodeGlobals { project = _project, instance = _instance }, templateDir, tpl.OwnCode);
        }
        catch (Exception ex)
        {
            // Общий код и ссылки готовятся один раз до старта. Если они не
            // сложились, маршрут не начинается вовсе — это не ошибка ветки.
            _log($"[xml] окружение шаблона не собрано: {ex.Message}");
            return new PlayResult(false, ex.Message, 0, null, ex);
        }

        if (code.UnknownUsings.Count > 0)
            _log($"[xml] usings без пространства имён, пропущены: {string.Join(", ", code.UnknownUsings)} " +
                 "— ветки, которые ими пользуются, упадут на именах");

        var exec = new BranchExecutor(_project, _instance, code, _log);

        // Имя файла, а не атрибут Name из XML: там у половины шаблонов лежит
        // «Template.xml» от мастера создания, и по такой строке в логе не понять,
        // какой из них прогоняли.
        var title = string.IsNullOrWhiteSpace(_project.Name) ? tpl.Name : _project.Name;
        _log($"[xml] {title}: старт с {tpl.Start}");

        var result = Walk(tpl, exec, entry, ct);

        // Обработчики конца — тоже обычные цепочки веток, просто вход в них
        // задан не стрелкой, а узлом холста.
        var ending = result.Success ? tpl.GoodEnd : tpl.BadEnd;
        if (!ending.IsNone && tpl.Locate(ending) is { } endEntry)
        {
            _log($"[xml] {(result.Success ? "GoodEnd" : "BadEnd")} → {ending}");
            var tail = Walk(tpl, exec, endEntry, ct);
            if (!tail.Success)
                _log($"[xml] обработчик конца сам упал: {tail.Message}");
        }

        return result;
    }

    private void SeedVariables(XmlTemplate tpl)
    {
        // ZP заводит объявленные переменные до старта. Без этого первое же
        // обращение к необъявленной переменной в ветке даёт пустоту там, где
        // шаблон рассчитывает на значение по умолчанию из настроек проекта.
        // Заданное вызывающим (InputSettings задачи) не трогаем даже когда там
        // пустая строка: в ZP пустое поле настроек — это выбор, а не «не задано».
        // simroute.bolt.lgn на пустом proxy_iso подставляет location; со значением
        // по умолчанию из XML («id») эта ветка не срабатывала вовсе.
        var assigned = _project.Variables as
            ZennoLab.InterfacesLibrary.ProjectModel.Collections.VariableList;

        foreach (var (name, value) in tpl.Variables)
        {
            if (assigned?.IsAssigned(name) == true) continue;
            if (string.IsNullOrEmpty(_project.Variables[name].Value))
                _project.Variables[name].Value = value;
        }
    }

    private PlayResult Walk(XmlTemplate tpl, BranchExecutor exec,
                            (Step Step, int Index) from, CancellationToken ct)
    {
        var (step, index) = from;
        int run = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (index >= step.Branches.Count)
            {
                _log($"[xml] маршрут закончен, выполнено веток: {run}");
                return PlayResult.Ok(run);
            }

            if (++run > _opt.MaxBranches)
                return new PlayResult(false,
                    $"превышен предел в {_opt.MaxBranches} веток — похоже на цикл без выхода",
                    run, step.Branches[index], null);

            var branch = step.Branches[index];
            BranchRef next;

            // Выключенное действие (серый кубик на холсте) не исполняется:
            // в ZP оно просто «успешно» и маршрут идёт по OnSuccess. Не по
            // index++ — стрелка с выключенной ветки никуда не девается, и
            // если она вела в другой узел, вести должна по-прежнему.
            if (branch.IsDisabled)
            {
                if (_opt.Trace) _log($"[xml] {run,4}. {branch} — выключено, пропуск");
                next = branch.OnSuccess;
                if (next.IsNone) { index++; continue; }
                if (tpl.Locate(next) is not { } skipTo)
                    return new PlayResult(false,
                        $"переход в никуда: {next} — такой ветки в шаблоне нет", run, branch, null);
                (step, index) = skipTo;
                continue;
            }

            try
            {
                if (_opt.Trace) _log($"[xml] {run,4}. {branch}");

                var result = exec.Execute(branch, ct);
                StoreOutput(branch, result);

                // Ветка могла выбрать переход сама — так делает Logic/Switch.
                // Её выбор старше OnSuccess, в том числе когда выбран вариант
                // без стрелки: тогда Goto равен None и дальше работает то же
                // правило, что при пустом OnSuccess.
                next = result.Goto ?? branch.OnSuccess;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!branch.OnError.IsNone)
                {
                    _log($"[xml] {branch} → ошибка [{ex.Message}], ухожу по OnError");
                    next = branch.OnError;
                }
                else if (branch.IsOptional)
                {
                    _log($"[xml] {branch} → ошибка [{ex.Message}], ветка необязательная, иду дальше");
                    next = BranchRef.None;
                }
                else
                {
                    _log($"[xml] {branch} → ошибка [{ex.Message}], OnError не задан — маршрут прерван");
                    return new PlayResult(false, ex.Message, run, branch, ex);
                }
            }

            if (next.IsNone)
            {
                index++;
                continue;
            }

            if (tpl.Locate(next) is not { } target)
                return new PlayResult(false,
                    $"переход в никуда: {next} — такой ветки в шаблоне нет", run, branch, null);

            (step, index) = target;
        }
    }

    private void StoreOutput(Branch branch, BranchResult result)
    {
        var name = Macros.OutputVariableName(branch.OutputVariable);
        if (name.Length == 0) return;

        _project.Variables[name].Value = result.Output;
        if (_opt.Trace) _log($"[xml]       → {name} = [{Cut(result.Output)}]");
    }

    private static string Cut(string s, int max = 120)
        => s.Length <= max ? s : s[..max] + "…";
}
