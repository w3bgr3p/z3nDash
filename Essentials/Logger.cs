using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace z3n8
{
    public enum LogLevel
    {
        Debug   = 0,
        Info    = 1,
        Warning = 2,
        Error   = 3,
        Off     = 99
    }

    public class Logger
    {
        // ── Static API ────────────────────────────────────────────────────────
        #region Static API

        [ThreadStatic]
        private static Logger _threadLogger;
        public static Logger Current => _threadLogger;

        public static Logger Init(string acc = "", string logHost = null, LogLevel logLevel = LogLevel.Info)
        {
            _threadLogger = Get(acc, logHost, logLevel);
            return _threadLogger;
        }

        public static void log(string msg,  [CallerMemberName] string caller = "") => _threadLogger?.Info(msg,  caller);
        public static void warn(string msg, [CallerMemberName] string caller = "") => _threadLogger?.Warn(msg,  caller);
        public static void err(string msg,  [CallerMemberName] string caller = "") => _threadLogger?.Error(msg, caller);

        #endregion

        // ── Cache ─────────────────────────────────────────────────────────────
        #region Cache

        private static readonly ConcurrentDictionary<string, Logger> _loggerCache = new();

        public static Logger Get(string acc = "", string logHost = null, LogLevel logLevel = LogLevel.Info)
        {
            string key = string.IsNullOrEmpty(acc) ? "__default__" : acc;
            return _loggerCache.GetOrAdd(key, _ => new Logger(acc: acc, logHost: logHost, logLevel: logLevel));
        }

        public static void ClearCache(string acc = "")
        {
            string key = string.IsNullOrEmpty(acc) ? "__default__" : acc;
            _loggerCache.TryRemove(key, out _);
            _threadLogger = null;
        }

        #endregion

        // ── Fields & Properties ───────────────────────────────────────────────
        #region Fields & Properties

        // Identity
        public string Emoji   { get; set; }
        public string Acc     { get; set; }
        public string TaskId  { get; set; }
        public string Session { get; set; }
        public string Project { get; set; }
        public string LogHost => _logHost;

        // Config flags
        private readonly LogLevel        _minLevel;
        private readonly bool            _persistent;
        private readonly bool            _http;
        private readonly int             _timezone;
        private readonly string          _logHost;
        private readonly bool            _fAcc, _fTime, _fCaller, _fWrap, _fForce;
        private readonly Stopwatch       _stopwatch;
        private readonly Action<string>? _sink;
        private readonly string _source = "z3n8";
        #endregion

        // ── Constructor ───────────────────────────────────────────────────────
        #region Constructor

        public Logger(
            string          classEmoji     = null,
            bool            persistent     = true,
            LogLevel        logLevel       = LogLevel.Info,
            string          logHost        = null,
            bool            http           = true,
            int             timezoneOffset = -5,
            string          acc            = "",
            string          taskId         = "",
            string          session        = "",
            string          project        = "z3n8",
            string          cfgLog         = "caller,wrap,acc,stopwatch",
            Action<string>? sink           = null)
        {
            Emoji       = classEmoji;
            Acc         = acc;
            TaskId      = taskId;
            Session     = session;
            Project     = project;
            _persistent = persistent;
            _http       = http;
            _timezone   = timezoneOffset;
            _minLevel   = logLevel;
            _logHost    = logHost ?? "http://localhost:38109/log";
            _sink       = sink;
            _stopwatch  = persistent ? Stopwatch.StartNew() : null;

            _fAcc    = cfgLog.Contains("acc");
            _fTime   = cfgLog.Contains("stopwatch");
            _fCaller = cfgLog.Contains("caller");
            _fWrap   = cfgLog.Contains("wrap");
            _fForce  = cfgLog.Contains("force");
        }

        #endregion

        // ── Public API ────────────────────────────────────────────────────────
        #region Public API

        public void Send(
            object   toLog,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "",
            bool     show  = false,
            bool     thrw  = false,
            int      cut   = 0,
            LogLevel level = LogLevel.Info)
        {
            if (_fForce) show = true;
            if (!show && level < _minLevel) return;

            string className  = Path.GetFileNameWithoutExtension(callerFilePath);
            string fullCaller = _fCaller ? $"{className}.{callerName}" : callerName;

            string body   = BuildBody(toLog?.ToString() ?? "null", cut);
            string header = _fWrap ? BuildHeader(fullCaller) : string.Empty;
            string full   = header + body;
            
            WriteConsole(fullCaller, body, level, full);

            if (_http)
                HttpSink.Send(_logHost, _timezone, level, body, fullCaller, this);

            if (thrw)
                throw new Exception(full);
        }

        public void Debug(object toLog, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFilePath = "")
            => Send(toLog, callerName, callerFilePath, level: LogLevel.Debug);

        public void Info(object toLog, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFilePath = "")
            => Send(toLog, callerName, callerFilePath, level: LogLevel.Info);

        public void Warn(
            object   toLog,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "",
            bool     show  = false,
            bool     thrw  = false,
            int      cut   = 0)
            => Send(toLog, callerName, callerFilePath, show, thrw, cut, LogLevel.Warning);

        public void Error(
            object   toLog,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "",
            bool     thrw = false)
            => Send(toLog, callerName, callerFilePath, show: true, thrw: thrw, level: LogLevel.Error);

        #endregion

        // ── Helpers ───────────────────────────────────────────────────────────
        #region Helpers

        private string BuildHeader(string fullCaller, bool caller = true)
        {
            var sb = new StringBuilder();
            if (_fAcc  && !string.IsNullOrEmpty(Acc)) sb.Append($"  🤖 [{Acc}]");
            if (_fTime && _stopwatch != null)          sb.Append($"  ⏱️ [{_stopwatch.Elapsed:hh\\:mm\\:ss}]");
            if (_fCaller && caller)                              sb.Append($"  🔲 [{fullCaller}]");
            return sb.ToString();
        }

        private string BuildBody(string text, int cut)
        {
            if (cut > 0 && text.Count(c => c == '\n') > cut)
                text = text.Replace("\r\n", " ").Replace('\n', ' ');
            string prefix = !string.IsNullOrEmpty(Emoji) ? $"[ {Emoji} ] " : "";
            return $"{prefix}{text.Trim()}";
        }

        private void WriteConsole(string fullCaller, string body, LogLevel level, string sink)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write($"{fullCaller}.");

            Console.ForegroundColor = level switch
            {
                LogLevel.Debug   => ConsoleColor.DarkGray,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error   => ConsoleColor.Red,
                _                => ConsoleColor.Cyan
            };
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write($"{body}\n");
            Console.ResetColor();

            _sink?.Invoke($"[{level.ToString().ToUpper()}] {sink} ");
        }

        #endregion
    }

    // ── HTTP Sink ─────────────────────────────────────────────────────────────
    #region HTTP Sink

    internal static class HttpSink
    {
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };

        public static void Send(string logHost, int timezone, LogLevel level, string body, string caller, Logger ctx)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = BuildPayload(timezone, level, body, caller, ctx);
                    string json = JsonConvert.SerializeObject(payload);

                    using var cts     = new System.Threading.CancellationTokenSource(1000);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _client.PostAsync(logHost, content, cts.Token);
                }
                catch { }
            });
        }

        private static object BuildPayload(int timezone, LogLevel level, string body, string caller, Logger ctx) => new
        {
            machine  = Environment.MachineName,
            project  = !string.IsNullOrEmpty(ctx.Project) ? ctx.Project : "z3n8",
            timestamp = DateTime.UtcNow.AddHours(timezone).ToString("yyyy-MM-dd HH:mm:ss"),
            level    = level.ToString().ToUpper(),
            account  = !string.IsNullOrEmpty(ctx.Acc)     ? ctx.Acc     : "-",
            session  = !string.IsNullOrEmpty(ctx.Session) ? ctx.Session : "-",
            port     = "-",
            pid      = "-",
            task_id  = !string.IsNullOrEmpty(ctx.TaskId)  ? ctx.TaskId  : "-",
            caller,
            message  = body.Trim(),
            origin = "z3n8"
        };
    }

    #endregion

    // ── Extension Methods ─────────────────────────────────────────────────────
    #region Extension Methods

    public static class LoggerExt
    {
        private static readonly object _lock = new();

        public static void Debug(this object toLog,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "")
        {
            string className = Path.GetFileNameWithoutExtension(callerFilePath);
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write($" {className}.");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"{callerName}");
                Console.ResetColor();
                Console.WriteLine($" {toLog}");
            }
        }

        public static void Err(this object toLog,
            bool thrw = false,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "")
        {
            string className = Path.GetFileNameWithoutExtension(callerFilePath);
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write($" {className}.");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"{callerName}");
                Console.ResetColor();
                Console.WriteLine($" {toLog}");
                Console.ForegroundColor = ConsoleColor.Red;
            }
        }

        public static void Err(this Exception ex,
            object toLog = null,
            bool thrw    = false,
            [CallerMemberName] string callerName     = "",
            [CallerFilePath]   string callerFilePath = "")
        {
            string className = Path.GetFileNameWithoutExtension(callerFilePath);
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write($" {className}.");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"{callerName}");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($" {toLog}");
                Console.ResetColor();
            }
            if (thrw) throw ex;
        }
    }

    #endregion
}