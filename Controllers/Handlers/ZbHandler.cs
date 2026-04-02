using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nIO;

internal sealed class ZbHandler
{
    private readonly HttpClient _http;

    // ZB API base: из конфига, дефолт localhost:8160
    private static string ZbBase =>
        !string.IsNullOrWhiteSpace(Config.ApiConfig.ZbHost)
            ? Config.ApiConfig.ZbHost.TrimEnd('/')
            : "http://localhost:8160";

    private static string ZbKey => Config.ApiConfig.ZB;

    public ZbHandler()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool Matches(string path) =>
        path.StartsWith("/zb/");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        // POST /zb/process/kill  — принудительное завершение процесса по PID
        if (method == "POST" && path == "/zb/process/kill")
        {
            await KillProcess(ctx);
            return;
        }

        // GET /zb/process/uptime?pids=123,456
        if (method == "GET" && path == "/zb/process/uptime")
        {
            await GetProcessUptime(ctx);
            return;
        }

        // /zb/api/* → проксируем в ZB API
        if (path.StartsWith("/zb/api/"))
        {
            await Proxy(ctx, path);
            return;
        }

        ctx.Response.StatusCode = 404;
        await HttpHelpers.WriteText(ctx.Response, "Not found");
    }

    // ── Proxy ─────────────────────────────────────────────────────────────────

    private async Task Proxy(HttpListenerContext ctx, string path)
    {
        // /zb/api/v1/profiles → http://zbhost/v1/profiles
        var zbPath = path["/zb/api".Length..]; // оставляем /v1/...
        var query  = ctx.Request.Url?.Query ?? "";
        var target = ZbBase + zbPath + query;

        var req = new HttpRequestMessage
        {
            Method     = new HttpMethod(ctx.Request.HttpMethod),
            RequestUri = new Uri(target),
        };

        req.Headers.TryAddWithoutValidation("Api-Token", ZbKey);

        // Пробрасываем тело для POST/PUT/DELETE с телом
        if (ctx.Request.HasEntityBody)
        {
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            req.Content = new ByteArrayContent(ms.ToArray());
            var ct = ctx.Request.ContentType;
            if (!string.IsNullOrEmpty(ct))
                req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        try
        {
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsByteArrayAsync();

            ctx.Response.StatusCode  = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 502;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "ZB unreachable: " + ex.Message });
        }
    }

    // ── Kill process ──────────────────────────────────────────────────────────

    private static async Task KillProcess(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var doc  = JsonSerializer.Deserialize<JsonElement>(body);

            if (!doc.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid))
            {
                ctx.Response.StatusCode = 400;
                await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "pid (int) required" });
                return;
            }

            var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);

            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, pid, killed = true });
        }
        catch (ArgumentException)
        {
            // процесс уже не существует — считаем успехом
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, killed = false, note = "Process not found (already dead)" });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
        }
    }

    // ── Process uptime ────────────────────────────────────────────────────────

    private static async Task GetProcessUptime(HttpListenerContext ctx)
    {
        var pidsParam = ctx.Request.QueryString["pids"] ?? "";
        var result    = new Dictionary<string, object?>();

        foreach (var part in pidsParam.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out var pid)) continue;
            try
            {
                var proc      = Process.GetProcessById(pid);
                var startTime = proc.StartTime.ToUniversalTime();
                var uptimeSec = (int)(DateTime.UtcNow - startTime).TotalSeconds;
                result[pid.ToString()] = new { startTime = startTime.ToString("O"), uptimeSeconds = uptimeSec };
            }
            catch
            {
                result[pid.ToString()] = null; // процесс не найден
            }
        }

        await HttpHelpers.WriteJson(ctx.Response, result);
    }
}