using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace z3n8;

// ── PlaywrightElement — аналог HtmlElement ────────────────────────────────────

public sealed class PlaywrightElement
{
    private readonly ILocator? _locator;

    public static readonly PlaywrightElement Void = new(null);

    public PlaywrightElement(ILocator? locator) => _locator = locator;

    // ── IsVoid ────────────────────────────────────────────────────────────────

    public bool IsVoid
    {
        get
        {
            if (_locator == null) return true;
            try { return _locator.CountAsync().GetAwaiter().GetResult() == 0; }
            catch { return true; }
        }
    }

    // ── GetAttribute — аналог he.GetAttribute(atr) ───────────────────────────

    public string GetAttribute(string attr = "innertext")
    {
        if (_locator == null) return "";
        try
        {
            return attr.ToLower() switch
            {
                "innertext" => _locator.InnerTextAsync().GetAwaiter().GetResult() ?? "",
                "innerHTML" or "innerhtml" => _locator.InnerHTMLAsync().GetAwaiter().GetResult() ?? "",
                _ => _locator.GetAttributeAsync(attr).GetAwaiter().GetResult() ?? ""
            };
        }
        catch { return ""; }
    }

    // ── RiseEvent — аналог he.RiseEvent("click", emulationLevel) ─────────────

    public void RiseEvent(string eventName, string emulationLevel = "Full")
    {
        if (_locator == null) return;
        switch (eventName.ToLower())
        {
            case "click":
                _locator.ClickAsync().GetAwaiter().GetResult();
                break;
            case "focus":
                _locator.FocusAsync().GetAwaiter().GetResult();
                break;
            case "hover":
                _locator.HoverAsync().GetAwaiter().GetResult();
                break;
            default:
                _locator.EvaluateAsync($"el => el.dispatchEvent(new Event('{eventName}'))").GetAwaiter().GetResult();
                break;
        }
    }

    // ── SetValue — аналог he.SetValue(value, "Full", false) ──────────────────

    public void SetValue(string value, string mode = "Full", bool clearFirst = false)
    {
        if (_locator == null) return;
        if (clearFirst) _locator.ClearAsync().GetAwaiter().GetResult();
        _locator.FillAsync(value).GetAwaiter().GetResult();
    }

    // ── GetXPath ──────────────────────────────────────────────────────────────

    public string GetXPath()
    {
        if (_locator == null) return "";
        try
        {
            return _locator.EvaluateAsync<string>(
                @"el => {
                    const parts = [];
                    let node = el;
                    while (node && node.nodeType === Node.ELEMENT_NODE) {
                        let idx = 1;
                        let sib = node.previousSibling;
                        while (sib) { if (sib.nodeType === Node.ELEMENT_NODE && sib.nodeName === node.nodeName) idx++; sib = sib.previousSibling; }
                        parts.unshift(node.nodeName.toLowerCase() + '[' + idx + ']');
                        node = node.parentNode;
                    }
                    return '/' + parts.join('/');
                }"
            ).GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    // ── ParentElement ─────────────────────────────────────────────────────────

    public PlaywrightElement ParentElement
        => _locator == null ? Void : new PlaywrightElement(_locator.Locator("xpath=.."));

    // ── RemoveChild ───────────────────────────────────────────────────────────

    public void RemoveChild(PlaywrightElement child)
    {
        if (child._locator == null) return;
        child._locator.EvaluateAsync("el => el.remove()").GetAwaiter().GetResult();
    }
}

// ── PlaywrightTab — аналог Tab (instance.ActiveTab) ──────────────────────────

public sealed class PlaywrightTab
{
    private readonly IPage _page;

    public PlaywrightTab(IPage page) => _page = page;

    public string URL => _page.Url;

    public bool IsBusy
    {
        get
        {
            try { _page.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = 100 }).GetAwaiter().GetResult(); return false; }
            catch { return true; }
        }
    }

    public void WaitDownloading()
        => _page.WaitForLoadStateAsync(LoadState.NetworkIdle).GetAwaiter().GetResult();

    public void Navigate(string url, string _ = "")
        => _page.GotoAsync(url).GetAwaiter().GetResult();

    public void Close()
        => _page.CloseAsync().GetAwaiter().GetResult();

    // ── Element finders ───────────────────────────────────────────────────────

    public PlaywrightElement FindElementById(string id)
        => Wrap(_page.Locator($"#{id}"));

    public PlaywrightElement FindElementByName(string name)
        => Wrap(_page.Locator($"[name='{name}']"));

    /// <summary>
    /// mode: "contains" | "equals" | "regexp"
    /// fulltagname поддерживается как tag:type (input:password → input[type=password])
    /// </summary>
    public PlaywrightElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int pos = 0)
        => Wrap(BuildLocator(tag, attr, pattern, mode, pos));

    public List<PlaywrightElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
    {
        var locator = BuildLocator(tag, attr, pattern, mode);
        int count   = locator.CountAsync().GetAwaiter().GetResult();
        return Enumerable.Range(0, count)
            .Select(i => new PlaywrightElement(locator.Nth(i)))
            .ToList();
    }

    // ── JS ────────────────────────────────────────────────────────────────────

    public string EvaluateScript(string js)
    {
        try { return _page.EvaluateAsync<string>(js).GetAwaiter().GetResult() ?? ""; }
        catch { return ""; }
    }

    // ── MainDocument (для EvaluateScript совместимости) ───────────────────────

    public PlaywrightTab MainDocument => this;

    // ── Input ─────────────────────────────────────────────────────────────────

    public void KeyEvent(string key, string action, string modifier = "")
    {
        string combo = string.IsNullOrEmpty(modifier) ? key : $"{modifier}+{key}";
        _page.Keyboard.PressAsync(combo).GetAwaiter().GetResult();
    }

    public void FullEmulationMouseWheel(int x, int y)
        => _page.Mouse.WheelAsync(x, y).GetAwaiter().GetResult();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ILocator BuildLocator(string tag, string attr, string pattern, string mode, int pos = -1)
    {
        string baseTag = tag.Contains(':') ? tag.Split(':')[0] : tag;
        string cssTag  = tag.Contains(':')
            ? $"{baseTag}[type='{tag.Split(':')[1]}']"
            : tag;

        ILocator locator;

        if (attr == "fulltagname")
        {
            locator = _page.Locator(cssTag);
        }
        else if (mode == "regexp")
        {
            var literal = new Regex(@"[^\\.()\[\]{}+*?^$|]+")
                .Matches(pattern)
                .Cast<Match>()
                .OrderByDescending(m => m.Length)
                .FirstOrDefault()?.Value ?? pattern;

            if (attr.ToLower() == "innertext")
            {
                // innertext — не атрибут, используем Playwright has-text
                locator = _page.Locator(cssTag).Filter(new LocatorFilterOptions
                {
                    HasTextRegex = new Regex(literal)
                });
            }
            else
            {
                // реальный атрибут — XPath contains()
                locator = _page.Locator(
                    $"xpath=//{baseTag}[contains(@{attr}, '{literal.Replace("'", "\\'")}')]");
            }
        }
        else if (mode == "notcontains")
        {
            locator = _page.Locator(cssTag).Filter(new LocatorFilterOptions
            {
                HasNot = _page.Locator($"[{attr}*='{pattern}']")
            });
        }
        else
        {
            string selector = mode == "equals"
                ? $"{cssTag}[{attr}='{pattern}']"
                : $"{cssTag}[{attr}*='{pattern}']";
            locator = _page.Locator(selector);
        }

        if (pos > 0)  return locator.Nth(pos);
        if (pos == 0) return locator.First;
        return locator;
    }

    private static PlaywrightElement Wrap(ILocator locator) => new(locator);
}