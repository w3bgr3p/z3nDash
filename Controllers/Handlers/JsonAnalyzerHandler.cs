using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

internal sealed class JsonAnalyzerHandler
{
    private readonly DbConnectionService _dbService;

    private const string CacheKey   = "__json_last";
    private const string CacheTable = "__ai_json_cache";
    private const string AiioUrl    = "https://api.intelligence.io.solutions/api/v1/chat/completions";
    private const string Lang       = "russian";

    public JsonAnalyzerHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool Matches(string path) => path.StartsWith("/json-analyzer");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/json-analyzer/ai-analyze" && method == "POST") { await HandleAnalyze(ctx); return; }
        if (path == "/json-analyzer/ai-cache"   && method == "GET")  { await HandleCacheGet(ctx); return; }
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
        var ts    = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var aEsc  = analysis.Replace("'", "''");
        var mEsc  = model.Replace("'", "''");
        var kEsc  = CacheKey.Replace("'", "''");

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
            await HttpHelpers.WriteJson(ctx.Response, new
            {
                entry = new
                {
                    analysis = cols[0].Trim(),
                    model    = cols[1].Trim(),
                    ts       = cols[2].Trim()
                }
            });
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

        var apiKey = GetApiKey(db!);
        if (string.IsNullOrEmpty(apiKey)) { ctx.Response.StatusCode = 500; await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" }); return; }

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

        var prompt = BuildPrompt(jsonData);

        string result;
        try   { result = await CallAiio(apiKey, model, prompt); }
        catch (Exception ex) { await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message }); return; }

        SaveCache(db!, model, result);
        await HttpHelpers.WriteJson(ctx.Response, new { analysis = result, model, ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
    }

    // ── prompt ─────────────────────────────────────────────────────────────────

    private static string BuildPrompt(JsonElement data)
    {
        // Serialize with limit to avoid oversized prompts
        var raw = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        if (raw.Length > 24_000) raw = raw.Substring(0, 24_000) + "\n... [truncated]";
        return raw;
    }

    // ── aiio ───────────────────────────────────────────────────────────────────

    private static async Task<string> CallAiio(string apiKey, string model, string prompt)
    {

        
        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts","JsonAudit.txt");
        
        var systemPrompt = File.ReadAllText(promptPath) + $" Language: {Lang}.";
        
        
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages    = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = prompt } },
            temperature = 0.2,
            top_p       = 0.9,
            stream      = false,
            max_tokens  = 1200
        });

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var request = new HttpRequestMessage(HttpMethod.Post, AiioUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)response.StatusCode}\n{raw}");

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            return json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "No response";
        }
        catch (Exception ex)
        {
            throw new Exception($"{ex.Message}\nRAW:\n{raw}");
        }
    }

    // ── api key ────────────────────────────────────────────────────────────────

    private static string? GetApiKey(Db db)
    {
        var now   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var lines = db.GetLines(
            "api",
            tableName: "__aiio",
            where: $"(\"expire\" = '' OR \"expire\" IS NULL OR \"expire\" > '{now}')"
        );
        var keys = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (keys.Count == 0) return null;
        return keys[new Random().Next(keys.Count)].Trim();
    }
}