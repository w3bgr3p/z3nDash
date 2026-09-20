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
        PlaySession session;
        try
        {
            session = new PlaySession(tpl, templateDir, _project, _instance, _log, _opt);
        }
        catch (PlaySetupException ex)
        {
            _log($"[xml] окружение шаблона не собрано: {ex.Message}");
            return new PlayResult(false, ex.Message, 0, null, ex.InnerException);
        }

        // Имя файла, а не атрибут Name из XML: там у половины шаблонов лежит
        // «Template.xml» от мастера создания, и по такой строке в логе не понять,
        // какой из них прогоняли.
        var title = string.IsNullOrWhiteSpace(_project.Name) ? tpl.Name : _project.Name;
        _log($"[xml] {title}: старт с {tpl.Start}");

        var result = RunToEnd(session, ct);

        // Обработчики конца — тоже обычные цепочки веток, просто вход в них
        // задан не стрелкой, а узлом холста.
        var ending = result.Success ? tpl.GoodEnd : tpl.BadEnd;
        if (!ending.IsNone && session.JumpTo(ending))
        {
            _log($"[xml] {(result.Success ? "GoodEnd" : "BadEnd")} → {ending}");
            var tail = RunToEnd(session, ct);
            if (!tail.Success)
                _log($"[xml] обработчик конца сам упал: {tail.Message}");
        }

        return result;
    }

    /// <summary>Крутить шаги, пока маршрут не кончится или не упрётся.</summary>
    private static PlayResult RunToEnd(PlaySession session, CancellationToken ct)
    {
        while (true)
        {
            var step = session.StepOnce(ct);
            if (step.Outcome == StepOutcome.Moved) continue;

            return step.Outcome == StepOutcome.Finished
                ? PlayResult.Ok(session.BranchesRun)
                : new PlayResult(false, step.Message, session.BranchesRun, step.FailedAt, step.Exception);
        }
    }
}
