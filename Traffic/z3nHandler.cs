
using System.Runtime.CompilerServices;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json;


namespace z3n8
{
    public class HttpCallDiagnostics
    {
        public string CallerMethod { get; set; }
        public string CallerClass { get; set; }
        public string CallerNamespace { get; set; }
        public string CallerFile { get; set; }
        public int CallerLine { get; set; }
        public string AssemblyName { get; set; }
        public int ThreadId { get; set; }
        public long MemoryMB { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    /// <summary>
    /// Extension методы для HttpClient с автоматическим захватом caller info
    /// Использование: await httpClient.GetAsyncWithDiagnostics(url);
    /// </summary>
    public static class HttpClientDiagnosticExtensions
    {
        private const string DiagnosticsKey = "z3n.HttpDiagnostics";

        public static async Task<HttpResponseMessage> GetAsyncWithDiagnostics(
            this HttpClient client,
            string requestUri,
            [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            AttachDiagnostics(request, callerName, callerFilePath, callerLineNumber);
            return await client.SendAsync(request);
        }

        public static async Task<HttpResponseMessage> PostAsyncWithDiagnostics(
            this HttpClient client,
            string requestUri,
            HttpContent content,
            [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
            AttachDiagnostics(request, callerName, callerFilePath, callerLineNumber);
            return await client.SendAsync(request);
        }

        public static async Task<HttpResponseMessage> SendAsyncWithDiagnostics(
            this HttpClient client,
            HttpRequestMessage request,
            [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            AttachDiagnostics(request, callerName, callerFilePath, callerLineNumber);
            return await client.SendAsync(request);
        }

        private static void AttachDiagnostics(HttpRequestMessage request, string callerName, string callerFilePath, int callerLineNumber)
        {
            var diagnostics = new HttpCallDiagnostics
            {
                CallerMethod = callerName,
                CallerClass = System.IO.Path.GetFileNameWithoutExtension(callerFilePath),
                CallerFile = callerFilePath,
                CallerLine = callerLineNumber,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                //MemoryMB = GC.GetTotalMemory(false) / 1024 / 1024,
                Timestamp = DateTime.UtcNow
            };

            #if NET5_0_OR_GREATER
            request.Options.Set(new HttpRequestOptionsKey<HttpCallDiagnostics>(DiagnosticsKey), diagnostics);
            #else
            request.Properties[DiagnosticsKey] = diagnostics;
            #endif
        }
        public static HttpCallDiagnostics GetDiagnostics(this HttpRequestMessage request)
        {
            
            if (request.Options.TryGetValue(new HttpRequestOptionsKey<HttpCallDiagnostics>(DiagnosticsKey), out var diagnostics))
            {
                return diagnostics;
            }
            return null;
        }
    }


    public class HttpDebugHandler : DelegatingHandler
    {
        private readonly string _projectName;
        private readonly string _logHost;
        private readonly bool _fallbackToStackTrace;
        private static readonly HttpClient _logClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        public HttpDebugHandler(string projectName, string logHost = "http://localhost:10993/http-log", bool fallbackToStackTrace = true)
        {
            _projectName = projectName;
            _logHost = logHost;
            _fallbackToStackTrace = fallbackToStackTrace;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            var diagnostics = request.GetDiagnostics() ?? (_fallbackToStackTrace ? GetCallerDiagnosticsFromStack() : null);

            // 1. Читаем тело запроса (до отправки)
            string requestBody = request.Content != null ? await request.Content.ReadAsStringAsync() : null;

            // 2. Выполняем запрос
            var response = await base.SendAsync(request, cancellationToken);
            var endTime = DateTime.UtcNow;

            // 3. ОБЯЗАТЕЛЬНО буферизируем ответ, чтобы и мы, и вызывающий код могли его прочитать
            await response.Content.LoadIntoBufferAsync();
            string responseBody = await response.Content.ReadAsStringAsync();

            // 4. Логируем в консоль (для живого контроля)
            Console.WriteLine($"[HTTP] {request.Method} {response.StatusCode} | {request.RequestUri.Host} |  {responseBody} | {(int)(endTime - startTime).TotalMilliseconds}ms");

            // 5. Отправляем в лог-сервер. 
            // ВАЖНО: передаем уже готовые строки (requestBody, responseBody), 
            // чтобы фоновая задача не зависела от состояния объектов request/response.

            _ = Task.Run(() => SendDebugLog(request, requestBody, response, responseBody, startTime, endTime, diagnostics));

            return response;
        }

        private HttpCallDiagnostics GetCallerDiagnosticsFromStack()
        {
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();

                if (frames == null) return null;

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method?.DeclaringType == null) continue;

                    var typeName = method.DeclaringType.FullName ?? "";
                    
                    if (typeName.StartsWith("System.Net.Http") ||
                        typeName.StartsWith("System.Runtime.CompilerServices") ||
                        typeName.Contains("HttpDebugHandler") ||
                        typeName.Contains("<>c") ||
                        typeName.Contains("StateMachine"))
                    {
                        continue;
                    }

                    return new HttpCallDiagnostics
                    {
                        CallerMethod = method.Name,
                        CallerClass = method.DeclaringType.Name,
                        CallerNamespace = method.DeclaringType.Namespace,
                        CallerFile = frame.GetFileName() ?? "Unknown",
                        CallerLine = frame.GetFileLineNumber(),
                        AssemblyName = method.DeclaringType.Assembly.GetName().Name,
                        ThreadId = Thread.CurrentThread.ManagedThreadId,
                        //MemoryMB = GC.GetTotalMemory(false) / 1024 / 1024,
                        Timestamp = DateTime.UtcNow
                    };
                }
            }
            catch { }

            return null;
        }

        private async Task SendDebugLog(HttpRequestMessage req, string reqBody, HttpResponseMessage res, string resBody, 
            DateTime start, DateTime end, HttpCallDiagnostics diagnostics)
        {
            try
            {
                var httpLog = new
                {
                    timestamp = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    method = req.Method.ToString(),
                    url = req.RequestUri.ToString(),
                    statusCode = (int)res.StatusCode,
                    durationMs = (int)(end - start).TotalMilliseconds,
                    
                    diagnostics = diagnostics != null ? new
                    {
                        caller = $"{diagnostics.CallerClass}.{diagnostics.CallerMethod}",
                        className = diagnostics.CallerClass,
                        methodName = diagnostics.CallerMethod,
                        @namespace = diagnostics.CallerNamespace,
                        fileName = diagnostics.CallerFile,
                        lineNumber = diagnostics.CallerLine,
                        assembly = diagnostics.AssemblyName,
                        threadId = diagnostics.ThreadId,
                        //memoryMB = diagnostics.MemoryMB,
                        capturedAt = diagnostics.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    } : null,
                    
                    request = new
                    {
                        headers = GetHeaders(req),
                        body = reqBody
                    },
                    response = new
                    {
                        headers = GetHeaders(res),
                        body = resBody
                    },
                    machine = Environment.MachineName,
                    project = _projectName,
                    processId = System.Diagnostics.Process.GetCurrentProcess().Id
                };

                var content = new StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(httpLog), 
                    Encoding.UTF8, 
                    "application/json");
                    
                await _logClient.PostAsync(_logHost, content);
            }
            catch (Exception ex) { 
                Console.WriteLine($"[HttpDebugHandler ERROR] {ex.Message}"); 
            }
        }

        private string[] GetHeaders(HttpRequestMessage req)
        {
            var headers = req.Headers.Concat(req.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
            return headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").ToArray();
        }

        private string[] GetHeaders(HttpResponseMessage res)
        {
            var headers = res.Headers.Concat(res.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
            return headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").ToArray();
        }
    }
}