using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Откуда брать браузер для xml-шаблона. Patchright поднимает свой, остальные
/// режимы отдают управление уже запущенным браузером по CDP.
/// </summary>
public sealed class BrowserConfig
{
    public string Mode = "patchright";   // patchright | zennobrowser | shardx | cdp | api
    public string Profile = "";          // id профиля по умолчанию, payload его перекрывает
    public string Cdp = "";              // готовый эндпоинт для mode=cdp

    public string ApiMethod   = "GET";
    public string ApiUrl      = "";
    public string ApiBody     = "";
    public string ApiWs       = "";      // шаблон эндпоинта: {data.ws.puppeteer}
    public string ApiToken    = "";      // Bearer-токен для API, если требуется
    public string StopMethod  = "GET";
    public string StopUrl     = "";

    public bool CloseAfterRun = true;

    /// <summary>
    /// Откуда брать прокси на старт браузера. Признак задачи, а не правило для
    /// всех: у разных шаблонов прокси устроен по-разному.
    ///
    ///   payload  — из данных запуска (по умолчанию);
    ///   template — из переменной шаблона с именем "proxy";
    ///   none     — не давать вовсе: шаблон ставит его сам своим SetProxy.
    ///
    /// "template" годится только там, где эта переменная — вход. Бывает и
    /// наоборот: в simroute.megapari.xml шаблон строит прокси из env и кладёт
    /// результат в неё (project.Var("proxy", proxyString)), то есть до запуска
    /// в ней лежит прокси прошлого прогона. Браузер поднимался на нём и получал
    /// пять отказов авторизации, пока шаблон не ставил актуальный. Для таких
    /// задач здесь "none".
    /// </summary>
    public string ProxySource = "payload";   // payload | template | none

    /// <summary>
    /// Окно браузера. По умолчанию его нет: в прогоне оно не нужно, а мешает.
    /// Включается на время разбора — посмотреть, на чём встал шаблон.
    /// Касается только режима patchright: в остальных браузер поднимаем не мы.
    /// </summary>
    public bool Headless = true;

    public static BrowserConfig Parse(string json)
    {
        var cfg = new BrowserConfig();
        if (string.IsNullOrWhiteSpace(json)) return cfg;

        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch { return cfg; }

        cfg.Mode    = Str(root, "mode", cfg.Mode);
        cfg.Profile = Str(root, "profile", "");
        cfg.Cdp     = Str(root, "cdp", "");
        if (root.TryGetProperty("close", out var cl) && cl.ValueKind == JsonValueKind.False)
            cfg.CloseAfterRun = false;
        if (root.TryGetProperty("headless", out var hl) && hl.ValueKind == JsonValueKind.False)
            cfg.Headless = false;

        var src = Str(root, "proxySource", cfg.ProxySource).ToLowerInvariant();
        if (src is "payload" or "template" or "none") cfg.ProxySource = src;

        if (!root.TryGetProperty("api", out var api) || api.ValueKind != JsonValueKind.Object)
            return cfg;

        cfg.ApiMethod  = Str(api, "method", "GET").ToUpperInvariant();
        cfg.ApiUrl     = Str(api, "url", "");
        cfg.ApiBody    = Str(api, "body", "");
        cfg.ApiWs      = Str(api, "ws", "");
        cfg.ApiToken   = Str(api, "token", "");
        cfg.StopMethod = Str(api, "stopMethod", "GET").ToUpperInvariant();
        cfg.StopUrl    = Str(api, "stopUrl", "");
        return cfg;
    }

    private static string Str(JsonElement el, string name, string fallback)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
}

/// <summary>
/// Клиент локальных API антидетект-браузеров, включая отдельный контракт ShardX. Разные антики отдают
/// эндпоинт по-разному: AdsPower одним полем data.ws.puppeteer, Dolphin —
/// портом и путём по отдельности. Поэтому эндпоинт не «путь к полю», а шаблон,
/// в который подставляются любые поля ответа: ws://127.0.0.1:{automation.port}{automation.wsEndpoint}.
/// </summary>
public static class BrowserProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    internal static bool UsesProviderApi(string mode)
        => mode.Equals("api", StringComparison.OrdinalIgnoreCase)
           || mode.Equals("shardx", StringComparison.OrdinalIgnoreCase);

    internal static void ApplyGlobalConfig(BrowserConfig cfg, BrowsersApiConfig global)
    {
        if (!cfg.Mode.Equals("shardx", StringComparison.OrdinalIgnoreCase)) return;
        cfg.ApiUrl = global.ShardX.Host;
        cfg.ApiToken = global.ShardX.Token;
    }

    /// <summary>Запустить профиль и получить CDP-эндпоинт. Пустая строка — не получилось.</summary>
    public static async Task<string> StartAsync(BrowserConfig cfg, string profile, Action<string>? log = null)
    {
        if (cfg.Mode.Equals("shardx", StringComparison.OrdinalIgnoreCase))
            return await StartShardXAsync(cfg, profile, log);

        if (string.IsNullOrWhiteSpace(cfg.ApiUrl)) { log?.Invoke("[br] не задан URL старта профиля"); return ""; }
        if (string.IsNullOrWhiteSpace(cfg.ApiWs))  { log?.Invoke("[br] не задан шаблон CDP-эндпоинта"); return ""; }

        var url = Substitute(cfg.ApiUrl, profile, null);
        log?.Invoke($"[br] старт профиля: {cfg.ApiMethod} {url}");

        string body;
        try { body = await SendAsync(cfg.ApiMethod, url, Substitute(cfg.ApiBody, profile, null)); }
        catch (Exception ex) { log?.Invoke($"[br] запрос не прошёл: {ex.Message}"); return ""; }

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch { log?.Invoke($"[br] ответ неJSON: {Trim(body)}"); return ""; }

        var ws = Substitute(cfg.ApiWs, profile, root);
        if (ws.Contains('{'))
        {
            log?.Invoke($"[br] в ответе нет полей для шаблона {cfg.ApiWs}: {Trim(body)}");
            return "";
        }

        log?.Invoke($"[br] подключаюсь к {ws}");
        return ws;
    }

    /// <summary>Закрыть профиль. Отказ не считается ошибкой прогона — шаблон уже отработал.</summary>
    public static async Task StopAsync(BrowserConfig cfg, string profile, Action<string>? log = null)
    {
        if (cfg.Mode.Equals("shardx", StringComparison.OrdinalIgnoreCase))
        {
            await StopShardXAsync(cfg, profile, log);
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.StopUrl)) return;

        var url = Substitute(cfg.StopUrl, profile, null);
        try
        {
            await SendAsync(cfg.StopMethod, url, "");
            log?.Invoke($"[br] профиль закрыт: {url}");
        }
        catch (Exception ex) { log?.Invoke($"[br] закрыть профиль не удалось: {ex.Message}"); }
    }

    private static async Task<string> StartShardXAsync(BrowserConfig cfg, string profile, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(profile)) { log?.Invoke("[br] ShardX: не задан UUID профиля"); return ""; }
        if (string.IsNullOrWhiteSpace(cfg.ApiUrl)) { log?.Invoke("[br] ShardX: не задан URL API"); return ""; }
        if (string.IsNullOrWhiteSpace(cfg.ApiToken)) { log?.Invoke("[br] ShardX: не задан Bearer-токен"); return ""; }

        using var req = CreateShardXRequest(cfg, profile, stop: false);
        log?.Invoke($"[br] ShardX: POST {req.RequestUri}");
        try
        {
            using var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                log?.Invoke($"[br] ShardX: HTTP {(int)res.StatusCode}: {Trim(body)}");
                return "";
            }

            var ws = ExtractShardXCdp(body);
            if (string.IsNullOrWhiteSpace(ws))
            {
                log?.Invoke($"[br] ShardX не отдал cdp.web_socket_debugger_url: {Trim(body)}");
                return "";
            }

            log?.Invoke($"[br] ShardX: подключаюсь к {ws}");
            return ws;
        }
        catch (Exception ex) { log?.Invoke($"[br] ShardX: запрос запуска не прошёл: {ex.Message}"); return ""; }
    }

    private static async Task StopShardXAsync(BrowserConfig cfg, string profile, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(cfg.ApiUrl) || string.IsNullOrWhiteSpace(cfg.ApiToken))
            return;

        using var req = CreateShardXRequest(cfg, profile, stop: true);
        try
        {
            using var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                log?.Invoke($"[br] ShardX: закрытие вернуло HTTP {(int)res.StatusCode}: {Trim(body)}");
            else
                log?.Invoke($"[br] ShardX: профиль закрыт {profile}");
        }
        catch (Exception ex) { log?.Invoke($"[br] ShardX: закрыть профиль не удалось: {ex.Message}"); }
    }

    internal static HttpRequestMessage CreateShardXRequest(BrowserConfig cfg, string profile, bool stop)
    {
        var baseUrl = cfg.ApiUrl.Trim().TrimEnd('/');
        var action = stop ? "stop" : "start";
        var url = $"{baseUrl}/profiles/{Uri.EscapeDataString(profile.Trim())}/{action}";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiToken.Trim());
        return req;
    }

    internal static string ExtractShardXCdp(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("cdp", out var cdp) || cdp.ValueKind != JsonValueKind.Object)
            return "";
        if (!cdp.TryGetProperty("web_socket_debugger_url", out var ws) || ws.ValueKind != JsonValueKind.String)
            return "";
        return ws.GetString() ?? "";
    }

    private static async Task<string> SendAsync(string method, string url, string body)
    {
        using var req = new HttpRequestMessage(
            method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
        if (method == "POST" && !string.IsNullOrWhiteSpace(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var res = await Http.SendAsync(req);
        return await res.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Подставляет {profile} и поля ответа по точечному пути: {data.ws.puppeteer}.
    /// Незнакомые плейсхолдеры остаются на месте — по ним и видно, что ответ
    /// оказался не такой, как ждали.
    /// </summary>
    internal static string Substitute(string template, string profile, JsonElement? json)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { sb.Append(template[i]); continue; }

            var close = template.IndexOf('}', i);
            if (close < 0) { sb.Append(template[i]); continue; }

            var key = template[(i + 1)..close];
            if (key == "profile")
            {
                sb.Append(Uri.EscapeDataString(profile));
                i = close;
                continue;
            }

            var value = json.HasValue ? Lookup(json.Value, key) : null;
            if (value is null) { sb.Append(template[i..(close + 1)]); }
            else               { sb.Append(value); }
            i = close;
        }
        return sb.ToString();
    }

    private static string? Lookup(JsonElement root, string path)
    {
        var cur = root;
        foreach (var part in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next))
                return null;
            cur = next;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.ToString(),
            _ => null,
        };
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
