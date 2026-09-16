using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Маршруты:
///   GET  /clipconv/status   — статус конвертора (хоткеи, гейт, счётчики)
///   POST /clipconv/config   — сохранить { enabled, processName }
///   GET  /clipconv/selftest — прогон преобразования на фикстурах
/// </summary>
public sealed class ClipboardConverterHandler : IScriptHandler
{
    public string PathPrefix => "/clipconv";

    private readonly ClipboardConverterService _service;

    public ClipboardConverterHandler(ClipboardConverterService service)
    {
        _service = service;
    }

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var path   = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = context.Request.HttpMethod;

        if (!path.StartsWith("/clipconv")) return false;

        try
        {
            if (path == "/clipconv/status" && method == "GET")
            {
                await HttpHelpers.WriteJson(context.Response, _service.GetStatus());
                return true;
            }
            if (path == "/clipconv/selftest" && method == "GET")
            {
                var cases = HeSelectorConverter.SelfTest();
                await HttpHelpers.WriteJson(context.Response, new
                {
                    ok    = cases.All(c => c.Passed),
                    cases = cases.Select(c => new
                    {
                        name     = c.Name,
                        passed   = c.Passed,
                        expected = c.Expected,
                        actual   = c.Actual,
                    }),
                });
                return true;
            }
            if (path == "/clipconv/config" && method == "POST")
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

    // ── POST /clipconv/config ─────────────────────────────────────────────
    // Body: { enabled: bool, processName: string }
    private async Task SaveConfig(HttpListenerResponse response, string body)
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        try
        {
            var doc         = JsonSerializer.Deserialize<JsonElement>(body);
            var enabled     = doc.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var processName = doc.TryGetProperty("processName", out var pn) ? pn.GetString() ?? "" : "";

            processName = processName.Trim();   // пусто = гейт выключен, это валидное значение

            // ── merge в существующий конфиг (паттерн ConfigHandler) ─────────
            var existing = new Dictionary<string, JsonElement>();
            if (File.Exists(cfgPath))
            {
                var existingDoc = JsonSerializer.Deserialize<JsonElement>(
                    await File.ReadAllTextAsync(cfgPath, Encoding.UTF8));
                if (existingDoc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in existingDoc.EnumerateObject())
                        existing[prop.Name] = prop.Value;
            }

            existing["ClipboardConfig"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new
                {
                    Enabled     = enabled,
                    ProcessName = processName,
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
}
