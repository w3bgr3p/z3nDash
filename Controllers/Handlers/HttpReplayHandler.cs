using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace z3n8;

internal sealed class HttpReplayHandler
{
    private static readonly HttpClient _client = new(new HttpClientHandler
    {
        AutomaticDecompression           = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect                = true,
        MaxAutomaticRedirections         = 5,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task Handle(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var bodyJson = await reader.ReadToEndAsync();

        JsonElement req;
        try { req = JsonSerializer.Deserialize<JsonElement>(bodyJson); }
        catch { await HttpHelpers.WriteJson(ctx.Response, new { error = "Invalid JSON" }); return; }

        var url    = req.TryGetProperty("url",    out var u) ? u.GetString() ?? "" : "";
        var method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
        var headers = req.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var body   = req.TryGetProperty("body",   out var b) ? b.GetString() : null;

        if (string.IsNullOrEmpty(url)) { await HttpHelpers.WriteJson(ctx.Response, new { error = "url required" }); return; }

        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var msg = new HttpRequestMessage(new HttpMethod(method), url);

        if (headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headers.EnumerateObject())
                try { msg.Headers.TryAddWithoutValidation(prop.Name, prop.Value.GetString()); } catch { }
        }

        if (!string.IsNullOrEmpty(body) && method != "GET" && method != "HEAD")
        {
            var ct = "application/json";
            try { if (msg.Headers.Contains("content-type")) ct = msg.Headers.GetValues("content-type").First(); } catch { }
            msg.Content = new StringContent(body, System.Text.Encoding.UTF8, ct);
        }

        try
        {
            var res = await _client.SendAsync(msg);
            sw.Stop();

            var responseBody    = await res.Content.ReadAsStringAsync();
            var responseHeaders = new Dictionary<string, string>();
            foreach (var kv in res.Headers)         responseHeaders[kv.Key] = string.Join(", ", kv.Value);
            foreach (var kv in res.Content.Headers) responseHeaders[kv.Key] = string.Join(", ", kv.Value);

            await HttpHelpers.WriteJson(ctx.Response, new
            {
                statusCode      = (int)res.StatusCode,
                statusText      = res.ReasonPhrase,
                responseHeaders,
                responseBody,
                durationMs      = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message, durationMs = sw.ElapsedMilliseconds });
        }
    }
}
