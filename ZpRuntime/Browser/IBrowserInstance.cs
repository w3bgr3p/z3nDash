using System.Collections.Generic;
using System.Drawing;

namespace z3nDash.Browser
{
    public interface IBrowserInstance
    {
        // ── Активная вкладка ──────────────────────────────────────────────────
        IBrowserTab        ActiveTab { get; }
        IList<IBrowserTab> AllTabs   { get; }

        // ── Эмуляция ──────────────────────────────────────────────────────────
        bool   UseFullMouseEmulation { get; set; }

        /// <summary>
        /// Копить трафик вкладок. В ZP это свойство инстанса, и перенесённый
        /// z3n7.Traffic включает его в конструкторе — значит включение обязано
        /// начинать сбор, а не просто запоминать флаг.
        /// </summary>
        bool   UseTrafficMonitoring  { get; set; }
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
        /// <summary>
        /// Cookie строкой. isCookieFormat=false — Netscape (табулированный формат
        /// cookies.txt), true — заголовок "name=value; name=value".
        /// </summary>
        string GetCookie(string domain = null, bool isCookieFormat = false);
        void WaitFieldEmulationDelay();
        /// <summary>Размер окна браузера (ZP: Instance.SetWindowSize).</summary>
        void SetWindowSize(int width, int height);
        void InstallCrxExtension(string path);

        // ── Прокси ────────────────────────────────────────────────────────────
        /// <summary>
        /// Прокси, с которым браузер поднят. Меняться на живом браузере не может:
        /// Playwright принимает его только в параметрах запуска. Нужен, чтобы
        /// Instance.SetProxy мог сверить запрошенный прокси с фактическим.
        /// </summary>
        string Proxy { get; }

        /// <summary>
        /// Сменить прокси на живом браузере. Возможно потому, что браузер
        /// смотрит в наш локальный релей, а меняется то, куда ходит релей.
        /// </summary>
        void SetProxy(string proxy);

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

        /// <summary>
        /// Запрос от имени браузера: делит cookie с его контекстом, поэтому уходит
        /// с той же сессией, а выставленные сервером cookie возвращаются в браузер.
        /// Это то, что под ZennoPoster даёт передача CookieContainer профиля.
        /// </summary>
        IBrowserHttpResponse SendFromBrowser(string method, string url, string body,
            string contentType, IDictionary<string, string> headers, int timeoutSec);
    }

    public interface IBrowserProfile
    {
        string UserAgent { get; }
    }

    public interface IBrowserTab
    {
        string    URL          { get; }
        /// <summary>Хост текущей страницы, как ZP-шный Tab.Domain.</summary>
        string    Domain       { get; }
        /// <summary>Домен второго уровня, как ZP-шный Tab.MainDomain.</summary>
        string    MainDomain   { get; }
        /// <summary>ZP отдаёт HWND вкладки. Оконных хендлов у нас нет — см. реализацию.</summary>
        int       Handle       { get; }
        bool      IsBusy       { get; }
        IDocument MainDocument { get; }
        ITouch    Touch        { get; }

        /// <summary>Текущая позиция курсора при полной эмуляции мыши.</summary>
        Point FullEmulationMouseCurrentPosition { get; set; }

        void Navigate(string url, string referer = "");
        void MouseClick(int x, int y, string button, string mouseEvent, bool considerScroll);
        void FullEmulationMouseMove(int toX, int toY);
        void WaitDownloading();
        /// <summary>
        /// Снимок страницы в base64 (ZP: Tab.GetPagePreview). Снимается видимая
        /// часть — то, что было на экране: снимок делается ради разбора, и важно
        /// именно состояние, которое видел шаблон.
        /// </summary>
        string GetPagePreview();
        void Close();
        void KeyEvent(string key, string type, string modifier = "");

        /// <summary>
        /// Набрать текст посимвольно с паузой между нажатиями — каждый
        /// символ со своим keydown/keypress/keyup. Это не то же, что
        /// <see cref="InsertText"/>: там одно событие на весь текст, и поля с
        /// посимвольными обработчиками — маски, автодополнение — его не видят.
        /// </summary>
        void TypeText(string text, int delayMs);

        /// <summary>
        /// Вставить текст в элемент, который сейчас в фокусе, одним событием —
        /// без посимвольного набора. Под ZennoPoster того же добивались через
        /// системный буфер и Ctrl+V, здесь для этого есть прямой примитив.
        /// </summary>
        void InsertText(string text);
        void FullEmulationMouseWheel(int x, int y);

        /// <summary>ZP-совместимая перегрузка: RiseEvent("click", new Rectangle(x,y,1,1), "Left")</summary>
        void RiseEvent(string eventName, Rectangle area, string button);

        /// <summary>
        /// Запросы вкладки, накопленные с момента включения захвата.
        /// Захват не бесплатный, поэтому включается первым обращением, а не всегда.
        /// </summary>
        IList<ITrafficItem> GetTraffic(IEnumerable<string> urlFilters);

        IHeElement            FindElementById(string id);
        IHeElement            FindElementByName(string name);
        IHeElement            FindElementByXPath(string xpath, int index);
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

    /// <summary>Ответ на запрос, отправленный от имени браузера.</summary>
    public interface IBrowserHttpResponse
    {
        int    Status { get; }
        string Reason { get; }
        string Body   { get; }
        IDictionary<string, string> Headers { get; }
    }

    /// <summary>Один запрос из трафика вкладки. Поля соответствуют ZP-шному TrafficItem.</summary>
    public interface ITrafficItem
    {
        string Url             { get; }
        string Method          { get; }
        uint   ResultCode      { get; }
        bool   HasResponse     { get; }
        string RequestHeaders  { get; }
        string RequestQuery    { get; }
        string RequestBody     { get; }
        string ResponseHeaders { get; }
        byte[] ResponseBody    { get; }
        string ResponseContentType { get; }
    }

    public interface IHeElement
    {
        bool      IsVoid    { get; }
        /// <summary>ZP-шный HtmlElement.IsNull. Для нас совпадает с IsVoid.</summary>
        bool      IsNull    { get; }
        string    InnerText { get; }
        string    InnerHtml { get; }
        string    OuterHtml { get; }
        string    TagName   { get; }
        int       Width     { get; }
        int       Height    { get; }
        /// <summary>Координаты элемента в окне браузера (ZP: DisplacementInBrowser).</summary>
        Point     DisplacementInBrowser { get; }

        string    GetAttribute(string attr);
        void      SetAttribute(string attr, string value);
        void      RemoveAttribute(string attr);
        void      RiseEvent(string eventName, string emulationLevel);
        void      SetValue(string value, string mode, bool clear);
        void      Focus();
        void      ScrollIntoView();
        string    GetXPath();
        /// <summary>Скриншот элемента в base64 (ZP: DrawToBitmap).</summary>
        string    DrawToBitmap();

        /// <summary>
        /// Скриншот куска элемента в base64. В ZP это DrawPartAsBitmap и им
        /// снимают QR-код с элемента — см. перенесённый HtmlExtensions.DecodeQr.
        /// Координаты отсчитываются от левого верхнего угла самого элемента.
        /// </summary>
        string    DrawPartToBitmap(int x, int y, int width, int height);

        IHeElement ParentElement { get; }
        IHeElement FindChildByAttribute(string tag, string attr, string pattern, string mode, int index);
        /// <summary>Прямые потомки с указанными тегами (ZP: FindChildrenByTags).</summary>
        IEnumerable<IHeElement> FindChildrenByTags(string tags);
        /// <summary>Потомки: все вложенные при recursive, иначе только прямые (ZP: GetChildren).</summary>
        IEnumerable<IHeElement> GetChildren(bool recursive);
        void      RemoveChild(IHeElement child);
    }
}