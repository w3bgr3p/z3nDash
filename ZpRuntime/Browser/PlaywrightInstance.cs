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
        }

        public IBrowserTab ActiveTab => new PlaywrightTab(_activePage);
        public IList<IBrowserTab> AllTabs
            => _context.Pages.Select(p => (IBrowserTab)new PlaywrightTab(p)).ToList();

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

        public void ClearCache(string domain = null)
            => Sync(_context.ClearCookiesAsync());

        public void ClearCookie(string domain = null)
        {
            if (domain == null)
                Sync(_context.ClearCookiesAsync());
            else
                Sync(_context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Domain = domain }));
        }

        public void SaveCookie(string path)
        {
            var cookies = Sync(_context.CookiesAsync());
            File.WriteAllText(path, string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        }

        public void WaitFieldEmulationDelay() => Thread.Sleep(new Random().Next(1337, 2077));

        public void InstallCrxExtension(string path) { /* pre-installed in ZB profile */ }

        public void SetTimezone(int offsetMinutes, int unused) { }
        public void SetIanaTimezone(string ianaName)           { }

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
                // XPath 1.0 регулярок не знает, поэтому это осознанное приближение:
                // из регулярки берётся самый длинный литеральный кусок и ищется
                // через contains(). Для якорей и альтернатив ('^btn-(a|b)$') поиск
                // выйдет шире, чем задумано.
                var literal = new Regex(@"[^\\.()\[\]{}+*?^$|]+")
                    .Matches(pattern)
                    .Cast<System.Text.RegularExpressions.Match>()
                    .OrderByDescending(m => m.Length)
                    .FirstOrDefault()?.Value ?? pattern;

                string cond = $"contains(@{attr}, '{literal.Replace("'", "\\'")}')";
                if (negate) cond = $"not({cond})";
                return page.Locator(XPathTags(tag, cond));
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
        public bool      IsBusy       => false;
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

        public void FullEmulationMouseMove(int toX, int toY)
        {
            Sync(_page.Mouse.MoveAsync(toX, toY));
            _mousePos = new System.Drawing.Point(toX, toY);
        }

        public void WaitDownloading()
            => Sync(_page.WaitForLoadStateAsync(LoadState.NetworkIdle));

        public void KeyEvent(string key, string type, string modifier = "")
            => Sync(_page.Keyboard.PressAsync(string.IsNullOrEmpty(modifier) ? key : $"{modifier}+{key}"));

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

    public sealed class PlaywrightDocument : IDocument
    {
        private readonly IPage _page;
        public PlaywrightDocument(IPage page) => _page = page;

        public string EvaluateScript(string js)
            => _page.EvaluateAsync<string>($"() => {{ {js} }}").GetAwaiter().GetResult() ?? "";
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

        public void RiseEvent(string eventName, string emulationLevel)
        {
            if (eventName != "click") { Sync(_loc.DispatchEventAsync(eventName)); return; }
            if (emulationLevel == "superEmulation") Sync(_loc.ClickAsync());
            else Sync(_loc.DispatchEventAsync("click"));
        }

        public void SetValue(string value, string mode, bool clear)
        {
            if (clear) Sync(_loc.ClearAsync());
            if (mode == "Full") Sync(_loc.FillAsync(value));
            else Sync(_loc.EvaluateAsync($"el => el.value = '{value.Replace("'", "\\'")}'"));
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