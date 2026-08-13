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

namespace ZennoLab.CommandCenter.Classes
{
    /// <summary>
    /// ZP-шные настройки запуска встроенного браузера. Запуском у нас управляет
    /// хост, поэтому объект только переносит поля до Instance.Launch, который
    /// отказывает явно.
    /// </summary>
    public class BuiltInBrowserLaunchSettings
    {
        public InterfacesLibrary.Enums.Browser.BrowserType BrowserType { get; set; }
        public string CachePath            { get; set; }
        public bool   ConvertProfileFolder { get; set; }
        public bool   UseProfile           { get; set; }
    }

    public static class BrowserLaunchSettingsFactory
    {
        public static BuiltInBrowserLaunchSettings Create(
            InterfacesLibrary.Enums.Browser.BrowserType browserType)
            => new BuiltInBrowserLaunchSettings { BrowserType = browserType };
    }

    /// <summary>ZP-шные настройки сбора трафика.</summary>
    public sealed class GetTrafficSettings
    {
        public System.Collections.Generic.IEnumerable<string> UrlFilters    { get; set; }
        public System.Collections.Generic.IEnumerable<string> HeaderFilters { get; set; }
        public System.Collections.Generic.IEnumerable<string> BodyFilters   { get; set; }
        public bool GatherAllTraffic { get; set; }
    }
}

namespace ZennoLab.CommandCenter
{
    /// <summary>
    /// ZP-шный статик ZennoPoster. Здесь два разных сорта членов, и путать их
    /// нельзя: HTTP работает по-настоящему, а управление задачами ZP-сервера в
    /// standalone смысла не имеет и явно отказывает.
    /// </summary>
    public static class ZennoPoster
    {
        // ── HTTP: реальная реализация ─────────────────────────────────────────

        /// <summary>
        /// Активный браузер для запросов «от имени браузера». Проставляется хостом,
        /// как Emulator.Attach — статик сам браузер не поднимает.
        /// </summary>
        public static void AttachBrowser(IBrowserInstance browser) => HTTP.Browser = browser;

        public static class HTTP
        {
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Net.Http.HttpClient> _clients = new();

            internal static IBrowserInstance Browser { get; set; }

            public static string Request(
                InterfacesLibrary.Enums.Http.HttpMethod method,
                string url, string body, string contentType,
                string proxy, string encoding,
                InterfacesLibrary.Enums.Http.ResponceType responseType,
                int timeout, string cookies, string userAgent,
                bool followRedirects, int maxRedirects,
                string[] headers, string cert, bool ignoreErrors,
                bool sendBody, object cookieContainer)
            {
                // Под ZennoPoster переданный CookieContainer профиля означает
                // «уйти с сессией браузера». Воспроизводим это через
                // IBrowserContext.APIRequest, который делит cookie с браузером.
                // Молча слать без сессии нельзя: вернётся 401 там, где ожидались
                // данные, и без всякого сигнала — поэтому явный отказ.
                if (cookieContainer != null)
                {
                    var br = Browser ?? throw new NotSupportedException(
                        "ZennoPoster.HTTP.Request: передан cookieContainer, то есть запрос должен " +
                        "уйти с сессией браузера, но браузер не привязан. Вызовите " +
                        "ZennoPoster.AttachBrowser(instance.Browser) или не передавайте cookieContainer.");

                    var hdrs = new Dictionary<string, string>();
                    foreach (var h in headers ?? Array.Empty<string>())
                    {
                        int idx = h.IndexOf(':');
                        if (idx > 0) hdrs[h[..idx].Trim()] = h[(idx + 1)..].Trim();
                    }
                    if (!string.IsNullOrEmpty(userAgent)) hdrs["User-Agent"] = userAgent;

                    var br_resp = br.SendFromBrowser(method.ToString().ToUpper(), url,
                        sendBody ? body : null, contentType, hdrs, timeout);

                    if (!ignoreErrors && (br_resp.Status < 200 || br_resp.Status >= 300))
                        throw new Exception($"{br_resp.Status}: {br_resp.Body}");

                    string brHead = $"HTTP {br_resp.Status} {br_resp.Reason}\r\n"
                        + string.Join("\r\n", br_resp.Headers.Select(h => $"{h.Key}: {h.Value}"));

                    return responseType switch
                    {
                        InterfacesLibrary.Enums.Http.ResponceType.HeaderOnly    => brHead,
                        InterfacesLibrary.Enums.Http.ResponceType.HeaderAndBody => brHead + "\r\n\r\n" + br_resp.Body,
                        _                                                       => br_resp.Body,
                    };
                }

                var client = _clients.GetOrAdd($"{proxy}|{followRedirects}", _ => Build(proxy, followRedirects));

                var req = new System.Net.Http.HttpRequestMessage(
                    new System.Net.Http.HttpMethod(method.ToString().ToUpper()), url);

                if (sendBody && !string.IsNullOrEmpty(body))
                    req.Content = new System.Net.Http.StringContent(
                        body, System.Text.Encoding.UTF8,
                        string.IsNullOrEmpty(contentType) ? "application/x-www-form-urlencoded" : contentType);

                if (!string.IsNullOrEmpty(userAgent)) req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                if (!string.IsNullOrEmpty(cookies))   req.Headers.TryAddWithoutValidation("Cookie", cookies);

                foreach (var h in headers ?? Array.Empty<string>())
                {
                    int i = h.IndexOf(':');
                    if (i <= 0) continue;
                    req.Headers.TryAddWithoutValidation(h[..i].Trim(), h[(i + 1)..].Trim());
                }

                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(
                        TimeSpan.FromSeconds(timeout <= 0 ? 30 : timeout));
                    var resp = client.Send(req, cts.Token);
                    string respBody = new System.IO.StreamReader(
                        resp.Content.ReadAsStream(), System.Text.Encoding.UTF8).ReadToEnd();

                    if (!ignoreErrors && !resp.IsSuccessStatusCode)
                        throw new Exception($"{(int)resp.StatusCode}: {respBody}");

                    string head = $"HTTP/{resp.Version} {(int)resp.StatusCode} {resp.ReasonPhrase}\r\n"
                        + string.Join("\r\n", resp.Headers.Concat(resp.Content.Headers)
                            .Select(h => $"{h.Key}: {string.Join("; ", h.Value)}"));

                    return responseType switch
                    {
                        InterfacesLibrary.Enums.Http.ResponceType.HeaderOnly    => head,
                        InterfacesLibrary.Enums.Http.ResponceType.HeaderAndBody => head + "\r\n\r\n" + respBody,
                        _                                                       => respBody,
                    };
                }
                catch when (ignoreErrors) { return ""; }
            }

            private static System.Net.Http.HttpClient Build(string proxy, bool followRedirects)
            {
                var h = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = followRedirects };
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    // ZP допускает "user:pass@host:port" и "host:port", со схемой и без.
                    string p = proxy.Contains("//") ? proxy.Split('/')[2] : proxy;
                    var wp = new System.Net.WebProxy();
                    if (p.Contains('@'))
                    {
                        var parts = p.Split('@');
                        var creds = parts[0].Split(':');
                        wp.Address     = new Uri("http://" + parts[1]);
                        wp.Credentials = new System.Net.NetworkCredential(creds[0], creds.Length > 1 ? creds[1] : "");
                    }
                    else wp.Address = new Uri("http://" + p);
                    h.Proxy    = wp;
                    h.UseProxy = true;
                }
                return new System.Net.Http.HttpClient(h);
            }
        }

        // ── Db: реальная реализация ───────────────────────────────────────────
        // ZP-шный ZennoPoster.Db.ExecuteQuery. Из переносимого кода его зовёт
        // только FastDb, и только через ODBC-DSN к SQLite, поэтому реализован
        // именно этот провайдер; остальные отказывают явно.

        public static class Db
        {
            public static string ExecuteQuery(
                string query, string[] parameters,
                InterfacesLibrary.Enums.Db.DbProvider provider,
                string connectionString,
                string columnDelimiter, string rowDelimiter,
                bool useTransaction)
            {
                if (provider != InterfacesLibrary.Enums.Db.DbProvider.Odbc)
                    throw new NotSupportedException(
                        $"ZennoPoster.Db.ExecuteQuery: провайдер {provider} в ZpRuntime не реализован — " +
                        "поддержан только Odbc, через него ходит FastDb.");

                using var conn = new System.Data.Odbc.OdbcConnection(connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = query;

                // Отличать SELECT по тексту запроса приходится и здесь: ODBC не
                // даёт узнать заранее, вернёт ли команда набор строк.
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        query.TrimStart(), @"^\s*(SELECT|PRAGMA|WITH)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return cmd.ExecuteNonQuery().ToString();

                using var reader = cmd.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString();
                    rows.Add(string.Join(columnDelimiter, cells));
                }
                return string.Join(rowDelimiter, rows);
            }
        }

        public static string HttpGet(string url, string proxy = "", string encoding = "UTF-8",
            InterfacesLibrary.Enums.Http.ResponceType respType = InterfacesLibrary.Enums.Http.ResponceType.BodyOnly,
            int timeout = 30000, string cookies = "", string userAgent = "", bool useRedirect = true,
            int maxRedirectCount = 5, string[] additionalHeaders = null, string downloadPath = "",
            bool useOriginalUrl = false)
            => HTTP.Request(InterfacesLibrary.Enums.Http.HttpMethod.Get, url, "", "", proxy, encoding,
                respType, timeout / 1000, cookies, userAgent, useRedirect, maxRedirectCount,
                additionalHeaders, "", false, false, null);

        public static string HttpPost(string url, string content, string contentPostingType = "application/x-www-form-urlencoded",
            string proxy = "", string encoding = "UTF-8",
            InterfacesLibrary.Enums.Http.ResponceType respType = InterfacesLibrary.Enums.Http.ResponceType.BodyOnly,
            int timeout = 30000, string cookies = "", string userAgent = "", bool useRedirect = true,
            int maxRedirectCount = 5, string[] additionalHeaders = null, string downloadPath = "",
            bool useOriginalUrl = false)
            => HTTP.Request(InterfacesLibrary.Enums.Http.HttpMethod.Post, url, content, contentPostingType,
                proxy, encoding, respType, timeout / 1000, cookies, userAgent, useRedirect,
                maxRedirectCount, additionalHeaders, "", false, true, null);

        // ── Управление задачами ZP-сервера ────────────────────────────────────
        // Планировщик здесь свой (DevDeck), очереди ZennoPoster нет. Отказываем
        // явно: тихая заглушка увела бы вызывающий код на неверных данных.

        private static Exception NoServer(string member) => new NotSupportedException(
            $"ZennoPoster.{member}: очереди задач ZennoPoster в standalone нет. " +
            "Управление задачами — на стороне планировщика DevDeck.");

        public static IEnumerable<string> TasksList => throw NoServer(nameof(TasksList));
        public static int[] AllInstances            => throw NoServer(nameof(AllInstances));

        public static void   AddTask(string task)                  => throw NoServer(nameof(AddTask));
        public static void   RemoveTask(Guid id)                   => throw NoServer(nameof(RemoveTask));
        public static void   StartTask(Guid id)                    => throw NoServer(nameof(StartTask));
        public static void   StartTask(string name)                => throw NoServer(nameof(StartTask));
        public static void   StopTask(Guid id)                     => throw NoServer(nameof(StopTask));
        public static void   StopTask(string name)                 => throw NoServer(nameof(StopTask));
        public static void   InterruptTask(Guid id)                => throw NoServer(nameof(InterruptTask));
        public static void   InterruptTask(string name)            => throw NoServer(nameof(InterruptTask));
        public static void   AddTries(Guid id, int count)          => throw NoServer(nameof(AddTries));
        public static void   AddTries(string name, int count)      => throw NoServer(nameof(AddTries));
        public static void   SetTries(Guid id, int count)          => throw NoServer(nameof(SetTries));
        public static void   SetTries(string name, int count)      => throw NoServer(nameof(SetTries));
        public static void   SetMaxThreads(Guid id, int count)     => throw NoServer(nameof(SetMaxThreads));
        public static void   SetMaxThreads(string name, int count) => throw NoServer(nameof(SetMaxThreads));
        public static void   ClearSuccess(Guid id)                 => throw NoServer(nameof(ClearSuccess));
        public static void   ClearSuccess(string name)             => throw NoServer(nameof(ClearSuccess));
        public static void   ClearFails(Guid id)                   => throw NoServer(nameof(ClearFails));
        public static void   ClearFails(string name)               => throw NoServer(nameof(ClearFails));
        public static string ExportInputSettings(Guid id)          => throw NoServer(nameof(ExportInputSettings));
        public static void   ImportInputSettings(Guid id, string source) => throw NoServer(nameof(ImportInputSettings));
        public static string GetTaskInfo(Guid id)                  => throw NoServer(nameof(GetTaskInfo));
        public static string GetTaskInfo(string projectPath)       => throw NoServer(nameof(GetTaskInfo));
        public static int    GetThreadsCount()                     => throw NoServer(nameof(GetThreadsCount));
        public static int    GetThreadsCount(Guid id)              => throw NoServer(nameof(GetThreadsCount));
        public static int    GetThreadsCount(string name)          => throw NoServer(nameof(GetThreadsCount));
        public static void   SetExecutionSettings(Guid id, string settings) => throw NoServer(nameof(SetExecutionSettings));
        public static void   SetSchedulerSettings(Guid id, string settings) => throw NoServer(nameof(SetSchedulerSettings));

        // ── Обработка изображений ─────────────────────────────────────────────
        // Перегрузки FromScreenshot адресуют инстанс по порту ZP — такой адресации
        // у нас нет. FromFile реализуемы, но требуют System.Drawing, недоступного
        // в net10.0 без Windows, поэтому пока тоже отказ.

        private static Exception NoImaging(string member) => new NotSupportedException(
            $"ZennoPoster.{member}: обработка изображений в ZpRuntime не реализована.");

        public static void ImageProcessingWaterMarkTextFromScreenshot(int instancePort, string savePath,
            string imposition, string location, string text, int transparency, string style,
            int offsetLeft, int offsetTop, int quality, string exif)
            => throw NoImaging(nameof(ImageProcessingWaterMarkTextFromScreenshot));

        public static void ImageProcessingCropFromScreenshot(int instancePort, string savePath,
            int leftBorder, int topBorder, int cropWidth, int cropHeight, string units, int quality, string exif)
            => throw NoImaging(nameof(ImageProcessingCropFromScreenshot));

        public static void ImageProcessingResizeFromFile(string filePath, string savePath,
            int width, int height, string units, bool keep, bool notIncImage, int quality, string exif)
            => throw NoImaging(nameof(ImageProcessingResizeFromFile));
    }

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

        /// <summary>Трёхаргументная перегрузка SDK — её зовёт перенесённый HeSet.</summary>
        public void SetValue(string value, string emulation, bool append)
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

    /// <summary>ZP-шный TrafficItem. Обёртка над <see cref="ITrafficItem"/>.</summary>
    public sealed class TrafficItem
    {
        private readonly ITrafficItem _t;
        internal TrafficItem(ITrafficItem t) => _t = t;

        public string Url             => _t.Url;
        public string Method          => _t.Method;
        public uint   ResultCode      => _t.ResultCode;
        public bool   HasResponse     => _t.HasResponse;
        public bool   IsBlocked       => false;
        public string RequestHeaders  => _t.RequestHeaders;
        public string RequestQuery    => _t.RequestQuery;
        public string RequestBody     => _t.RequestBody;
        public string ResponseHeaders => _t.ResponseHeaders;
        public byte[] ResponseBody    => _t.ResponseBody;
        public string ResponseContentType => _t.ResponseContentType;

        public override string ToString() => $"{Method} {Url} → {ResultCode}";

        /// <summary>Подписка отдаёт ответ уже завершённым, ждать нечего.</summary>
        public void WaitResponse(int timeout, int delayBetweenChecks) { }
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

        /// <summary>
        /// Вставка текста в элемент под фокусом. В SDK ZennoPoster такого члена
        /// нет — там то же делали через системный буфер и Ctrl+V. Здесь метод
        /// добавлен, чтобы перенесённый CtrlV обходился без буфера.
        /// </summary>
        public void InsertText(string text) => T.InsertText(text);

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

        // ── Трафик ────────────────────────────────────────────────────────────

        /// <summary>Перегрузка с одним фильтром — её зовёт перенесённый Traffic.</summary>
        public IEnumerable<TrafficItem> GetTraffic(IEnumerable<string> urlFilters)
            => T.GetTraffic(urlFilters).Select(x => new TrafficItem(x));

        public IEnumerable<TrafficItem> GetTraffic(IEnumerable<string> urlFilters,
                                                   IEnumerable<string> headerFilters,
                                                   IEnumerable<string> bodyFilters)
            => T.GetTraffic(urlFilters).Select(x => new TrafficItem(x));

        public IEnumerable<TrafficItem> GetTraffic(Classes.GetTrafficSettings settings)
            => GetTraffic(settings?.UrlFilters, settings?.HeaderFilters, settings?.BodyFilters);
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
        public InterfacesLibrary.Enums.Browser.TimezoneMode TimezoneWorkMode { get; set; }
            = InterfacesLibrary.Enums.Browser.TimezoneMode.Emulate;

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

        // Сигнатура как в SDK: BrowserType, а не string — её ждёт перенесённый
        // InstanceExtencions.UpFromFolder/UpEmpty.
        public void Launch(InterfacesLibrary.Enums.Browser.BrowserType browserType, bool useProfile)
            => throw new NotSupportedException(
                "Instance.Launch: браузер поднимает хост и передаёт готовый IBrowserInstance");

        public void Launch(Classes.BuiltInBrowserLaunchSettings settings)
            => throw new NotSupportedException(
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
