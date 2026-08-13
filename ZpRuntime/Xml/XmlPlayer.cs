// ══════════════════════════════════════════════════════════════════════════════
// XmlPlayer.cs — обход графа шаблона.
//
// Правила перехода сняты с формата, а не выдуманы:
//   успех + OnSuccess задан  → прыжок по адресу;
//   успех + OnSuccess пуст   → следующая ветка того же Step;
//   ветка была последней     → маршрут закончен;
//   ошибка + OnError задан   → прыжок по адресу;
//   ошибка + OnError пуст    → маршрут падает, если ветка не IsNotNecessarily.
//
// Отдельно считается число шагов: шаблон с циклом, у которого сломано условие
// выхода, иначе крутится вечно. Предел настраивается, по умолчанию щедрый.
// ══════════════════════════════════════════════════════════════════════════════

using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace DevDeck.Xml;

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
        => Play(XmlTemplate.Load(templatePath),
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(templatePath))!,
                ct);

    public PlayResult Play(XmlTemplate tpl, string templateDir, CancellationToken ct = default)
    {
        var entry = tpl.EntryStep();
        if (entry is null)
            return new PlayResult(false, "в шаблоне нет ни одного Step", 0, null, null);

        var dead = tpl.UnreachableSteps();
        if (dead.Count > 0)
            _log($"[xml] {dead.Count} узлов недостижимы из точки входа — заготовки на холсте, пропускаю");

        var code = new XmlCodeRunner(
            new XmlCodeGlobals { project = _project, instance = _instance }, templateDir);
        var exec = new BranchExecutor(_project, _instance, code, _log);

        _log($"[xml] {tpl.Name}: старт с узла {entry.Id[..8]}, веток в нём {entry.Branches.Count}");

        var    step  = entry;
        int    index = 0;
        int    run   = 0;

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

            try
            {
                if (_opt.Trace) _log($"[xml] {run,4}. {branch}");

                var result = exec.Execute(branch, ct);
                StoreOutput(branch, result);
                next = branch.OnSuccess;
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
