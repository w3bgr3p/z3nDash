using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

using System.Text;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;


namespace z3n8
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Off = 99
    }
    
    public class Logger
    {
        private string _emoji = null;
        private readonly bool _persistent;
        private readonly Stopwatch _stopwatch;
        private int _timezone;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private string _logHost;
        private readonly bool _http;
        public string  _acc;
        
        public Logger( string classEmoji = null, bool persistent = true, LogLevel logLevel = LogLevel.Info, string logHost = null, bool http = true, int timezoneOffset = -5, string acc = "")
        {
            _emoji = classEmoji;
            _persistent = persistent;
            _stopwatch = persistent ? Stopwatch.StartNew() : null;
            _http = http;
            _logHost =  logHost;
            _timezone = timezoneOffset;
            _acc = acc;
        }
        
        public void Send(object toLog, string type = "INFO",
            [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = ""
            , int cut = 0)
        {
            string className = Path.GetFileNameWithoutExtension(callerFilePath);
            string fullCaller = $"{className}.{callerName}";
            
            string header = string.Empty;
            string body = toLog?.ToString() ?? "null";

            header = LogHeader(fullCaller); 
            if (cut > 0 && body.Count(c => c == '\n') > cut)
                body = body.Replace("\r\n", " ").Replace('\n', ' ');
            body = $"          {(!string.IsNullOrEmpty(_emoji) ? $"[ {_emoji} ] " : "")}{body.Trim()}";
            string toSend = header + body;
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"■ [{fullCaller}]");
            Console.ForegroundColor = ConsoleColor.Gray; 
            Console.Write($"          {body}\n");
            Console.ResetColor();
            
            
            if (_http)
            {
                string prjName =  "ZBTest";
                string acc = (string.IsNullOrEmpty(_acc)) ? _acc : "-";
                string port =  "-";
                string pid =  "-";
                string sessionId =  "-";
                SendToHttpLogger(body, type, fullCaller, prjName, acc, port, pid ,sessionId);
            }
        }
        
        private void SendToHttpLogger(string message, string type, string caller, string prj, string acc, string port,  string pid, string session)
        { _ = Task.Run(async () =>
            {
                try
                {
                    var logData = new
                    {
                        machine = Environment.MachineName,
                        project = prj,
                        timestamp = DateTime.UtcNow.AddHours(_timezone).ToString("yyyy-MM-dd HH:mm:ss"),
                        level = type.ToString().ToUpper(),
                        account = acc,
                        session = session,
                        port = port,
                        pid = pid,
                        caller = caller,
                        extra = new { caller },
                        message = message.Trim(),
                    };

                    string json = JsonConvert.SerializeObject(logData);
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[DEBUG] {json}");
                    Console.ResetColor();
                    using (var cts = new System.Threading.CancellationTokenSource(1000))
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    {
                        await _httpClient.PostAsync(_logHost, content, cts.Token);
                    }
                }
                catch { }
            });
        }      

        private string LogHeader(string callerName)
        {
            var sb = new StringBuilder();
            sb.Append($"🔲 [{callerName}]");
            return sb.ToString();
            
        }
        private string LogBody(string toLog, int cut)
        {
            if (string.IsNullOrEmpty(toLog)) return string.Empty;
            
            if (cut > 0)
            {
                int lineCount = toLog.Count(c => c == '\n') + 1;
                if (lineCount > cut)
                {
                    toLog = toLog.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                }
            }
            
            if (!string.IsNullOrEmpty(_emoji))
            {
                toLog = $"[ {_emoji} ] {toLog}";
            }
            return $"\n          {toLog.Trim()}";
        }
        private string GetFullCallerName(string methodName)
        {
            try
            {
                var stackTrace = new StackTrace();
                var frame = stackTrace.GetFrame(2); // Пропускаем Send() и GetFullCallerName()
                var method = frame?.GetMethod();
        
                if (method != null)
                {
                    string className = method.DeclaringType?.Name ?? "Unknown";
                    return $"{className}.{methodName}";
                }
            }
            catch { }
    
            return methodName;
        }

    }
}

