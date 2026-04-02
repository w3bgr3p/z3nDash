using System.Net;
using System.Text.Json;

namespace z3nIO;

internal sealed class TrafficHandler
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TrafficHandler(string logPath)
    {
        _logPath = logPath;
    }

    public bool Matches(string path, string method) =>
        (method == "POST" && path is "/traffic" or "/clear-traffic") ||
        (method == "GET"  && path is "/traffic-logs" or "/traffic-stats");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "POST" && path == "/traffic")
        {
            using var r = new StreamReader(ctx.Request.InputStream);
            await Save(await r.ReadToEndAsync());
            ctx.Response.StatusCode = 200;
            return;
        }

        if (method == "GET" && path == "/traffic-logs")
        {
            var q     = ctx.Request.QueryString;
            int limit = int.TryParse(q["limit"], out var l) ? l : 100;
            await HttpHelpers.WriteJson(ctx.Response, await Read(limit, q["project"], q["account"], q["session"], q["task_id"]));
            return;
        }

        if (method == "GET" && path == "/traffic-stats")
        {
            await HttpHelpers.WriteJson(ctx.Response, await GetStats());
            return;
        }

        if (method == "POST" && path == "/clear-traffic")
        {
            await _lock.WaitAsync();
            try
            {
                string f = Path.Combine(_logPath, "traffic-current.jsonl");
                if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                await HttpHelpers.WriteText(ctx.Response, "OK");
            }
            finally { _lock.Release(); }
        }
    }

    private async Task Save(string json)
    {
        await _lock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "traffic-current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 50 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, $"traffic_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
            $" {json} saved to {f}".Debug();
            
        }
        finally { _lock.Release(); }
    }

    private async Task<List<object>> Read(int limit, string? project, string? account, string? session, string? taskId)
    {
        var result   = new List<object>();
        string filePath = Path.Combine(_logPath, "traffic-current.jsonl");
        if (!File.Exists(filePath)) return result;

        foreach (var line in (await File.ReadAllLinesAsync(filePath)).Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var log = JsonSerializer.Deserialize<JsonElement>(line);
                if (!string.IsNullOrEmpty(project) && (!log.TryGetProperty("project", out var p) || !p.ToString().Contains(project, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrEmpty(account) && (!log.TryGetProperty("account", out var a) || !a.ToString().Contains(account, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrEmpty(session) && (!log.TryGetProperty("session", out var s) || !s.ToString().Contains(session, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrEmpty(taskId)  && (!log.TryGetProperty("task_id", out var t) || !t.ToString().Equals(taskId,  StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(log);
                if (result.Count >= limit) return result;
            }
            catch { continue; }
        }
        return result;
    }

    private async Task<object> GetStats()
    {
        var logs     = await Read(2000, null, null, null, null);
        var methods  = new Dictionary<string, int>(); var statuses  = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>(); var accounts  = new Dictionary<string, int>();
        var urls     = new Dictionary<string, int>();
        long totalDuration = 0; int durationCount = 0;

        foreach (JsonElement log in logs)
        {
            try
            {
                string m   = log.TryGetProperty("method",     out var mv) ? mv.ToString() : "UNKNOWN";
                int sc     = log.TryGetProperty("statusCode", out var sv) && sv.TryGetInt32(out var sci) ? sci : 0;
                string sg  = sc >= 500 ? "5xx" : sc >= 400 ? "4xx" : sc >= 300 ? "3xx" : sc >= 200 ? "2xx" : "0xx";
                string prj = log.TryGetProperty("project",    out var pv) ? pv.ToString() : "UNKNOWN";
                string acc = log.TryGetProperty("account",    out var av) ? av.ToString() : "";
                string u   = log.TryGetProperty("url",        out var uv) ? uv.ToString() : "";
                try { u = new Uri(u).Host; } catch { }

                methods[m]    = methods.GetValueOrDefault(m)    + 1;
                statuses[sg]  = statuses.GetValueOrDefault(sg)  + 1;
                projects[prj] = projects.GetValueOrDefault(prj) + 1;
                urls[u]       = urls.GetValueOrDefault(u)       + 1;
                if (!string.IsNullOrEmpty(acc))
                    accounts[acc] = accounts.GetValueOrDefault(acc) + 1;
                if (log.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var ms)) 
                { totalDuration += ms; durationCount++; }
            }
            catch { continue; }
        }

        return new { 
            totalRequests = logs.Count,
            byMethod = methods,
            byStatus = statuses,
            byProject = projects,
            byAccount = accounts,
            byUrl = urls.OrderByDescending(kv => kv.Value).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value),
            avgDurationMs = durationCount > 0 ? totalDuration / durationCount : 0 
        };
    }
}

