using System.Net;

namespace z3n8;

public interface IScriptHandler
{
    string PathPrefix { get; }
    void Init();
    Task<bool> HandleRequest(HttpListenerContext context);
}
