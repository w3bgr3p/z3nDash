using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash;

public sealed class CsxZp7Globals
{
    public TaskRunContext current_task => TaskRunContext.Current;
    public StubProject project  { get; init; } = null!;
    public Instance    instance { get; init; } = new Instance();
    public Logger      log      { get; init; } = null!;
}
