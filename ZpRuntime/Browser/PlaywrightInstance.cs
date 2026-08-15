using Microsoft.Playwright;
using System;
using System.Collections.Generic;
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

        public bool   UseFullMouseEmulation { get; set; } = false;
        public string EmulationLevel => UseFullMouseEmulation ? "superEmulation" : "none";

        public IBrowserTab NewTab(string _ = "new")
        {
            var page    = Sync(_context.NewPageAsync());
            _activePage = page;
            return new PlaywrightTab(page);
        }

        public void SetActivePage(IPage page) => _activePage = page;

        public void CloseAllTabs()
        {
            foreach (var p in _context.Pages.Skip(1).ToList())
                Sync(p.CloseAsync());
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

            var cdp = Sync(_context.NewCDPSessionAsync(_activePage));
            Sync(cdp.SendAsync("Network.clearBrowserCache"));
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
                        Domain = f[0],
                        Path   = f[2],
                        Secure = f[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                        Name   = f[5],
                        Value  = f[6],
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

            var cdp = Sync(_context.NewCDPSessionAsync(_activePage));
            Sync(cdp.SendAsync("Emulation.setTimezoneOverride",
                new Dictionary<string, object> { ["timezoneId"] = ianaName }));
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

        public void CFSolve(int timeoutSeconds = 30)
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
                        if (Sync(cb.CountAsync()) > 0) { Sync(cb.ClickAsync()); Thread.Sleep(3000); return; }
                    }
                    catch { }
                }
                try
                {
                    var verify = _activePage.Locator("text=Verify you are human");
                    if (Sync(verify.CountAsync()) > 0) { Sync(verify.ClickAsync()); Thread.Sleep(3000); return; }
                }
                catch { }
                Thread.Sleep(1000);
            }
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

            // fulltagname: ZP-специфика — ищем по типу тега
            // "input:password" → input[type="password"]
            if (attr == "fulltagname")
            {
                string cssTag = tag.Contains(':')
                    ? $"{tag.Split(':')[0]}[type='{tag.Split(':')[1]}']"
                    : tag;
                return page.Locator(cssTag);
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

            if (regexp)
            {
                // Регулярка выполняется в самой странице — см. RegexSelector.cs.
                // Раньше здесь из шаблона брался самый длинный литеральный кусок
                // и подставлялся в XPath contains(): "^btn-(a|b)$" находил и
                // btn-c, и xbtn-a. Ветка при этом не падала — просто кликала не
                // туда, потому что вместе с лишними совпадениями сдвигался
                // Number, по которому выбирается нужный элемент.
                return page.Locator(RegexSelector.Build(tag, attr, pattern, negate));
            }

            // Точное совпадение оставляем на CSS: в отличие от XPath его движок
            // пробивает открытый shadow DOM, и терять это поведение нельзя.
            string escaped = pattern.Replace("\\", "\\\\").Replace("'", "\\'");
            string clause  = negate ? $":not([{attr}='{escaped}'])" : $"[{attr}='{escaped}']";
            var    tags    = SplitTags(tag);
            if (tags.Length == 0) tags = new[] { "*" };
            return page.Locator(string.Join(", ", tags.Select(t => t + clause)));
        }

        /// <summary>Список тегов ZP ("a;div") → CSS-селектор ("a, div"). Пустой тег → "*".</summary>
        private static string CssTags(string tag)
        {
            var tags = SplitTags(tag);
            return tags.Length == 0 ? "*" : string.Join(", ", tags);
        }

        /// <summary>Список тегов ZP + условие → XPath-объединение ("//a[c]|//div[c]").</summary>
        private static string XPathTags(string tag, string condition)
        {
            var tags = SplitTags(tag);
            if (tags.Length == 0) tags = new[] { "*" };
            return "xpath=" + string.Join("|", tags.Select(t => $"//{t}[{condition}]"));
        }

        private static string[] SplitTags(string tag)
            => (tag ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        private System.Drawing.Point _mousePos;
        public System.Drawing.Point FullEmulationMouseCurrentPosition
        {
            get => _mousePos;
            set { _mousePos = value; Sync(_page.Mouse.MoveAsync(value.X, value.Y)); }
        }

        public void Navigate(string url, string referer = "")
            => Sync(_page.GotoAsync(url, new PageGotoOptions { Referer = referer == "" ? null : referer }));

        public void MouseClick(int x, int y, string button, string mouseEvent, bool considerScroll)
        {
            var btn = button?.ToLower() switch
            {
                "right"  => MouseButton.Right,
                "middle" => MouseButton.Middle,
                _        => MouseButton.Left,
            };
            Sync(_page.Mouse.ClickAsync(x, y, new MouseClickOptions { Button = btn }));
            _mousePos = new System.Drawing.Point(x, y);
        }

        /// <summary>
        /// Движение мыши с промежуточными точками. Один MoveAsync без Steps даёт
        /// телепорт: курсор оказывается в цели, не побывав между — а метод
        /// называется FullEmulation, и вызывающий код на эту эмуляцию
        /// рассчитывает. Число шагов берём от расстояния, чтобы короткий сдвиг не
        /// растягивался на десятки событий.
        /// </summary>
        public void FullEmulationMouseMove(int toX, int toY)
        {
            int dx = toX - _mousePos.X, dy = toY - _mousePos.Y;
            int distance = (int)Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Clamp(distance / 12, 6, 40);

            Sync(_page.Mouse.MoveAsync(toX, toY, new MouseMoveOptions { Steps = steps }));
            _mousePos = new System.Drawing.Point(toX, toY);
        }

        /// <summary>
        /// Дождаться, пока страница догрузится. В ZP это именно загрузка
        /// документа, а не тишина в сети.
        ///
        /// Раньше здесь стоял NetworkIdle, и на любой странице с опросом или
        /// вебсокетом он не наступал никогда: вызов висел 30 секунд и падал по
        /// таймауту. Пока IsBusy врал «не занята», путь был мёртвый и это не
        /// проявлялось — а после его починки Go и F5 начали сюда заходить.
        /// </summary>
        public void WaitDownloading()
            => Sync(_page.WaitForLoadStateAsync(LoadState.Load));

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
                Sync(_page.Mouse.ClickAsync(x, y));
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
                _                                        => el.GetRawText(),
            };
        }
    }

    public sealed class PlaywrightElement : IHeElement
    {
        private readonly ILocator _loc;
        public PlaywrightElement(ILocator loc) => _loc = loc;

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

        public IHeElement FindChildByAttribute(string tag, string attr, string pattern, string mode, int index)
        {
            var tags = (tag ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Length == 0) tags = new[] { "*" };
            bool negate = mode == "notext";
            string escaped = pattern.Replace("\\", "\\\\").Replace("'", "\\'");
            string clause  = negate ? $":not([{attr}='{escaped}'])" : $"[{attr}='{escaped}']";
            return new PlaywrightElement(
                _loc.Locator(string.Join(", ", tags.Select(t => t + clause))).Nth(index));
        }

        private static readonly Random _rnd = new();

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
            if (eventName != "click") { Sync(_loc.DispatchEventAsync(eventName)); return; }

            Sync(_loc.ScrollIntoViewIfNeededAsync());
            Sync(_loc.ClickAsync(new LocatorClickOptions { Delay = _rnd.Next(40, 140) }));
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

            Sync(_loc.ScrollIntoViewIfNeededAsync());
            Sync(_loc.ClickAsync(new LocatorClickOptions { Delay = _rnd.Next(40, 140) }));
            if (clear) Sync(_loc.ClearAsync());

            foreach (var ch in value)
            {
                Sync(_loc.PressAsync(ch.ToString(), new LocatorPressOptions { Delay = _rnd.Next(20, 70) }));
                Thread.Sleep(_rnd.Next(35, 145));
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
        public void RemoveChild(IHeElement child) => Sync(_loc.EvaluateAsync("el => el.remove()"));
    }
}