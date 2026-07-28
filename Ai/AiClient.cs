using System.Text;
using System.Text.Json;

namespace DevDeck;

internal sealed class AiClient
{
    private static List<string>? _modelsCache;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Config.AiConfig.OmniRouteHost);

    // ── complete ───────────────────────────────────────────────────────────────

    public async Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        double temp      = 0.3,
        int    maxTokens = 800,
        int    timeoutSec = 90)
    {
        var url = OmniRouteUrl("/v1/chat/completions");

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

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, OmniRouteUrl("/v1/models"));

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

    private static string OmniRouteUrl(string path) =>
        Config.AiConfig.OmniRouteHost.TrimEnd('/') + path;
}
