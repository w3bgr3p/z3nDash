// DeBankClient.cs
// Порт Python DeBankClient на C#.
// Подпись запроса: HMAC-SHA256(SHA256(prefix+nonce+ts), SHA256(METHOD+path+sorted_params))
// Прокси передаётся в конструктор — один клиент на один запрос (не переиспользовать).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace z3nIO;

internal sealed class DeBankClient : IDisposable
{
    private const string ApiBase        = "https://api.debank.com";
    private const string NonceAlphabet  = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXTZabcdefghiklmnopqrstuvwxyz";
    private const int    NonceLength    = 40;
    private const double RequestTimeout = 10.0;

    // Ротируемый API-ключ — общий на все инстансы, обновляется сервером через x-set-api-key
    private static string          _apiKey     = "3b92c003-ddc1-4c2d-b36e-781838f362c5";
    private static readonly object _apiKeyLock = new();

    private readonly HttpClient _http;
    private readonly string     _randomId;
    private readonly long       _initTs;

    // proxy: "http://user:pass@host:port" или "http://host:port"
    // System.Net.WebProxy не парсит credentials из URI автоматически для HTTPS CONNECT —
    // парсим вручную.
    public DeBankClient(string proxy)
    {
        _initTs   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _randomId = Guid.NewGuid().ToString("N");

        var uri      = new Uri(proxy);
        var webProxy = new System.Net.WebProxy($"{uri.Scheme}://{uri.Host}:{uri.Port}");
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            webProxy.Credentials = new System.Net.NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        var handler = new HttpClientHandler
        {
            Proxy    = webProxy,
            UseProxy = true,
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip   |
                System.Net.DecompressionMethods.Deflate |
                System.Net.DecompressionMethods.Brotli,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(RequestTimeout) };
    }

    // ── Публичный метод ───────────────────────────────────────────────────────

    // Возвращает список токенов с ненулевым балансом.
    // address: EVM-адрес в формате 0x...
    public async Task<List<TokenInfo>> GetTokens(string address)
    {
        var result = await Get("/token/cache_balance_list",
            new Dictionary<string, string> { ["user_addr"] = address });

        if (result.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<TokenInfo>>(result.GetRawText()) ?? new();

        return new();
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<JsonElement> Get(string path, Dictionary<string, string> @params)
    {
        var ts    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = GenerateNonce();
        var sign  = Sign(@params, "GET", path, nonce, ts);

        string apiKey;
        lock (_apiKeyLock) apiKey = _apiKey;

        var account = JsonSerializer.Serialize(new
        {
            random_at      = _initTs,
            random_id      = _randomId,
            user_addr      = (string?)null,
            connected_addr = (string?)null,
        });

        var req = new HttpRequestMessage(HttpMethod.Get, BuildUrl(path, @params));
        req.Headers.TryAddWithoutValidation("Referer",      "https://debank.com/");
        req.Headers.TryAddWithoutValidation("Origin",       "https://debank.com");
        req.Headers.TryAddWithoutValidation("X-API-Key",    apiKey);
        req.Headers.TryAddWithoutValidation("X-API-Time",   _initTs.ToString());
        req.Headers.TryAddWithoutValidation("x-api-ts",     ts.ToString());
        req.Headers.TryAddWithoutValidation("x-api-nonce",  nonce);
        req.Headers.TryAddWithoutValidation("x-api-ver",    "v2");
        req.Headers.TryAddWithoutValidation("x-api-sign",   sign);
        req.Headers.TryAddWithoutValidation("source",       "web");
        req.Headers.TryAddWithoutValidation("account",      account);

        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        // Сервер может ротировать ключ
        if (resp.Headers.TryGetValues("x-set-api-key", out var values))
        {
            var newKey = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(newKey))
                lock (_apiKeyLock) _apiKey = newKey;
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        doc.RootElement.GetRawText().Debug();
        // DeBank оборачивает payload в { "data": ... }
        if (doc.RootElement.TryGetProperty("data", out var data))
            return data.Clone();

        return doc.RootElement.Clone();
    }

    // ── Подпись ───────────────────────────────────────────────────────────────

    // K = SHA256("debank-api\n{nonce}\n{ts}")
    // M = SHA256("GET\n{path}\n{sorted_params}")
    // sign = HMAC-SHA256(K, M)
    private static string Sign(Dictionary<string, string> @params, string method,
                                string path, string nonce, long ts)
    {
        var sorted = string.Join("&", @params.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var K      = Sha256Hex($"debank-api\n{nonce}\n{ts}");
        var M      = Sha256Hex($"{method.ToUpper()}\n{path}\n{sorted}");
        return HmacSha256Hex(K, M);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLower();
    }

    private static string HmacSha256Hex(string key, string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var hash     = HMACSHA256.HashData(keyBytes, msgBytes);
        return Convert.ToHexString(hash).ToLower();
    }

    private static string GenerateNonce()
    {
        return "n_" + new string(Enumerable.Range(0, NonceLength)
            .Select(_ => NonceAlphabet[Random.Shared.Next(NonceAlphabet.Length)])
            .ToArray());
    }

    private static string BuildUrl(string path, Dictionary<string, string> @params)
    {
        if (@params.Count == 0) return ApiBase + path;
        var qs = string.Join("&", @params.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"{ApiBase}{path}?{qs}";
    }

    public void Dispose() => _http.Dispose();

    // ── DTO ───────────────────────────────────────────────────────────────────

    public sealed class TokenInfo
    {
        [JsonPropertyName("id")]      public string  Id       { get; set; } = "";
        [JsonPropertyName("chain")]   public string  Chain    { get; set; } = "";
        [JsonPropertyName("symbol")]  public string  Symbol   { get; set; } = "";
        [JsonPropertyName("amount")]  public double  Amount   { get; set; }
        [JsonPropertyName("decimals")]public int     Decimals { get; set; }
        [JsonPropertyName("price")]   public double  Price    { get; set; }
        [JsonPropertyName("name")]    public string  Name     { get; set; } = "";

        public decimal ValueUsd => (decimal)(Amount * Price);
        public bool    IsStable => Math.Abs(Price - 1.0) <= 0.01;
    }
}