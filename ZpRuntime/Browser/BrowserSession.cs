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
    private readonly ProxyRelay?      _relay;

    public PlaywrightInstance          Browser  { get; }
    public ZennoLab.CommandCenter.Instance Instance { get; }
    public IPage                       Page     { get; }

    private BrowserSession(IPlaywright? pw, IBrowser? browser, IBrowserContext? ownedContext,
                           IPage page, bool owned, string proxy = "", ProxyRelay? relay = null)
    {
        _pw           = pw;
        _browser      = browser;
        _ownedContext = ownedContext;
        _owned        = owned;
        _relay        = relay;

        Page     = page;
        Browser  = new PlaywrightInstance(page) { Proxy = proxy, Relay = relay };
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

        var pw = await Playwright.CreateAsync();
        RegexSelector.Register(pw);

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
    /// <param name="log">
    /// Куда релей прокси рассказывает про отказы. Без этого отказ авторизации
    /// выглядит снаружи как молчание: браузер отдаёт ERR_EMPTY_RESPONSE, а
    /// причина не доезжает никуда.
    /// </param>
    public static async Task<BrowserSession> LaunchAsync(
        string profileDir, bool headless = false, string? proxy = null, string? channel = "chrome",
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(profileDir))
            throw new ArgumentException(
                "нужен каталог профиля: Patchright работает через persistent context, " +
                "обычный Launch с отдельным контекстом детектируется", nameof(profileDir));

        var pw = await Playwright.CreateAsync();
        RegexSelector.Register(pw);

        // Релей поднимается всегда, даже когда прокси на старте нет.
        //
        // Причина — ZP-шный SetProxy: шаблоны зовут его посреди прогона, когда
        // прокси только что получен от поставщика. Playwright принимает прокси
        // лишь в параметрах запуска, поэтому единственный способ дать SetProxy
        // работать по-настоящему — держать браузер на постоянном локальном
        // адресе и менять то, куда ходит релей. Так же устроен и proxifier ZP.
        //
        // Без этого шаблон, вычисляющий прокси на ходу, отказывал: «браузер
        // поднят без прокси». А шаблон с готовым прокси в переменной работал —
        // разница была не в шаблонах, а в том, успел ли планировщик увидеть
        // прокси до запуска.
        var relay = ProxyRelay.Start();
        if (log is not null) relay.Log = m => log("[proxy] " + m);
        relay.SetUpstream(proxy);

        var proxySettings = new Proxy { Server = relay.Endpoint };

        Directory.CreateDirectory(profileDir);

        var ctx = await pw.Chromium.LaunchPersistentContextAsync(profileDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel      = string.IsNullOrWhiteSpace(channel) ? null : channel,
                Headless     = headless,
                Proxy        = proxySettings,
                // Первый ключ Patchright ставит сам — но только в фикстурах своего
                // тест-раннера, а не в LaunchPersistentContextAsync, которым
                // пользуемся мы. Без него navigator.webdriver === true, а это
                // первое, что смотрит любая проверка на автоматизацию.
                //
                // Второй нужен из-за первого: на --disable-blink-features Chrome
                // показывает плашку «unsupported command-line flag», то есть
                // сам ключ против детекта приносил детект пожирнее — плашка
                // занимает высоту окна и лезет в скриншоты. --test-type её
                // гасит. Замерено по зазору outerHeight-innerHeight:
                //
                //   без Args                       зазор  95, плашки нет, webdriver true
                //   AutomationControlled           зазор 147, плашка ЕСТЬ, webdriver false
                //   AutomationControlled+test-type зазор  95, плашки нет, webdriver false
                //   только test-type               зазор  95, плашки нет, webdriver true
                //
                // Сверка отпечатка со страницы (UA, языки, плагины, mime, chrome,
                // ядра, память, экран, глубина, WebGL) с --test-type и без него
                // различий не дала.
                Args = ["--disable-blink-features=AutomationControlled", "--test-type"],
                // Песочница включена намеренно. Playwright по умолчанию её
                // выключает — «if (options.chromiumSandbox !== true) push("--no-sandbox")» —
                // а Chrome на этот ключ показывает плашку «unsupported
                // command-line flag». Плашка занимает высоту окна: сдвигает
                // вьюпорт и попадает в скриншоты, по которым считаются
                // координаты. Заодно настоящий браузер у человека всегда
                // запущен с песочницей, и её отсутствие — лишнее отличие.
                ChromiumSandbox = true,
                // Свой UserAgent и принудительный размер окна — те самые признаки,
                // которые Patchright и убирает. Не задаём ни того, ни другого.
                ViewportSize = ViewportSize.NoViewport,
            });

        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        // Proxy держим исходный, с авторизацией: шаблон сверяет то, что просил,
        // а не адрес нашей петли.
        return new BrowserSession(pw, null, ctx, page, owned: true, proxy: proxy ?? "", relay: relay);
    }

    /// <summary>
    /// Привести ZP-шную строку прокси к виду, который понимает Playwright.
    /// В шаблонах она чаще всего записана как host:port:user:pass — так лежит и
    /// в переменной proxy у simroute_test. Схема по умолчанию socks5: именно её
    /// подставляют ветки, разбирая ту же строку.
    /// </summary>
    public static string NormalizeProxy(string? raw, string defaultScheme = "socks5")
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var v = raw.Trim();
        if (v.Contains("://")) return v;          // уже готовая строка
        if (v.Contains('@'))   return $"{defaultScheme}://{v}";

        var p = v.Split(':', 4);
        return p.Length switch
        {
            >= 4 => $"{defaultScheme}://{p[2]}:{p[3]}@{p[0]}:{p[1]}",
            2    => $"{defaultScheme}://{p[0]}:{p[1]}",
            _    => "",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned)
        {
            if (_ownedContext is not null) { try { await _ownedContext.CloseAsync(); } catch { } }
            if (_browser      is not null) { try { await _browser.CloseAsync();      } catch { } }
        }

        _relay?.Dispose();
        _pw?.Dispose();
    }
}
