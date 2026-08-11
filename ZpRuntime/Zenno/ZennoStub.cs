// ══════════════════════════════════════════════════════════════════════════════
// ZennoStub.cs  —  эмуляция ZennoPoster SDK для standalone-запуска ZpRuntime.
// Namespace'ы и типы совпадают с оригинальным SDK, поэтому код, написанный под
// ZennoPoster, компилируется без правок. Реальные ZennoLab.dll не подключаются.
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
using DevDeck;
using z3n7;   // Var/Int и прочие перенесённые из эталона расширения

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.InterfacesLibrary.Enums
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.Enums.Log
{
    public enum LogType  { Info, Warning, Error }
    public enum LogColor { Default, Red, Green, Yellow, Blue, Orange }
}

namespace ZennoLab.InterfacesLibrary.Enums.Browser
{
    public enum BrowserType   { Chromium, ChromiumFromZB, WithoutBrowser }
    public enum TimezoneMode  { Disable, Emulate, Manual }
}

namespace ZennoLab.InterfacesLibrary.Enums.Db
{
    public enum DbProvider { Odbc, OleDb, SqlServer, MySql, Postgre, SQLite }
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
    // Имена типов совпадают с реальным SDK (ZennoLab.InterfacesLibrary.dll).
    // Реализованы только члены, которые вызывает переносимый код.

    public interface IVariable
    {
        string Value { get; set; }
    }

    /// <summary>Реальный тип свойства <c>IZennoPosterProjectModel.Variables</c>.</summary>
    public interface ILocalVariables
    {
        IVariable this[string name] { get; }
    }

    /// <summary>Реальный тип свойства <c>IZennoPosterProjectModel.GlobalVariables</c>.</summary>
    public interface IGlobalVariables
    {
        IVariable this[string ns, string key] { get; }
        void SetVariable(string ns, string key, string value);
    }

    public interface IZennoList : IList<string>
    {
        void AddRange(IEnumerable<string> items);
    }

    public interface ILists
    {
        IZennoList this[string name] { get; }
        bool ContainsKey(string name);
    }

    public interface IZennoTable
    {
        int RowCount    { get; }
        int ColumnCount { get; }
        string GetCell(int column, int row);
        void   SetCell(int column, int row, string value);
    }

    public interface ITables
    {
        IZennoTable this[string name] { get; }
        bool ContainsKey(string name);
    }

    public interface IProfile
    {
        string UserAgent       { get; set; }
        string Login           { get; set; }
        string NickName        { get; set; }
        object CookieContainer { get; }
    }

    public interface IContext
    {
        string SessionId { get; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.InterfacesLibrary.ProjectModel
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.InterfacesLibrary.ProjectModel
{
    using ZennoLab.InterfacesLibrary.Enums.Log;
    using ZennoLab.InterfacesLibrary.ProjectModel.Collections;

    /// <summary>
    /// Сигнатуры сняты с реальной ZennoLab.InterfacesLibrary.dll. Члены, не нужные
    /// переносимому коду, опущены — но всё, что объявлено, объявлено точно.
    /// </summary>
    public interface IZennoPosterProjectModel
    {
        // ── Identity / пути ───────────────────────────────────────────────────
        string Name      { get; }
        string Path      { get; }
        string Directory { get; }
        string TaskId    { get; }

        // ── Storage ───────────────────────────────────────────────────────────
        ILocalVariables  Variables       { get; }
        IGlobalVariables GlobalVariables { get; }
        ILists           Lists           { get; }
        ITables          Tables          { get; }
        IProfile         Profile         { get; }
        IContext         Context         { get; }

        // ── Сериализация ──────────────────────────────────────────────────────
        // В SDK это dynamic: в метаданных хранится как object плюс
        // DynamicAttribute, поэтому дамп показывал object.
        dynamic Json { get; }
        dynamic Xml  { get; }

        // ── Диагностика ───────────────────────────────────────────────────────
        string LastExecutedActionId { get; }
        string LastErrorComment     { get; }

        // ── Прокси ────────────────────────────────────────────────────────────
        string GetProxy();
        void   SetProxy(string proxy);

        // ── Макросы и вложенные проекты ───────────────────────────────────────
        string ExecuteMacro(string text);
        bool   ExecuteProject(string pathToProject,
                              IEnumerable<Tuple<string, string>> varibleMapping,
                              bool mapOnBadExist, bool passProjectContext, bool useBrowser);

        // ── Лог. Перегрузки повторяют SDK один в один ─────────────────────────
        void SendToLog(string message, LogType type);
        void SendToLog(string message, LogType type, bool showInPoster);
        void SendToLog(string message, LogType type, bool showInPoster, LogColor color);
        void SendInfoToLog(string message,    bool showInPoster = false);
        void SendWarningToLog(string message, bool showInPoster = false);
        void SendErrorToLog(string message,   bool showInPoster = false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ZennoLab.CommandCenter  (Instance — только методы нужные z3nCore)
// ─────────────────────────────────────────────────────────────────────────────

namespace ZennoLab.CommandCenter
{
    // Instance/Tab/HtmlElement живут в CommandCenter.cs — там они не заглушки,
    // а адаптеры поверх IBrowserInstance.

    // Статик ZennoPoster живёт в CommandCenter.cs: HTTP там работает
    // по-настоящему, управление задачами ZP-сервера явно отказывает.
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

    internal sealed class VariableList : ILocalVariables
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

    internal sealed class GlobalVariableList : IGlobalVariables
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

    internal sealed class ZennoList : List<string>, IZennoList
    {
        void IZennoList.AddRange(IEnumerable<string> items) => base.AddRange(items);
    }

    internal sealed class ListCollection : ILists
    {
        private readonly ConcurrentDictionary<string, ZennoList> _store
            = new ConcurrentDictionary<string, ZennoList>(StringComparer.OrdinalIgnoreCase);

        public IZennoList this[string name] => _store.GetOrAdd(name, _ => new ZennoList());
        public bool ContainsKey(string name) => _store.ContainsKey(name);
    }

    internal sealed class ZennoTable : IZennoTable
    {
        private readonly List<List<string>> _rows = new List<List<string>>();

        public int RowCount    => _rows.Count;
        public int ColumnCount => _rows.Count == 0 ? 0 : _rows.Max(r => r.Count);

        public string GetCell(int column, int row)
            => row < _rows.Count && column < _rows[row].Count ? _rows[row][column] : "";

        public void SetCell(int column, int row, string value)
        {
            while (_rows.Count <= row) _rows.Add(new List<string>());
            var line = _rows[row];
            while (line.Count <= column) line.Add("");
            line[column] = value ?? "";
        }
    }

    internal sealed class TableCollection : ITables
    {
        private readonly ConcurrentDictionary<string, ZennoTable> _store
            = new ConcurrentDictionary<string, ZennoTable>(StringComparer.OrdinalIgnoreCase);

        public IZennoTable this[string name] => _store.GetOrAdd(name, _ => new ZennoTable());
        public bool ContainsKey(string name) => _store.ContainsKey(name);
    }

    internal sealed class StubContext : IContext
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
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
        // Квалифицировано: после переноса z3n7.Logger имя стало неоднозначным.
        // Здесь именно приложенческий логгер DevDeck, а не проектный из эталона.
        public DevDeck.Logger? Logger { get; set; }
        public Action<string>? OnLog { get; set; }

        private readonly ListCollection  _lists   = new ListCollection();
        private readonly TableCollection _tables  = new TableCollection();
        private readonly StubContext     _context = new StubContext();

        public string Name      { get; set; } = "stub.zp";
        public string Path      { get; set; } = System.IO.Directory.GetCurrentDirectory();
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;
        public string TaskId    { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        public ILocalVariables  Variables       => _variables;
        public IGlobalVariables GlobalVariables => _globals;
        public ILists           Lists           => _lists;
        public ITables          Tables          => _tables;
        public IProfile         Profile         => _profile;
        public IContext         Context         => _context;
        public dynamic          Json            => _json;
        public dynamic          Xml             => _json;

        /// <summary>Проставляется исполнителем шаблона перед каждым действием.</summary>
        public string LastExecutedActionId { get; set; } = "";
        /// <summary>Проставляется исполнителем при переходе по ветке OnError.</summary>
        public string LastErrorComment     { get; set; } = "";

        private string _proxy = "";
        public string GetProxy()            => _proxy;
        public void   SetProxy(string proxy) => _proxy = proxy ?? "";

        public bool ExecuteProject(string pathToProject,
                                   IEnumerable<Tuple<string, string>> varibleMapping,
                                   bool mapOnBadExist, bool passProjectContext, bool useBrowser)
            => throw new NotSupportedException(
                "ExecuteProject: вложенные шаблоны пока не поддержаны ZpRuntime");

        // ── Загрузка данных аккаунта из JSON-файла ────────────────────────────
        // Формат: { "acc0": "1", "proxy": "user:pass@host:port",
        //           "secp256k1": "<plaintext_pk>", ... }

        // Квалифицировано: после переноса z3n7.Db имя стало неоднозначным.
        // Здесь нужен именно приложенческий — его настраивает DbConnectionService,
        // и приложенческий код зовёт его методы напрямую, мимо расширений.
        public DevDeck.Db Db { get; set; }

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
        
        

        public void SendToLog(string message, LogType type)
            => SendToLog(message, type, false, LogColor.Default);

        public void SendToLog(string message, LogType type, bool showInPoster)
            => SendToLog(message, type, showInPoster, LogColor.Default);

        public void SendToLog(string message, LogType type, bool showInPoster, LogColor color)
        {
            WriteConsole(message, type);
            OnLog?.Invoke(message);
        }

        public void SendInfoToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Info);
            Logger?.Info(message);
            OnLog?.Invoke(message);
        }

        public void SendWarningToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Warning);
            Logger?.Warn(message);
            OnLog?.Invoke(message);
        }

        public void SendErrorToLog(string message, bool show = false)
        {
            WriteConsole(message, LogType.Error);
            Logger?.Error(message);
            OnLog?.Invoke(message);
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

namespace DevDeck
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

namespace DevDeck
{
    using System.Globalization;
    using System.Net.Http;
    using System.Threading;
    using ZennoLab.InterfacesLibrary.ProjectModel;

    // ── Vars ──────────────────────────────────────────────────────────────────

    public static partial class ProjectExtensions
    {
        // Var/Int/Decimal/Bool/MaxErr перенесены в Z3n7/Vars.cs из эталона.
        // Наши версии удалены: два ProjectExtensions с одинаковой сигнатурой в
        // разных namespace дают неоднозначность в точке вызова.

        // GVar/GGetBusyList перенесены в Z3n7/GVars.cs из эталона.

        // log/warn перенесены в Z3n7/Logger.cs из эталона.


        // TableName перенесён в Z3n7/DbExtencions.cs (DbHelpers, internal).

        // GET/POST/PUT/DELETE перенесены в Z3n7/Rqst.cs из эталона
        // (RqstExtensions). Наши версии удалены вместе с SendHttp/GetClient:
        // они были самостоятельной реализацией, а не обёрткой над эталонной.
    }

    // ── DbKey ─────────────────────────────────────────────────────────────────

    // DbKey перенесён в Z3n7/DbExtencions.cs (класс Get) из эталона. Наш снят.
    // Он работал иначе: брал plaintext-ключ и расшифровывал через
    // DevDeck.SAFU.Decode(raw, pin, acc), то есть на обеих целях сборки.
    // Эталонный зовёт z3n7.SAFU.Decode(project, resp), а тот под #if WINDOWS —
    // поэтому и Get.DbKey там доступен только в net10.0-windows.

    // ── String helpers (используются в z3nCore) ───────────────────────────────

    public static partial class StringExtensions
    {
        // ToBase64/FromBase64/ParseJwt перенесены в Z3n7/StringExtentions.cs.
        // Наши сняты. ParseJwt различался существенно: наш возвращал плоские
        // claims плюс is_expired, эталон — структурированные alg/typ/kid/iss/
        // sub/aud/iat/exp/ttl_seconds/is_expired и сырые header_json/payload_json.
    }

    // ── Time (копия из z3nCore/Time.cs без изменений) ─────────────────────────

    // Класс Time перенесён в Z3n7/Time.cs из эталона.


    // ── Db extensions ────────────────────────────────────────────────────────

    public static partial class ProjectExtensions
    {
        // Db-расширения (GetDb/DicToDb/JsonToDb/DbUpd/DbQ/DbGet/DbGetColumns/
        // DbDone) перенесены в Z3n7/DbExtencions.cs из эталона. Наши удалены:
        // они ходили в DevDeck.Db через StubProject.Db, эталон поднимает Sql по
        // строке подключения из переменной dbSource.
        
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

    // Класс Tools снят целиком: единственным его содержимым были две версии
    // OtpCode, а это тот же код, что в эталонном z3n7.Tools.Otp.Offline.
    // Перенесён он в Z3n7/Otp.cs, вместе с ProjectExtensions.OtpCode из
    // Z3n7/FirstMail.cs — тот выбирает между почтой и офлайн-TOTP по входу.
    // Имя убрано и по второй причине: у эталона Tools — пространство имён,
    // и одноимённый класс делал бы его неоднозначным.
}
