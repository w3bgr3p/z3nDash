using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using z3nIO.Browser;

namespace z3nIO.Browser
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

        internal static ILocator BuildLocator(IPage page, string tag, string attr, string pattern, string mode)
        {
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
                return mode == "regexp"
                    ? page.Locator(tag).Filter(new LocatorFilterOptions { HasTextRegex = new Regex(pattern, RegexOptions.IgnoreCase) })
                    : page.Locator(tag).Filter(new LocatorFilterOptions { HasTextString = pattern });

            if (mode == "regexp")
            {
                var literal = new Regex(@"[^\\.()\[\]{}+*?^$|]+")
                    .Matches(pattern)
                    .Cast<System.Text.RegularExpressions.Match>()
                    .OrderByDescending(m => m.Length)
                    .FirstOrDefault()?.Value ?? pattern;
                return page.Locator($"xpath=//{tag}[contains(@{attr}, '{literal.Replace("'", "\\'")}')]");
            }

            return page.Locator($"{tag}[{attr}='{pattern}']");
        }

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

        public void Navigate(string url, string referer = "")
            => Sync(_page.GotoAsync(url, new PageGotoOptions { Referer = referer == "" ? null : referer }));

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

        public string InnerText => Sync(_loc.InnerTextAsync());

        public string GetAttribute(string attr) => attr.ToLower() switch
        {
            "innertext" => Sync(_loc.InnerTextAsync()),
            "value"     => Sync(_loc.InputValueAsync()),
            _           => Sync(_loc.GetAttributeAsync(attr)) ?? ""
        };

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