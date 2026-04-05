using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace z3nIO;

internal sealed class ConfigHandler
{
    private readonly string _logPath;
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly DbConnectionService _dbService;
    private readonly AiClient _aiClient;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConfigHandler(string logPath, HttpListener listener, int port, DbConnectionService dbService, AiClient aiClient)
    {
        _logPath   = logPath;
        _listener  = listener;
        _port      = port;
        _dbService = dbService;
        _aiClient  = aiClient;
    }

    public bool Matches(string path, string method) =>
        (method == "GET"  && path is "/config" or "/config/status" or "/config/storage" or "/config/ui") ||
        (method == "POST" && path is "/config" or "/config/jvars" or "/config/ai-validate" or "/clear-all-logs" or "/config/ui");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET"  && path == "/config")               { await GetConfig(ctx.Response);    return; }
        if (method == "POST" && path == "/config")               { using var r = new StreamReader(ctx.Request.InputStream); await SaveConfig(ctx.Response, await r.ReadToEndAsync()); return; }
        if (method == "POST" && path == "/config/jvars")         { using var r = new StreamReader(ctx.Request.InputStream); await SaveJVars(ctx.Response, await r.ReadToEndAsync()); return; }
        if (method == "POST" && path == "/config/ai-validate")   { using var r = new StreamReader(ctx.Request.InputStream); await ValidateAiConfig(ctx.Response, await r.ReadToEndAsync()); return; }
        if (method == "GET"  && path == "/config/status")        { await GetStatus(ctx.Response);    return; }
        if (method == "GET"  && path == "/config/storage")       { await GetStorage(ctx.Response);   return; }
        if (method == "POST" && path == "/clear-all-logs")       { await ClearAllLogs(ctx.Response); return; }

        if (method == "GET"  && path == "/config/ui") { await GetUiState(ctx.Response);  return; }
        if (method == "POST" && path == "/config/ui") { using var r = new StreamReader(ctx.Request.InputStream); await SaveUiState(ctx.Response, await r.ReadToEndAsync()); return; }
    }

    private static async Task GetConfig(HttpListenerResponse response)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        if (!File.Exists(cfgPath)) { response.StatusCode = 404; await HttpHelpers.WriteText(response, "Config file not found"); return; }

        var raw  = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8);
        var json = JsonSerializer.Deserialize<JsonElement>(raw);

        var dict = new Dictionary<string, object?>();
        if (json.ValueKind == JsonValueKind.Object)
            foreach (var p in json.EnumerateObject())
                dict[p.Name] = p.Value;

        dict["securityConfig"] = new { jVarsPath = Config.SecurityConfig.JVarsPath };

        await HttpHelpers.WriteJson(response, dict);
    }

    private static async Task SaveConfig(HttpListenerResponse response, string body)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        try
        {
            var incoming = JsonSerializer.Deserialize<JsonElement>(body);
            var existing = new Dictionary<string, JsonElement>();

            if (File.Exists(cfgPath))
            {
                var existingDoc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
                if (existingDoc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in existingDoc.EnumerateObject())
                        existing[prop.Name] = prop.Value;
            }

            var keyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbConfig"]   = "DbConfig",
                ["logsConfig"] = "LogsConfig",
                ["apiConfig"]  = "ApiConfig",
                ["crx"]        = "Crx",
            };

            if (incoming.ValueKind == JsonValueKind.Object)
                foreach (var prop in incoming.EnumerateObject())
                    existing[keyMap.TryGetValue(prop.Name, out var mapped) ? mapped : prop.Name] = prop.Value;

            var opts    = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(existing, opts);

            if (File.Exists(cfgPath)) File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);
            await File.WriteAllTextAsync(cfgPath, json, Encoding.UTF8);

            Config.Init();
            await HttpHelpers.WriteJson(response, new { ok = true, message = "Config saved and reloaded" });
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await HttpHelpers.WriteJson(response, new { ok = false, error = ex.Message });
        }
    }

    // ── POST /config/ai-validate ───────────────────────────────────────────────
    // Body: { provider: "aiio"|"omniroute"|"", omniRouteHost: "http://..." }
    // Validates the chosen provider, saves effective result (may downgrade to "").

    private async Task ValidateAiConfig(HttpListenerResponse response, string body)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        try
        {
            var doc          = JsonSerializer.Deserialize<JsonElement>(body);
            var provider     = doc.TryGetProperty("provider",      out var pv) ? pv.GetString() ?? "" : "";
            var omniRouteHost = doc.TryGetProperty("omniRouteHost", out var oh) ? oh.GetString() ?? "http://localhost:20128" : "http://localhost:20128";

            string effectiveProvider = provider;
            string? reason           = null;

            if (provider == "aiio")
            {
                if (!_aiClient.HasAiioKey())
                {
                    effectiveProvider = "";
                    reason = "No valid key found in __aiio table";
                }
            }
            else if (provider == "omniroute")
            {
                var reachable = await AiClient.CheckOmniRouteAsync(omniRouteHost);
                if (!reachable)
                {
                    effectiveProvider = "";
                    reason = $"OmniRoute not reachable at {omniRouteHost}";
                }
            }

            // Persist
            var existing = new Dictionary<string, JsonElement>();
            if (File.Exists(cfgPath))
            {
                var existingDoc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
                if (existingDoc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in existingDoc.EnumerateObject())
                        existing[prop.Name] = prop.Value;
            }

            existing["AiConfig"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { Provider = effectiveProvider, OmniRouteHost = omniRouteHost }));

            if (File.Exists(cfgPath)) File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);
            await File.WriteAllTextAsync(cfgPath, JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            Config.Init();
            AiClient.InvalidateModelsCache();

            await HttpHelpers.WriteJson(response, new { ok = true, provider = effectiveProvider, reason });
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await HttpHelpers.WriteJson(response, new { ok = false, error = ex.Message });
        }
    }

    // Принимает Base64(JSON{pin, jVarsPath})
    private static async Task SaveJVars(HttpListenerResponse response, string body)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(body.Trim()));
            var doc     = JsonSerializer.Deserialize<JsonElement>(decoded);

            var pin       = doc.GetProperty("pin").GetString() ?? "";
            var jVarsPath = doc.GetProperty("jVarsPath").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(pin))      { response.StatusCode = 400; await HttpHelpers.WriteText(response, "pin is required");       return; }
            if (string.IsNullOrWhiteSpace(jVarsPath)) { response.StatusCode = 400; await HttpHelpers.WriteText(response, "jVarsPath is required"); return; }

            var vars      = new Dictionary<string, string> { ["cfgPin"] = pin };
            if (doc.TryGetProperty("localVars", out var localVarsEl) && localVarsEl.ValueKind == JsonValueKind.Object)
                foreach (var prop in localVarsEl.EnumerateObject())
                    vars[prop.Name] = prop.Value.GetString() ?? "";
            var plaintext = System.Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vars)));
            var encrypted = SAFU.EncryptHWIDOnly(plaintext);

            if (string.IsNullOrEmpty(encrypted)) { response.StatusCode = 500; await HttpHelpers.WriteText(response, "Encryption failed"); return; }

            var dir = Path.GetDirectoryName(jVarsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(jVarsPath, encrypted, Encoding.UTF8);

            string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
            var existing   = new Dictionary<string, JsonElement>();
            if (File.Exists(cfgPath))
            {
                var existingDoc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
                if (existingDoc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in existingDoc.EnumerateObject())
                        existing[prop.Name] = prop.Value;
            }

            existing["SecurityConfig"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { JVarsPath = jVarsPath }));

            if (File.Exists(cfgPath)) File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);
            await File.WriteAllTextAsync(cfgPath, JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            Config.Init();
            InternalTasks.Load();
            await HttpHelpers.WriteJson(response, new { ok = true });
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await HttpHelpers.WriteJson(response, new { ok = false, error = ex.Message });
        }
    }

    private async Task GetStatus(HttpListenerResponse response)
    {
        var ports = _listener.Prefixes
            .Select(p => Uri.TryCreate(p, UriKind.Absolute, out var u) ? u.Port : 0)
            .Where(p => p > 0).Distinct().OrderBy(p => p).ToList();

        var cfg = Config.LogsConfig;
        var db  = Config.DbConfig;

        await HttpHelpers.WriteJson(response, new
        {
            isConfigured   = Config.IsConfigured,
            isUnlocked     = InternalTasks.IsUnlocked,
            isDbConnected  = _dbService.IsConnected,
            dashboardPort  = _port,
            listeningPorts = ports,
            logHost        = cfg.LogHost,
            trafficHost    = cfg.TrafficHost,
            logsFolder     = _logPath,
            reportsFolder  = cfg.ReportsFolder,
            tempFolder     = cfg.TempFolder,
            maxFileSizeMb  = cfg.MaxFileSizeMb,
            dbMode         = db.Mode.ToString(),
            sqlitePath     = db.SqlitePath,
            pgHost         = db.PostgresHost,
            pgPort         = db.PostgresPort,
            pgDatabase     = db.PostgresDatabase,
        });
    }

    private async Task GetStorage(HttpListenerResponse response)
    {
        if (!Directory.Exists(_logPath))
        {
            await HttpHelpers.WriteJson(response, new { totalBytes = 0L, fileCount = 0, files = Array.Empty<object>() });
            return;
        }

        var files = Directory.GetFiles(_logPath).Select(f => new FileInfo(f)).OrderByDescending(fi => fi.Length).ToList();
        long totalBytes = files.Sum(fi => fi.Length);

        await HttpHelpers.WriteJson(response, new
        {
            totalBytes,
            totalMb    = Math.Round(totalBytes / 1024.0 / 1024.0, 2),
            fileCount  = files.Count,
            logsFolder = _logPath,
            maxFileSizeMbHint = Config.LogsConfig.MaxFileSizeMb > 0 ? Config.LogsConfig.MaxFileSizeMb * 20 : 500,
            files = files.Select(fi => new { name = fi.Name, size = fi.Length, lastWrite = fi.LastWriteTimeUtc.ToString("O") }).ToArray()
        });
    }

    private async Task ClearAllLogs(HttpListenerResponse response)
    {
        await _lock.WaitAsync();
        int deleted = 0;
        var errors  = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(_logPath, "*.jsonl"))
            {
                try
                {
                    string name = Path.GetFileName(file);
                    bool isCurrent = name.StartsWith("current") || name.StartsWith("http-current") || name.StartsWith("traffic-current");
                    if (isCurrent) File.WriteAllText(file, string.Empty);
                    else           File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
        }
        finally { _lock.Release(); }

        await HttpHelpers.WriteJson(response, new { ok = errors.Count == 0, deleted, errors });
    }

    private static string UiStatePath => Path.Combine(AppContext.BaseDirectory, "ui-state.json");

    private static async Task GetUiState(HttpListenerResponse response)
    {
        if (!File.Exists(UiStatePath)) { await HttpHelpers.WriteJson(response, new { theme = "dark" }); return; }
        var raw = await File.ReadAllTextAsync(UiStatePath, Encoding.UTF8);
        await HttpHelpers.WriteRawJson(response, raw);
    }

    private static async Task SaveUiState(HttpListenerResponse response, string body)
    {
        try
        {
            JsonSerializer.Deserialize<JsonElement>(body);
            await File.WriteAllTextAsync(UiStatePath, body, Encoding.UTF8);
            await HttpHelpers.WriteJson(response, new { ok = true });
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await HttpHelpers.WriteJson(response, new { ok = false, error = ex.Message });
        }
    }
}