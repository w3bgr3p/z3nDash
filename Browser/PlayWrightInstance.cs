using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using z3n8.Browser;

namespace z3n8.Browser
{
    public sealed class PlaywrightInstance : IBrowserInstance
    {
        private readonly IPage _page;
        public IBrowserContext Context => _page.Context;

        public PlaywrightInstance(IPage page) => _page = page;

        public IBrowserTab ActiveTab => new PlaywrightTab(_page);
        public IList<IBrowserTab> AllTabs
            => _page.Context.Pages.Select(p => (IBrowserTab)new PlaywrightTab(p)).ToList();

        public bool   UseFullMouseEmulation { get; set; } = false;
        public string EmulationLevel => UseFullMouseEmulation ? "superEmulation" : "none";

        public IBrowserTab NewTab(string _ = "new")
            => new PlaywrightTab(Sync(_page.Context.NewPageAsync()));

        public void CloseAllTabs()
        {
            foreach (var p in _page.Context.Pages.Skip(1).ToList())
                Sync(p.CloseAsync());
        }

        public void ClearCache(string domain = null)
            => Sync(_page.Context.ClearCookiesAsync());

        public void ClearCookie(string domain = null)
        {
            if (domain == null)
                Sync(_page.Context.ClearCookiesAsync());
            else
                Sync(_page.Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Domain = domain }));
        }

        public void SaveCookie(string path)
        {
            var cookies = Sync(_page.Context.CookiesAsync());
            File.WriteAllText(path, string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        }

        public void WaitFieldEmulationDelay() => Thread.Sleep(new Random().Next(1337, 2077));

        public void SetTimezone(int offsetMinutes, int unused) { }
        public void SetIanaTimezone(string ianaName)           { }

        public IHeElement FindElementById(string id)
            => new PlaywrightElement(_page.Locator($"#{id}"));

        public IHeElement FindElementByName(string name)
            => new PlaywrightElement(_page.Locator($"[name='{name}']"));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new PlaywrightElement(BuildLocator(_page, tag, attr, pattern, mode).Nth(index));

        public IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
        {
            var loc   = BuildLocator(_page, tag, attr, pattern, mode);
            int count = Sync(loc.CountAsync());
            return Enumerable.Range(0, count).Select(i => (IHeElement)new PlaywrightElement(loc.Nth(i)));
        }

        public void CFSolve(int timeoutSeconds = 30)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < deadline)
            {
                var cfFrame = _page.Frames
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
                    var verify = _page.Locator("text=Verify you are human");
                    if (Sync(verify.CountAsync()) > 0) { Sync(verify.ClickAsync()); Thread.Sleep(3000); return; }
                }
                catch { }
                Thread.Sleep(1000);
            }
        }

        internal static ILocator BuildLocator(IPage page, string tag, string attr, string pattern, string mode)
        {
            if (attr is "innertext" or "text")
                return mode == "regexp"
                    ? page.Locator(tag).Filter(new LocatorFilterOptions { HasTextRegex = new Regex(pattern, RegexOptions.IgnoreCase) })
                    : page.Locator(tag).Filter(new LocatorFilterOptions { HasTextString = pattern });

            return mode == "regexp"
                ? page.Locator($"xpath=//{tag}[re:test(@{attr}, '{pattern}', 'i')]")
                : page.Locator($"{tag}[{attr}='{pattern}']");
        }

        private static T    Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();
        private static void Sync(Task t)        => t.GetAwaiter().GetResult();
    }

    public sealed class PlaywrightTab : IBrowserTab
    {
        private readonly IPage _page;
        public PlaywrightTab(IPage page) => _page = page;

        public string    URL     => _page.Url;
        public bool      IsBusy  => false;
        public IDocument MainDocument => new PlaywrightDocument(_page);

        public void Navigate(string url, string referer = "")
            => Sync(_page.GotoAsync(url, new PageGotoOptions { Referer = referer == "" ? null : referer }));

        public void WaitDownloading()
            => Sync(_page.WaitForLoadStateAsync(LoadState.NetworkIdle));

        public void KeyEvent(string key, string type, string modifier = "")
            => Sync(_page.Keyboard.PressAsync(string.IsNullOrEmpty(modifier) ? key : $"{modifier}+{key}"));

        public void FullEmulationMouseWheel(int x, int y)
            => Sync(_page.Mouse.WheelAsync(x, y));

        public void Close() => Sync(_page.CloseAsync());

        public IHeElement FindElementById(string id)
            => new PlaywrightElement(_page.Locator($"#{id}"));

        public IHeElement FindElementByName(string name)
            => new PlaywrightElement(_page.Locator($"[name='{name}']"));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new PlaywrightElement(PlaywrightInstance.BuildLocator(_page, tag, attr, pattern, mode).Nth(index));

        public IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
        {
            var loc   = PlaywrightInstance.BuildLocator(_page, tag, attr, pattern, mode);
            int count = Sync(loc.CountAsync());
            return Enumerable.Range(0, count).Select(i => (IHeElement)new PlaywrightElement(loc.Nth(i)));
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