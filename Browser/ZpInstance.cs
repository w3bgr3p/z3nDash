#if ZENNOPOSTER

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ZennoLab.CommandCenter;
using z3n8.Browser;

namespace z3n8.Browser
{
    /// <summary>
    /// Тонкая обёртка над ZennoPoster Instance.
    /// Делегирует все вызовы напрямую в Instance.
    /// Компилируется только при наличии символа ZENNOPOSTER.
    /// </summary>
    public sealed class ZpInstance : IBrowserInstance
    {
        private readonly Instance _instance;
        public ZpInstance(Instance instance) => _instance = instance;

        public IBrowserTab ActiveTab => new ZpTab(_instance);
        public IList<IBrowserTab> AllTabs
            => _instance.AllTabs.Select(t => (IBrowserTab)new ZpTab(_instance, t)).ToList();

        public bool   UseFullMouseEmulation
        {
            get => _instance.UseFullMouseEmulation;
            set => _instance.UseFullMouseEmulation = value;
        }
        public string EmulationLevel => _instance.EmulationLevel;

        public IBrowserTab NewTab(string name = "new")
        {
            _instance.NewTab(name);
            return new ZpTab(_instance);
        }

        public void CloseAllTabs()     => _instance.CloseAllTabs();
        public void ClearCache(string domain = null) => _instance.ClearCache(domain ?? "");
        public void ClearCookie(string domain = null) => _instance.ClearCookie(domain ?? "");
        public void SaveCookie(string path)  => _instance.SaveCookie(path);
        public void WaitFieldEmulationDelay() => _instance.WaitFieldEmulationDelay();

        public void SetTimezone(int offsetMinutes, int unused)
            => _instance.SetTimezone(offsetMinutes, unused);
        public void SetIanaTimezone(string ianaName)
            => _instance.SetIanaTimezone(ianaName);

        public IHeElement FindElementById(string id)
            => new ZpElement(_instance.ActiveTab.FindElementById(id));

        public IHeElement FindElementByName(string name)
            => new ZpElement(_instance.ActiveTab.FindElementByName(name));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new ZpElement(_instance.ActiveTab.FindElementByAttribute(tag, attr, pattern, mode, index));

        public IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
            => _instance.ActiveTab.FindElementsByAttribute(tag, attr, pattern, mode)
                .Select(he => (IHeElement)new ZpElement(he));
    }

    public sealed class ZpTab : IBrowserTab
    {
        private readonly Instance _instance;
        private readonly Tab      _tab;

        public ZpTab(Instance instance, Tab tab = null)
        {
            _instance = instance;
            _tab      = tab;
        }

        private ZennoLab.CommandCenter.Tab ActiveZpTab => _tab ?? _instance.ActiveTab;

        public string    URL    => ActiveZpTab.URL;
        public bool      IsBusy => ActiveZpTab.IsBusy;
        public IDocument MainDocument => new ZpDocument(ActiveZpTab.MainDocument);

        public void Navigate(string url, string referer = "")
            => ActiveZpTab.Navigate(url, referer);

        public void WaitDownloading() => ActiveZpTab.WaitDownloading();

        public void KeyEvent(string key, string type, string modifier = "")
            => ActiveZpTab.KeyEvent(key, type, modifier);

        public void FullEmulationMouseWheel(int x, int y)
            => ActiveZpTab.FullEmulationMouseWheel(x, y);

        public void Close() => ActiveZpTab.Close();

        public IHeElement FindElementById(string id)
            => new ZpElement(ActiveZpTab.FindElementById(id));

        public IHeElement FindElementByName(string name)
            => new ZpElement(ActiveZpTab.FindElementByName(name));

        public IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index)
            => new ZpElement(ActiveZpTab.FindElementByAttribute(tag, attr, pattern, mode, index));

        public IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode)
            => ActiveZpTab.FindElementsByAttribute(tag, attr, pattern, mode)
                .Select(he => (IHeElement)new ZpElement(he));
    }

    public sealed class ZpDocument : IDocument
    {
        private readonly ZennoLab.CommandCenter.HtmlDocument _doc;
        public ZpDocument(ZennoLab.CommandCenter.HtmlDocument doc) => _doc = doc;
        public string EvaluateScript(string js) => _doc.EvaluateScript(js);
    }

    public sealed class ZpElement : IHeElement
    {
        private readonly HtmlElement _he;
        public ZpElement(HtmlElement he) => _he = he;

        public bool        IsVoid      => _he.IsVoid;
        public string      InnerText   => _he.InnerText;
        public string      GetAttribute(string attr) => _he.GetAttribute(attr);
        public IHeElement  ParentElement => new ZpElement(_he.ParentElement);
        public string      GetXPath()  => _he.GetXPath();

        public void RiseEvent(string eventName, string emulationLevel)
            => _he.RiseEvent(eventName, emulationLevel);

        public void SetValue(string value, string mode, bool clear)
            => _he.SetValue(value, mode, clear);

        public void RemoveChild(IHeElement child)
            => _he.ParentElement.RemoveChild(((ZpElement)child)._he);
    }
}

#endif // ZENNOPOSTER