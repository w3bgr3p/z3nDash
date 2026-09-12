using System.Net;

namespace z3nDash;

/// <summary>
/// Локальный трафик самого z3nDash: хвост &lt;logsFolder&gt;/trafficLog.jsonl,
/// который пишет ZpRuntime. Формат ответа тот же, что у ноды z3n7 на /traffic,
/// поэтому фронт разбирает оба источника одним кодом.
///
///   GET /traffic?tail=200&amp;project=…&amp;task_id=…
/// </summary>
internal sealed class TrafficHandler
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    public bool Matches(string path, string method) => method == "GET" && path == "/traffic";

    public async Task Handle(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;

        if (!string.IsNullOrWhiteSpace(query["tail"]) && !int.TryParse(query["tail"], out _))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "tail must be a number" });
            return;
        }

        var limit = Math.Clamp(int.TryParse(query["tail"], out var value) ? value : DefaultLimit, 1, MaxLimit);

        try
        {
            await HttpHelpers.WriteJson(ctx.Response, ZpTraffic.Tail(limit, query["project"], query["task_id"]));
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }
}
