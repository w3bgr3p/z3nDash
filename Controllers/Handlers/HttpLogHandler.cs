using System.Net;
using System.Text.Json;

namespace z3n8;

internal sealed class HttpLogHandler
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public HttpLogHandler(string logPath)
    {
        _logPath = logPath;
    }

    public bool Matches(string path, string method) =>
        (method == "POST" && path is "/http-log" or "/clear-http" or "/clear-http-logs-by-task") ||
        (method == "GET"  && path is "/http-logs" or "/http-stats" or "/http-logs/stream");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "POST" && path == "/http-log")
        {
            using var r = new StreamReader(ctx.Request.InputStream);
            await Save(await r.ReadToEndAsync());
            ctx.Response.StatusCode = 200;
            return;
        }

        if (method == "GET" && path == "/http-logs")
        {
            var q     = ctx.Request.QueryString;
            int limit = int.TryParse(q["limit"], out var l) ? l : 100;
            await HttpHelpers.WriteJson(ctx.Response, await Read(limit, q["method"], q["url"], q["status"], q["machine"], q["project"], q["session"], q["account"], q["cookiesSource"], q["task_id"]));
            return;
        }

        if (method == "GET" && path == "/http-logs/stream")
        {
            var taskId = ctx.Request.QueryString["task_id"] ?? "";
            await SseHub.SubscribeHttp(ctx.Response, taskId, GetDisconnectToken(ctx));
            return;
        }

        if (method == "GET" && path == "/http-stats")
        {
            await HttpHelpers.WriteJson(ctx.Response, await GetStats());
            return;
        }

        if (method == "POST" && path == "/clear-http")
        {
            await _lock.WaitAsync();
            try
            {
                string f = Path.Combine(_logPath, "http-current.jsonl");
                if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                foreach (var old in Directory.GetFiles(_logPath, "http-log_*.jsonl")) File.Delete(old);
                await HttpHelpers.WriteText(ctx.Response, "OK");
            }
            finally { _lock.Release(); }
            return;
        }

        if (method == "POST" && path == "/clear-http-logs-by-task")
        {
            var (ok, taskId) = await HttpHelpers.ReadTaskId(ctx.Request);
            if (!ok) { ctx.Response.StatusCode = 400; return; }
            int removed = await DeleteByTaskId("http-current.jsonl", taskId!);
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, removed });
        }
    }

    private async Task Save(string json)
    {
        await _lock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "http-current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 50 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, $"http-log_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
        }
        finally { _lock.Release(); }

        BroadcastToSse(json);
    }

    private static void BroadcastToSse(string json)
    {
        try
        {
            var log    = JsonSerializer.Deserialize<JsonElement>(json);
            string tid = (log.TryGetProperty("task_id", out var t1) ? t1.ToString() : null)
                         ?? (log.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null)
                         ?? (log.TryGetProperty("diagnostics", out var diag) && diag.TryGetProperty("task_id", out var dt) ? dt.ToString() : null)
                         ?? "";
            if (!string.IsNullOrEmpty(tid))
                SseHub.BroadcastHttp(json, tid);
        }
        catch { }
    }

    private async Task<List<object>> Read(int limit, string? method, string? url, string? status,
        string? machine, string? project, string? session, string? account, string? cookiesSource, string? taskId = null)
    {
        var result = new List<object>();
        var files  = Directory.GetFiles(_logPath, "http-current.jsonl").OrderByDescending(File.GetCreationTime).Take(5);

        foreach (var file in files)
        {
            foreach (var line in (await File.ReadAllLinesAsync(file)).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<JsonElement>(line);
                    if (!string.IsNullOrEmpty(method)  && !log.GetProperty("method").ToString().Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(url)     && !log.GetProperty("url").ToString().Contains(url, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(status)  && !log.GetProperty("statusCode").ToString().StartsWith(status)) continue;
                    if (!string.IsNullOrEmpty(machine) && !log.GetProperty("machine").ToString().Contains(machine, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(project) && !log.GetProperty("project").ToString().Contains(project, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(session) && (!log.TryGetProperty("session", out var sp) || !sp.ToString().Equals(session, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(account) && (!log.TryGetProperty("account", out var ap) || !ap.ToString().Equals(account, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(cookiesSource) && (!log.TryGetProperty("request", out var req) || !req.TryGetProperty("cookiesSource", out var cs) || !cs.ToString().Contains(cookiesSource, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        string tid = (log.TryGetProperty("task_id", out var t1) ? t1.ToString() : null)
                                     ?? (log.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null)
                                     ?? (log.TryGetProperty("diagnostics", out var diag) && diag.TryGetProperty("task_id", out var dt) ? dt.ToString() : null)
                                     ?? "";
                        if (!tid.Equals(taskId, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    result.Add(log);
                    if (result.Count >= limit) return result;
                }
                catch { continue; }
            }
        }
        return result;
    }

    private async Task<object> GetStats()
    {
        var logs         = await Read(2000, null, null, null, null, null, null, null, null);
        var methods      = new Dictionary<string, int>(); var statuses      = new Dictionary<string, int>();
        var urls         = new Dictionary<string, int>(); var machines      = new Dictionary<string, int>();
        var projects     = new Dictionary<string, int>(); var accounts      = new Dictionary<string, int>();
        var cookieSources = new Dictionary<string, int>();
        long totalDuration = 0; int durationCount = 0;

        foreach (JsonElement log in logs)
        {
            try
            {
                string m   = log.TryGetProperty("method",     out var mv)  ? mv.ToString()  : "UNKNOWN";
                int sc     = log.TryGetProperty("statusCode", out var sv)  ? sv.GetInt32()  : 0;
                string sg  = sc >= 500 ? "5xx" : sc >= 400 ? "4xx" : sc >= 300 ? "3xx" : sc >= 200 ? "2xx" : "0xx";
                string u   = log.TryGetProperty("url",        out var uv)  ? uv.ToString()  : "UNKNOWN";
                string mc  = log.TryGetProperty("machine",    out var mcv) ? mcv.ToString() : "UNKNOWN";
                string prj = log.TryGetProperty("project",    out var pv)  ? pv.ToString()  : "UNKNOWN";
                string acc = log.TryGetProperty("account",    out var av)  ? av.ToString()  : "";
                string src = log.TryGetProperty("cookiesSource", out var csv) ? csv.ToString() : "";
                try { u = new Uri(u).Host; } catch { }

                methods[m]    = methods.GetValueOrDefault(m)    + 1;
                statuses[sg]  = statuses.GetValueOrDefault(sg)  + 1;
                urls[u]       = urls.GetValueOrDefault(u)       + 1;
                machines[mc]  = machines.GetValueOrDefault(mc)  + 1;
                projects[prj] = projects.GetValueOrDefault(prj) + 1;
                if (!string.IsNullOrEmpty(acc)) accounts[acc]      = accounts.GetValueOrDefault(acc)      + 1;
                cookieSources[src] = cookieSources.GetValueOrDefault(src) + 1;
                if (log.TryGetProperty("durationMs", out var d)) { totalDuration += d.GetInt64(); durationCount++; }
            }
            catch { continue; }
        }

        return new { totalRequests = logs.Count, byMethod = methods, byStatus = statuses,
                     byUrl = urls.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key, kv => kv.Value),
                     byMachine = machines, byProject = projects, byAccount = accounts,
                     avgDurationMs = durationCount > 0 ? totalDuration / durationCount : 0,
                     byCookieSource = cookieSources };
    }

    private async Task<int> DeleteByTaskId(string fileName, string taskId)
    {
        await _lock.WaitAsync();
        try
        {
            string filePath = Path.Combine(_logPath, fileName);
            if (!File.Exists(filePath)) return 0;

            var lines   = await File.ReadAllLinesAsync(filePath);
            var kept    = new List<string>();
            int removed = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<JsonElement>(line);
                    string tid = (log.TryGetProperty("task_id", out var t1) ? t1.ToString() : null)
                                 ?? (log.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null)
                                 ?? (log.TryGetProperty("diagnostics", out var diag) && diag.TryGetProperty("task_id", out var dt) ? dt.ToString() : null)
                                 ?? "";
                    if (tid.Equals(taskId, StringComparison.OrdinalIgnoreCase)) removed++;
                    else kept.Add(line);
                }
                catch { kept.Add(line); }
            }

            await File.WriteAllLinesAsync(filePath, kept);
            return removed;
        }
        finally { _lock.Release(); }
    }

    private static CancellationToken GetDisconnectToken(HttpListenerContext ctx)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(5000);
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(Array.Empty<byte>());
                    await ctx.Response.OutputStream.FlushAsync();
                }
                catch { cts.Cancel(); break; }
            }
        });
        return cts.Token;
    }
}