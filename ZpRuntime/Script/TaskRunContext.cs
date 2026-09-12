using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace z3nDash;

public sealed record TaskControlState(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("run_id")] string? RunId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("deferred_until")] DateTimeOffset? DeferredUntil,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("schedule_mode")] string ScheduleMode);

/// <summary>Per-run context; never stored in process-wide environment for in-process scripts.</summary>
public sealed class TaskRunContext(string apiBaseUrl, string taskId, string runId, string token)
{
    private static readonly AsyncLocal<TaskRunContext?> Ambient = new();
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    public static TaskRunContext Current => Ambient.Value
        ?? throw new InvalidOperationException("This code is not running as a z3nDash task");
    public string ApiBaseUrl { get; } = apiBaseUrl;
    public string TaskId { get; } = taskId;
    public string RunId { get; } = runId;
    [JsonIgnore] public string Token { get; } = token;

    public static IDisposable Enter(TaskRunContext context)
    {
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(TaskRunContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    public Task<TaskControlState> GetAsync(CancellationToken cancellationToken = default)
        => SendAsync("", null, cancellationToken);

    public Task<TaskControlState> DeferAsync(double seconds, string reason = "", CancellationToken cancellationToken = default)
        => SendAsync("/defer", new { delay_seconds = seconds, reason }, cancellationToken);

    public Task<TaskControlState> DeferUntilAsync(DateTimeOffset until, string reason = "", CancellationToken cancellationToken = default)
        => SendAsync("/defer", new { until = until.ToUniversalTime().ToString("o"), reason }, cancellationToken);

    public Task<TaskControlState> PauseAsync(CancellationToken cancellationToken = default)
        => SendAsync("/pause", new { }, cancellationToken);

    public Task<TaskControlState> ResumeAsync(CancellationToken cancellationToken = default)
        => SendAsync("/resume", new { }, cancellationToken);

    private async Task<TaskControlState> SendAsync(string action, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post,
            ApiBaseUrl.TrimEnd('/') + "/api/v1/self" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"z3nDash API {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadFromJsonAsync<TaskControlState>(cancellationToken: ct)
            ?? throw new HttpRequestException("z3nDash API returned no state");
    }

    public void ApplyEnvironment(IDictionary<string, string?> environment)
    {
        environment["Z3NDASH_API_URL"] = ApiBaseUrl;
        environment["Z3NDASH_RUN_TOKEN"] = Token;
        environment["Z3NDASH_TASK_ID"] = TaskId;
        environment["Z3NDASH_RUN_ID"] = RunId;
    }
}
