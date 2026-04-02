using System.Text;
using System.Text.Json;

namespace z3nIO;

internal sealed class AiClient
{
    private readonly DbConnectionService _dbService;

    private const string AiioCompletionsUrl = "https://api.intelligence.io.solutions/api/v1/chat/completions";
    private const string AiioModelsUrl      = "https://api.intelligence.io.solutions/api/v1/models?page=1&page_size=200";

    private static List<string>? _modelsCache;

    public AiClient(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool IsEnabled => Config.AiConfig.Provider is "aiio" or "omniroute";

    // ── complete ───────────────────────────────────────────────────────────────

    public async Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        double temp      = 0.3,
        int    maxTokens = 800,
        int    timeoutSec = 90)
    {
        var provider = Config.AiConfig.Provider;

        var (url, apiKey) = provider switch
        {
            "aiio"       => (AiioCompletionsUrl,             GetAiioKey()),
            "omniroute"  => (OmniRouteUrl("/v1/chat/completions"), ""),
            _            => throw new InvalidOperationException("AI provider not configured")
        };

        var body = JsonSerializer.Serialize(new
        {
            model,
            messages    = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } },
            temperature = temp,
            top_p       = 0.9,
            stream      = false,
            max_tokens  = maxTokens
        });

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(apiKey))
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

    // ── models ─────────────────────────────────────────────────────────────────

    public async Task<List<string>> GetModelsAsync()
    {
        if (_modelsCache != null) return _modelsCache;

        var provider = Config.AiConfig.Provider;

        var (url, apiKey) = provider switch
        {
            "aiio"      => (AiioModelsUrl,              GetAiioKey()),
            "omniroute" => (OmniRouteUrl("/v1/models"),  ""),
            _           => throw new InvalidOperationException("AI provider not configured")
        };

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)response.StatusCode}\n{raw}");

        var json   = JsonSerializer.Deserialize<JsonElement>(raw);
        var models = json.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id)
            .ToList();

        _modelsCache = models;
        return models;
    }

    public static void InvalidateModelsCache() => _modelsCache = null;

    // ── validation helpers (used by ConfigHandler) ─────────────────────────────

    public static async Task<bool> CheckOmniRouteAsync(string host)
    {
        try
        {
            var url = host.TrimEnd('/') + "/v1/models";
            using var http     = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public bool HasAiioKey()
    {
        if (!_dbService.TryGetDb(out var db)) return false;
        return !string.IsNullOrEmpty(GetAiioKey(db!));
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private string GetAiioKey()
    {
        if (!_dbService.TryGetDb(out var db)) throw new Exception("DB not available");
        var key = GetAiioKey(db!);
        if (string.IsNullOrEmpty(key)) throw new Exception("No valid aiio key in __aiio table");
        return key;
    }

    private static string? GetAiioKey(Db db)
    {
        var now  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var keys = db.GetLines(
                "api",
                tableName: "__aiio",
                where: $"(\"expire\" = '' OR \"expire\" IS NULL OR \"expire\" > '{now}')")
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return keys.Count == 0 ? null : keys[new Random().Next(keys.Count)].Trim();
    }

    private static string OmniRouteUrl(string path) =>
        Config.AiConfig.OmniRouteHost.TrimEnd('/') + path;
}