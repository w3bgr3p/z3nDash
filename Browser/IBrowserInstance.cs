using System.Collections.Generic;
using System.Drawing;

namespace z3nIO.Browser
{
    public interface IBrowserInstance
    {
        // ── Активная вкладка ──────────────────────────────────────────────────
        IBrowserTab        ActiveTab { get; }
        IList<IBrowserTab> AllTabs   { get; }

        // ── Эмуляция ──────────────────────────────────────────────────────────
        bool   UseFullMouseEmulation { get; set; }
        string EmulationLevel        { get; }   // "none" | "superEmulation"

        // ── Профиль ───────────────────────────────────────────────────────────
        IBrowserProfile Profile { get; }

        // ── Управление браузером ─────────────────────────────────────────────
        IBrowserTab NewTab(string name = "new");
        void CloseAllTabs();
        void CloseExtraTabs();
        void ClearCache(string domain = null);
        void ClearCookie(string domain = null);
        void SaveCookie(string path);
        void SetCookie(string cookieString);
        void WaitFieldEmulationDelay();
        void InstallCrxExtension(string path);

        // ── Временная зона ────────────────────────────────────────────────────
        void SetTimezone(int offsetMinutes, int unused);
        void SetIanaTimezone(string ianaName);

        // ── DOM-поиск ─────────────────────────────────────────────────────────
        IHeElement            FindElementById(string id);
        IHeElement            FindElementByName(string name);
        IHeElement            FindElementByAttribute(string tag, string attr, string pattern, string mode, int index);
        IList<IHeElement>     FindElementsByAttribute(string tag, string attr, string pattern, string mode);

        // ── Canvas/viewport ───────────────────────────────────────────────────
        /// <summary>[x, y, w, h] — квадратная область вокруг центра страницы.</summary>
        int[] CenterArea(int w, int h = 0);
        /// <summary>[x, y] — центр видимой области страницы.</summary>
        int[] GetCenter();

        // ── Image matching (OpenCvSharp matchTemplate) ────────────────────────
        /// <summary>Возвращает [x, y] найденного шаблона или null.</summary>
        int[] FindImg(string base64Template, int[] area, float threshold = 0.97f);
        /// <summary>Ищет несколько шаблонов в одной области. Возвращает словарь name→coords найденных.</summary>
        Dictionary<string, int[]> FindMultipleInScreenshot(
            Dictionary<string, string> templates, int[] area, float threshold = 0.85f);
        /// <summary>Каждый шаблон ищется в своей area. Возвращает словарь name→coords найденных.</summary>
        Dictionary<string, int[]> FindMultipleInMultipleAreas(
            Dictionary<string, (string template, int[] area)> templatesWithAreas, float threshold = 0.85f);

        // ── Canvas actions ────────────────────────────────────────────────────
        void   JsClick(int x, int y);
        void   TapCenter();
        void   CenterMouse();
        /// <summary>Свайп от центра в случайном направлении на distance px, ограниченный bounds [x,y,w,h].</summary>
        void   SwipeFromCenter(int distance, object unused, int[] bounds);
        /// <summary>Найти шаблон в area и тапнуть. Бросает если не найден.</summary>
        void   TapImg(string base64Template, int[] area, float threshold = 0.97f);
        /// <summary>Найти шаблон в area и свайпнуть к центру. Бросает если не найден.</summary>
        void   SwipeImgToCenter(string base64Template, int[] area, float threshold = 0.97f);
        /// <summary>Найти шаблон в area и кликнуть. Бросает если не найден.</summary>
        void   ClickImg(string base64Template, int[] area, float threshold = 0.97f);

        // ── HTTP ──────────────────────────────────────────────────────────────
        void SaveRequestHeadersToVariable(IBrowserInstance instance, string url, bool includeBody);
    }

    public interface IBrowserProfile
    {
        string UserAgent { get; }
    }

    public interface IBrowserTab
    {
        string    URL          { get; }
        bool      IsBusy       { get; }
        IDocument MainDocument { get; }
        ITouch    Touch        { get; }

        void Navigate(string url, string referer = "");
        void WaitDownloading();
        void Close();
        void KeyEvent(string key, string type, string modifier = "");
        void FullEmulationMouseWheel(int x, int y);

        /// <summary>ZP-совместимая перегрузка: RiseEvent("click", new Rectangle(x,y,1,1), "Left")</summary>
        void RiseEvent(string eventName, Rectangle area, string button);

        IHeElement            FindElementById(string id);
        IHeElement            FindElementByName(string name);
        IHeElement            FindElementByAttribute(string tag, string attr, string pattern, string mode, int index);
        IList<IHeElement>     FindElementsByAttribute(string tag, string attr, string pattern, string mode);
    }

    public interface ITouch
    {
        void Touch(int x, int y);
        void SwipeBetween(int x1, int y1, int x2, int y2);
    }

    public interface IDocument
    {
        string EvaluateScript(string js);
    }

    public interface IHeElement
    {
        bool      IsVoid    { get; }
        string    InnerText { get; }
        string    GetAttribute(string attr);
        void      RiseEvent(string eventName, string emulationLevel);
        void      SetValue(string value, string mode, bool clear);
        string    GetXPath();
        IHeElement ParentElement { get; }
        void      RemoveChild(IHeElement child);
    }
}