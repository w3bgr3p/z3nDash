// ══════════════════════════════════════════════════════════════════════════════
// PlaySession.cs — контекст одного прогона шаблона.
//
// Держит всё, что живёт между ветками: проект с переменными и профилем,
// скомпилированный общий код, исполнитель веток и позицию в графе. Один вызов
// StepOnce выполняет одну ветку и сдвигает позицию по правилам перехода.
//
// Про HTTP и про экран не знает ничего: этим пользуется и планировщик, который
// гонит маршрут целиком, и отладчик, который идёт по шагу. Правила перехода
// поэтому лежат ровно в одном месте, и «как отлаживали» не может разойтись
// с «как побежало в бою».
// ══════════════════════════════════════════════════════════════════════════════

using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash.Xml;

/// <summary>Чем закончился шаг.</summary>
public enum StepOutcome
{
    /// <summary>Ветка обработана, позиция сдвинута, можно продолжать.</summary>
    Moved,
    /// <summary>Маршрут закончился: у последней ветки узла не было перехода.</summary>
    Finished,
    /// <summary>Уходить некуда — в бою это обрыв маршрута.</summary>
    Failed
}

/// <summary>Результат одного шага. FailedAt и Exception заполнены только у Failed.</summary>
public readonly record struct StepResult(
    StepOutcome Outcome,
    Branch?     Executed,
    Branch?     FailedAt,
    string      Message,
    Exception?  Exception)
{
    public static StepResult Moved(Branch executed) => new(StepOutcome.Moved, executed, null, "", null);
}

/// <summary>
/// Окружение шаблона не собралось: общий код не компилируется или нет ссылок.
/// Отдельный тип, потому что это не ошибка ветки — маршрут не начинается вовсе.
/// </summary>
public sealed class PlaySetupException : Exception
{
    public PlaySetupException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PlaySession
{
    public XmlTemplate              Template { get; }
    public IZennoPosterProjectModel Project  { get; }
    public Instance                 Instance { get; }

    private readonly BranchExecutor   _exec;
    private readonly Action<string>   _log;
    private readonly XmlPlayerOptions _opt;

    // Позиция обхода. В прежнем Walk это были локальные переменные цикла;
    // здесь они поля, поэтому «где мы стоим» можно спросить снаружи.
    private Step _step;
    private int  _index;
    private int  _run;

    /// <summary>Ветка, которая будет выполнена следующей. null — маршрут закончен.</summary>
    public Branch? Current => _index < _step.Branches.Count ? _step.Branches[_index] : null;

    /// <summary>Сколько веток уже выполнено.</summary>
    public int BranchesRun => _run;

    /// <summary>
    /// Собрать контекст: переменные, личность, общий код, исполнитель. Всё, что
    /// может не сложиться, падает здесь, до первой ветки — PlaySetupException.
    /// </summary>
    public PlaySession(XmlTemplate tpl, string templateDir,
                       IZennoPosterProjectModel project, Instance instance,
                       Action<string>? log = null, XmlPlayerOptions? options = null)
    {
        Template = tpl;
        Project  = project;
        Instance = instance;
        _log     = log ?? Console.WriteLine;
        _opt     = options ?? new XmlPlayerOptions();

        if (tpl.Start.IsNone)
            throw new PlaySetupException("в шаблоне нет <Start> — это не проект ZennoPoster");

        if (tpl.Locate(tpl.Start) is not { } entry)
            throw new PlaySetupException($"<Start> ведёт в никуда: {tpl.Start}");

        (_step, _index) = entry;

        var dead = tpl.UnreachableSteps();
        if (dead.Count > 0)
            _log($"[xml] недостижимо узлов: {dead.Count} — артефакты разработки, в маршрут не входят");

        // Каталог проекта — папка шаблона, имя — сам файл. Так их разбирает и
        // ZP, и перенесённый код: ReadEnv ищет .env в project.Path, а
        // Constantes.ProjectName — файл с именем project.Name внутри него. Пока
        // это не проставлено, .env искался рядом с приложением, и шаблон падал
        // на «… is not set in .env», хотя файл лежит рядом с ним.
        if (project is StubProject stub)
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
        ProfileGenerator.Fill(project, tpl.Profile);
        _log($"[xml] профиль: {project.Profile.Name} {project.Profile.Surname}, " +
             $"{project.Profile.Gender}, {project.Profile.BirthDate}, login {project.Profile.Login}");

        XmlCodeRunner code;
        try
        {
            code = new XmlCodeRunner(
                new XmlCodeGlobals { project = project, instance = instance }, templateDir, tpl.OwnCode);
        }
        catch (Exception ex)
        {
            // Общий код и ссылки готовятся один раз до старта. Если они не
            // сложились, маршрут не начинается вовсе — это не ошибка ветки.
            throw new PlaySetupException(ex.Message, ex);
        }

        if (code.UnknownUsings.Count > 0)
            _log($"[xml] usings без пространства имён, пропущены: {string.Join(", ", code.UnknownUsings)} " +
                 "— ветки, которые ими пользуются, упадут на именах");

        _exec = new BranchExecutor(project, instance, code, _log);
    }

    /// <summary>
    /// Перевести позицию на другую ветку. Нужно для GoodEnd/BadEnd, вход в
    /// которые задан не стрелкой, а узлом холста, и для «выполнить до ветки».
    /// Возвращает false, если такой ветки в шаблоне нет.
    /// </summary>
    public bool JumpTo(BranchRef target)
    {
        if (Template.Locate(target) is not { } at) return false;
        (_step, _index) = at;
        return true;
    }

    /// <summary>
    /// Выполнить текущую ветку и сдвинуть позицию. Тело — прежний цикл Walk без
    /// изменений в правилах: выключенная ветка уходит по OnSuccess, выбор
    /// Logic/Switch старше OnSuccess, необязательная ветка глотает ошибку,
    /// пустой переход означает следующую ветку узла.
    /// </summary>
    public StepResult StepOnce(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_index >= _step.Branches.Count)
        {
            _log($"[xml] маршрут закончен, выполнено веток: {_run}");
            return new StepResult(StepOutcome.Finished, null, null, "", null);
        }

        if (++_run > _opt.MaxBranches)
            return new StepResult(StepOutcome.Failed, null, _step.Branches[_index],
                $"превышен предел в {_opt.MaxBranches} веток — похоже на цикл без выхода", null);

        var branch = _step.Branches[_index];
        BranchRef next;

        // Выключенное действие (серый кубик на холсте) не исполняется:
        // в ZP оно просто «успешно» и маршрут идёт по OnSuccess. Не по
        // index++ — стрелка с выключенной ветки никуда не девается, и
        // если она вела в другой узел, вести должна по-прежнему.
        if (branch.IsDisabled)
        {
            if (_opt.Trace) _log($"[xml] {_run,4}. {branch} — выключено, пропуск");
            next = branch.OnSuccess;
            if (next.IsNone) { _index++; return StepResult.Moved(branch); }
            if (Template.Locate(next) is not { } skipTo)
                return new StepResult(StepOutcome.Failed, branch, branch,
                    $"переход в никуда: {next} — такой ветки в шаблоне нет", null);
            (_step, _index) = skipTo;
            return StepResult.Moved(branch);
        }

        try
        {
            if (_opt.Trace) _log($"[xml] {_run,4}. {branch}");

            var result = _exec.Execute(branch, ct);
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
                return new StepResult(StepOutcome.Failed, branch, branch, ex.Message, ex);
            }
        }

        if (next.IsNone)
        {
            _index++;
            return StepResult.Moved(branch);
        }

        if (Template.Locate(next) is not { } target)
            return new StepResult(StepOutcome.Failed, branch, branch,
                $"переход в никуда: {next} — такой ветки в шаблоне нет", null);

        (_step, _index) = target;
        return StepResult.Moved(branch);
    }

    private void StoreOutput(Branch branch, BranchResult result)
    {
        var name = Macros.OutputVariableName(branch.OutputVariable);
        if (name.Length == 0) return;

        Project.Variables[name].Value = result.Output;
        if (_opt.Trace) _log($"[xml]       → {name} = [{Cut(result.Output)}]");
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
        var assigned = Project.Variables as
            ZennoLab.InterfacesLibrary.ProjectModel.Collections.VariableList;

        foreach (var (name, value) in tpl.Variables)
        {
            if (assigned?.IsAssigned(name) == true) continue;
            if (string.IsNullOrEmpty(Project.Variables[name].Value))
                Project.Variables[name].Value = value;
        }
    }

    private static string Cut(string s, int max = 120)
        => s.Length <= max ? s : s[..max] + "…";
}
