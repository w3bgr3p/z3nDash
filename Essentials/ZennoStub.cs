#if !ZENNOPOSTER

// ══════════════════════════════════════════════════════════════════════════════
// ZennoStub.cs  —  эмуляция ZennoPoster SDK для standalone-запуска z3nCore
// Компилируется только при отсутствии символа ZENNOPOSTER.
// Namespace'ы и типы совпадают с оригинальным SDK — конфликтов нет,
// т.к. реальные dll не подключаются в этой конфигурации.
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using z3nIO;

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.InterfacesLibrary.Enums
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.Enums.Log
{
    public enum LogType  { Info, Warning, Error }
    public enum LogColor { Default, Red, Green, Yellow, Blue }
}

namespace ZennoLab.InterfacesLibrary.Enums.Http
{
    public enum HttpMethod { Get, Post, Put, Delete, Head, Patch }
    public enum ResponceType { HeaderAndBody, BodyOnly, HeaderOnly }
}

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.InterfacesLibrary.ProjectModel.Collections
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.ProjectModel.Collections
{
    public interface IVariable
    {
        string Value { get; set; }
    }

    public interface IVariableList
    {
        IVariable this[string name] { get; }
    }

    public interface IGlobalVariableList
    {
        IVariable this[string ns, string key] { get; }
        void SetVariable(string ns, string key, string value);
    }

    public interface IProfile
    {
        string UserAgent          { get; set; }
        string Login              { get; set; }
        string NickName           { get; set; }
        object CookieContainer    { get; }
        // Остальные члены IProfile не используются в z3nCore — заглушены интерфейсом
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.InterfacesLibrary.ProjectModel
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.ProjectModel
{
    using ZennoLab.InterfacesLibrary.Enums.Log;
    using ZennoLab.InterfacesLibrary.ProjectModel.Collections;

    public interface IZennoPosterProjectModel
    {
        // ── Identity ──────────────────────────────────────────────────────────
        string Name   { get; }
        string Path   { get; }
        string TaskId { get; }

        // ── Storage ───────────────────────────────────────────────────────────
        IVariableList       Variables       { get; }
        IGlobalVariableList GlobalVariables { get; }
        IProfile            Profile         { get; }

        // ── Dynamic JSON ──────────────────────────────────────────────────────
        dynamic Json { get; }

        // ── ZennoPoster helpers ───────────────────────────────────────────────
        string ExecuteMacro(string macro);
        void   SendToLog(string message, LogType type, bool show, LogColor color);
        void   SendInfoToLog(string message,    bool show = false);
        void   SendWarningToLog(string message, bool show = false);
        void   SendErrorToLog(string message,   bool show = false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.CommandCenter  (Instance — только методы нужные z3nCore)
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.CommandCenter
{
    /// <summary>
    /// Stub Instance. Методы, требующие браузера, бросают NotImplementedException.
    /// </summary>
    public class Instance
    {
        public string FormTitle { get; set; } = "";

        public void SetCookie(string cookies)
            => throw new NotImplementedException("Instance.SetCookie requires ZennoPoster browser");

        public void F5()
            => throw new NotImplementedException("Instance.F5 requires ZennoPoster browser");
    }

    /// <summary>
    /// Stub ZennoPoster — только HTTP.Request используется в Rqst.cs через ExecuteViaZennoPoster.
    /// В standalone-режиме всегда используется NetHttp (useNetHttp=true), поэтому достаточно throw.
    /// </summary>
    public static class ZennoPoster
    {
        public static class HTTP
        {
            public static string Request(
                ZennoLab.InterfacesLibrary.Enums.Http.HttpMethod method,
                string url, string body, string contentType,
                string proxy, string encoding,
                ZennoLab.InterfacesLibrary.Enums.Http.ResponceType responseType,
                int timeout, string cookies, string userAgent,
                bool followRedirects, int maxRedirects,
                string[] headers, string cert, bool ignoreErrors,
                bool sendBody, object cookieContainer)
                => throw new NotImplementedException(
                    "ZennoPoster.HTTP.Request is not available in standalone mode. " +
                    "Use useNetHttp=true in all API calls.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Реализации коллекций
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.ProjectModel.Collections
{
    internal sealed class Variable : IVariable
    {
        private readonly ConcurrentDictionary<string, string> _store;
        private readonly string _key;

        public Variable(ConcurrentDictionary<string, string> store, string key)
        {
            _store = store;
            _key   = key;
        }

        public string Value
        {
            get => _store.GetOrAdd(_key, "");
            set => _store[_key] = value ?? "";
        }
    }

    internal sealed class VariableList : IVariableList
    {
        private readonly ConcurrentDictionary<string, string> _store
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IVariable this[string name]
            => new Variable(_store, name);

        public string Get(string name)
            => _store.GetOrAdd(name, "");

        public void Set(string name, string value)
            => _store[name] = value ?? "";
    }

    internal sealed class GlobalVariableList : IGlobalVariableList
    {
        // ключ: "ns::key"
        private readonly ConcurrentDictionary<string, string> _store
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string ns, string key) => $"{ns}::{key}";

        public IVariable this[string ns, string key]
            => new Variable(_store, Key(ns, key));

        public void SetVariable(string ns, string key, string value)
            => _store[Key(ns, key)] = value ?? "";
    }

    internal sealed class StubProfile : IProfile
    {
        public string UserAgent
        {
            get => _ua;
            set => _ua = value;
        }
        private string _ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36";

        public string Login    { get; set; } = "";
        public string NickName { get; set; } = "";
        public object CookieContainer => null;
    }

    // Dynamic JSON — project.Json.FromString / project.Json.field
    internal sealed class DynamicJson : System.Dynamic.DynamicObject
    {
        private Newtonsoft.Json.Linq.JToken _root
            = Newtonsoft.Json.Linq.JValue.CreateNull();

        public void FromString(string json)
        {
            try   { _root = Newtonsoft.Json.Linq.JToken.Parse(json); }
            catch { _root = Newtonsoft.Json.Linq.JValue.CreateNull(); }
        }

        public override bool TryGetMember(
            System.Dynamic.GetMemberBinder binder, out object result)
        {
            result = null;
            if (_root is Newtonsoft.Json.Linq.JObject obj)
            {
                var token = obj[binder.Name];
                result = token == null ? (object)new DynamicJson() : Wrap(token);
                return true;
            }
            return false;
        }

        private static object Wrap(Newtonsoft.Json.Linq.JToken t)
        {
            switch (t.Type)
            {
                case Newtonsoft.Json.Linq.JTokenType.String:  return (string)t;
                case Newtonsoft.Json.Linq.JTokenType.Integer: return (long)t;
                case Newtonsoft.Json.Linq.JTokenType.Float:   return (double)t;
                case Newtonsoft.Json.Linq.JTokenType.Boolean: return (bool)t;
                case Newtonsoft.Json.Linq.JTokenType.Null:    return null;
                default:
                    var d = new DynamicJson();
                    d.FromString(t.ToString());
                    return d;
            }
        }

        public override string ToString() => _root?.ToString() ?? "";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StubProject : IZennoPosterProjectModel
// Основная точка входа для standalone-запуска
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.ProjectModel
{
    using ZennoLab.InterfacesLibrary.Enums.Log;
    using ZennoLab.InterfacesLibrary.ProjectModel.Collections;

    public sealed class StubProject : IZennoPosterProjectModel
    {
        // ── Конфигурация перед запуском ───────────────────────────────────────

        private readonly VariableList       _variables = new VariableList();
        private readonly GlobalVariableList _globals   = new GlobalVariableList();
        private readonly StubProfile        _profile   = new StubProfile();
        private readonly DynamicJson        _json      = new DynamicJson();
        public Logger? Logger { get; set; }
        public string Name   { get; set; } = "stub.zp";
        public string Path   { get; set; } = System.IO.Directory.GetCurrentDirectory();
        public string TaskId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        public IVariableList       Variables       => _variables;
        public IGlobalVariableList GlobalVariables => _globals;
        public IProfile            Profile         => _profile;
        public dynamic             Json            => _json;

        // ── Загрузка данных аккаунта из JSON-файла ────────────────────────────
        // Формат: { "acc0": "1", "proxy": "user:pass@host:port",
        //           "secp256k1": "<plaintext_pk>", ... }

        public Db Db { get; set; }

        public void LoadAccount(string jsonFilePath)
        {
            if (!File.Exists(jsonFilePath))
                throw new FileNotFoundException(jsonFilePath);

            var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(jsonFilePath));

            foreach (var kv in d)
                _variables.Set(kv.Key, kv.Value);
        }

        // ── IZennoPosterProjectModel ──────────────────────────────────────────

        public string ExecuteMacro(string macro)
        {
            // {-Environment.CurrentUser-} → имя текущего пользователя ОС
            if (macro == "{-Environment.CurrentUser-}")
                return Environment.UserName;
            return macro;
        }

        public void SendToLog(string message, LogType type, bool show, LogColor color)
            => WriteConsole(message, type);

        public void SendInfoToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Info);
            Logger?.Info(message);
        }

        public void SendWarningToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Warning);
            Logger?.Warn(message);
        }

        public void SendErrorToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Error);
            Logger?.Error(message);
        }

        private static void WriteConsole(string message, LogType type)
        {
            Console.ForegroundColor = type switch
            {
                LogType.Warning => ConsoleColor.Yellow,
                LogType.Error   => ConsoleColor.Red,
                _               => ConsoleColor.Gray,
            };
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SAFU  —  stub без шифрования (dev-среда, ключи в plaintext)
// Воспроизводит публичный API, который вызывается из z3nCore
// ─────────────────────────────────────────────────────────────────────────────


// ─────────────────────────────────────────────────────────────────────────────
// FunctionStorage  —  stub
// ─────────────────────────────────────────────────────────────────────────────

namespace z3nIO
{
    public static class FunctionStorage
    {
        public static readonly ConcurrentDictionary<string, object> Functions
            = new ConcurrentDictionary<string, object>();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// z3nCore extension methods для IZennoPosterProjectModel
// Эмулируют публичный API из Vars.cs, Rqst.cs, DbExtencions.cs
// ─────────────────────────────────────────────────────────────────────────────

namespace z3nIO
{
    using System.Globalization;
    using System.Net.Http;
    using System.Threading;
    using ZennoLab.InterfacesLibrary.ProjectModel;

    // ── Vars ──────────────────────────────────────────────────────────────────

    public static partial class ProjectExtensions
    {
        public static string Var(this IZennoPosterProjectModel project, string name)
            => project.Variables[name].Value;

        public static string Var(this IZennoPosterProjectModel project, string name, object value)
        {
            project.Variables[name].Value = value?.ToString() ?? "";
            return "";
        }

        public static string GVar(this IZennoPosterProjectModel project, string name)
        {
            string ns = project.ExecuteMacro("{-Environment.CurrentUser-}");
            return project.GlobalVariables[ns, name].Value;
        }

        public static string GVar(this IZennoPosterProjectModel project, string name, object value)
        {
            string ns = project.ExecuteMacro("{-Environment.CurrentUser-}");
            project.GlobalVariables.SetVariable(ns, name, value?.ToString() ?? "");
            return "";
        }

        public static void log(this IZennoPosterProjectModel project, object msg,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
            bool show = true, bool thrw = false, bool toZp = true)
            => project.SendInfoToLog($"[{caller}] {msg}", show);

        public static void warn(this IZennoPosterProjectModel project, string msg,
            bool thrw = false, bool show = true, bool toZp = true,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            project.SendWarningToLog($"[{caller}] {msg}", show);
            if (thrw) throw new Exception(msg);
        }

        public static void warn(this IZennoPosterProjectModel project, Exception ex,
            bool thrw = false, bool withStack = false, bool toZp = true,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            var msg = withStack ? ex.Message + "\n" + ex.StackTrace : ex.Message;
            project.SendWarningToLog($"[{caller}] {msg}", true);
            if (thrw) throw ex;
        }

        public static void VarsFromDict(this IZennoPosterProjectModel project,
            System.Collections.Generic.Dictionary<string, string> dict)
        {
            foreach (var kv in dict)
                project.Variables[kv.Key].Value = kv.Value ?? "";
        }

        public static void ListSync(this IZennoPosterProjectModel project,
            string listName, System.Collections.Generic.List<string> list)
        {
            // В standalone нет ZennoPoster-списков — сохраняем как JSON-переменную
            project.Variables[listName].Value = JsonConvert.SerializeObject(list);
        }

        public static string ProjectName(this IZennoPosterProjectModel project)
            => System.IO.Path.GetFileNameWithoutExtension(project.Name);

        public static string ProjectTable(this IZennoPosterProjectModel project)
        {
            string table = "__" + project.ProjectName();
            project.Var("projectTable", table);
            return table;
        }

        public static string TableName(this IZennoPosterProjectModel project, string tableName)
            => string.IsNullOrEmpty(tableName) ? project.ProjectTable() : tableName;
    }

    // ── HTTP (Rqst-совместимый API) ───────────────────────────────────────────

    
    public static partial class ProjectExtensions
    {
        private static readonly ConcurrentDictionary<string, HttpClient> _clients = new();

        private static HttpClient GetClient(IZennoPosterProjectModel project)
        {
            string proxy       = project.Variables["proxy"].Value;
            string projectName = project.ProjectName();
            string taskId      = project.Variables["__scheduleTag"].Value is { Length: > 0 } tag ? tag : project.TaskId;
            string account     = project.Variables["acc0"].Value ?? "";
            string clientKey = $"{projectName}::{proxy}::{account}::{taskId}";

            return _clients.GetOrAdd(clientKey, _ =>
            {
                string logHost = Config.LogsConfig.TrafficHost is { Length: > 0 } h
                    ? h
                    : "http://localhost:38109/http-log";

                var innerHandler = new HttpClientHandler();

                if (!string.IsNullOrEmpty(proxy))
                {
                    try
                    {
                        string proxyUrl = proxy.StartsWith("http") ? proxy : $"http://{proxy}";
                        var uri         = new Uri(proxyUrl);
                        var webProxy    = new System.Net.WebProxy(uri);

                        if (!string.IsNullOrEmpty(uri.UserInfo))
                        {
                            var parts = uri.UserInfo.Split(':');
                            webProxy.Credentials = new System.Net.NetworkCredential(parts[0], parts[1]);
                        }

                        innerHandler.Proxy           = webProxy;
                        innerHandler.UseProxy        = true;
                        innerHandler.PreAuthenticate = true;
                    }
                    catch { }
                }

                var handler = new HttpDebugHandler(
                    projectName: projectName, 
                    logHost : logHost, 
                    proxy : proxy,
                    taskId:taskId,
                    account:account)
                {
                    InnerHandler = innerHandler
                };
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            });
        }

        private static string SendHttp(string method, string url, string body,
            string[] headers, string cookies, string proxy,
            bool parse, bool thrw, int deadline, IZennoPosterProjectModel project)
        {
            try
            {
                var result = Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(deadline));
                    using var req = new HttpRequestMessage(new System.Net.Http.HttpMethod(method), url);

                    req.Headers.TryAddWithoutValidation("User-Agent", project.Profile.UserAgent);

                    if (headers != null)
                        foreach (var h in headers)
                        {
                            var ci = h.IndexOf(':');
                            if (ci < 0) continue;
                            req.Headers.TryAddWithoutValidation(h.Substring(0, ci).Trim(), h.Substring(ci + 1).Trim());
                        }

                    if (!string.IsNullOrEmpty(cookies))
                        req.Headers.TryAddWithoutValidation("Cookie", cookies);

                    if (body != null)
                    {
                        req.Content = new StringContent(body, Encoding.UTF8);
                        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    }

                    using var resp = await GetClient(project).SendAsync(req, cts.Token);
                    var respBody = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"{(int)resp.StatusCode}: {respBody}");

                    return respBody;
                }).GetAwaiter().GetResult();

                if (parse) project.Json.FromString(result);
                return result;
            }
            catch (Exception ex)
            {
                if (thrw) throw;
                return $"Error: {ex.Message}";
            }
        }
    }
    
    public static partial class ProjectExtensions
    {
        
        public static string GET(this IZennoPosterProjectModel project,
            string url, string proxy = "", string[] headers = null,
            string cookies = null, bool log = false, bool parse = false,
            int deadline = 30, bool thrw = false, bool useNetHttp = true,
            bool returnSuccessWithStatus = false, bool bodyOnly = false)
            => SendHttp("GET", url, null, headers, cookies, proxy, parse, thrw, deadline, project);

        public static string POST(this IZennoPosterProjectModel project,
            string url, string body, string proxy = "", string[] headers = null,
            string cookies = null, bool log = false, bool parse = false,
            int deadline = 30, bool thrw = false, bool useNetHttp = true,
            bool returnSuccessWithStatus = false, bool bodyOnly = false)
            => SendHttp("POST", url, body, headers, cookies, proxy, parse, thrw, deadline, project);

        public static string PUT(this IZennoPosterProjectModel project,
            string url, string body, string proxy = "", string[] headers = null,
            string cookies = null, bool log = false, bool parse = false,
            int deadline = 30, bool thrw = false, bool useNetHttp = true,
            bool returnSuccessWithStatus = false)
            => SendHttp("PUT", url, body, headers, cookies, proxy, parse, thrw, deadline, project);

        public static string DELETE(this IZennoPosterProjectModel project,
            string url, string proxy = "", string[] headers = null,
            string cookies = null, bool log = false, int deadline = 30,
            bool thrw = false, bool useNetHttp = true, bool returnSuccessWithStatus = false)
            => SendHttp("DELETE", url, null, headers, cookies, proxy, false, thrw, deadline, project);

       
    }

    // ── DbKey (plaintext — без SAFU) ──────────────────────────────────────────

    public static partial class ProjectExtensions
    {
        /// <summary>
        /// Возвращает приватный ключ из переменной напрямую (plaintext, без расшифровки).
        /// Переменная должна быть загружена через StubProject.LoadAccount().
        /// evm  → secp256k1
        /// sol  → base58
        /// seed → bip39
        /// </summary>
        public static string DbKey(this IZennoPosterProjectModel project, string chainType = "evm")
        {
            string column = chainType.ToLower().Trim() switch
            {
                "evm"  => "secp256k1",
                "sol"  => "base58",
                "seed" => "bip39",
                _      => throw new ArgumentException($"DbKey: unexpected chainType '{chainType}'")
            };

            string acc    = project.Variables["acc0"].Value;
            string raw    = GetDb(project).Get(column, "_wlt", key: "id", id: acc);

            string jVarsJson = SAFU.DecryptHWIDOnly(project.Variables["jVars"].Value);
            
            if (string.IsNullOrEmpty(jVarsJson))
                throw new Exception($"DecryptHWIDOnly returned empty. jVars starts with: '{project.Variables["jVars"].Value?[..Math.Min(20, project.Variables["jVars"].Value?.Length??0)]}'");
            var json = jVarsJson.TrimStart().StartsWith("{") ? jVarsJson : jVarsJson.FromBase64();

            var vars = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            string pin       = vars.GetValueOrDefault("cfgPin", "");
            return SAFU.Decode(raw, pin, acc);
        }

    }

    // ── String helpers (используются в z3nCore) ───────────────────────────────

    public static partial class StringExtensions
    {
        public static string ToBase64(this string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
        
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            return Convert.ToBase64String(bytes);
        }

        public static string FromBase64(this string s)
        {
            try   { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return s; }
        }

        public static System.Collections.Generic.Dictionary<string, object> ParseJwt(this string token)
        {
            var result = new System.Collections.Generic.Dictionary<string, object>();
            if (string.IsNullOrEmpty(token)) { result["is_expired"] = true; return result; }
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) { result["is_expired"] = true; return result; }
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "=";  break;
                }
                var claims = JsonConvert.DeserializeObject<
                    System.Collections.Generic.Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                foreach (var kv in claims) result[kv.Key] = kv.Value;
                if (claims.TryGetValue("exp", out var expObj))
                    result["is_expired"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                           >= Convert.ToInt64(expObj);
                else
                    result["is_expired"] = false;
            }
            catch { result["is_expired"] = true; }
            return result;
        }
    }

    // ── Time (копия из z3nCore/Time.cs без изменений) ─────────────────────────

    public class Time
    {
        public class Deadline
        {
            private long Init { get; set; }
            public Deadline() => Reset();

            public double Check(double limitSec)
            {
                double diff = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Init) / 1000.0;
                if (diff > limitSec) throw new TimeoutException($"Deadline Exception: {limitSec}s");
                return diff;
            }

            public void Reset()
                => Init = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public class Sleeper
        {
            private readonly int _min, _max;
            private readonly Random _random;

            public Sleeper(int min, int max)
            {
                if (min < 0) throw new ArgumentException("Min не может быть отрицательным");
                if (max < min) throw new ArgumentException("Max не может быть меньше Min");
                _min = min; _max = max;
                _random = new Random(Guid.NewGuid().GetHashCode());
            }

            public void Sleep(double multiplier = 1.0)
                => Thread.Sleep((int)(_random.Next(_min, _max + 1) * multiplier));
        }

        public static string Now(string format = "unix")
        {
            return format switch
            {
                "unix"    => ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString(),
                "iso"     => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                "short"   => DateTime.UtcNow.ToString("MM-ddTHH:mm"),
                "utcToId" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                _         => throw new ArgumentException("Invalid format. Use: 'unix|iso|short|utcToId'")
            };
        }

        public static string Cd(object input = null, string o = "iso")
        {
            DateTime t = DateTime.UtcNow;
            if (input == null)
                t = t.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            else if (input is string s && s == "nextH")
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1).AddMinutes(1);
            else if (input is decimal || input is int)
            {
                decimal minutes = Convert.ToDecimal(input);
                if (minutes == 0m) minutes = 999999999m;
                t = t.AddSeconds((long)Math.Round(minutes * 60));
            }
            else if (input is string ts)
                t = t.Add(TimeSpan.Parse(ts));

            return o switch
            {
                "unix" => ((long)(t - new DateTime(1970, 1, 1)).TotalSeconds).ToString(),
                "iso"  => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                _      => throw new ArgumentException($"unexpected format {o}")
            };
        }

        public static long Elapsed(long startTime = 0, bool useMs = false)
        {
            long now = useMs
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return startTime != 0 ? now - startTime : now;
        }
    }


    // ── Db extensions — делегируют в StubProject.Db ──────────────────────────
    // Воспроизводят API из DbExtencions.cs (z3nCore) поверх класса Db (z3nIO)

    public static partial class ProjectExtensions
    {
        private static Db GetDb(IZennoPosterProjectModel project)
        {
            var stub = project as global::ZennoLab.InterfacesLibrary.ProjectModel.StubProject;
            if (stub?.Db == null)
                throw new InvalidOperationException(
                    "StubProject.Db is not initialized. Set project.Db = new Db(...) before use.");
            return stub.Db;
        }

        public static void DicToDb(this IZennoPosterProjectModel project,
            Dictionary<string, string> data,
            string tableName = null,
            bool log = false,
            bool thrw = false,
            string where = "")
            => GetDb(project).DicToDb(data, tableName ?? project.ProjectTable(), log, thrw, where);

        public static void JsonToDb(this IZennoPosterProjectModel project,
            string json,
            string tableName = null,
            bool log = false,
            bool thrw = false,
            string where = "",
            bool saveStructure = false)
            => GetDb(project).JsonToDb(json, tableName ?? project.ProjectTable(), log, thrw, where);

        public static void DbUpd(this IZennoPosterProjectModel project,
            string setClause,
            string tableName = null,
            bool log = false,
            bool thrw = false,
            string key = "id",
            object acc = null,
            string where = "",
            string saveToVar = "lastQuery")
        {
            if (!string.IsNullOrEmpty(saveToVar))
                project.Variables[saveToVar].Value = setClause;
            string id = acc?.ToString() ?? project.Variables["acc0"].Value;
            GetDb(project).Upd(setClause, tableName ?? project.ProjectTable(), log, thrw, key, id, where);
        }

        public static string DbQ(this IZennoPosterProjectModel project,
            string query,
            bool log = false,
            string sqLitePath = null,
            string pgHost = null,
            string pgPort = null,
            string pgDbName = null,
            string pgUser = null,
            string pgPass = null,
            bool thrw = false,
            bool unSafe = false)
            => GetDb(project).Query(query, thrw, unSafe);

        public static string DbGet(this IZennoPosterProjectModel project,
            string column,
            string tableName = null,
            bool log = false,
            bool thrw = false,
            string key = "id",
            string acc = null,
            string where = "")
        {
            string id = acc?.ToString() ?? project.Variables["acc0"].Value;
            return GetDb(project).Get(column, tableName ?? project.ProjectTable(), log, thrw, key, id, where);
        }
        public static Dictionary<string, string> DbGetColumns(this IZennoPosterProjectModel project,
            string column,
            string tableName = null,
            bool log = false,
            bool thrw = false,
            string key = "id",
            string acc = null,
            string where = "")
        {
            string id = acc?.ToString() ?? project.Variables["acc0"].Value;
            return GetDb(project).GetColumns(column, tableName ?? project.ProjectTable(), log, thrw, key, id, where);
        }
        
        public static void DbDone(this IZennoPosterProjectModel project, string task = "daily", int cooldownMin = 0, string tableName = null, bool log = false, bool thrw = false, string key = "id", object acc = null, string where = "")
        {
            var cd = (cooldownMin == 0) ? Time.Cd() : Time.Cd(cooldownMin);
            project.DbUpd($"{task} = '{cd}'", tableName, log, thrw);
        }
        
    }
    

    // ── Rpc — публичные RPC-эндпоинты цепей ──────────────────────────────────

    public static class Rpc
    {
        public const string Ethereum  = "https://eth.llamarpc.com";
        public const string Base      = "https://mainnet.base.org";
        public const string Arbitrum  = "https://arb1.arbitrum.io/rpc";
        public const string Optimism  = "https://mainnet.optimism.io";
        public const string Polygon   = "https://polygon-rpc.com";
        public const string Bsc       = "https://bsc-dataseed.binance.org";
        public const string Avalanche = "https://api.avax.network/ext/bc/C/rpc";
        public const string Zksync    = "https://mainnet.era.zksync.io";
        public const string Scroll    = "https://rpc.scroll.io";
        public const string Linea     = "https://rpc.linea.build";
    }
}

#endif // !ZENNOPOSTER