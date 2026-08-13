// ══════════════════════════════════════════════════════════════════════════════
// BrowserSession.cs — получение живого браузера и обёртка его в ZP-шный Instance.
//
// До сих пор браузер в проекте поднимался ровно в одном месте — в csx-пути
// планировщика, и только через ZennoBrowser по zb_id. Из-за этого XML-плеер и
// путь csx-zp7 работали с безбраузерным Instance: любое обращение к ActiveTab
// заканчивалось отказом.
//
// Здесь два способа получить страницу и один результат — Instance, поверх
// которого работает весь перенесённый из z3n7 слой:
//
//   Attach — подключиться по CDP к уже поднятому браузеру. Это рабочий путь:
//            профиль ZennoBrowser со своим отпечатком, прокси и куками.
//   Launch — поднять локальный Chromium самим. Путь для разработки: отпечаток
//            обычный, антидетекта нет. Гнать через него реальные аккаунты —
//            способ их сжечь, о чём сессия предупреждает в логе.
// ══════════════════════════════════════════════════════════════════════════════

using Microsoft.Playwright;

namespace DevDeck.Browser;

/// <summary>
/// Живой браузер плюс готовый ZP-шный Instance. Владеет всем, что подняла сама,
/// и не трогает то, к чему только подключилась: закрывать чужой браузер по
/// выходу из блока using — верный способ уронить соседнюю задачу.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IPlaywright?     _pw;
    private readonly IBrowser?        _browser;
    private readonly IBrowserContext? _ownedContext;
    private readonly bool             _owned;

    public PlaywrightInstance          Browser  { get; }
    public ZennoLab.CommandCenter.Instance Instance { get; }
    public IPage                       Page     { get; }

    private BrowserSession(IPlaywright? pw, IBrowser? browser, IBrowserContext? ownedContext,
                           IPage page, bool owned)
    {
        _pw           = pw;
        _browser      = browser;
        _ownedContext = ownedContext;
        _owned        = owned;

        Page     = page;
        Browser  = new PlaywrightInstance(page);
        Instance = new ZennoLab.CommandCenter.Instance(Browser);

        // Чтобы ZennoPoster.HTTP.Request умел уйти с сессией браузера — та самая
        // ветка, которая включается переданным CookieContainer профиля.
        ZennoLab.CommandCenter.ZennoPoster.AttachBrowser(Browser);
    }

    /// <summary>
    /// Подключиться к уже поднятому браузеру по CDP-эндпоинту. Так работает
    /// ZennoBrowser: сначала ZB.RunProfile(zbId) отдаёт connectionString.
    /// </summary>
    public static async Task<BrowserSession> AttachAsync(string wsEndpoint)
    {
        if (string.IsNullOrWhiteSpace(wsEndpoint))
            throw new ArgumentException("пустой CDP-эндпоинт", nameof(wsEndpoint));

        var pw      = await Playwright.CreateAsync();
        var browser = await pw.Chromium.ConnectOverCDPAsync(wsEndpoint);

        var context = browser.Contexts.FirstOrDefault()
                      ?? await browser.NewContextAsync();
        var page    = context.Pages.FirstOrDefault()
                      ?? await context.NewPageAsync();

        return new BrowserSession(pw, browser, null, page, owned: false);
    }

    /// <summary>
    /// Поднять локальный Chromium. Отпечаток обычный — для разработки и разбора
    /// шаблонов, не для боевых аккаунтов.
    /// </summary>
    public static async Task<BrowserSession> LaunchAsync(
        bool headless = false, string? profileDir = null, string? proxy = null)
    {
        var pw = await Playwright.CreateAsync();

        var proxySettings = string.IsNullOrWhiteSpace(proxy)
            ? null
            : new Proxy { Server = proxy.Contains("://") ? proxy : $"http://{proxy}" };

        // С profileDir берём persistent context: у него свои куки и localStorage,
        // как у профиля ZP. Без него — обычный одноразовый браузер.
        if (!string.IsNullOrWhiteSpace(profileDir))
        {
            Directory.CreateDirectory(profileDir);
            var ctx = await pw.Chromium.LaunchPersistentContextAsync(profileDir,
                new BrowserTypeLaunchPersistentContextOptions { Headless = headless, Proxy = proxySettings });
            var p = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
            return new BrowserSession(pw, null, ctx, p, owned: true);
        }

        var browser = await pw.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = headless, Proxy = proxySettings });
        var context = await browser.NewContextAsync();
        var page    = await context.NewPageAsync();

        return new BrowserSession(pw, browser, context, page, owned: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned)
        {
            if (_ownedContext is not null) { try { await _ownedContext.CloseAsync(); } catch { } }
            if (_browser      is not null) { try { await _browser.CloseAsync();      } catch { } }
        }

        _pw?.Dispose();
    }
}
