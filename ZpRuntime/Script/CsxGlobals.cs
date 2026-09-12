using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash;

public sealed class CsxGlobals
{
    public TaskRunContext current_task => TaskRunContext.Current;
    public StubProject                    project  { get; init; } = null!;
    public z3nDash.Browser.PlaywrightInstance instance { get; init; } = null!;
    public Logger                         log      { get; init; } = null!;
}
