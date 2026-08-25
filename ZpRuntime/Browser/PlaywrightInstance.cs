using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DevDeck.Browser;

namespace DevDeck.Browser
{
    public sealed partial class PlaywrightInstance : IBrowserInstance
    {
        private readonly IBrowserContext _context;
        private IPage _activePage;

        public IBrowserContext Context => _context;

        public PlaywrightInstance(IPage page)
        {
            _context    = page.Context;
            _activePage = page;

            // Новые вкладки тоже должны попадать под сбор, если он включён.
            _context.Page += (_, p) => { if (_useTraffic) TrafficCapture.Enable(p); };
        }

        /// <summary>
        /// CDP-сессия на страницу, одна и та же. Заводить новую на каждый вызов
        /// нельзя: Emulation.setTimezoneOverride привязан к сессии, и второй
        /// вызов с новой сессией падает с «Timezone override is already in
        /// effect» — то есть SetTimezone работал ровно один раз за жизнь
        /// страницы, а SetTimeFromDb зовут на каждом запуске.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, ICDPSession> _cdp = new();

        private ICDPSession Cdp()
        {
            if (_cdp.TryGetValue(_activePage, out var existing)) return existing;
            var session = Sync(_context.NewCDPSessionAsync(_activePage));
            _cdp.Add(_activePage, session);
            return session;
        }

        private bool _useTraffic;

        public bool UseTrafficMonitoring
        {
            get => _useTraffic;
            set
            {
                _useTraffic = value;
                if (!value) return;
                foreach (var p in _context.Pages) TrafficCapture.Enable(p);
            }
        }

        public IBrowserTab ActiveTab => new PlaywrightTab(_activePage);
        public IList<IBrowserTab> AllTabs
            => _context.Pages.Select(p => (IBrowserTab)new PlaywrightTab(p)).ToList();

        /// <summary>Проставляется тем, кто поднял браузер, — см. BrowserSession.</summary>
        public string Proxy { get; internal set; } = "";

        /// <summary>
        /// Релей, через который ходит браузер. Есть всегда у поднятого нами
        /// инстанса; у подключённого по CDP его нет — там прокси чужой.
        /// </summary>
        internal ProxyRelay? Relay { get; set; }

        public void SetProxy(string proxy)
        {
            if (Relay is null)
                throw new NotSupportedException(
                    "Instance.SetProxy: браузер подняли не мы (подключение по CDP), " +
                    "прокси у него свой и меняется на его стороне.");

            Relay.SetUpstream(proxy);
            Proxy = proxy ?? "";
        }

        public bool   UseFullMouseEmulation { get; set; } = false;
        public string EmulationLevel => UseFullMouseEmulation ? "superEmulation" : "none";

        public IBrowserTab NewTab(string _ = "new")
        {
            var page    = Sync(_context.NewPageAsync());
            _activePage = page;
            return new PlaywrightTab(page);
        }

        public void SetActivePage(IPage page) => _activePage = page;

        /// <summary>
        /// Закрыть лишние вкладки, оставив первую.
        ///
        /// Раньше активная вкладка не переставлялась: если активной была одна из
        /// закрытых, инстанс продолжал на неё смотреть. URL при этом ещё
        /// возвращался — Playwright держит его на своей стороне, — а любое
        /// действие падало с «Target page, context or browser has been closed»,
        /// причём уже далеко от места, где вкладку закрыли.
        /// </summary>
        public void CloseAllTabs()
        {
            var pages = _context.Pages.ToList();
            foreach (var p in pages.Skip(1))
                Sync(p.CloseAsync());

            if (pages.Count > 0) _activePage = pages[0];
        }

        /// <summary>
        /// ZP чистит кеш; у нас это делалось через ClearCookies, то есть метод
        /// назывался «очистить кеш», а сносил cookie — и делал это независимо от
        /// domain, который молча игнорировался. Кеш чистится через CDP.
        /// </summary>
        public void ClearCache(string domain = null)
        {
            if (!string.IsNullOrEmpty(domain))
                throw new NotSupportedException(
                    "ClearCache: очистка кеша по домену не поддерживается — " +
                    "CDP чистит его целиком. Для cookie конкретного домена есть ClearCookie.");

            Sync(Cdp().SendAsync("Network.clearBrowserCache"));
        }

        public void ClearCookie(string domain = null)
        {
            if (domain == null)
                Sync(_context.ClearCookiesAsync());
            else
                Sync(_context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Domain = domain }));
        }

        /// <summary>
        /// Выгрузка cookie в том же виде, в каком их ждёт перенесённый код: JSON-массив
        /// с domain, path, сроком и флагами.
        ///
        /// Раньше сюда писалась строка "name=value; name=value". Она не падала, но
        /// не читалась ничем: Rqst.GetCookiesForRequest разбирает переменную cookies
        /// как JArray и берёт cookie["domain"], а Cookies.ConvertCookieFormat на такой
        /// строке бросает «Unknown input format». То есть SaveCookies отрабатывал
        /// «успешно», а сессия терялась — вместе с доменом, сроком и httpOnly.
        /// </summary>
        public void SaveCookie(string path)
        {
            var cookies = Sync(_context.CookiesAsync());

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                cookies.Select(c => new
                {
                    domain         = c.Domain,
                    expirationDate = c.Expires < 0 ? (double?)null : c.Expires,
                    hostOnly       = !c.Domain.StartsWith("."),
                    httpOnly       = c.HttpOnly,
                    name           = c.Name,
                    path           = c.Path,
                    sameSite       = c.SameSite.ToString(),
                    secure         = c.Secure,
                    session        = c.Expires < 0,
                    value          = c.Value,
                }),
                Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Обратная операция: восстановить cookie из того же JSON. В ZP это
        /// Instance.SetCookie, и им пользуются шаблоны, поднимающие сохранённую
        /// сессию. Принимается и строка "name=value; …" — с ней домен берётся из
        /// текущей вкладки, потому что в самой строке его нет.
        /// </summary>
        public void SetCookie(string cookies)
        {
            if (string.IsNullOrWhiteSpace(cookies)) return;

            var list = new List<Cookie>();
            var text = cookies.Trim();

            if (text.StartsWith("["))
            {
                foreach (var c in Newtonsoft.Json.Linq.JArray.Parse(text))
                {
                    var name = c["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    list.Add(new Cookie
                    {
                        Name     = name,
                        Value    = c["value"]?.ToString() ?? "",
                        Domain   = c["domain"]?.ToString(),
                        Path     = c["path"]?.ToString() ?? "/",
                        Expires  = c["expirationDate"] is { } exp && exp.Type != Newtonsoft.Json.Linq.JTokenType.Null
                                       ? (float)exp.ToObject<double>() : -1,
                        HttpOnly = c["httpOnly"]?.ToObject<bool>(),
                        Secure   = c["secure"]?.ToObject<bool>(),
                    });
                }
            }
            else if (text.Contains('\t'))
            {
                // Netscape: domain, includeSubdomains, path, secure, expires,
                // name, value — через табуляцию. Этот разбор лежал отдельно в
                // CanvasExtensions.cs; держать половину формата в стороне от
                // выгрузки — способ снова их развести.
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7) continue;
                    list.Add(new Cookie
                    {
                        Domain   = f[0],
                        Path     = f[2],
                        Secure   = f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                        Expires  = ParseNetscapeExpiry(f[4]),
                        Name     = f[5],
                        Value    = f[6],
                        HttpOnly = f.Length > 7
                                   && f[7].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            else
            {
                var host = new Uri(_activePage.Url).Host;
                foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length < 2) continue;
                    list.Add(new Cookie
                    {
                        Name   = kv[0].Trim(),
                        Value  = kv[1].Trim(),
                        Domain = host,
                        Path   = "/",
                    });
                }
            }

            if (list.Count > 0) Sync(_context.AddCookiesAsync(list));
        }

        /// <summary>
        /// Чтение cookie строкой — то, чем в ZP пользуется Cookies.GetCookies и
        /// вся выгрузка сессии в базу. Раньше метод бросал NotSupportedException,
        /// и перенос Browser/Cookies.cs был об это заблокирован.
        ///
        /// Формат по умолчанию — Netscape (cookies.txt): именно его ждёт
        /// NetscapeToJson и разбирает наш же SetCookie. isCookieFormat=true даёт
        /// заголовочный вид "name=value; name=value" без домена и срока.
        /// </summary>
        public string GetCookie(string domain = null, bool isCookieFormat = false)
        {
            var cookies = Sync(_context.CookiesAsync());

            if (!string.IsNullOrEmpty(domain))
            {
                var target = domain.TrimStart('.');
                cookies = cookies.Where(c =>
                {
                    var d = (c.Domain ?? "").TrimStart('.');
                    return d.Equals(target, StringComparison.OrdinalIgnoreCase)
                        || d.EndsWith("." + target, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            if (isCookieFormat)
                return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            // domain, includeSubdomains, path, secure, expires, name, value,
            // httpOnly, FALSE — ровно те девять колонок, которые пишет
            // Cookies.JsonToNetscape эталона и читает его же NetscapeToJson.
            var lines = cookies.Select(c => string.Join("\t",
                c.Domain,
                (c.Domain ?? "").StartsWith(".") ? "TRUE" : "FALSE",
                string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                c.Secure == true ? "TRUE" : "FALSE",
                FormatNetscapeExpiry(c.Expires),
                c.Name,
                c.Value,
                c.HttpOnly == true ? "TRUE" : "FALSE",
                "FALSE"));

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Срок в колонке expires — не unix-секунды, а "MM/dd/yyyy HH:mm:ss": так
        /// его пишет ZennoPoster, и только этот вид разбирает Cookies.NetscapeToJson
        /// эталона (TryParseExact с этой маской). Unix-секунды он не бросал, а молча
        /// клал expirationDate = null — cookie превращалась в сессионную.
        ///
        /// Время местное, не UTC: NetscapeToJson заворачивает разобранное в
        /// new DateTimeOffset(expiry), то есть трактует его как локальное. При
        /// выгрузке в UTC срок уезжал ровно на смещение пояса — проверено, +5 часов.
        ///
        /// Пустая строка означает сессионную cookie.
        /// </summary>
        private static string FormatNetscapeExpiry(float expires)
            => expires < 0
                   ? ""
                   : DateTimeOffset.FromUnixTimeSeconds((long)expires).LocalDateTime
                       .ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>Обратный разбор той же колонки, в тех же местных сутках.
        /// Unix-секунды тоже принимаются: их пишут выгрузки сторонних
        /// расширений.</summary>
        private static float ParseNetscapeExpiry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return -1;

            if (DateTime.TryParseExact(raw, "MM/dd/yyyy HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return (float)new DateTimeOffset(dt).ToUnixTimeSeconds();

            return double.TryParse(raw, NumberStyles.Any,
                       CultureInfo.InvariantCulture, out var unix) && unix > 0
                       ? (float)unix : -1;
        }

        public void WaitFieldEmulationDelay() => Thread.Sleep(new Random().Next(1337, 2077));

        /// <summary>
        /// Chromium ставит расширения только флагом при запуске, на живом браузере
        /// их не добавить. Раньше метод был пустым телом с комментарием «уже стоит
        /// в профиле ZB» — то есть шаблон, ставящий расширение сам, считал что
        /// поставил, и падал потом в непонятном месте.
        /// </summary>
        public void InstallCrxExtension(string path) => throw new NotSupportedException(
            "InstallCrxExtension: расширение подключается при запуске браузера " +
            $"(--load-extension), на живом инстансе нельзя. Путь: [{path}]");

        /// <summary>
        /// Часовой пояс страницы через CDP. Раньше оба метода были пустыми телами:
        /// перенесённый SetTimeFromDb отрабатывал «успешно» и не делал ничего, а
        /// расхождение пояса с прокси — первое, на что смотрит антифрод.
        ///
        /// Playwright задаёт TimezoneId только при создании контекста, но
        /// Emulation.setTimezoneOverride работает и на живой странице.
        /// </summary>
        public void SetTimezone(int offsetMinutes, int unused)
        {
            // CDP принимает только имя зоны, не смещение. Берём любую зону с
            // нужным смещением — для JS-кода страницы важно именно оно.
            var zone = TimeZoneInfo.GetSystemTimeZones()
                .FirstOrDefault(z => (int)z.BaseUtcOffset.TotalMinutes == offsetMinutes);

            if (zone is null)
                throw new NotSupportedException(
                    $"SetTimezone: не нашлось зоны со смещением {offsetMinutes} минут — " +
                    "передайте имя зоны через SetIanaTimezone");

            SetIanaTimezone(zone.HasIanaId ? zone.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id);
        }

        public void SetIanaTimezone(string ianaName)
        {
            if (string.IsNullOrWhiteSpace(ianaName)) return;

            var cdp = Cdp();

            // Пустая строка снимает прежнюю подмену. Без этого повторный вызов
            // отвергается: CDP считает, что подмена уже действует.
            Sync(cdp.SendAsync("Emulation.setTimezoneOverride",
                new Dictionary<string, object> { ["timezoneId"] = "" }));
            Sync(cdp.SendAsync("Emulation.setTimezoneOverride",
                new Dictionary<string, object> { ["timezoneId"] = ianaName }));
        }

        /// <summary>
        /// Размер окна браузера. Шаблоны ставят его первой же веткой, чтобы у
        /// всех запусков была одинаковая геометрия — от неё зависят координаты
        /// кликов и то, какая вёрстка достанется от сайта.
        ///
        /// Меняется именно окно, а не вьюпорт: мы поднимаемся с NoViewport,
        /// страница следует за окном, и подмена вьюпорта отдельно от окна как
        /// раз и есть один из признаков автоматизации. Поэтому CDP
        /// Browser.setWindowBounds, а не Page.setDeviceMetricsOverride.
        /// </summary>
        public void SetWindowSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            var cdp = Cdp();
            var win = Sync(cdp.SendAsync("Browser.getWindowForTarget"));
            if (win is not { } node || !node.TryGetProperty("windowId", out var idNode))
                throw new InvalidOperationException("SetWindowSize: CDP не отдал windowId");
            var id = idNode.GetInt32();

            // Развёрнутое или свёрнутое окно размеров не принимает — сначала
            // возвращаем его в обычное состояние.
            Sync(cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = id,
                ["bounds"]   = new Dictionary<string, object> { ["windowState"] = "normal" },
            }));

            Sync(cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = id,
                ["bounds"]   = new Dictionary<string, object> { ["width"] = width, ["height"] = height },
            }));
        }

        public IHeElement FindElementById(string id)
            => new PlaywrightElement(_activePage.Locator($"#{id}"));

        public IHeElement FindElementByName(string name)
            => new PlaywrightElement(_activePage.Locator($"[name='{name}']"));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new PlaywrightElement(BuildLocator(_activePage, tag, attr, pattern, mode).Nth(index));

        public IList<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
        {
            var loc   = BuildLocator(_activePage, tag, attr, pattern, mode);
            int count = Sync(loc.CountAsync());
            return Enumerable.Range(0, count)
                .Select(i => (IHeElement)new PlaywrightElement(loc.Nth(i)))
                .ToList();
        }

        /// <summary>
        /// Через IBrowserContext.APIRequest — он делит хранилище cookie с браузером,
        /// поэтому вручную переносить сессию не нужно.
        /// </summary>
        public IBrowserHttpResponse SendFromBrowser(string method, string url, string body,
            string contentType, IDictionary<string, string> headers, int timeoutSec)
        {
            var opts = new APIRequestContextOptions
            {
                Method  = method,
                Timeout = (timeoutSec <= 0 ? 30 : timeoutSec) * 1000,
            };

            if (!string.IsNullOrEmpty(body))
            {
                opts.Data = body;
                var hdrs = headers == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(headers);
                if (!hdrs.Keys.Any(k => k.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                    hdrs["Content-Type"] = string.IsNullOrEmpty(contentType)
                        ? "application/x-www-form-urlencoded"
                        : contentType;
                opts.Headers = hdrs;
            }
            else if (headers != null)
            {
                opts.Headers = headers;
            }

            var resp = Sync(_context.APIRequest.FetchAsync(url, opts));
            return new PlaywrightHttpResponse(
                resp.Status,
                resp.StatusText,
                Sync(resp.TextAsync()),
                resp.Headers);
        }

        /// <summary>
        /// Нажать чекбокс Cloudflare, если он есть. Возвращает, удалось ли:
        /// раньше метод был void и по истечении срока просто выходил, так что
        /// вызывающий не отличал решённую капчу от несделанной и шёл дальше на
        /// странице, которая его не пустила.
        /// </summary>
        public bool CFSolve(int timeoutSeconds = 30)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < deadline)
            {
                var cfFrame = _activePage.Frames
                    .FirstOrDefault(f => f.Url.Contains("challenges.cloudflare.com"));
                if (cfFrame != null)
                {
                    try
                    {
                        var cb = cfFrame.Locator("input[type='checkbox']");
                        if (Sync(cb.CountAsync()) > 0) { Sync(cb.ClickAsync()); Thread.Sleep(3000); return true; }
                    }
                    catch { }
                }
                try
                {
                    var verify = _activePage.Locator("text=Verify you are human");
                    if (Sync(verify.CountAsync()) > 0) { Sync(verify.ClickAsync()); Thread.Sleep(3000); return true; }
                }
                catch { }
                Thread.Sleep(1000);
            }

            return false;
        }

        /// <summary>
        /// Разбирает ZP-шный Finder в Playwright-локатор.
        /// searchKind в ZP принимает "text", "notext" и "regexp"; теги, если их
        /// несколько, перечисляются через ';'.
        /// </summary>
        internal static ILocator BuildLocator(IPage page, string tag, string attr, string pattern, string mode)
        {
            bool negate = mode == "notext";
            bool regexp = mode == "regexp";

            // Поиск по самому тегу, а не по атрибуту: в XML это
            // AttrName="fulltag", а в значении лежит тег вида "input:checkbox".
            //
            // Здесь стояло только "fulltagname" — имя, которого в шаблонах нет
            // вовсе (в них 2 вхождения "fulltag" и ни одного "fulltagname").
            // То есть особый случай не срабатывал никогда, и поиск уходил
            // искать атрибут с именем fulltag, которого ни у кого нет.
            if (attr is "fulltag" or "fulltagname")
            {
                // Значение первично: в нём и лежит искомый тег. Tag дублирует
                // его, но пустым тоже встречается.
                var spec = string.IsNullOrWhiteSpace(pattern) ? tag : pattern;
                if (!regexp) return page.Locator(CssTag(spec));

                // Регулярка по тегу — через свой движок: он умеет считать
                // "полный тег" сам, см. RegexSelector.
                return page.Locator(RegexSelector.Build(tag, "fulltag", pattern, negate, regexp: true));
            }

            if (attr is "innertext" or "text")
            {
                var byText = page.Locator(CssTags(tag));
                if (regexp)
                {
                    var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                    return byText.Filter(negate
                        ? new LocatorFilterOptions { HasNotTextRegex = rx }
                        : new LocatorFilterOptions { HasTextRegex    = rx });
                }
                return byText.Filter(negate
                    ? new LocatorFilterOptions { HasNotTextString = pattern }
                    : new LocatorFilterOptions { HasTextString    = pattern });
            }

            // Живые значения: в CSS их не выразить ни точным совпадением, ни
            // регуляркой. outerhtml вообще не атрибут, а value после ввода
            // расходится с атрибутом — в разметке остаётся начальное.
            if (attr is "innerhtml" or "outerhtml" or "value")
                return page.Locator(RegexSelector.Build(tag, attr, pattern, negate, regexp));

            if (regexp)
            {
                // Регулярка выполняется в самой странице — см. RegexSelector.cs.
                // Раньше здесь из шаблона брался самый длинный литеральный кусок
                // и подставлялся в XPath contains(): "^btn-(a|b)$" находил и
                // btn-c, и xbtn-a. Ветка при этом не падала — просто кликала не
                // туда, потому что вместе с лишними совпадениями сдвигался
                // Number, по которому выбирается нужный элемент.
                return page.Locator(RegexSelector.Build(tag, attr, pattern, negate, regexp: true));
            }

            // Точное совпадение оставляем на CSS: в отличие от XPath его движок
            // пробивает открытый shadow DOM, и терять это поведение нельзя.
            string escaped = pattern.Replace("\\", "\\\\").Replace("'", "\\'");
            string clause  = negate ? $":not([{attr}='{escaped}'])" : $"[{attr}='{escaped}']";
            var    tags    = SplitTags(tag);
            if (tags.Length == 0) tags = new[] { "*" };
            return page.Locator(string.Join(", ", tags.Select(t => CssTag(t) + clause)));
        }

        /// <summary>Список тегов ZP ("a;div") → CSS-селектор ("a, div"). Пустой тег → "*".</summary>
        private static string CssTags(string tag)
        {
            var tags = SplitTags(tag);
            return tags.Length == 0 ? "*" : string.Join(", ", tags.Select(CssTag));
        }

        /// <summary>Список тегов ZP + условие → XPath-объединение ("//a[c]|//div[c]").</summary>
        private static string XPathTags(string tag, string condition)
        {
            var tags = SplitTags(tag);
            if (tags.Length == 0) tags = new[] { "*" };
            return "xpath=" + string.Join("|", tags.Select(t => $"//{XPathTag(t)}[{condition}]"));
        }

        private static string[] SplitTags(string tag)
            => (tag ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// ZP пишет тип поля через двоеточие: "input:text", "input:password".
        /// В CSS это не тег: Playwright понимает ":text" как свою
        /// псевдокласс-функцию, и разбор селектора на "input:text" падает.
        ///
        /// Перевод стоял ровно в одной ветке поиска — по fulltagname. Во всех
        /// остальных (точное совпадение, регулярка, поиск по тексту) тег уходил
        /// в CSS как есть: селектор не находил ничего, цикл ожидания это глотал
        /// и отчитывался «not found in Ns» про элемент, который всё это время
        /// был на экране.
        /// </summary>
        internal static string CssTag(string tag)
        {
            var t = (tag ?? "").Trim();
            var i = t.IndexOf(':');
            if (i <= 0) return t;

            var name = t[..i];
            var type = t[(i + 1)..];

            // Тип сравнивается без учёта регистра: в разметке встречается TEXT.
            //
            // Для text отдельный случай: поле без атрибута type — текстовое по
            // умолчанию, и ZP его находит. Селектор [type='text'] такое поле
            // пропускает, потому что атрибута нет вовсе. На React-формах его
            // не пишут сплошь и рядом — так «not found in 30s» получала форма,
            // которая была на экране.
            return type.Equals("text", StringComparison.OrdinalIgnoreCase)
                ? $"{name}:is([type='text' i], :not([type]))"
                : $"{name}[type='{type}' i]";
        }

        /// <summary>То же для XPath: "input:text" → "input[@type='text']".</summary>
        private static string XPathTag(string tag)
        {
            var t = (tag ?? "").Trim();
            var i = t.IndexOf(':');
            if (i <= 0) return t;

            var name = t[..i];
            var type = t[(i + 1)..];

            return type.Equals("text", StringComparison.OrdinalIgnoreCase)
                ? $"{name}[@type='text' or not(@type)]"
                : $"{name}[@type='{type}']";
        }

        private static T    Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();
        private static void Sync(Task t)        => t.GetAwaiter().GetResult();
    }

    public sealed class PlaywrightTab : IBrowserTab
    {
        private readonly IPage _page;
        public PlaywrightTab(IPage page) => _page = page;

        public string    URL          => _page.Url;

        /// <summary>
        /// Страница ещё грузится. Раньше здесь стояло «всегда false», и из-за
        /// этого перенесённые Go и F5 не ждали загрузку вовсе: у них написано
        /// «if (ActiveTab.IsBusy) ActiveTab.WaitDownloading()», и условие никогда
        /// не выполнялось. То же в Canvas.cs перед каждым скриншотом.
        ///
        /// readyState — то же, на что смотрит ZP: complete значит загрузка
        /// закончена. Обращение к странице, которая как раз меняет документ,
        /// бросает — в этот момент она точно занята.
        /// </summary>
        public bool IsBusy
        {
            get
            {
                try
                {
                    return Sync(_page.EvaluateAsync<string>("() => document.readyState")) != "complete";
                }
                catch { return true; }
            }
        }
        public IDocument MainDocument => new PlaywrightDocument(_page);
        public ITouch    Touch        => new PlaywrightTouch(_page);

        public string Domain
        {
            get { try { return new Uri(_page.Url).Host; } catch { return ""; } }
        }

        /// <summary>Домен второго уровня: "a.b.example.com" → "example.com".</summary>
        public string MainDomain
        {
            get
            {
                var parts = Domain.Split('.');
                return parts.Length < 2 ? Domain : string.Join(".", parts[^2..]);
            }
        }

        /// <summary>
        /// В ZP это HWND окна вкладки, нужный для Emulator.SendKey. У CDP-страницы
        /// окна нет, поэтому отдаём стабильный суррогат — он годится как ключ, но
        /// не как настоящий хендл. Реальные нажатия идут через Keyboard.
        /// </summary>
        public int Handle => _page.GetHashCode();

        // System.Drawing.Point — у Microsoft.Playwright есть одноимённый тип.
        /// <summary>
        /// Позиция курсора общая с MouseEmulation: клики по элементам едут
        /// оттуда же. Держи её тут отдельно — путь начинался бы от точки, где
        /// курсор давно не стоит, и траектория выходила бы фальшивой.
        /// </summary>
        public System.Drawing.Point FullEmulationMouseCurrentPosition
        {
            get => MouseEmulation.Position(_page);
            set
            {
                Sync(_page.Mouse.MoveAsync(value.X, value.Y));
                MouseEmulation.Remember(_page, value.X, value.Y);
            }
        }

        /// <summary>
        /// Переход. Возвращается, как только новый документ принят браузером
        /// (Commit), а не когда доехало всё до последнего пикселя.
        ///
        /// Так устроен ZP, и на это прямо рассчитан эталонный Go:
        /// «Navigate(...); if (ActiveTab.IsBusy &amp;&amp; waitTdle) WaitDownloading()» —
        /// ждать или не ждать решает вызывающий, а не переход. Мы же ждали
        /// состояния load всегда и на таймауте бросали. На живом сайте вроде
        /// airbnb.fr, где аналитика не даёт load случиться никогда, это роняло
        /// ветку на полностью загруженной странице: «Timeout 30000ms exceeded,
        /// waiting until load». В ZP исключения в этом месте нет.
        /// </summary>
        public void Navigate(string url, string referer = "")
            => Sync(_page.GotoAsync(url, new PageGotoOptions
            {
                Referer   = referer == "" ? null : referer,
                WaitUntil = WaitUntilState.Commit,
            }));

        public void MouseClick(int x, int y, string button, string mouseEvent, bool considerScroll)
        {
            var btn = button?.ToLower() switch
            {
                "right"  => MouseButton.Right,
                "middle" => MouseButton.Middle,
                _        => MouseButton.Left,
            };
            Sync(_page.Mouse.ClickAsync(x, y, new MouseClickOptions { Button = btn }));
            MouseEmulation.Remember(_page, x, y);
        }

        /// <summary>
        /// Движение мыши с промежуточными точками. Один MoveAsync без Steps даёт
        /// телепорт: курсор оказывается в цели, не побывав между — а метод
        /// называется FullEmulation, и вызывающий код на эту эмуляцию
        /// рассчитывает. Число шагов берём от расстояния, чтобы короткий сдвиг не
        /// растягивался на десятки событий.
        /// </summary>
        public void FullEmulationMouseMove(int toX, int toY)
            => MouseEmulation.MoveTo(_page, toX, toY);

        /// <summary>
        /// Дождаться, пока страница догрузится. В ZP это именно загрузка
        /// документа, а не тишина в сети.
        ///
        /// Раньше здесь стоял NetworkIdle, и на любой странице с опросом или
        /// вебсокетом он не наступал никогда: вызов висел 30 секунд и падал по
        /// таймауту. Пока IsBusy врал «не занята», путь был мёртвый и это не
        /// проявлялось — а после его починки Go и F5 начали сюда заходить.
        /// </summary>
        /// <summary>
        /// Ожидание загрузки. По истечении срока просто возвращается: «подожди
        /// загрузку» в ZP — это ожидание, а не проверка. Сайты, где load не
        /// наступает никогда из-за висящей аналитики, иначе роняют ветку на
        /// готовой странице.
        /// </summary>
        public void WaitDownloading()
        {
            // Ловятся оба типа, и это проверено, а не выведено из названий:
            // Playwright .NET бросает по сроку System.TimeoutException, который
            // PlaywrightException НЕ наследует. Прежний catch(PlaywrightException)
            // не ловил ничего, и первый же сайт, не доходящий до load, ронял
            // ветку прямо здесь. PlaywrightException оставлен ради смены
            // документа во время ожидания.
            try { Sync(_page.WaitForLoadStateAsync(LoadState.Load)); }
            catch (System.TimeoutException)  { }
            catch (PlaywrightException)      { }
        }

        public string GetPagePreview()
            => Convert.ToBase64String(Sync(_page.ScreenshotAsync()));

        public void KeyEvent(string key, string type, string modifier = "")
            => Sync(_page.Keyboard.PressAsync(string.IsNullOrEmpty(modifier) ? key : $"{modifier}+{key}"));

        public void InsertText(string text)
            => Sync(_page.Keyboard.InsertTextAsync(text));

        public void FullEmulationMouseWheel(int x, int y)
            => Sync(_page.Mouse.WheelAsync(x, y));

        public void Close() => Sync(_page.CloseAsync());

        /// <summary>ZP-совместимость: RiseEvent("click", new Rectangle(x,y,1,1), "Left")</summary>
        public void RiseEvent(string eventName, System.Drawing.Rectangle area, string button)
        {
            int x = area.X + area.Width  / 2;
            int y = area.Y + area.Height / 2;
            if (eventName == "click")
            {
                MouseEmulation.MoveTo(_page, x, y);
                Thread.Sleep(Random.Shared.Next(45, 160));
                Sync(_page.Mouse.ClickAsync(x, y, new MouseClickOptions { Delay = Random.Shared.Next(40, 140) }));
                MouseEmulation.Remember(_page, x, y);
            }
            else
                Sync(_page.EvaluateAsync(
                    $"document.elementFromPoint({x},{y})?.dispatchEvent(new MouseEvent('{eventName}',{{bubbles:true,clientX:{x},clientY:{y}}}))"));
        }

        public IList<ITrafficItem> GetTraffic(IEnumerable<string> urlFilters)
            => TrafficCapture.Get(_page, urlFilters);

        public IHeElement FindElementById(string id)
            => new PlaywrightElement(_page.Locator($"#{id}"));

        public IHeElement FindElementByName(string name)
            => new PlaywrightElement(_page.Locator($"[name='{name}']"));

        public IHeElement FindElementByXPath(string xpath, int index)
            => new PlaywrightElement(_page.Locator($"xpath={xpath}").Nth(index));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new PlaywrightElement(PlaywrightInstance.BuildLocator(_page, tag, attr, pattern, mode).Nth(index));

        public IList<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
        {
            var loc   = PlaywrightInstance.BuildLocator(_page, tag, attr, pattern, mode);
            int count = Sync(loc.CountAsync());
            return Enumerable.Range(0, count)
                .Select(i => (IHeElement)new PlaywrightElement(loc.Nth(i)))
                .ToList();
        }

        private static T    Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();
        private static void Sync(Task t)        => t.GetAwaiter().GetResult();
    }

    internal sealed class PlaywrightHttpResponse : IBrowserHttpResponse
    {
        internal PlaywrightHttpResponse(int status, string reason, string body,
                                        IDictionary<string, string> headers)
        {
            Status  = status;
            Reason  = reason ?? "";
            Body    = body ?? "";
            Headers = headers ?? new Dictionary<string, string>();
        }

        public int    Status { get; }
        public string Reason { get; }
        public string Body   { get; }
        public IDictionary<string, string> Headers { get; }
    }

    public sealed class PlaywrightDocument : IDocument
    {
        private readonly IPage _page;
        public PlaywrightDocument(IPage page) => _page = page;

        /// <summary>
        /// ZP-шный EvaluateScript принимает две формы, и переносимый код пользуется
        /// обеими: тело функции с <c>return</c> (JsGet, GetViewportSize) и выражение,
        /// значение которого нужно вернуть (JsClick и JsSet шлют IIFE, JsPost —
        /// произвольный скрипт пользователя).
        ///
        /// Одним вызовом Playwright их не покрыть: сырая строка идёт как выражение и
        /// отдаёт значение, но <c>return</c> на верхнем уровне для неё — синтаксическая
        /// ошибка; обёртка <c>() =&gt; {…}</c> делает <c>return</c> рабочим, зато
        /// значение выражения глотает.
        ///
        /// Поэтому сначала пробуем как выражение, а на синтаксической ошибке
        /// оборачиваем и повторяем. Повтор безопасен: разбор падает до выполнения,
        /// побочных эффектов от первой попытки не остаётся. Строку компилирует CDP,
        /// а не страница, так что CSP тут ни при чём — eval мы не зовём.
        /// </summary>
        public string EvaluateScript(string js)
        {
            try
            {
                return Stringify(_page.EvaluateAsync(js).GetAwaiter().GetResult());
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("SyntaxError"))
            {
                return Stringify(_page.EvaluateAsync($"() => {{ {js} }}").GetAwaiter().GetResult());
            }
        }

        /// <summary>ZP отдаёт результат строкой, объекты — их JSON-представлением.</summary>
        private static string Stringify(System.Text.Json.JsonElement? value)
        {
            if (value is not { } el) return "";
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Undefined => "",
                System.Text.Json.JsonValueKind.Null      => "",
                System.Text.Json.JsonValueKind.String    => el.GetString() ?? "",
                _                                        => Clean(el.GetRawText()),
            };
        }

        /// <summary>
        /// Playwright, отдавая объект, подмешивает служебный ключ "$id" своего
        /// сериализатора. В исходном объекте страницы его нет, а скрипт, который
        /// разберёт результат через JSON.parse, увидит лишнее поле и может на нём
        /// споткнуться — например перебирая ключи. Убираем.
        /// </summary>
        private static string Clean(string json)
        {
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(json);
                Strip(token);
                return token.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return json; }

            static void Strip(Newtonsoft.Json.Linq.JToken t)
            {
                if (t is Newtonsoft.Json.Linq.JObject o)
                {
                    o.Remove("$id");
                    foreach (var prop in o.Properties().ToList()) Strip(prop.Value);
                }
                else if (t is Newtonsoft.Json.Linq.JArray a)
                {
                    foreach (var item in a) Strip(item);
                }
            }
        }
    }

    public sealed class PlaywrightElement : IHeElement
    {
        private readonly ILocator _loc;
        public PlaywrightElement(ILocator loc) => _loc = loc;

        /// <summary>Нужен соседнему элементу в RemoveChild.</summary>
        internal ILocator Locator => _loc;

        private static T    Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();
        private static void Sync(Task t)        => t.GetAwaiter().GetResult();

        public bool IsVoid
        {
            get { try { return Sync(_loc.CountAsync()) == 0; } catch { return true; } }
        }

        /// <summary>В ZP IsNull и IsVoid различаются нюансами; у нас источник один.</summary>
        public bool IsNull => IsVoid;

        public string InnerText => Sync(_loc.InnerTextAsync());
        public string InnerHtml => Sync(_loc.InnerHTMLAsync());
        public string OuterHtml => Sync(_loc.EvaluateAsync<string>("el => el.outerHTML")) ?? "";
        public string TagName   => (Sync(_loc.EvaluateAsync<string>("el => el.tagName")) ?? "").ToLower();

        public int Width  => (int)(Sync(_loc.BoundingBoxAsync())?.Width  ?? 0);
        public int Height => (int)(Sync(_loc.BoundingBoxAsync())?.Height ?? 0);

        public System.Drawing.Point DisplacementInBrowser
        {
            get
            {
                var box = Sync(_loc.BoundingBoxAsync());
                return box == null
                    ? System.Drawing.Point.Empty
                    : new System.Drawing.Point((int)box.X, (int)box.Y);
            }
        }

        public string GetAttribute(string attr) => attr.ToLower() switch
        {
            "innertext" => Sync(_loc.InnerTextAsync()),
            "value"     => Sync(_loc.InputValueAsync()),
            _           => Sync(_loc.GetAttributeAsync(attr)) ?? ""
        };

        public void SetAttribute(string attr, string value)
        {
            // value у input/select — это свойство, а не атрибут: правка атрибута
            // не двинет реальное значение поля, поэтому разводим случаи.
            if (attr.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                SetValue(value, "None", false);
                return;
            }
            Sync(_loc.EvaluateAsync(
                "(el, a) => el.setAttribute(a.name, a.value)",
                new { name = attr, value }));
        }

        public void RemoveAttribute(string attr)
            => Sync(_loc.EvaluateAsync("(el, a) => el.removeAttribute(a)", attr));

        public void Focus()          => Sync(_loc.FocusAsync());
        public void ScrollIntoView() => Sync(_loc.ScrollIntoViewIfNeededAsync());

        public string DrawToBitmap()
            => Convert.ToBase64String(Sync(_loc.ScreenshotAsync()));

        /// <summary>
        /// ZP-шный DrawPartAsBitmap: кусок элемента, координаты от его левого
        /// верхнего угла. Playwright умеет обрезать только по странице, поэтому
        /// область смещается на положение элемента в документе.
        /// </summary>
        public string DrawPartToBitmap(int x, int y, int width, int height)
        {
            var box = Sync(_loc.BoundingBoxAsync())
                      ?? throw new InvalidOperationException(
                          "DrawPartAsBitmap: элемент не отрисован — нет геометрии");

            var png = Sync(_loc.Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Clip = new Clip
                {
                    X      = (float)box.X + x,
                    Y      = (float)box.Y + y,
                    Width  = width,
                    Height = height,
                },
            }));

            return Convert.ToBase64String(png);
        }

        public IEnumerable<IHeElement> FindChildrenByTags(string tags)
        {
            var list = (tags ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length == 0) list = new[] { "*" };
            // Прямые потомки, а не любые вложенные: ZP считает по ним позицию
            // элемента среди братьев, и вложенные сбили бы нумерацию.
            var loc = _loc.Locator(string.Join(", ", list.Select(t => "> " + PlaywrightInstance.CssTag(t))));
            return Enumerable.Range(0, Sync(loc.CountAsync()))
                             .Select(i => (IHeElement)new PlaywrightElement(loc.Nth(i)))
                             .ToList();
        }

        public IHeElement FindChildByAttribute(string tag, string attr, string pattern, string mode, int index)
        {
            var tags = (tag ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Length == 0) tags = new[] { "*" };
            bool negate = mode == "notext";
            string escaped = pattern.Replace("\\", "\\\\").Replace("'", "\\'");
            string clause  = negate ? $":not([{attr}='{escaped}'])" : $"[{attr}='{escaped}']";
            return new PlaywrightElement(
                _loc.Locator(string.Join(", ", tags.Select(t => PlaywrightInstance.CssTag(t) + clause))).Nth(index));
        }

        // Random.Shared, а не свой экземпляр: System.Random не потокобезопасен,
        // и при параллельных запусках одного шаблона его внутреннее состояние
        // портится — вместо случайных чисел начинают идти нули. Задержки и
        // отступы, ради которых он тут и стоит, при этом пропадают молча.

        /// <summary>
        /// Клик всегда настоящий, мышью. Раньше здесь при уровне эмуляции ниже
        /// superEmulation уходил DispatchEvent("click") — синтетическое событие
        /// без isTrusted, которое и детектируется, и на половине сайтов просто
        /// не срабатывает: обработчики висят на mousedown/mouseup. А уровень по
        /// умолчанию как раз "none", то есть так кликало всё.
        ///
        /// DispatchEvent остаётся только для событий, которые мышью не изобразить.
        /// </summary>
        public void RiseEvent(string eventName, string emulationLevel)
        {
            bool full = string.Equals(emulationLevel, "superEmulation", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(emulationLevel, "Full",           StringComparison.OrdinalIgnoreCase);

            switch ((eventName ?? "").ToLowerInvariant())
            {
                case "click":
                    // Без эмуляции — тоже настоящий клик мышью, просто без
                    // подъезда курсора: DispatchEvent("click") даёт событие без
                    // isTrusted, которое и детектируется, и на половине сайтов
                    // не срабатывает — обработчики висят на mousedown/mouseup.
                    if (full) MouseEmulation.Click(_loc);
                    else
                    {
                        Sync(_loc.ScrollIntoViewIfNeededAsync());
                        Sync(_loc.ClickAsync(new LocatorClickOptions { Delay = Random.Shared.Next(40, 140) }));
                    }
                    return;

                case "contextmenu":
                    if (full) MouseEmulation.Click(_loc, MouseButton.Right);
                    else Sync(_loc.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right }));
                    return;

                case "dblclick":
                    if (full) MouseEmulation.Hover(_loc);
                    Sync(_loc.DblClickAsync(new LocatorDblClickOptions { Delay = Random.Shared.Next(40, 120) }));
                    return;

                // Наведение мышью изобразимо по-настоящему, и при полной
                // эмуляции так и надо: браузер сам выдаст mouseover, mouseenter
                // и хвост mousemove по дороге — с isTrusted, чего
                // DispatchEvent не даёт никогда.
                case "mouseover":
                case "mouseenter":
                case "mousemove":
                case "hover":
                    if (full) { MouseEmulation.Hover(_loc); return; }
                    break;

                // Отдельные фазы нажатия: мышь стоит там, где надо, и жмёт.
                case "mousedown":
                case "mouseup":
                    if (full)
                    {
                        MouseEmulation.Hover(_loc);
                        Thread.Sleep(Random.Shared.Next(30, 90));
                        var page = _loc.Page;
                        if (eventName.Equals("mousedown", StringComparison.OrdinalIgnoreCase))
                            Sync(page.Mouse.DownAsync());
                        else
                            Sync(page.Mouse.UpAsync());
                        return;
                    }
                    break;
            }

            // Всё, что мышью не изобразить (change, input, blur, submit...).
            Sync(_loc.DispatchEventAsync(eventName));
        }

        /// <summary>
        /// Ввод посимвольный, со случайными паузами. Раньше режим "Full" уходил в
        /// FillAsync — тот проставляет value одним присваиванием и шлёт один
        /// input. Для ZP "Full" означает полную эмуляцию набора, и весь расчёт
        /// перенесённого HeSet на человеческие задержки этим сводился на нет:
        /// поля с посимвольной валидацией такого ввода не принимают, а антибот
        /// видит мгновенно заполненную форму.
        /// </summary>
        public void SetValue(string value, string mode, bool clear)
        {
            // Не "Full" — ZP-шное присваивание без эмуляции, оставляем как есть.
            if (mode != "Full")
            {
                if (clear) Sync(_loc.ClearAsync());
                Sync(_loc.EvaluateAsync($"el => el.value = '{value.Replace("'", "\\'")}'"));
                return;
            }

            // Фокус берём тем же полным кликом, а не голым ClickAsync: до поля
            // сначала доезжает курсор. Валидаторы и антибот смотрят на то, как
            // поле получило фокус, не меньше, чем на сам набор.
            MouseEmulation.Click(_loc);
            if (clear) Sync(_loc.ClearAsync());

            foreach (var ch in value)
            {
                Sync(_loc.PressAsync(ch.ToString(), new LocatorPressOptions { Delay = Random.Shared.Next(20, 70) }));
                Thread.Sleep(Random.Shared.Next(35, 145));
            }
        }

        public string GetXPath() => Sync(_loc.EvaluateAsync<string>(@"el => {
            const parts = [];
            let n = el;
            while (n && n.nodeType === 1) {
                let idx = 1, sib = n.previousSibling;
                while (sib) { if (sib.nodeType === 1 && sib.nodeName === n.nodeName) idx++; sib = sib.previousSibling; }
                parts.unshift(n.nodeName.toLowerCase() + '[' + idx + ']');
                n = n.parentNode;
            }
            return '/' + parts.join('/');
        }"));

        public IHeElement ParentElement  => new PlaywrightElement(_loc.Locator("xpath=.."));
        /// <summary>
        /// Удалить из этого элемента переданного потомка.
        ///
        /// Раньше аргумент просто игнорировался, а выполнялось el.remove() на
        /// самом элементе — то есть вызов parent.RemoveChild(child) сносил
        /// родителя вместе со всем содержимым. Молча: ошибки нет, дерево уже
        /// другое, и падает потом совсем другой код, не нашедший родителя.
        /// </summary>
        public void RemoveChild(IHeElement child)
        {
            if (child is not PlaywrightElement pe)
                throw new ArgumentException("ожидается элемент этого же браузера", nameof(child));

            var handle = Sync(pe.Locator.ElementHandleAsync());
            if (handle is null) return;

            Sync(_loc.EvaluateAsync(
                "(parent, c) => { if (c && parent.contains(c)) c.remove(); }", handle));
        }
    }
}