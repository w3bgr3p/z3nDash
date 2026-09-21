using System.Net;
using System.Text.Json;
using static z3nDash.HttpHelpers;

namespace z3nDash;

/// <summary>
/// Пульт отладки шаблона.
///
/// Префикс «/dbg», а не «/zp-debug»: обработчики выбираются по StartsWith, и
/// «/zp-debug/...» перехватывал бы ZpOrchestratorHandler с префиксом «/zp».
///
///   POST /dbg/start   { xml, projectDir, headless } — собрать сессию
///   POST /dbg/step    — выполнить одну ветку
///   POST /dbg/run     — идти до конца, точки останова или паузы
///   POST /dbg/pause   — встать на текущей ветке
///   POST /dbg/runto   { stepId, branchId } — дойти до ветки
///   POST /dbg/stop    — прервать и освободить браузер
///   GET  /dbg/state   — текущее состояние одним ответом
///   GET  /dbg/events  — поток событий (SSE)
///
/// start отвечает синхронно: дешёвые проверки — каталог, разбор шаблона,
/// сборка общего кода — успевают до ответа, и об их провале честнее сказать
/// сразу, а не событием. Остальные команды подтверждаются немедленно, а
/// результат приходит событием: ветка может думать минуту.
/// </summary>
public sealed class ZpDebugHandler : IScriptHandler
{
    public string PathPrefix => "/dbg";

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "";
        var method = ctx.Request.HttpMethod;

        if (!path.StartsWith("/dbg")) return false;

        try
        {
            if (path == "/dbg/events" && method == "GET")
            {
                await SseHub.SubscribeOutput(ctx.Response, "zp-debug", CancellationToken.None);
                return true;
            }

            if (path == "/dbg/state" && method == "GET")
            {
                await WriteJson(ctx.Response, ZpDebugService.CurrentState());
                ctx.Response.Close();
                return true;
            }

            if (method != "POST") return false;

            if (path == "/dbg/start")
            {
                var body = await ReadBody(ctx.Request);
                var (ok, error) = await ZpDebugService.StartAsync(
                    Str(body, "xml"), Str(body, "projectDir"),
                    body.TryGetProperty("headless", out var h) && h.ValueKind == JsonValueKind.True);
                await WriteJson(ctx.Response, new { ok, error });
                ctx.Response.Close();
                return true;
            }

            if (path == "/dbg/stop")
            {
                await ZpDebugService.StopAsync("остановлено с пульта");
                await WriteJson(ctx.Response, new { ok = true });
                ctx.Response.Close();
                return true;
            }

            var cmd = path switch
            {
                "/dbg/step"  => DebugCommand.Step,
                "/dbg/run"   => DebugCommand.Run,
                "/dbg/pause" => DebugCommand.Pause,
                "/dbg/runto" => DebugCommand.RunTo,
                _                 => (DebugCommand?)null
            };
            if (cmd is null) return false;

            if (cmd == DebugCommand.RunTo)
            {
                var body = await ReadBody(ctx.Request);
                ZpDebugService.SetRunTarget(Str(body, "stepId"), Str(body, "branchId"));
            }

            var accepted = ZpDebugService.Enqueue(cmd.Value);
            await WriteJson(ctx.Response, new { ok = accepted, error = accepted ? "" : "сессии нет" });
            ctx.Response.Close();
            return true;
        }
        catch (Exception ex)
        {
            // Текст ошибки — что произошло, без трактовки причины.
            await WriteJson(ctx.Response, new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
            ctx.Response.Close();
            return true;
        }
    }

    private static async Task<JsonElement> ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(text).RootElement;
    }

    private static string Str(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
