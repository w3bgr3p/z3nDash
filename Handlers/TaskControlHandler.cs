using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace z3nDash;

public sealed class TaskControlHandler(SchedulerService scheduler) : IScriptHandler
{
    public string PathPrefix => "/api/v1/self";
    public void Init() { }

    internal static async Task<(DateTimeOffset Until, string Reason)> ReadDefer(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var buffer = new char[16_385];
        var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        if (count == buffer.Length) throw new ArgumentException("Request body is too large");
        using var document = JsonDocument.Parse(new string(buffer, 0, count));
        var body = document.RootElement;
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object");
        var hasDelay = body.TryGetProperty("delay_seconds", out var delay);
        var hasUntil = body.TryGetProperty("until", out var untilValue);
        if (hasDelay == hasUntil) throw new ArgumentException("Provide exactly one of delay_seconds or until");
        DateTimeOffset until;
        if (hasDelay)
        {
            if (delay.ValueKind != JsonValueKind.Number || !delay.TryGetDouble(out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0)
                throw new ArgumentException("delay_seconds must be a positive finite number");
            until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        else
        {
            if (untilValue.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(untilValue.GetString()!, @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
                || !DateTimeOffset.TryParse(untilValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out until))
                throw new ArgumentException("until must be an ISO 8601 timestamp with Z or a UTC offset");
        }
        var reason = "";
        if (body.TryGetProperty("reason", out var reasonValue))
        {
            if (reasonValue.ValueKind != JsonValueKind.String) throw new ArgumentException("reason must be a string");
            reason = reasonValue.GetString()!;
        }
        return (until, reason);
    }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        try
        {
            var auth = request.Headers["Authorization"] ?? "";
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !scheduler.TryGetRun(auth[7..], out var run) || run == null)
            {
                response.StatusCode = 401;
                await HttpHelpers.WriteJson(response, new { error = "Missing or expired run token" });
                return true;
            }
            var action = request.Url!.AbsolutePath.TrimEnd('/');
            TaskControlState state;
            if (action == PathPrefix && request.HttpMethod == "GET")
                state = scheduler.GetControlState(run.TaskId, run.RunId);
            else if (request.HttpMethod == "POST" && action == PathPrefix + "/defer")
            {
                var (until, reason) = await ReadDefer(request);
                state = scheduler.DeferTask(run.TaskId, until, reason, run.RunId);
            }
            else if (request.HttpMethod == "POST" && action == PathPrefix + "/pause")
                state = scheduler.PauseTask(run.TaskId, true, run.RunId);
            else if (request.HttpMethod == "POST" && action == PathPrefix + "/resume")
                state = scheduler.PauseTask(run.TaskId, false, run.RunId);
            else
            {
                response.StatusCode = 404;
                await HttpHelpers.WriteJson(response, new { error = "Unknown self endpoint or method" });
                return true;
            }
            await HttpHelpers.WriteJson(response, state);
        }
        catch (Exception ex)
        {
            response.StatusCode = ex switch
            {
                JsonException or ArgumentException => 400,
                KeyNotFoundException => 404,
                InvalidOperationException => 503,
                _ => 500
            };
            await HttpHelpers.WriteJson(response, new { error = ex.Message });
        }
        return true;
    }
}
