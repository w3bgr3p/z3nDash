using System.Reflection;
using z3nDash.Browser;
using ZennoLab.CommandCenter;

namespace BasBridgeRegression;

/// Пустышка IBrowserInstance: нужен только как различимый объект.
public class NullBrowser : DispatchProxy
{
    public static IBrowserInstance Create() => DispatchProxy.Create<IBrowserInstance, NullBrowser>();
    protected override object? Invoke(MethodInfo? m, object?[]? a) => throw new NotSupportedException(m?.Name);
}

public static class HttpBrowserTests
{
    public static Task Run(ExpectFn expect)
    {
        var global = NullBrowser.Create();
        var a = NullBrowser.Create();
        var b = NullBrowser.Create();
        ZennoPoster.AttachBrowser(global);

        IBrowserInstance? seenA = null, seenB = null, seenAfterA = null;
        var ta = new Thread(() => { ZennoPoster.AttachBrowserForCurrentFlow(a); Thread.Sleep(100); seenA = ZennoPoster.HTTP.Browser; });
        var tb = new Thread(() => { ZennoPoster.AttachBrowserForCurrentFlow(b); Thread.Sleep(100); seenB = ZennoPoster.HTTP.Browser; });
        ta.Start(); tb.Start(); ta.Join(); tb.Join();
        var tc = new Thread(() => seenAfterA = ZennoPoster.HTTP.Browser);
        tc.Start(); tc.Join();

        expect("http browser: поток A видит свой", ReferenceEquals(seenA, a));
        expect("http browser: поток B видит свой", ReferenceEquals(seenB, b));
        expect("http browser: без своего — глобальный", ReferenceEquals(seenAfterA, global));
        expect("http browser: главный поток — глобальный", ReferenceEquals(ZennoPoster.HTTP.Browser, global));
        return Task.CompletedTask;
    }
}
