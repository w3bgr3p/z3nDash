using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n8;

public sealed class CsxGlobals
{
    public StubProject                    project  { get; init; } = null!;
    public z3n8.Browser.PlaywrightInstance instance { get; init; } = null!;
    public Logger                         log      { get; init; } = null!;
}