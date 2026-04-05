using System.Net;
using System.Text.Json;

namespace z3nIO;

internal sealed class JsonAnalyzerHandler
{
    private readonly DbConnectionService _dbService;
    private readonly AiClient _aiClient;

    private const string CacheKey   = "__json_last";
    private const string CacheTable = "__ai_json_cache";
    private const string Lang       = "russian";

    public JsonAnalyzerHandler(DbConnectionService dbService, AiClient aiClient)
    {
        _dbService = dbService;
        _aiClient  = aiClient;
    }

    public bool Matches(string path) => path.StartsWith("/json-analyzer");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/json-analyzer/ai-analyze" && method == "POST")   { await HandleAnalyze(ctx);     return; }
        if (path == "/json-analyzer/ai-cache"   && method == "GET")    { await HandleCacheGet(ctx);    return; }
        if (path == "/json-analyzer/ai-cache"   && method == "DELETE") { await HandleCacheDelete(ctx); return; }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ── cache ──────────────────────────────────────────────────────────────────

    private static void EnsureCacheTable(Db db)
    {
        db.CreateTable(new Dictionary<string, string>
        {
            ["key"]      = "TEXT PRIMARY KEY",
            ["analysis"] = "TEXT",
            ["model"]    = "TEXT",
            ["ts"]       = "TEXT"
        }, CacheTable);
    }

    private static void SaveCache(Db db, string model, string analysis)
    {
        EnsureCacheTable(db);
        var ts   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var aEsc = analysis.Replace("'", "''");
        var mEsc = model.Replace("'", "''");
        var kEsc = CacheKey.Replace("'", "''");

        db.Query(
            $"INSERT INTO \"{CacheTable}\" (\"key\", \"analysis\", \"model\", \"ts\") " +
            $"VALUES ('{kEsc}', '{aEsc}', '{mEsc}', '{ts}') " +
            $"ON CONFLICT (\"key\") DO UPDATE SET \"analysis\" = excluded.\"analysis\", \"model\" = excluded.\"model\", \"ts\" = excluded.\"ts\""
        );
    }

    // ── GET /json-analyzer/ai-cache ───────────────────────────────────────────

    private async Task HandleCacheGet(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        EnsureCacheTable(db!);

        var lines = db!.GetLines("analysis, model, ts", tableName: CacheTable,
            where: $"\"key\" = '{CacheKey.Replace("'", "''")}'");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('¦');
            if (cols.Length < 3) continue;
            await HttpHelpers.WriteJson(ctx.Response, new { entry = new { analysis = cols[0].Trim(), model = cols[1].Trim(), ts = cols[2].Trim() } });
            return;
        }

        await HttpHelpers.WriteJson(ctx.Response, new { entry = (object?)null });
    }

    // ── DELETE /json-analyzer/ai-cache ────────────────────────────────────────

    private async Task HandleCacheDelete(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        EnsureCacheTable(db!);
        db!.Del(tableName: CacheTable, where: $"\"key\" = '{CacheKey.Replace("'", "''")}'");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── POST /json-analyzer/ai-analyze ────────────────────────────────────────

    private async Task HandleAnalyze(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        if (!_aiClient.IsEnabled)             { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "ai disabled" }); return; }

        string model;
        JsonElement jsonData;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            model    = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            jsonData = root.TryGetProperty("data",  out var d) ? d : default;
        }
        catch { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" }); return; }

        if (string.IsNullOrEmpty(model)) model = "deepseek-ai/DeepSeek-V3.2";

        var promptPath   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "JsonAudit.txt");
        var systemPrompt = File.ReadAllText(promptPath) + $" Language: {Lang}.";

        string result;
        try   { result = await _aiClient.CompleteAsync(model, systemPrompt, BuildPrompt(jsonData), temp: 0.2, maxTokens: 1200); }
        catch (Exception ex) { await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message }); return; }

        SaveCache(db!, model, result);
        await HttpHelpers.WriteJson(ctx.Response, new { analysis = result, model, ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
    }

    // ── prompt ─────────────────────────────────────────────────────────────────

    private static string BuildPrompt(JsonElement data)
    {
        var raw = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        if (raw.Length > 24_000) raw = raw.Substring(0, 24_000) + "\n... [truncated]";
        return raw;
    }
}