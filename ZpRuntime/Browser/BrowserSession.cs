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
//   Attach — подключиться по CDP к уже поднятому браузеру: профиль
//            ZennoBrowser со своим отпечатком, прокси и куками.
//   Launch — поднять браузер самим, через Patchright.
//
// Patchright — пропатченный Playwright под тем же namespace: те же типы, тот же
// API, но драйвер без следов, по которым узнают автоматизацию. Из-за этого у
// запуска есть требования, и они не косметические — нарушив их, теряешь ровно то,
// ради чего он взят:
//
//   Channel = "chrome"        настоящий Chrome, а не Chromium из поставки;
//   LaunchPersistentContext   обычный Launch + NewContext детектируется;
//   ViewportSize.NoViewport   окно как у человека, без принудительного размера;
//   без своих UserAgent и заголовков — они и выдают;
//   Headless = false          headless виден по десятку признаков.
//
// Поэтому профиль здесь не опция, а условие: без каталога профиля persistent
// context не поднять. Headless оставлен, но с предупреждением — он полезен на
// разборе шаблона и вреден на живых аккаунтах.
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
    /// Поднять браузер через Patchright. Каталог профиля обязателен: без него не
    /// собрать persistent context, а обычный Launch + NewContext детектируется.
    /// </summary>
    /// <param name="profileDir">Каталог профиля — куки, localStorage, отпечаток.</param>
    /// <param name="headless">Только для разбора шаблонов: headless виден.</param>
    /// <param name="proxy">Прокси в виде host:port или схема://user:pass@host:port.</param>
    /// <param name="channel">
    /// Канал Chrome. Patchright просит настоящий "chrome"; пустая строка оставит
    /// Chromium из поставки — тогда часть патчей теряет смысл.
    /// </param>
    public static async Task<BrowserSession> LaunchAsync(
        string profileDir, bool headless = false, string? proxy = null, string? channel = "chrome")
    {
        if (string.IsNullOrWhiteSpace(profileDir))
            throw new ArgumentException(
                "нужен каталог профиля: Patchright работает через persistent context, " +
                "обычный Launch с отдельным контекстом детектируется", nameof(profileDir));

        var pw = await Playwright.CreateAsync();

        var proxySettings = string.IsNullOrWhiteSpace(proxy)
            ? null
            : new Proxy { Server = proxy.Contains("://") ? proxy : $"http://{proxy}" };

        Directory.CreateDirectory(profileDir);

        var ctx = await pw.Chromium.LaunchPersistentContextAsync(profileDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel      = string.IsNullOrWhiteSpace(channel) ? null : channel,
                Headless     = headless,
                Proxy        = proxySettings,
                // Свой UserAgent и принудительный размер окна — те самые признаки,
                // которые Patchright и убирает. Не задаём ни того, ни другого.
                ViewportSize = ViewportSize.NoViewport,
            });

        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        return new BrowserSession(pw, null, ctx, page, owned: true);
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
