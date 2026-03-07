using System.Net;
using System.Text;
using System.Text.Json;
using z3n8;

public class EmbeddedServer
{
    private HttpListener _listener;
    private bool _isRunning;
    private readonly string _logPath;
    private readonly string _reportsPath;
    private readonly string _wwwrootPath;
    private readonly int _port;

    private const int DefaultPort = 10993;
    private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
    private readonly ZpOrchestratorHandler? _zpHandler;

    public EmbeddedServer(LogsConfig config, DbConnectionService? dbService = null)
    {
        _port = int.TryParse(config.DashboardPort, out var p) ? p : DefaultPort;

        _listener = new HttpListener();

        var ports = new HashSet<int> { _port };
        if (Uri.TryCreate(config.LogHost, UriKind.Absolute, out var logUri))
            ports.Add(logUri.Port);
        if (Uri.TryCreate(config.TrafficHost, UriKind.Absolute, out var trafficUri))
            ports.Add(trafficUri.Port);

        var listeningPorts = new List<int>();
        foreach (var port in ports)
        {
            try
            {
                var test = new System.Net.HttpListener();
                test.Prefixes.Add($"http://*:{port}/");
                test.Start(); test.Stop(); test.Close();
                _listener.Prefixes.Add($"http://*:{port}/");
                listeningPorts.Add(port);
            }
            catch { Console.WriteLine($"Port {port} already in use, skipping"); }
        }

        if (listeningPorts.Count == 0)
            throw new InvalidOperationException("No ports available to listen on");

        Console.WriteLine($"Listening ports: {string.Join(", ", listeningPorts)}");

        _logPath = !string.IsNullOrEmpty(config.LogsFolder)
            ? config.LogsFolder
            : Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(_logPath)) Directory.CreateDirectory(_logPath);
        Console.WriteLine($"LogsFolder: {_logPath}");

        _reportsPath = !string.IsNullOrEmpty(config.ReportsFolder)
            ? config.ReportsFolder
            : Path.Combine(AppContext.BaseDirectory, "reports");
        if (!Directory.Exists(_reportsPath)) Directory.CreateDirectory(_reportsPath);
        Console.WriteLine($"ReportsFolder: {_reportsPath}");

        _wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(_wwwrootPath)) Directory.CreateDirectory(_wwwrootPath);
        Console.WriteLine($"Wwwroot: {_wwwrootPath}");

        if (dbService != null)
        {
            _zpHandler = new ZpOrchestratorHandler(dbService);
            _zpHandler.Init();
        }
    }

    public void Start()
    {
        _isRunning = true;
        _listener.Start();
        Task.Run(Listen);
        Console.WriteLine($"Listening {_port}");
    }

    private async Task Listen()
    {
        while (_isRunning)
        {
            try {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequest(context));
            } catch { if (!_isRunning) break; }
        }
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var request  = context.Request;
        var response = context.Response;

        response.Headers.Add("Access-Control-Allow-Origin",  "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        // /zp/* → ZP orchestrator (handles its own Close)
        if (_zpHandler != null && request.Url?.AbsolutePath.ToLower().StartsWith("/zp") == true)
        {
            await _zpHandler.HandleRequest(context);
            return;
        }

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }
       
        try
        {
            string path   = request.Url?.AbsolutePath.ToLower() ?? "";
            string method = request.HttpMethod;

            //Console.WriteLine(path);
            // ── PAGES ──────────────────────────────────────────
            // GET /          → home (default)
            // GET /?page=X   → wwwroot/X.html
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                string page = request.QueryString["page"] ?? "home";
                await ServePage(response, page);
                return;
            }

            // ── REPORT ─────────────────────────────────────────
            if (path == "/report" || path == "/report/")
            {
                await ServePage(response, "/report");
                return;
            }
            if (path.StartsWith("/report/api/") && method == "GET")
            {
                await HandleReportApi(context, path);
                return;
            }
            if (path.StartsWith("/report/") && method == "GET")
            {
                await ServeFile(response, request.Url!.AbsolutePath.Substring("/report/".Length), _reportsPath);
                return;
            }

            // ── API ────────────────────────────────────────────
            if (method == "POST" && path == "/log")
            {
                using var r = new StreamReader(request.InputStream);
                await SaveLog(await r.ReadToEndAsync());
                response.StatusCode = 200;
            }
            else if (method == "GET" && path == "/logs")
            {
                var q     = request.QueryString;
                int limit = int.TryParse(q["limit"], out var l) ? l : 100;
                await WriteJson(response, await ReadLogs(limit, q["level"], q["machine"], q["project"], q["session"], q["port"], q["pid"], q["account"], q["task_id"]));
            }
            else if (method == "GET" && path == "/stats")
            {
                await WriteJson(response, await GetStats());
            }
            else if (method == "POST" && path == "/clear")
            {
                await _fileLock.WaitAsync();
                try
                {
                    string f = Path.Combine(_logPath, "current.jsonl");
                    if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                    foreach (var old in Directory.GetFiles(_logPath, "log_*.jsonl")) File.Delete(old);
                    await WriteText(response, "OK");
                }
                finally { _fileLock.Release(); }
            }
            else if (method == "POST" && path == "/http-log")
            {
                using var r = new StreamReader(request.InputStream);
                await SaveHttpLog(await r.ReadToEndAsync());
                response.StatusCode = 200;
            }
            else if (method == "GET" && path == "/http-logs")
            {
                var q     = request.QueryString;
                int limit = int.TryParse(q["limit"], out var l) ? l : 100;
                await WriteJson(response, await ReadHttpLogs(limit, q["method"], q["url"], q["status"], q["machine"], q["project"], q["session"], q["account"], q["cookiesSource"], q["task_id"]));
            }
            else if (method == "GET" && path == "/http-stats")
            {
                await WriteJson(response, await GetHttpStats());
            }
            else if (method == "POST" && path == "/clear-http")
            {
                await _fileLock.WaitAsync();
                try
                {
                    string f = Path.Combine(_logPath, "http-current.jsonl");
                    if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                    foreach (var old in Directory.GetFiles(_logPath, "http-log_*.jsonl")) File.Delete(old);
                    await WriteText(response, "OK");
                }
                finally { _fileLock.Release(); }
            }
            else if (method == "POST" && path == "/clear-logs-by-task")
            {
                var (ok, taskId) = await ReadTaskId(request);
                if (!ok) { response.StatusCode = 400; return; }
                int removed = await DeleteLogsByTaskId("current.jsonl", taskId!);
                await WriteJson(response, new { ok = true, removed });
            }
            else if (method == "POST" && path == "/clear-http-logs-by-task")
            {
                var (ok, taskId) = await ReadTaskId(request);
                if (!ok) { response.StatusCode = 400; return; }
                int removed = await DeleteLogsByTaskId("http-current.jsonl", taskId!);
                await WriteJson(response, new { ok = true, removed });
            }

            // ── HTTP REPLAY ──────────────────────────────────
            else if (method == "POST" && path == "/http-replay")
            {
                await HandleHttpReplay(context);
            }
            
            
            // ── TRAFFIC ────────────────────────────────────────────

            else if (method == "POST" && path == "/traffic")
            {
                using (var r = new StreamReader(request.InputStream))
                {
                    await SaveTraffic(await r.ReadToEndAsync());
                }
                response.StatusCode = 200;
            }
            else if (method == "GET" && path == "/traffic-logs")
            {
                var q     = request.QueryString;
                int limit = int.TryParse(q["limit"], out var l) ? l : 100;
                await WriteJson(response, await ReadTrafficLogs(limit, q["project"], q["account"], q["session"], q["task_id"]));
            }
            else if (method == "POST" && path == "/clear-traffic")
            {
                await _fileLock.WaitAsync();
                try
                {
                    string f = Path.Combine(_logPath, "traffic-current.jsonl");
                    if (File.Exists(f)) File.WriteAllText(f, string.Empty);
                    await WriteText(response, "OK");
                }
                finally { _fileLock.Release(); }
            }
            else if (method == "GET" && path == "/traffic-stats")
            {
                await WriteJson(response, await GetTrafficStats());
            }
            
            // ═══════════════════════════════════════════════════════════════════
// CONFIG ENDPOINTS  —  вставить в ProcessRequest, в блок else-if цепочки
// ═══════════════════════════════════════════════════════════════════

// ── GET /config → текущий appsettings.secrets.json ─────────────────
            else if (method == "GET" && path == "/config")
            {
                await HandleGetConfig(response);
            }
// ── POST /config → перезаписать appsettings.secrets.json ───────────
            else if (method == "POST" && path == "/config")
            {
                using var r = new StreamReader(request.InputStream);
                await HandleSaveConfig(response, await r.ReadToEndAsync());
            }
// ── GET /config/status → runtime-состояние сервера ─────────────────
            else if (method == "GET" && path == "/config/status")
            {
                await HandleConfigStatus(response);
            }
// ── GET /config/storage → размер папки с логами ────────────────────
            else if (method == "GET" && path == "/config/storage")
            {
                await HandleStorageInfo(response);
            }
// ── POST /clear-all-logs → очистить ВСЕ логи ───────────────────────
            else if (method == "POST" && path == "/clear-all-logs")
            {
                await HandleClearAllLogs(response);
            }



            // ── STATIC (wwwroot) ───────────────────────────────
            else if (method == "GET")
            {
                await ServeFile(response, request.Url!.AbsolutePath.TrimStart('/'), _wwwrootPath);
            }
            
            
            
            else
            {
                response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            var err = Encoding.UTF8.GetBytes(ex.Message);
            response.OutputStream.Write(err, 0, err.Length);
        }
        finally { response.Close(); }
    }

    // ── Page helpers ───────────────────────────────────────────

    private async Task ServePage(HttpListenerResponse response, string page, string? root = null)
    {
        root ??= _wwwrootPath;
        // sanitize
        page = Path.GetFileName(page).Replace("..", "");
        if (!page.EndsWith(".html")) page += ".html";
        await ServeFile(response, page, root);
    }

    private async Task ServeFile(HttpListenerResponse response, string relativePath, string root)
    {
        relativePath = relativePath.Replace("..", "").Replace("\\", "/").TrimStart('/');
        string filePath = Path.Combine(root, relativePath);

        // extensionless path → try .html
        if (!File.Exists(filePath) && string.IsNullOrEmpty(Path.GetExtension(filePath)))
            filePath = filePath + ".html";

        if (!File.Exists(filePath))
        {
            response.StatusCode = 404;
            var msg = Encoding.UTF8.GetBytes($"Not found: {filePath}");
            response.ContentType = "text/plain; charset=utf-8";
            await response.OutputStream.WriteAsync(msg, 0, msg.Length);
            Console.WriteLine($"[404] Not found: {filePath}");
            return;
        }

        string ext = Path.GetExtension(filePath).ToLower();
        response.ContentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png"  => "image/png",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream"
        };

        byte[] buf = await File.ReadAllBytesAsync(filePath);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf, 0, buf.Length);
    }

    // ── Write helpers ──────────────────────────────────────────

    private async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf, 0, buf.Length);
    }

    private static async Task WriteRawJson(HttpListenerResponse response, string json)
    {
        response.ContentType = "application/json; charset=utf-8";
        var buf = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf, 0, buf.Length);
        response.Close();
    }

    private async Task WriteText(HttpListenerResponse response, string text)
    {
        var buf = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf, 0, buf.Length);
    }

    private static async Task<(bool ok, string? taskId)> ReadTaskId(HttpListenerRequest request)
    {
        using var r  = new StreamReader(request.InputStream);
        var body     = await r.ReadToEndAsync();
        var json     = JsonSerializer.Deserialize<JsonElement>(body);
        var taskId   = json.TryGetProperty("task_id", out var t) ? t.GetString() : null;
        return (!string.IsNullOrEmpty(taskId), taskId);
    }

    // ── Log I/O ────────────────────────────────────────────────

    private async Task SaveLog(string json)
    {
        await _fileLock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 100 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
        }
        finally { _fileLock.Release(); }
    }

    private async Task<List<object>> ReadLogs(int limit, string? level, string? machine, string? project,
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
        var logs = await ReadLogs(2000, null, null, null, null, null, null, null);
        var levels = new Dictionary<string, int>(); var machines = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>(); var sessions = new Dictionary<string, int>();
        var ports = new Dictionary<string, int>(); var pids = new Dictionary<string, int>();
        var accounts = new Dictionary<string, int>(); var taskIds = new Dictionary<string, int>();

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

                levels[lvl]   = levels.GetValueOrDefault(lvl)   + 1;
                machines[mch] = machines.GetValueOrDefault(mch) + 1;
                projects[prj] = projects.GetValueOrDefault(prj) + 1;
                sessions[sess]= sessions.GetValueOrDefault(sess) + 1;
                ports[prt]    = ports.GetValueOrDefault(prt)    + 1;
                pids[pd]      = pids.GetValueOrDefault(pd)      + 1;
                if (!string.IsNullOrEmpty(acc)) accounts[acc] = accounts.GetValueOrDefault(acc) + 1;
                if (!string.IsNullOrEmpty(tid)) taskIds[tid]  = taskIds.GetValueOrDefault(tid)  + 1;
            }
            catch { continue; }
        }

        return new { totalLogs = logs.Count, byLevel = levels, byMachine = machines, byProject = projects,
                     bySession = sessions, byPort = ports, byPid = pids, byAccount = accounts, byTaskId = taskIds };
    }

    private async Task<int> DeleteLogsByTaskId(string fileName, string taskId)
    {
        await _fileLock.WaitAsync();
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
        finally { _fileLock.Release(); }
    }

    // ── HTTP log I/O ───────────────────────────────────────────

    private async Task SaveHttpLog(string json)
    {
        await _fileLock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "http-current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 50 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, $"http-log_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
        }
        finally { _fileLock.Release(); }
    }

    private async Task<List<object>> ReadHttpLogs(int limit, string? method, string? url, string? status,
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

    private async Task<object> GetHttpStats()
    {
        var logs = await ReadHttpLogs(2000, null, null, null, null, null, null, null, null);
        var methods = new Dictionary<string, int>(); var statuses = new Dictionary<string, int>();
        var urls = new Dictionary<string, int>(); var machines = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>(); var accounts = new Dictionary<string, int>();
        var cookieSources = new Dictionary<string, int>();
        long totalDuration = 0; int durationCount = 0;

        foreach (JsonElement log in logs)
        {
            try
            {
                string m   = log.TryGetProperty("method",     out var mv) ? mv.ToString() : "UNKNOWN";
                int sc     = log.TryGetProperty("statusCode", out var sv) ? sv.GetInt32() : 0;
                string sg  = sc >= 500 ? "5xx" : sc >= 400 ? "4xx" : sc >= 300 ? "3xx" : sc >= 200 ? "2xx" : "0xx";
                string u   = log.TryGetProperty("url",        out var uv) ? uv.ToString() : "UNKNOWN";
                string mc  = log.TryGetProperty("machine",    out var mcv)? mcv.ToString(): "UNKNOWN";
                string prj = log.TryGetProperty("project",    out var pv) ? pv.ToString() : "UNKNOWN";
                string acc = log.TryGetProperty("account",    out var av) ? av.ToString() : "";
                string src = log.TryGetProperty("cookiesSource", out var csv) ? csv.ToString() : "";
                try { u = new Uri(u).Host; } catch { }

                methods[m]    = methods.GetValueOrDefault(m)    + 1;
                statuses[sg]  = statuses.GetValueOrDefault(sg)  + 1;
                urls[u]       = urls.GetValueOrDefault(u)       + 1;
                machines[mc]  = machines.GetValueOrDefault(mc)  + 1;
                projects[prj] = projects.GetValueOrDefault(prj) + 1;
                if (!string.IsNullOrEmpty(acc)) accounts[acc]    = accounts.GetValueOrDefault(acc)     + 1;
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
    
    private async Task<object> GetTrafficStats()
    {
        var logs = await ReadTrafficLogs(2000, null, null, null, null);
        var methods = new Dictionary<string, int>();
        var statuses = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>();
        var accounts = new Dictionary<string, int>();
        var urls = new Dictionary<string, int>();

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
            }
            catch { continue; }
        }

        return new
        {
            totalRequests = logs.Count,
            byMethod  = methods,
            byStatus  = statuses,
            byProject = projects,
            byAccount = accounts,
            byUrl     = urls.OrderByDescending(kv => kv.Value).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
    
    
    
    private async Task SaveTraffic(string json)
    {
        await _fileLock.WaitAsync();
        try
        {
            string f = Path.Combine(_logPath, "traffic-current.jsonl");
            if (File.Exists(f) && new FileInfo(f).Length > 50 * 1024 * 1024)
                File.Move(f, Path.Combine(_logPath, string.Format("traffic_{0:yyyyMMdd_HHmmss}.jsonl", DateTime.Now)));
            await File.AppendAllTextAsync(f, json + Environment.NewLine);
            Console.WriteLine($"saved to {f}");
        }
        finally { _fileLock.Release(); }
    }

    private async Task<List<object>> ReadTrafficLogs(int limit, string project, string account, string session, string taskId)
    {
        var result = new List<object>();
        string filePath = Path.Combine(_logPath, "traffic-current.jsonl");
        if (!File.Exists(filePath)) return result;

        var lines = await File.ReadAllLinesAsync(filePath);
        foreach (var line in lines.Reverse())
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

    // ── Report API ─────────────────────────────────────────────

    private async Task HandleReportApi(HttpListenerContext ctx, string path)
    {
        string ExtractJsVar(string js) { int eq = js.IndexOf('='); return eq < 0 ? js : js.Substring(eq + 1).Trim().TrimEnd(';'); }
        string ReadJs(string file) { string p = Path.Combine(_reportsPath, file); return File.Exists(p) ? ExtractJsVar(File.ReadAllText(p, Encoding.UTF8)) : "null"; }

        string apiPath = path.Substring("/report/api".Length).TrimEnd('/');

        switch (apiPath)
        {
            case "/metadata": await WriteRawJson(ctx.Response, ReadJs("metadata.js")); break;
            case "/social":   await WriteRawJson(ctx.Response, ReadJs("social.js"));   break;
            case "/projects":
            {
                string projDir = Path.Combine(_reportsPath, "projects");
                var names = Directory.Exists(projDir)
                    ? Directory.GetFiles(projDir, "*.js").Select(f => Path.GetFileNameWithoutExtension(f)).ToArray()
                    : Array.Empty<string>();
                await WriteJson(ctx.Response, names);
                break;
            }
            case "/project":
            {
                string name = (ctx.Request.QueryString["name"] ?? "").Replace("..", "").Replace("/", "").Replace("\\", "");
                await WriteRawJson(ctx.Response, ReadJs($"projects/{name}.js"));
                break;
            }
            case "/process":
            {
                string name = (ctx.Request.QueryString["name"] ?? "").Replace("..", "").Replace("/", "").Replace("\\", "");
                await WriteRawJson(ctx.Response, ReadJs($"process_{name}.js"));
                break;
            }
            case "/all":
            {
                string metaJson = ReadJs("metadata.js"), socialJson = ReadJs("social.js");
                var projectsDict = new Dictionary<string, object>(); var processesDict = new Dictionary<string, object>();
                try
                {
                    var meta = JsonSerializer.Deserialize<JsonElement>(metaJson);
                    if (meta.TryGetProperty("projects", out var pa))
                        foreach (var pn in pa.EnumerateArray()) { string n = pn.GetString() ?? ""; string j = ReadJs($"projects/{new string(n.Where(char.IsLetterOrDigit).ToArray())}.js"); if (j != "null") projectsDict[n] = JsonSerializer.Deserialize<JsonElement>(j); }
                    if (meta.TryGetProperty("machines", out var ma))
                        foreach (var mn in ma.EnumerateArray()) { string n = mn.GetString() ?? ""; string j = ReadJs($"process_{n}.js"); if (j != "null") processesDict[n] = JsonSerializer.Deserialize<JsonElement>(j); }
                }
                catch { }
                await WriteJson(ctx.Response, new { metadata = JsonSerializer.Deserialize<JsonElement>(metaJson), social = JsonSerializer.Deserialize<JsonElement>(socialJson), projects = projectsDict, processes = processesDict });
                break;
            }
            default: ctx.Response.StatusCode = 404; ctx.Response.Close(); break;
        }
    }


    // ── HTTP REPLAY ───────────────────────────────────────
    // POST /http-replay  body: { url, method, headers, body }
    // Proxies the request through HttpClient and returns the response.
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip 
                                 | DecompressionMethods.Deflate 
                                 | DecompressionMethods.Brotli,
        AllowAutoRedirect   = true,
        MaxAutomaticRedirections = 5,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true // allow self-signed
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private async Task HandleHttpReplay(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var bodyJson = await reader.ReadToEndAsync();

        JsonElement req;
        try { req = JsonSerializer.Deserialize<JsonElement>(bodyJson); }
        catch { await WriteJson(ctx.Response, new { error = "Invalid JSON" }); return; }

        var url     = req.TryGetProperty("url",    out var u) ? u.GetString() ?? "" : "";
        var method  = req.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
        var headers = req.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var body    = req.TryGetProperty("body",   out var b) ? b.GetString() : null;

        if (string.IsNullOrEmpty(url)) { await WriteJson(ctx.Response, new { error = "url required" }); return; }

        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var msg = new HttpRequestMessage(new HttpMethod(method), url);

        // Forward headers
        if (headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headers.EnumerateObject())
            {
                try { msg.Headers.TryAddWithoutValidation(prop.Name, prop.Value.GetString()); }
                catch { }
            }
        }

        if (!string.IsNullOrEmpty(body) && method != "GET" && method != "HEAD")
        {
            var ct = "application/json";
            try { if (msg.Headers.Contains("content-type")) ct = msg.Headers.GetValues("content-type").First(); } catch {}
            msg.Content = new StringContent(body, System.Text.Encoding.UTF8, ct);
        }

        try
        {
            var res = await _httpClient.SendAsync(msg);
            sw.Stop();

            var responseBody    = await res.Content.ReadAsStringAsync();
            var responseHeaders = new Dictionary<string, string>();
            foreach (var kv in res.Headers)       responseHeaders[kv.Key] = string.Join(", ", kv.Value);
            foreach (var kv in res.Content.Headers) responseHeaders[kv.Key] = string.Join(", ", kv.Value);

            await WriteJson(ctx.Response, new
            {
                statusCode      = (int)res.StatusCode,
                statusText      = res.ReasonPhrase,
                responseHeaders,
                responseBody,
                durationMs      = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            await WriteJson(ctx.Response, new { error = ex.Message, durationMs = sw.ElapsedMilliseconds });
        }
    }

    
    // ═══════════════════════════════════════════════════════════════════
// IMPLEMENTATION — добавить как private методы в класс EmbeddedServer
// ═══════════════════════════════════════════════════════════════════

/// <summary>Возвращает текущий appsettings.secrets.json как JSON.</summary>
private async Task HandleGetConfig(HttpListenerResponse response)
{
    string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
    if (!File.Exists(cfgPath))
    {
        response.StatusCode = 404;
        await WriteText(response, "Config file not found");
        return;
    }
    string raw = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8);
    await WriteRawJson(response, raw);
}

/// <summary>
/// Принимает JSON с полями dbConfig / logsConfig / apiConfig,
/// мёржит в существующий файл и перезаписывает его.
/// </summary>
private async Task HandleSaveConfig(HttpListenerResponse response, string body)
{
    string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
    try
    {
        // Парсим входящий payload
        var incoming = JsonSerializer.Deserialize<JsonElement>(body);

        // Читаем текущий файл (или пустой объект)
        var existing = new Dictionary<string, JsonElement>();
        if (File.Exists(cfgPath))
        {
            var existingRaw = await File.ReadAllTextAsync(cfgPath, Encoding.UTF8);
            var existingDoc = JsonSerializer.Deserialize<JsonElement>(existingRaw);
            if (existingDoc.ValueKind == JsonValueKind.Object)
                foreach (var prop in existingDoc.EnumerateObject())
                    existing[prop.Name] = prop.Value;
        }

        // Мёржим секции из payload поверх существующих
        string[] sections = { "DbConfig", "LogsConfig", "ApiConfig", "Crx" };
        // Маппинг camelCase (от фронта) → PascalCase (в файле)
        var keyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbConfig"]   = "DbConfig",
            ["logsConfig"] = "LogsConfig",
            ["apiConfig"]  = "ApiConfig",
            ["crx"]        = "Crx",
        };

        if (incoming.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in incoming.EnumerateObject())
            {
                var key = keyMap.TryGetValue(prop.Name, out var mapped) ? mapped : prop.Name;
                existing[key] = prop.Value;
            }
        }

        // Сериализуем обратно с отступами
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string newJson = JsonSerializer.Serialize(existing, opts);

        // Бэкап перед записью
        if (File.Exists(cfgPath))
            File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);

        await File.WriteAllTextAsync(cfgPath, newJson, Encoding.UTF8);

        // Перезагружаем Config в памяти
        Config.Init();

        await WriteJson(response, new { ok = true, message = "Config saved and reloaded" });
    }
    catch (Exception ex)
    {
        response.StatusCode = 400;
        await WriteJson(response, new { ok = false, error = ex.Message });
    }
}

// Время старта сервера — добавить как поле в класс:
// private readonly DateTime _startedAt = DateTime.UtcNow;

/// <summary>Runtime-состояние: порты, пути, время старта, DB mode.</summary>
private async Task HandleConfigStatus(HttpListenerResponse response)
{
    // Собираем порты из prefixes
    var ports = _listener.Prefixes
        .Select(p => { if (Uri.TryCreate(p, UriKind.Absolute, out var u)) return u.Port; return 0; })
        .Where(p => p > 0)
        .Distinct()
        .OrderBy(p => p)
        .ToList();

    var cfg = Config.LogsConfig;
    var db  = Config.DbConfig;

    await WriteJson(response, new
    {
        //startedAt      = _startedAt.ToString("O"),
        dashboardPort  = _port,
        listeningPorts = ports,
        logHost        = cfg.LogHost,
        trafficHost    = cfg.TrafficHost,
        logsFolder     = _logPath,
        reportsFolder  = _reportsPath,
        tempFolder     = cfg.TempFolder,
        maxFileSizeMb  = cfg.MaxFileSizeMb,
        dbMode         = db.Mode.ToString(),   // "SQLite" | "Postgre"
        sqlitePath     = db.SqlitePath,
        pgHost         = db.PostgresHost,
        pgPort         = db.PostgresPort,
        pgDatabase     = db.PostgresDatabase,
    });
}

/// <summary>Размер папки логов: суммарный байт, кол-во файлов, список файлов.</summary>
private async Task HandleStorageInfo(HttpListenerResponse response)
{
    await Task.CompletedTask; // метод синхронный внутри, но сигнатура async
    if (!Directory.Exists(_logPath))
    {
        await WriteJson(response, new { totalBytes = 0L, fileCount = 0, files = Array.Empty<object>() });
        return;
    }

    var files = Directory.GetFiles(_logPath)
        .Select(f => new FileInfo(f))
        .OrderByDescending(fi => fi.Length)
        .ToList();

    long totalBytes = files.Sum(fi => fi.Length);

    var fileList = files.Select(fi => new
    {
        name = fi.Name,
        size = fi.Length,
        lastWrite = fi.LastWriteTimeUtc.ToString("O"),
    }).ToArray();

    await WriteJson(response, new
    {
        totalBytes,
        totalMb        = Math.Round(totalBytes / 1024.0 / 1024.0, 2),
        fileCount      = files.Count,
        logsFolder     = _logPath,
        maxFileSizeMbHint = Config.LogsConfig.MaxFileSizeMb > 0
            ? Config.LogsConfig.MaxFileSizeMb * 20  // ~20 файлов как мягкий лимит для progress bar
            : 500,
        files = fileList,
    });
}

/// <summary>Удаляет все *.jsonl файлы в папке логов (app + http + traffic).</summary>
private async Task HandleClearAllLogs(HttpListenerResponse response)
{
    await _fileLock.WaitAsync();
    int deleted = 0;
    var errors  = new List<string>();
    try
    {
        foreach (var file in Directory.GetFiles(_logPath, "*.jsonl"))
        {
            try
            {
                // current-файлы — обнуляем, архивные — удаляем
                if (Path.GetFileName(file).StartsWith("current") ||
                    Path.GetFileName(file).StartsWith("http-current") ||
                    Path.GetFileName(file).StartsWith("traffic-current"))
                    File.WriteAllText(file, string.Empty);
                else
                    File.Delete(file);
                deleted++;
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
    }
    finally { _fileLock.Release(); }

    await WriteJson(response, new { ok = errors.Count == 0, deleted, errors });
}

    
    public void Stop()
    {
        _isRunning = false;
        if (_listener.IsListening) _listener.Stop();
    }
}