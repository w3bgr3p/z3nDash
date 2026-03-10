using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace z3n8;

internal sealed class ConfigHandler
{
    private readonly string _logPath;
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConfigHandler(string logPath, HttpListener listener, int port)
    {
        _logPath  = logPath;
        _listener = listener;
        _port     = port;
    }

    public bool Matches(string path, string method) =>
        (method == "GET"  && path is "/config" or "/config/status" or "/config/storage") ||
        (method == "POST" && path is "/config" or "/clear-all-logs");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET" && path == "/config")        { await GetConfig(ctx.Response);    return; }
        if (method == "POST" && path == "/config")       { using var r = new StreamReader(ctx.Request.InputStream); await SaveConfig(ctx.Response, await r.ReadToEndAsync()); return; }
        if (method == "GET" && path == "/config/status") { await GetStatus(ctx.Response);    return; }
        if (method == "GET" && path == "/config/storage"){ await GetStorage(ctx.Response);   return; }
        if (method == "POST" && path == "/clear-all-logs"){ await ClearAllLogs(ctx.Response); return; }
       
    }

    private static async Task GetConfig(HttpListenerResponse response)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        if (!File.Exists(cfgPath)) { response.StatusCode = 404; await HttpHelpers.WriteText(response, "Config file not found"); return; }
        await HttpHelpers.WriteRawJson(response, await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
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

    private async Task GetStatus(HttpListenerResponse response)
    {
        var ports = _listener.Prefixes
            .Select(p => Uri.TryCreate(p, UriKind.Absolute, out var u) ? u.Port : 0)
            .Where(p => p > 0).Distinct().OrderBy(p => p).ToList();

        var cfg = Config.LogsConfig;
        var db  = Config.DbConfig;

        await HttpHelpers.WriteJson(response, new
        {
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
}
