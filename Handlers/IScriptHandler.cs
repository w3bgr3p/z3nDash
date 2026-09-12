using System.Net;

namespace z3nDash;

public interface IScriptHandler
{
    string PathPrefix { get; }
    void Init();
    Task<bool> HandleRequest(HttpListenerContext context);
}
