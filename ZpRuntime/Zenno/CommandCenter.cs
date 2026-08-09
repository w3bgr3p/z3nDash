// ══════════════════════════════════════════════════════════════════════════════
// CommandCenter.cs — адаптеры ZennoLab.CommandCenter поверх IBrowserInstance.
//
// Имена типов и сигнатуры совпадают с реальным SDK (сняты с ZennoLab.dll), но
// за ними стоит не браузер ZennoPoster, а наш IBrowserInstance. Благодаря этому
// код, написанный под ZP, компилируется без правок.
//
// Реализованы члены, которые вызывает переносимый код. Методы, которым в
// standalone нет осмысленного соответствия, бросают NotSupportedException с
// внятным текстом, а не возвращают тихую заглушку.
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DevDeck.Browser;

namespace ZennoLab.CommandCenter
{
    /// <summary>ZP-шный HtmlElement. Обёртка над <see cref="IHeElement"/>.</summary>
    public sealed class HtmlElement
    {
        internal readonly IHeElement He;
        internal HtmlElement(IHeElement he) => He = he;

        public bool   IsVoid    => He.IsVoid;
        public bool   IsNull    => He.IsNull;
        public string InnerText => He.InnerText;
        public string InnerHtml => He.InnerHtml;
        public string OuterHtml => He.OuterHtml;
        public string TagName   => He.TagName;
        public string FullTagName => He.TagName;
        public int    Width     => He.Width;
        public int    Height    => He.Height;
        public int    BoundingClientWidth  => He.Width;
        public int    BoundingClientHeight => He.Height;

        public string Id   => He.GetAttribute("id");
        public string Name => He.GetAttribute("name");

        public Point DisplacementInBrowser    => He.DisplacementInBrowser;
        public Point DisplacementInDocument   => He.DisplacementInBrowser;
        public Point DisplacementInTabWindow  => He.DisplacementInBrowser;

        public HtmlElement ParentElement => new HtmlElement(He.ParentElement);

        public string GetAttribute(string attrName)          => He.GetAttribute(attrName);
        public void   SetAttribute(string attrName, string v) => He.SetAttribute(attrName, v);
        public void   RemoveAttribute(string attrName)        => He.RemoveAttribute(attrName);

        public void RiseEvent(string eventName, string emulation) => He.RiseEvent(eventName, emulation);
        public void Click()          => He.RiseEvent("click", "superEmulation");
        public void Focus()          => He.Focus();
        public void ScrollIntoView() => He.ScrollIntoView();

        /// <summary>ZP: SetValue(value, emulation, useSelectedItems, append).</summary>
        public void SetValue(string value, string emulation, bool useSelectedItems, bool append)
            => He.SetValue(value, emulation, clear: !append);

        public string GetValue(bool useSelectedItems) => He.GetAttribute("value");

        public string DrawToBitmap(bool isImage, string hash) => He.DrawToBitmap();

        public HtmlElement FindChildByAttribute(string tags, string attrName, string attrValue,
                                                string searchKind, int number)
            => new HtmlElement(He.FindChildByAttribute(tags, attrName, attrValue, searchKind, number));

        public void RemoveChild(HtmlElement child) => He.RemoveChild(child.He);

        public string GetXPath() => He.GetXPath();
    }

    /// <summary>ZP-шная коллекция элементов. Итерируется и индексируется, как в SDK.</summary>
    public sealed class HtmlElementCollection : IEnumerable<HtmlElement>
    {
        private readonly List<HtmlElement> _items;
        internal HtmlElementCollection(IEnumerable<IHeElement> items)
            => _items = items.Select(x => new HtmlElement(x)).ToList();

        public int  Count  => _items.Count;
        public bool IsVoid => _items.Count == 0;

        public HtmlElement[] Elements => _items.ToArray();
        public HtmlElement GetByNumber(int number) => _items[number];
        public HtmlElement this[int index]         => _items[index];

        public int IndexOf(HtmlElement element) => _items.IndexOf(element);

        public string AttributesToString(string attrName) => AttributesToString(attrName, "\r\n");
        public string AttributesToString(string attrName, string delemiter)
            => string.Join(delemiter, _items.Select(x => x.GetAttribute(attrName)));

        public IEnumerator<HtmlElement> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()         => _items.GetEnumerator();

        public void Dispose() { }
    }

    /// <summary>ZP-шный Document. Из переносимого кода используется для EvaluateScript.</summary>
    public sealed class Document
    {
        private readonly IDocument _doc;
        internal Document(IDocument doc) => _doc = doc;

        public string EvaluateScript(string script) => _doc.EvaluateScript(script);
        public string EvaluateScript(string script, bool throwException, bool altWay)
            => _doc.EvaluateScript(script);
    }

    /// <summary>ZP-шный TouchSimulation.</summary>
    public sealed class TouchSimulation
    {
        private readonly ITouch _touch;
        internal TouchSimulation(ITouch touch) => _touch = touch;

        public void Touch(int x, int y) => _touch.Touch(x, y);
        public void SwipeBetween(int x1, int y1, int x2, int y2)
            => _touch.SwipeBetween(x1, y1, x2, y2);
    }

    /// <summary>ZP-шный Tab. Обёртка над <see cref="IBrowserTab"/>.</summary>
    public sealed class Tab
    {
        internal readonly IBrowserTab T;
        internal Tab(IBrowserTab tab) => T = tab;

        public string URL        => T.URL;
        public string Domain     => T.Domain;
        public string MainDomain => T.MainDomain;
        public int    Handle     => T.Handle;
        public bool   IsBusy     => T.IsBusy;
        public bool   IsVoid     => false;
        public bool   IsNull     => false;

        public Document        MainDocument => new Document(T.MainDocument);
        public TouchSimulation Touch        => new TouchSimulation(T.Touch);

        public Point FullEmulationMouseCurrentPosition
        {
            get => T.FullEmulationMouseCurrentPosition;
            set => T.FullEmulationMouseCurrentPosition = value;
        }

        public void Navigate(string url, string referrer = "") => T.Navigate(url, referrer);
        public void WaitDownloading()                          => T.WaitDownloading();
        public void Close()                                    => T.Close();
        public void Stop()                                     => throw new NotSupportedException(
            "Tab.Stop: остановка загрузки в ZpRuntime не реализована");

        public void KeyEvent(string key, string keyEvent, string keyModifer = "")
            => T.KeyEvent(key, keyEvent, keyModifer);

        public void RiseEvent(string eventName, Rectangle rectangle, string clickType)
            => T.RiseEvent(eventName, rectangle, clickType);

        public void MouseClick(int x, int y, string button, string mouseEvent, bool considerScroll)
            => T.MouseClick(x, y, button, mouseEvent, considerScroll);

        public void MouseMove(int toX, int toY, bool useClick, bool considerScroll)
            => T.FullEmulationMouseMove(toX, toY);

        public void FullEmulationMouseMove(int toX, int toY) => T.FullEmulationMouseMove(toX, toY);
        public void FullEmulationMouseWheel(int deltaX, int deltaY) => T.FullEmulationMouseWheel(deltaX, deltaY);

        // ── Поиск ─────────────────────────────────────────────────────────────

        public HtmlElement FindElementById(string id)     => new HtmlElement(T.FindElementById(id));
        public HtmlElement FindElementByName(string name) => new HtmlElement(T.FindElementByName(name));

        public HtmlElement FindElementByXPath(string xpath, int number)
            => new HtmlElement(T.FindElementByXPath(xpath, number));

        public HtmlElement FindElementByAttribute(string tags, string attrName, string attrValue,
                                                  string searchKind, int number)
            => new HtmlElement(T.FindElementByAttribute(tags, attrName, attrValue, searchKind, number));

        public HtmlElementCollection FindElementsByAttribute(string tags, string attrName,
                                                             string attrValue, string searchKind)
            => new HtmlElementCollection(T.FindElementsByAttribute(tags, attrName, attrValue, searchKind));
    }

    /// <summary>
    /// ZP-шный Instance. Обёртка над <see cref="IBrowserInstance"/>.
    /// Заменяет прежнюю трёхчленную заглушку из ZennoStub.cs.
    /// </summary>
    public class Instance
    {
        private readonly IBrowserInstance? _br;

        /// <summary>
        /// Безбраузерный режим — для скриптов из чистого кода (путь csx-zp7).
        /// Любое обращение к браузеру бросит NotSupportedException с пояснением.
        /// </summary>
        public Instance() => _br = null;

        public Instance(IBrowserInstance browser)
            => _br = browser ?? throw new ArgumentNullException(nameof(browser));

        private IBrowserInstance Br => _br ?? throw new NotSupportedException(
            "Instance: браузер не подключён. Инстанс создан в безбраузерном режиме — " +
            "запустите задачу с браузером или не обращайтесь к ActiveTab/поиску элементов.");

        /// <summary>Доступ к нижнему слою для кода, который работает с ним напрямую.</summary>
        public IBrowserInstance Browser => Br;

        public Tab   ActiveTab => new Tab(Br.ActiveTab);
        public Tab   MainTab   => new Tab(Br.ActiveTab);
        public Tab[] AllTabs   => Br.AllTabs.Select(t => new Tab(t)).ToArray();

        /// <summary>Как в ZP: инстанс без живого браузера считается пустым.</summary>
        public bool   IsVoid        => _br == null;
        public string FormTitle     { get; set; } = "";
        public string EmulationLevel => Br.EmulationLevel;

        public bool UseFullMouseEmulation
        {
            get => Br.UseFullMouseEmulation;
            set => Br.UseFullMouseEmulation = value;
        }

        // Свойства эмуляции: у нас за отпечаток отвечает профиль браузера, а не
        // instance, поэтому здесь они хранятся, но ни на что не влияют.
        public bool   UseTrafficMonitoring { get; set; }
        public string BrowserType          { get; set; } = "Chromium";
        public string WebGLPreferences     { get; set; } = "";
        public string TimezoneWorkMode     { get; set; } = "Emulate";

        // ── Вкладки и состояние ───────────────────────────────────────────────

        public Tab  NewTab(string address = "new")   => new Tab(Br.NewTab(address));
        public void CloseAllTabs()                   => Br.CloseAllTabs();
        public void CloseExtraTabs()                 => Br.CloseExtraTabs();
        public void ClearCache(string domainFilter = null, bool storeCookie = false)
            => Br.ClearCache(domainFilter);
        public void ClearCookie(string domainFilter = null) => Br.ClearCookie(domainFilter);

        public void   SaveCookie(string path)   => Br.SaveCookie(path);
        public void   SetCookie(string cookie)  => Br.SetCookie(cookie);
        public string GetCookie(string domain, bool isCookieFormat) => throw new NotSupportedException(
            "Instance.GetCookie: чтение cookie в ZpRuntime не реализовано — используйте SaveCookie");

        public void WaitFieldEmulationDelay() => Br.WaitFieldEmulationDelay();

        public void SetTimezone(int hours, int minutes, string mode = "Emulate")
            => Br.SetTimezone(hours * 60 + minutes, 0);
        public void SetIanaTimezone(string ianaZone, string mode = "Emulate")
            => Br.SetIanaTimezone(ianaZone);

        public void InstallCrxExtension(string path) => Br.InstallCrxExtension(path);

        public void Reload() => throw new NotSupportedException(
            "Instance.Reload: перезагрузка страницы идёт через ActiveTab.Navigate");

        public void Stop() => Br.CloseAllTabs();

        // ── Прокси и запуск ───────────────────────────────────────────────────
        // Браузер поднимает хост (планировщик или свой лаунчер) и передаёт готовый
        // IBrowserInstance, поэтому instance им не управляет.

        public void Launch(string browserType, bool useProfile) => throw new NotSupportedException(
            "Instance.Launch: браузер поднимает хост и передаёт готовый IBrowserInstance");

        public void SetProxy(string proxyString, bool useProxifier = false,
                             bool emulateGeolocation = false, bool emulateTimezone = false,
                             bool emulateWebrtc = false) => throw new NotSupportedException(
            "Instance.SetProxy: прокси задаётся при создании профиля, а не на живом инстансе");

        public string GetProxy() => throw new NotSupportedException(
            "Instance.GetProxy: прокси известен хосту, а не инстансу");

        // ── Поиск на активной вкладке ─────────────────────────────────────────

        public HtmlElement FindElementById(string id)     => ActiveTab.FindElementById(id);
        public HtmlElement FindElementByName(string name) => ActiveTab.FindElementByName(name);

        public HtmlElement FindElementByAttribute(string tags, string attrName, string attrValue,
                                                  string searchKind, int number)
            => ActiveTab.FindElementByAttribute(tags, attrName, attrValue, searchKind, number);

        public HtmlElementCollection FindElementsByAttribute(string tags, string attrName,
                                                             string attrValue, string searchKind)
            => ActiveTab.FindElementsByAttribute(tags, attrName, attrValue, searchKind);

        public void Dispose() { }
    }
}
