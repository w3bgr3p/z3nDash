using System.Text;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using z3n8;



public class TrafficMonitor
{
    private readonly IBrowserContext? _context;
    private readonly IPage? _page;
    private readonly string _logHost = "http://localhost:10993/http-log";
    private static readonly HttpClient _logClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    private bool _consoleLog = false;
    public string? FilterByKeyword { get; set; }
    public List<string> IgnoreExtensions { get; set; } = new() { ".js", ".json", ".css", ".woff2", ".png", ".jpg", ".svg", ".ico" };

    public TrafficMonitor(IPage page, LogsConfig  config = null, bool console = false)
    {
        _page = page;
        _logHost = config?.TrafficHost ?? "http://localhost:10993/http-log";
        _consoleLog = console;
    }
    
    public TrafficMonitor(IBrowserContext context, LogsConfig  config = null, bool console = false)
    {
        _context = context;
        _logHost = config?.TrafficHost ?? "http://localhost:10993/http-log";
    }

    /// <summary>
    /// Включает прослушивание трафика
    /// </summary>
    public void Start()
    {
        if (_page != null)
        {
            _page.Response += HandleResponse;
            Console.WriteLine("[Monitor] Логгер трафика запущен для Page.");
        }
        else if (_context != null)
        {
            _context.Response += HandleResponse;
            Console.WriteLine("[Monitor] Логгер трафика запущен для Context.");
        }
    }

    /// <summary>
    /// Выключает прослушивание
    /// </summary>
    public void Stop()
    {
        if (_page != null)
        {
            _page.Response -= HandleResponse;
            Console.WriteLine("[Monitor] Логгер трафика остановлен для Page.");
        }
        else if (_context != null)
        {
            _context.Response -= HandleResponse;
            Console.WriteLine("[Monitor] Логгер трафика остановлен для Context.");
        }
    }
    public async Task<bool> Filter(IResponse response) 
    {
        var url = response.Url;
        string urlPath = url.Split('?')[0];
        if (IgnoreExtensions.Any(ext => urlPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return false;
        var type = response.Request.ResourceType;
        if (url.StartsWith("chrome-extension://") && type != "document") 
            return false;
        if (!string.IsNullOrEmpty(FilterByKeyword) && !response.Url.Contains(FilterByKeyword))
            return false;
          
        return true;
    }
    public class TrafficElement
    {
        public string Type { get; set; }
        public string Method { get; set; }
        public string Url { get; set; }
        public string RequestBody { get; set; }
        public string ResponseBody { get; set; }
        public string RequestHeaders { get; set; }
        public string ResponseHeaders { get; set; }
        public int Status { get; set; }
    }
    private static object ExtractHeaders(IRequest req, IResponse res)
    {
        var reqHeaders = req.Headers
            .Select(h => $"{h.Key}: {h.Value}")
            .ToArray();

        var resHeaders = res.Headers
            .Select(h => $"{h.Key}: {h.Value}")
            .ToArray();

        return new
        {
            request = reqHeaders,
            response = resHeaders
        };
    }
    private async void HandleResponse(object? sender, IResponse response)
    {
        try
        {
            var req = response.Request;
            var type = req.ResourceType;

            //if (!await Filter(response)) return;
            if (type != "xhr" && type != "fetch") return;

            string resBody = null;
            string reqBody = req.PostData;
            var startTime = DateTime.UtcNow;
            var endTime = DateTime.UtcNow;

            try
            {
                if (response.Status != 204 && response.Status != 205)
                    resBody = await response.TextAsync();
            }
            catch (Exception ex)
            {
                resBody = $"[Read Error: {ex.Message}]";
            }

            var allHeaders = ExtractHeaders(req, response);

            var httpLog = new
            {
                timestamp = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                method = req.Method,
                url = response.Url,
                statusCode = response.Status,
                durationMs = (int)(endTime - startTime).TotalMilliseconds,
                request = new
                {
                    headers = allHeaders.GetType().GetProperty("request")!.GetValue(allHeaders),
                    body = reqBody,
                    proxy = "Playwright"
                },
                response = new
                {
                    body = resBody
                },
                machine = Environment.MachineName,
                project = "test"
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var content = new StringContent(
                        JsonConvert.SerializeObject(httpLog),
                        Encoding.UTF8,
                        "application/json"
                    );

                    await _logClient.PostAsync(_logHost, content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            });

            if (_consoleLog)
            {
                Console.ForegroundColor = GetColorByStatus(response.Status);
                Console.WriteLine($"[{response.Status}] | {type.PadRight(6)} | {response.Url} \n {resBody}");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playwright Monitor ERROR] {ex.Message}");
        }
    }
    private ConsoleColor GetColorByStatus(int status) => status switch
    {
        >= 200 and < 300 => ConsoleColor.Green,
        >= 300 and < 400 => ConsoleColor.Cyan,
        >= 400 and < 500 => ConsoleColor.Yellow,
        _ => ConsoleColor.Red
    };
}





namespace z3n8
{
    /// <summary>
    /// Работа с трафиком браузера - поиск и извлечение данных из HTTP запросов/ответов
    /// </summary>
    public class Traffic
    {
        private readonly IPage? _page;
        private readonly IBrowserContext? _context;
        private readonly Logger? _logger;
        
        // Мониторинг HTTP трафика (отдельный эндпоинт)
        private readonly string _trafficHost;
        private static readonly HttpClient _trafficClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly bool _consoleLog;
        
        private readonly List<TrafficElement> _cache = new();
        private readonly object _cacheLock = new object();
        
        private const int CACHE_LIFETIME_SECONDS = 2;
        private DateTime _lastCacheCheck = DateTime.MinValue;

        public Traffic(IPage page, Logger logger = null,  string trafficHost = null, bool consoleLog = false)
        {
            _page = page;
            _logger = logger;
            _trafficHost = trafficHost ?? "http://localhost:10993/http-log";
            _consoleLog = consoleLog;
            
            _page.Response += HandleResponse;
             _logger?.Send("Traffic monitoring started for Page");
        }
        
        public Traffic(IBrowserContext context, Logger logger = null,  string trafficHost = null, bool consoleLog = false)
        {
            _context = context;
            _logger = logger;
            _trafficHost = trafficHost ?? "http://localhost:10993/http-log";
            _consoleLog = consoleLog;
            
            _context.Response += HandleResponse;
             _logger?.Send("Traffic monitoring started for Context");
        }

        /// <summary>
        /// Найти первый элемент трафика по URL (с ожиданием)
        /// </summary>
        public async Task<TrafficElement> FindTrafficElement(string url, bool strict = false, 
            int timeoutSeconds = 15, int retryDelaySeconds = 1)
        {
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            int attemptNumber = 0;

            while (DateTime.Now - startTime < timeout)
            {
                attemptNumber++;
                 _logger?.Send($"Attempt #{attemptNumber} searching URL: {url}");

                var element = SearchInCache(url, strict);
                if (element != null)
                {
                     _logger?.Send($"✓ Found traffic for: {url}");
                    return element;
                }

                await Task.Delay(1000 * retryDelaySeconds);
            }

            throw new TimeoutException($"Traffic element not found for URL '{url}' within {timeoutSeconds} seconds");
        }

        /// <summary>
        /// Найти все элементы трафика по URL (без ожидания)
        /// </summary>
        public List<TrafficElement> FindAllTrafficElements(string url, bool strict = false)
        {
            CleanOldCache();
            
            var matches = new List<TrafficElement>();
            lock (_cacheLock)
            {
                foreach (var element in _cache)
                {
                    bool isMatch = strict ? element.Url == url : element.Url.Contains(url);
                    if (isMatch) matches.Add(element);
                }
            }

             _logger?.Send($"Found {matches.Count} traffic elements for: {url}");
            return matches;
        }

        /// <summary>
        /// Получить весь текущий трафик
        /// </summary>
        public List<TrafficElement> GetAllTraffic()
        {
            CleanOldCache();
            lock (_cacheLock)
            {
                return new List<TrafficElement>(_cache);
            }
        }

        /// <summary>
        /// Получить тело ответа (response body) по URL
        /// </summary>
        public async Task<string> GetResponseBody(string url, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.ResponseBody;
        }

        /// <summary>
        /// Получить тело запроса (request body) по URL
        /// </summary>
        public async Task<string> GetRequestBody(string url, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.RequestBody;
        }

        /// <summary>
        /// Получить заголовок из запроса
        /// </summary>
        public async Task<string> GetRequestHeader(string url, string headerName, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.GetRequestHeader(headerName);
        }

        /// <summary>
        /// Получить заголовок из ответа
        /// </summary>
        public async Task<string> GetResponseHeader(string url, string headerName, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.GetResponseHeader(headerName);
        }

        /// <summary>
        /// Получить все заголовки запроса
        /// </summary>
        public async Task<Dictionary<string, string>> GetAllRequestHeaders(string url, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.GetAllRequestHeaders();
        }

        /// <summary>
        /// Получить все заголовки ответа
        /// </summary>
        public async Task<Dictionary<string, string>> GetAllResponseHeaders(string url, bool strict = false, int timeoutSeconds = 15)
        {
            var element = await FindTrafficElement(url, strict, timeoutSeconds);
            return element.GetAllResponseHeaders();
        }

        /// <summary>
        /// Получить структуру API (уникальные эндпоинты с примерами)
        /// </summary>
        public string GetApiStructure(string urlFilter)
        {
            var all = FindAllTrafficElements(urlFilter);
            var uniqueEndpoints = new Dictionary<string, JObject>();

            foreach (var el in all)
            {
                string key = $"{el.Method}:{el.Url}";
                if (uniqueEndpoints.ContainsKey(key)) continue;

                var item = new JObject
                {
                    ["method"] = el.Method,
                    ["url"] = el.Url,
                    ["statusCode"] = el.StatusCode
                };

                if (!string.IsNullOrEmpty(el.RequestBody))
                {
                    try { item["requestBody"] = JToken.Parse(el.RequestBody); }
                    catch { item["requestBody"] = el.RequestBody; }
                }

                if (!string.IsNullOrEmpty(el.ResponseBody))
                {
                    try { item["responseBody"] = JToken.Parse(el.ResponseBody); }
                    catch { item["responseBody"] = el.ResponseBody; }
                }

                uniqueEndpoints[key] = item;
            }

            var snapshot = new JObject
            {
                ["totalEndpoints"] = uniqueEndpoints.Count,
                ["endpoints"] = new JArray(uniqueEndpoints.Values)
            };

            return JsonConvert.SerializeObject(snapshot, Formatting.Indented);
        }

        /// <summary>
        /// Перезагрузить страницу и очистить кэш
        /// </summary>
        public async Task<Traffic> ReloadPage(int delaySeconds = 1)
        {
            if (_page != null)
            {
                await _page.ReloadAsync();
            }
            
            await Task.Delay(1000 * delaySeconds);
            ClearCache();
            
            return this;
        }

        /// <summary>
        /// Очистить кэш трафика
        /// </summary>
        public Traffic ClearCache()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
            }
             _logger?.Send("Cache cleared");
            return this;
        }

        /// <summary>
        /// Остановить мониторинг трафика
        /// </summary>
        public void Stop()
        {
            if (_page != null)
            {
                _page.Response -= HandleResponse;
                 _logger?.Send("Traffic monitoring stopped for Page");
            }
            else if (_context != null)
            {
                _context.Response -= HandleResponse;
                 _logger?.Send("Traffic monitoring stopped for Context");
            }
        }

        private TrafficElement SearchInCache(string url, bool strict)
        {
            CleanOldCache();
            
            lock (_cacheLock)
            {
                foreach (var element in _cache)
                {
                    bool isMatch = strict ? element.Url == url : element.Url.Contains(url);
                    if (isMatch) return element;
                }
            }
            return null;
        }

        private void CleanOldCache()
        {
            var now = DateTime.Now;
            if ((now - _lastCacheCheck).TotalSeconds < CACHE_LIFETIME_SECONDS) return;
            
            lock (_cacheLock)
            {
                _cache.RemoveAll(e => (now - e.Timestamp).TotalSeconds > CACHE_LIFETIME_SECONDS);
                _lastCacheCheck = now;
            }
        }

        private async void HandleResponse(object? sender, IResponse response)
        {
            try
            {
                var req = response.Request;
                var type = req.ResourceType;

                if (type != "xhr" && type != "fetch") return;

                string resBody = string.Empty;
                string reqBody = req.PostData ?? string.Empty;
                var startTime = DateTime.UtcNow;

                try
                {
                    if (response.Status != 204 && response.Status != 205)
                        resBody = await response.TextAsync();
                }
                catch (Exception ex)
                {
                    resBody = $"[Read Error: {ex.Message}]";
                }

                var endTime = DateTime.UtcNow;

                var element = new TrafficElement
                {
                    Timestamp = DateTime.Now,
                    Method = req.Method,
                    Url = response.Url,
                    StatusCode = response.Status,
                    RequestHeaders = string.Join("\n", req.Headers.Select(h => $"{h.Key}: {h.Value}")),
                    ResponseHeaders = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {h.Value}")),
                    RequestBody = reqBody,
                    ResponseBody = resBody
                };

                lock (_cacheLock)
                {
                    _cache.Add(element);
                }

                // Logger — только для внутренних состояний класса
                
                {
                    _logger?.Send($"[{response.Status}] {req.Method} {response.Url}");
                }

                // Console — опциональный вывод
                if (_consoleLog)
                {
                    Console.ForegroundColor = GetColorByStatus(response.Status);
                    Console.WriteLine($"[{response.Status}] | {type.PadRight(6)} | {response.Url}");
                    Console.ResetColor();
                }

                // TrafficHost — весь HTTP трафик уходит сюда (отдельная панель мониторинга)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var httpLog = new
                        {
                            timestamp = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            method = req.Method,
                            url = response.Url,
                            statusCode = response.Status,
                            durationMs = (int)(endTime - startTime).TotalMilliseconds,
                            request = new
                            {
                                headers = req.Headers.Select(h => $"{h.Key}: {h.Value}").ToArray(),
                                body = reqBody,
                                proxy = "Playwright"
                            },
                            response = new
                            {
                                headers = response.Headers.Select(h => $"{h.Key}: {h.Value}").ToArray(),
                                body = resBody
                            },
                            machine = Environment.MachineName,
                            project = "z3n8"
                        };

                        var content = new StringContent(
                            JsonConvert.SerializeObject(httpLog),
                            Encoding.UTF8,
                            "application/json"
                        );

                        await _trafficClient.PostAsync(_trafficHost, content);
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                 _logger?.Send($"Traffic handler error: {ex.Message}");
            }
        }

        private ConsoleColor GetColorByStatus(int status) => status switch
        {
            >= 200 and < 300 => ConsoleColor.Green,
            >= 300 and < 400 => ConsoleColor.Cyan,
            >= 400 and < 500 => ConsoleColor.Yellow,
            _ => ConsoleColor.Red
        };

        public class TrafficElement
        {
            internal DateTime Timestamp { get; set; }
            public string Method { get; internal set; }
            public string Url { get; internal set; }
            public int StatusCode { get; internal set; }
            public string RequestHeaders { get; internal set; }
            public string RequestBody { get; internal set; }
            public string ResponseHeaders { get; internal set; }
            public string ResponseBody { get; internal set; }

            /// <summary>
            /// Получить конкретный заголовок из запроса
            /// </summary>
            public string GetRequestHeader(string headerName)
            {
                var headers = ParseHeaders(RequestHeaders);
                var key = headerName.ToLower();
                return headers.ContainsKey(key) ? headers[key] : null;
            }

            /// <summary>
            /// Получить конкретный заголовок из ответа
            /// </summary>
            public string GetResponseHeader(string headerName)
            {
                var headers = ParseHeaders(ResponseHeaders);
                var key = headerName.ToLower();
                return headers.ContainsKey(key) ? headers[key] : null;
            }

            /// <summary>
            /// Получить все заголовки запроса
            /// </summary>
            public Dictionary<string, string> GetAllRequestHeaders()
            {
                return ParseHeaders(RequestHeaders);
            }

            /// <summary>
            /// Получить все заголовки ответа
            /// </summary>
            public Dictionary<string, string> GetAllResponseHeaders()
            {
                return ParseHeaders(ResponseHeaders);
            }

            private Dictionary<string, string> ParseHeaders(string headersString)
            {
                var headers = new Dictionary<string, string>();
                if (string.IsNullOrWhiteSpace(headersString)) return headers;

                foreach (var line in headersString.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex <= 0) continue;

                    var key = trimmed.Substring(0, colonIndex).Trim().ToLower();
                    var value = trimmed.Substring(colonIndex + 1).Trim();
                    headers[key] = value;
                }

                return headers;
            }
        }
        
        
        
        
    }
}