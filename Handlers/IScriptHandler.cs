using System.Net;

namespace DevDeck;

public interface IScriptHandler
{
    string PathPrefix { get; }
    void Init();
    Task<bool> HandleRequest(HttpListenerContext context);
}
