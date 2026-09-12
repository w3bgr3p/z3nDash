using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Маршруты:
///   GET  /watchdog/status  — текущий статус сторожа (память, лимит, pid)
///   POST /watchdog/config  — сохранить настройки { enabled, processName, limitMb, intervalSec }
/// </summary>
public sealed class MemoryWatchdogHandler : IScriptHandler
{
    public string PathPrefix => "/watchdog";

    private readonly MemoryWatchdogService _service;

    public MemoryWatchdogHandler(MemoryWatchdogService service)
    {
        _service = service;
    }

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var path   = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = context.Request.HttpMethod;

        if (!path.StartsWith("/watchdog")) return false;

        try
        {
            if (path == "/watchdog/status" && method == "GET")
            {
                await HttpHelpers.WriteJson(context.Response, _service.GetStatus());
                return true;
            }
            if (path == "/watchdog/config" && method == "POST")
            {
                using var r = new StreamReader(context.Request.InputStream);
                await SaveConfig(context.Response, await r.ReadToEndAsync());
                return true;
            }

            context.Response.StatusCode = 404;
            await HttpHelpers.WriteJson(context.Response, new { error = "Not found" });
            return true;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(context.Response, new { ok = false, error = ex.Message });
            return true;
        }
    }

    // ── POST /watchdog/config ─────────────────────────────────────────────
    // Body: { enabled: bool, processName: string, limitMb: int, intervalSec: int }
    private async Task SaveConfig(HttpListenerResponse response, string body)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        try
        {
            var doc         = JsonSerializer.Deserialize<JsonElement>(body);
            var enabled     = doc.TryGetProperty("enabled",     out var e)  && e.ValueKind == JsonValueKind.True;
            var processName = doc.TryGetProperty("processName", out var pn) ? pn.GetString() ?? "ZennoPoster" : "ZennoPoster";
            var limitMb     = doc.TryGetProperty("limitMb",     out var lm) ? ReadInt(lm) : 0;
            var intervalSec = doc.TryGetProperty("intervalSec", out var iv) ? ReadInt(iv) : 15;

            if (string.IsNullOrWhiteSpace(processName)) processName = "ZennoPoster";
            if (intervalSec < 1) intervalSec = 15;
            if (limitMb < 0) limitMb = 0;

            // ── merge в существующий конфиг (паттерн ConfigHandler) ─────────
            var existing = new Dictionary<string, JsonElement>();
            if (File.Exists(cfgPath))
            {
                var existingDoc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
                if (existingDoc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in existingDoc.EnumerateObject())
                        existing[prop.Name] = prop.Value;
            }

            existing["WatchdogConfig"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new
                {
                    Enabled     = enabled,
                    ProcessName = processName,
                    LimitMb     = limitMb,
                    IntervalSec = intervalSec,
                }));

            if (File.Exists(cfgPath)) File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);
            await File.WriteAllTextAsync(cfgPath,
                JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            Config.Init();
            _service.Reload();

            await HttpHelpers.WriteJson(response, new { ok = true, status = _service.GetStatus() });
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await HttpHelpers.WriteJson(response, new { ok = false, error = ex.Message });
        }
    }

    private static int ReadInt(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number                                  => el.TryGetInt32(out var n) ? n : 0,
        JsonValueKind.String when int.TryParse(el.GetString(), out var s) => s,
        _                                                     => 0,
    };
}
