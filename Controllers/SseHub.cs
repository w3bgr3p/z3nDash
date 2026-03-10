using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace z3n8;

/// <summary>
/// Центральный хаб SSE-подписок.
/// Три канала: logs, http-logs, output (per task id).
/// </summary>
internal static class SseHub
{
    private sealed class Subscriber
    {
        public HttpListenerResponse Response { get; }
        public string               Filter   { get; }  // task_id или schedule id
        public CancellationToken    Token    { get; }

        public Subscriber(HttpListenerResponse response, string filter, CancellationToken token)
        {
            Response = response;
            Filter   = filter;
            Token    = token;
        }
    }

    private static readonly ConcurrentDictionary<Guid, Subscriber> _logs   = new();
    private static readonly ConcurrentDictionary<Guid, Subscriber> _http   = new();
    private static readonly ConcurrentDictionary<Guid, Subscriber> _output = new();

    // ── Subscribe ─────────────────────────────────────────────────────────────

    public static async Task SubscribeLogs(HttpListenerResponse res, string taskId, CancellationToken ct)
        => await Keep(_logs, res, taskId, ct);

    public static async Task SubscribeHttp(HttpListenerResponse res, string taskId, CancellationToken ct)
        => await Keep(_http, res, taskId, ct);

    public static async Task SubscribeOutput(HttpListenerResponse res, string scheduleId, CancellationToken ct)
        => await Keep(_output, res, scheduleId, ct);

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public static void BroadcastLog(string json, string taskId)
        => Broadcast(_logs, json, taskId);

    public static void BroadcastHttp(string json, string taskId)
        => Broadcast(_http, json, taskId);

    public static void BroadcastOutput(string line, string scheduleId)
        => Broadcast(_output, line, scheduleId, isOutput: true);

    // ── Core ──────────────────────────────────────────────────────────────────

    private static async Task Keep(
        ConcurrentDictionary<Guid, Subscriber> bucket,
        HttpListenerResponse res,
        string filter,
        CancellationToken ct)
    {
        var id  = Guid.NewGuid();
        var sub = new Subscriber(res, filter, ct);

        res.ContentType              = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        res.SendChunked              = true;

        // отправить пустой ping чтобы браузер понял что соединение живое
        try { await WriteEvent(res, "ping", "{}"); } catch { return; }

        bucket[id] = sub;

        try
        {
            // держим соединение пока клиент не отключится или сервер не остановится
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            bucket.TryRemove(id, out _);
            try { res.Close(); } catch { }
        }
    }

    private static void Broadcast(
        ConcurrentDictionary<Guid, Subscriber> bucket,
        string data,
        string filter,
        bool isOutput = false)
    {
        foreach (var (id, sub) in bucket)
        {
            if (sub.Token.IsCancellationRequested)
            {
                bucket.TryRemove(id, out _);
                continue;
            }

            // пустой filter на подписчике = получает всё
            if (!string.IsNullOrEmpty(sub.Filter) &&
                !filter.Equals(sub.Filter, StringComparison.OrdinalIgnoreCase))
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    string evt = isOutput ? "output" : "message";
                    await WriteEvent(sub.Response, evt, data);
                }
                catch
                {
                    bucket.TryRemove(id, out _);
                }
            });
        }
    }

    private static async Task WriteEvent(HttpListenerResponse res, string eventName, string data)
    {
        var text = $"event: {eventName}\ndata: {data}\n\n";
        var buf  = Encoding.UTF8.GetBytes(text);
        await res.OutputStream.WriteAsync(buf);
        await res.OutputStream.FlushAsync();
    }
}