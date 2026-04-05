using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nIO;

public sealed class CsxGlobals
{
    public StubProject                    project  { get; init; } = null!;
    public z3nIO.Browser.PlaywrightInstance instance { get; init; } = null!;
    public Logger                         log      { get; init; } = null!;
}