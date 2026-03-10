using System.Collections.Generic;

namespace z3n8.Browser
{
    /// <summary>
    /// Минимальный контракт браузерного инстанса.
    /// Реализуется ZpInstance (тонкая обёртка над ZennoPoster Instance)
    /// и PlaywrightInstance (обёртка над IPage).
    /// Extension-методы InstanceExtensions работают через этот интерфейс.
    /// </summary>
    public interface IBrowserInstance
    {
        // ── Активная вкладка ──────────────────────────────────────────────────
        IBrowserTab  ActiveTab              { get; }
        IList<IBrowserTab> AllTabs          { get; }

        // ── Эмуляция ──────────────────────────────────────────────────────────
        bool   UseFullMouseEmulation        { get; set; }
        string EmulationLevel               { get; }       // "none" | "superEmulation"

        // ── Управление браузером ─────────────────────────────────────────────
        IBrowserTab NewTab(string name = "new");
        void CloseAllTabs();
        void ClearCache(string domain = null);
        void ClearCookie(string domain = null);
        void SaveCookie(string path);
        void WaitFieldEmulationDelay();

        // ── Временная зона ────────────────────────────────────────────────────
        void SetTimezone(int offsetMinutes, int unused);
        void SetIanaTimezone(string ianaName);

        // ── Поиск элементов (делегируется в ActiveTab, но нужен на уровне instance) ──
        IHeElement FindElementById(string id);
        IHeElement FindElementByName(string name);
        IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index);
        IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode);
    }

    /// <summary>
    /// Контракт вкладки браузера.
    /// </summary>
    public interface IBrowserTab
    {
        string URL    { get; }
        bool IsBusy   { get; }

        void Navigate(string url, string referer = "");
        void WaitDownloading();
        void KeyEvent(string key, string type, string modifier = "");
        void FullEmulationMouseWheel(int x, int y);

        // Поиск элементов через вкладку (нужен для ActiveTab.FindElementBy*)
        IHeElement FindElementById(string id);
        IHeElement FindElementByName(string name);
        IHeElement FindElementByAttribute(string tag, string attr, string pattern, string mode, int index);
        IEnumerable<IHeElement> FindElementsByAttribute(string tag, string attr, string pattern, string mode);

        // EvaluateScript используется в F5() через MainDocument
        IDocument MainDocument { get; }
    }

    /// <summary>
    /// Контракт DOM-документа (используется только для EvaluateScript).
    /// </summary>
    public interface IDocument
    {
        string EvaluateScript(string js);
    }

    /// <summary>
    /// Контракт HTML-элемента.
    /// Заменяет ZennoPoster HtmlElement в extension-методах.
    /// </summary>
    public interface IHeElement
    {
        bool   IsVoid                           { get; }
        string InnerText                        { get; }
        string GetAttribute(string attr);
        void   RiseEvent(string eventName, string emulationLevel);
        void   SetValue(string value, string mode, bool clear);
        string GetXPath();
        IHeElement ParentElement                { get; }
        void   RemoveChild(IHeElement child);
    }
}