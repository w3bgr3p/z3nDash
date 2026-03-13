using System.Net;
using System.Text.Json;

namespace z3n8;

internal sealed class LogHandler
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _debug = false;

    public LogHandler(string logPath)
    {
        _logPath = logPath;
    }

    public bool Matches(string path, string method) =>
        (method == "POST" && path is "/log" or "/clear" or "/clear-logs-by-task") ||
        (method == "GET"  && path is "/logs" or "/stats" or "/logs/stream");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        
        
        
        if (method == "POST" && path == "/log")
        {
            using var r = new StreamReader(ctx.Request.InputStream);
            string body = await r.ReadToEndAsync(); // Сохраняем тело в переменную
    
            await Save(body);
            if (_debug ) body.Debug();
            
            ctx.Response.StatusCode = 200;
            return;
        }

        if (method == "GET" && path == "/logs")
        {
            var q     = ctx.Request.QueryString;
            int limit = int.TryParse(q["limit"], out var l) ? l : 100;
            await HttpHelpers.WriteJson(ctx.Response, await Read(limit, q["level"], q["machine"], q["project"], q["session"], q["port"], q["pid"], q["account"], q["task_id"]));
            return;
        }

        if (method == "GET" && path == "/logs/stream")
        {
            var taskId = ctx.Request.QueryString["task_id"] ?? "";
            await SseHub.SubscribeLogs(ctx.Response, taskId, GetDisconnectToken(ctx));
            return;
        }
        

        if (method == "GET" && path == "/stats")
        {
            await HttpHelpers.WriteJson(ctx.Response, await GetStats());
            return;
        }

        if (method == "POST" && path == "/clear")
        {
            await _lock.WaitAsync();
            try
            {
                string f = Path.Combine(_logPath, "current.jsonl");
                if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                foreach (var old in Directory.GetFiles(_logPath, "log_*.jsonl")) File.Delete(old);
                await HttpHelpers.WriteText(ctx.Response, "OK");
            }
            finally { _lock.Release(); }
            return;
        }

        if (method == "POST" && path == "/clear-logs-by-task")
        {
            var (ok, taskId) = await HttpHelpers.ReadTaskId(ctx.Request);
            if (!ok) { ctx.Response.StatusCode = 400; return; }
            int removed = await DeleteByTaskId("current.jsonl", taskId!);
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, removed });
        }
    }

    private async Task Save(string json)
    {
        await _lock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 100 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
            //$" {json} saved to {f}".Debug();
        }
        finally { _lock.Release(); }
        // broadcast после релиза лока
        BroadcastToSse(json);
    }

    private static void BroadcastToSse(string json)
    {
        try
        {
            var doc    = JsonSerializer.Deserialize<JsonElement>(json);
            string tid = (doc.TryGetProperty("task_id", out var t1) ? t1.ToString() : null)
                      ?? (doc.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null) ?? "";
            if (!string.IsNullOrEmpty(tid))
                SseHub.BroadcastLog(json, tid);
        }
        catch { }
    }

    internal async Task<List<object>> Read(int limit, string? level, string? machine, string? project,
        string? session, string? port, string? pid, string? account, string? taskId = null)
    {
        var result = new List<object>();
        var files  = Directory.GetFiles(_logPath, "current.jsonl").OrderByDescending(File.GetCreationTime).Take(5);

        foreach (var file in files)
        {
            foreach (var line in (await File.ReadAllLinesAsync(file)).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<JsonElement>(line);
                    if (!string.IsNullOrEmpty(level)   && !log.GetProperty("level").ToString().Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(machine)  && !log.GetProperty("machine").ToString().Contains(machine, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(project)  && !log.GetProperty("project").ToString().Contains(project, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(session)  && (!log.TryGetProperty("session",  out var sp) || !sp.ToString().Contains(session,  StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(port)     && (!log.TryGetProperty("port",     out var pp) || !pp.ToString().Contains(port,     StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(pid)      && (!log.TryGetProperty("pid",      out var pi) || !pi.ToString().Contains(pid,      StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(account)  && (!log.TryGetProperty("account",  out var ap) || !ap.ToString().Contains(account,  StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        string tid = (log.TryGetProperty("task_id", out var t1) ? t1.ToString() : null)
                                  ?? (log.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null) ?? "";
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
        var logs     = await Read(2000, null, null, null, null, null, null, null);
        var levels   = new Dictionary<string, int>(); var machines = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>(); var sessions = new Dictionary<string, int>();
        var ports    = new Dictionary<string, int>(); var pids     = new Dictionary<string, int>();
        var accounts = new Dictionary<string, int>(); var taskIds  = new Dictionary<string, int>();

        foreach (JsonElement log in logs)
        {
            try
            {
                string lvl  = log.TryGetProperty("level",   out var l)  ? l.ToString()  : "UNKNOWN";
                string mch  = log.TryGetProperty("machine", out var m)  ? m.ToString()  : "UNKNOWN";
                string prj  = log.TryGetProperty("project", out var pr) ? pr.ToString() : "UNKNOWN";
                string sess = log.TryGetProperty("session", out var s)  ? s.ToString()  : "0";
                string prt  = log.TryGetProperty("port",    out var pt) ? pt.ToString() : "UNKNOWN";
                string pd   = log.TryGetProperty("pid",     out var pi) ? pi.ToString() : "UNKNOWN";
                string acc  = log.TryGetProperty("account", out var a)  ? a.ToString()  : "";
                string tid  = log.TryGetProperty("task_id", out var ti) ? ti.ToString() : "";

                levels[lvl]    = levels.GetValueOrDefault(lvl)    + 1;
                machines[mch]  = machines.GetValueOrDefault(mch)  + 1;
                projects[prj]  = projects.GetValueOrDefault(prj)  + 1;
                sessions[sess] = sessions.GetValueOrDefault(sess) + 1;
                ports[prt]     = ports.GetValueOrDefault(prt)     + 1;
                pids[pd]       = pids.GetValueOrDefault(pd)       + 1;
                if (!string.IsNullOrEmpty(acc)) accounts[acc] = accounts.GetValueOrDefault(acc) + 1;
                if (!string.IsNullOrEmpty(tid)) taskIds[tid]  = taskIds.GetValueOrDefault(tid)  + 1;
            }
            catch { continue; }
        }

        return new { totalLogs = logs.Count, byLevel = levels, byMachine = machines, byProject = projects,
                     bySession = sessions, byPort = ports, byPid = pids, byAccount = accounts, byTaskId = taskIds };
    }

    internal async Task<int> DeleteByTaskId(string fileName, string taskId)
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
                              ?? (log.TryGetProperty("taskId",  out var t2) ? t2.ToString() : null) ?? "";
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

    // HttpListener не даёт нативный disconnect token — используем polling через timer
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
                    // попытка записи нулевого пинга — если клиент ушёл, бросит исключение
                    await ctx.Response.OutputStream.WriteAsync(Array.Empty<byte>());
                    await ctx.Response.OutputStream.FlushAsync();
                }
                catch { cts.Cancel(); break; }
            }
        });
        return cts.Token;
    }
}