using System.Net;
using System.Text;
using System.Text.Json;
using z3nIO;

namespace z3nIO;

/// <summary>
/// Маршруты:
///   GET  /scheduler          — HTML страница
///   GET  /scheduler/list     — список расписаний из БД
///   POST /scheduler/save     — создать / обновить запись
///   POST /scheduler/delete   — удалить по id
///   POST /scheduler/run      — запустить вручную немедленно
///   POST /scheduler/stop     — Kill процесса по id
///   GET  /scheduler/output   — last_output из БД по ?id=
/// </summary>
public sealed class SchedulerHandler : IScriptHandler
{
    public string PathPrefix => "/scheduler";

    private readonly DbConnectionService _dbService;
    private readonly SchedulerService    _scheduler;
    private readonly string              _wwwrootPath;

    private const string Table = "_schedules";

    private static readonly List<string> Columns = new()
    {
        "id", "name", "executor", "script_path", "args", "enabled",
        "cron", "interval_minutes", "fixed_time", "on_overlap", "max_threads",
        "status", "last_run", "last_exit", "last_output",
        "payload_schema", "payload_values",
        "runs_total", "runs_success", "schedule_tag", "last_run_id"
    };

    public SchedulerHandler(DbConnectionService dbService, SchedulerService scheduler, string wwwrootPath)
    {
        _dbService   = dbService;
        _scheduler   = scheduler;
        _wwwrootPath = wwwrootPath;
    }

    public void Init() { /* таблица создаётся в SchedulerService.Init() */ }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var path   = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = context.Request.HttpMethod;

        if (!path.StartsWith("/scheduler")) return false;

        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            await HttpHelpers.WriteJson(context.Response, new { error = "DB not connected" });
            return true;
        }

        try
        {
            if (path == "/scheduler" || path == "/scheduler/" || path == "/scheduler.html")           { await ServePage(context.Response);          return true; }
            if (path == "/scheduler/list"    && method == "GET")         { await List(context, db);                    return true; }
            if (path == "/scheduler/save"    && method == "POST")        { await Save(context, db);                    return true; }
            if (path == "/scheduler/delete"  && method == "POST")        { await Delete(context, db);                  return true; }
            if (path == "/scheduler/run"     && method == "POST")        { await RunNow(context, db);                  return true; }
            if (path == "/scheduler/stop"    && method == "POST")        { await Stop(context);                        return true; }
            if (path == "/scheduler/output"       && method == "GET")  { await Output(context, db);    return true; }
            if (path == "/scheduler/live-output"  && method == "GET")  { await LiveOutput(context);     return true; }
            if (path == "/scheduler/clear-output" && method == "POST") { await ClearOutput(context, db); return true; }
            if (path == "/scheduler/payload"      && method == "GET")  { await GetPayload(context, db); return true; }
            if (path == "/scheduler/payload" && method == "POST")        { await SavePayload(context, db);             return true; }
            if (path == "/scheduler/process-stats" && method == "GET") { await ProcessStats(context); return true; }
            if (path == "/scheduler/instances"     && method == "GET")  { await Instances(context); return true; }
            if (path == "/scheduler/kill-instance" && method == "POST") { await KillInstance(context); return true; }
            if (path == "/scheduler/queue"         && method == "GET")  { await QueueItems(context, db); return true; }
            if (path == "/scheduler/clear-queue"   && method == "POST") { await ClearQueue(context, db); return true; }
            if (path == "/scheduler/output/stream" && method == "GET")
            {
                var sid      = context.Request.QueryString["id"] ?? "";
                var snapshot = _scheduler.GetLiveOutput(sid);
                var lines    = string.IsNullOrEmpty(snapshot)
                    ? null
                    : snapshot.Split('\n').Where(l => l.Length > 0);
                await SseHub.SubscribeOutput(context.Response, sid, GetDisconnectToken(context), lines);
                return true;
            }
            if (path == "/scheduler/build"         && method == "POST") { await Build(context, db); return true; }
            if (path == "/scheduler/open-file"   && method == "GET") { await OpenFile(context);   return true; }
            if (path == "/scheduler/open-folder" && method == "GET") { await OpenFolder(context); return true; }
            if (path == "/scheduler/scan-folder" && method == "GET") { await ScanFolder(context, db); return true; }
            if (path == "/scheduler/package-scripts" && method == "GET") { await PackageScripts(context, db); return true; }
            if (path == "/scheduler/config-file" && method == "GET") { await GetConfigFile(context, db); return true; }
            if (path == "/scheduler/config-file" && method == "POST") { await SaveConfigFile(context); return true; }
            if (path == "/scheduler/terminal-config" && method == "GET") { await TerminalConfig(context); return true; }
            if (path == "/scheduler/detect-venv" && method == "GET") { await DetectVenv(context, db); return true; }
            if (path == "/scheduler/open-terminal" && method == "GET") { await OpenTerminal(context, db); return true; }

        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(context.Response, new { error = ex.Message });
        }

        return true;
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private async Task List(HttpListenerContext ctx, Db db)
    {
        var cols = db.GetTableColumns(Table);
        if (cols.Count == 0) { await HttpHelpers.WriteJson(ctx.Response, new List<object>()); return; }

        var rows = db.GetLines(string.Join(",", cols), Table, where: "\"id\" != ''");
        await HttpHelpers.WriteJson(ctx.Response, RowsToList(rows, cols));
    }

    private async Task Save(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "Invalid JSON" }); return; }

        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString();

        var record = new Dictionary<string, string> { { "id", id } };
        foreach (var col in Columns.Where(c => c != "id" && c != "status" && c != "last_run" && c != "last_exit" && c != "last_output"))
        {
            if (json.Value.TryGetProperty(col, out var val))
                record[col] = val.GetString() ?? "";
        }

        // upsert: проверить существование записи
        var existing = db.Get("id", Table, where: $"\"id\" = '{id}'");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            var setParts = record.Where(kv => kv.Key != "id")
                                 .Select(kv => $"\"{kv.Key}\" = '{kv.Value.Replace("'", "''")}'");
            db.Query($"UPDATE \"{Table}\" SET {string.Join(", ", setParts)} WHERE \"id\" = '{id}'");
        }
        else
        {
            record["status"]      = "idle";
            record["last_run"]    = "";
            record["last_exit"]   = "";
            record["last_output"] = "";
            db.InsertDic(record, Table);
        }

        await HttpHelpers.WriteJson(ctx.Response, new { ok = true, id });
    }

    private async Task Delete(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }

        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        _scheduler.Kill(id);
        db.Del(Table, where: $"\"id\" = '{id}'");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    private async Task RunNow(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }

        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        // Получить запись и прогнать через LaunchAsync минуя триггер
        var cols = db.GetTableColumns(Table);
        var rows = db.GetLines(string.Join(",", cols), Table, where: $"\"id\" = '{id}'");
        if (rows.Count == 0) { ctx.Response.StatusCode = 404; return; }

        // Форсировать запуск через FireNow
        _scheduler.FireNow(id, ParseRow(rows[0], cols), db);
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true, id });
    }

    private async Task Stop(HttpListenerContext ctx)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }

        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        _scheduler.Kill(id);
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
        Console.BackgroundColor = ConsoleColor.Red;
        Console.WriteLine($"killed {id}");
        Console.ResetColor();
    }

    private async Task LiveOutput(HttpListenerContext ctx)
    {
        var id    = ctx.Request.QueryString["id"]    ?? "";
        var runId = ctx.Request.QueryString["runId"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        var output = _scheduler.GetLiveOutput(id, string.IsNullOrEmpty(runId) ? null : runId);
        var isLive = _scheduler.IsRunning(id);
        var result = _scheduler.GetResult(id);
        await HttpHelpers.WriteJson(ctx.Response, new { id, isLive, output, result });
    }

    private async Task ClearOutput(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }
        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        _scheduler.ClearLiveOutput(id);
        db.Query($"UPDATE \"{Table}\" SET \"last_output\" = '' WHERE \"id\" = '{id}'");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    private async Task Output(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        var output   = db.Get("last_output", Table, where: $"\"id\" = '{id}'") ?? "";
        var status   = db.Get("status",      Table, where: $"\"id\" = '{id}'") ?? "";
        var lastExit = db.Get("last_exit",   Table, where: $"\"id\" = '{id}'") ?? "";
        var isLive   = _scheduler.IsRunning(id);
        var result   = _scheduler.GetResult(id);
        await HttpHelpers.WriteJson(ctx.Response, new { id, status, isLive, output, result });
    }

    private async Task GetPayload(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        var schema = db.Get("payload_schema", Table, where: $"\"id\" = '{id}'") ?? "";
        var values = db.Get("payload_values", Table, where: $"\"id\" = '{id}'") ?? "";
        await HttpHelpers.WriteJson(ctx.Response, new { id, schema, values });
    }

    private async Task SavePayload(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }

        var id     = json.Value.TryGetProperty("id",     out var eid) ? eid.GetString() ?? "" : "";
        var schema = json.Value.TryGetProperty("schema", out var sch) ? sch.GetString() ?? "" : "";
        var values = json.Value.TryGetProperty("values", out var val) ? val.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        db.Query($"UPDATE \"{Table}\" SET \"payload_schema\" = '{schema.Replace("'", "''")}', \"payload_values\" = '{values.Replace("'", "''")}' WHERE \"id\" = '{id}'");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
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

    private async Task Instances(HttpListenerContext ctx)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        await HttpHelpers.WriteJson(ctx.Response, _scheduler.GetInstances(id));
    }

    private async Task KillInstance(HttpListenerContext ctx)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }
        var id    = json.Value.TryGetProperty("id",    out var eid)    ? eid.GetString()    ?? "" : "";
        var runId = json.Value.TryGetProperty("runId", out var erunid) ? erunid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        if (string.IsNullOrEmpty(runId)) _scheduler.Kill(id);
        else                             _scheduler.KillInstance(id, runId);
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    private async Task QueueItems(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        await HttpHelpers.WriteJson(ctx.Response, _scheduler.GetQueueItems(db, id));
    }

    private async Task ClearQueue(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }
        var id = json.Value.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        _scheduler.ClearQueue(db, id);
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── Page ──────────────────────────────────────────────────────────────────

    private async Task ServePage(HttpListenerResponse response)
    {
        string filePath = Path.Combine(_wwwrootPath, "scheduler.html");
        if (File.Exists(filePath))
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            response.StatusCode = 404;
            var bytes = Encoding.UTF8.GetBytes($"scheduler.html not found at: {filePath}");
            await response.OutputStream.WriteAsync(bytes);
        }
        response.Close();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private async Task ProcessStats(HttpListenerContext ctx)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        var (pid, uptimeSec, memoryMB, running) = _scheduler.GetProcessInfo(id);
        await HttpHelpers.WriteJson(ctx.Response, new { pid, uptimeSec, memoryMB, running });
    }
    
    private static async Task<JsonElement?> ReadJson(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    private static List<Dictionary<string, string>> RowsToList(List<string> rows, List<string> columns)
    {
        var result = new List<Dictionary<string, string>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var values = row.Split('¦');
            var dict   = new Dictionary<string, string>();
            for (int i = 0; i < columns.Count && i < values.Length; i++)
                dict[columns[i]] = values[i];
            result.Add(dict);
        }
        return result;
    }

    private static Dictionary<string, string> ParseRow(string row, List<string> columns)
    {
        var values = row.Split('¦');
        var dict   = new Dictionary<string, string>();
        for (int i = 0; i < columns.Count && i < values.Length; i++)
            dict[columns[i]] = values[i];
        return dict;
    }

    private async Task Build(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "Invalid JSON" }); return; }

        if (!json.Value.TryGetProperty("id", out var idProp))
        { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "id required" }); return; }

        var id         = idProp.GetString() ?? "";
        var scriptPath = db.Get("script_path", Table, where: $"\"id\" = '{id}'");
        var executor   = db.Get("executor",    Table, where: $"\"id\" = '{id}'");

        if (executor != "csx-internal" && executor != "csx-zp7")
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, errors = Array.Empty<string>(), message = "not a csx task" });
            return;
        }

        var errors = executor == "csx-zp7"
            ? await CsxExecutor.CompileAsync<CsxZp7Globals>(scriptPath)
            : await CsxExecutor.CompileAsync<CsxGlobals>(scriptPath);

        if (errors.Count == 0)
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, errors = Array.Empty<string>() });
        else
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, errors });
    }
    
    private async Task OpenFile(HttpListenerContext ctx)
    {
        var filePath = ctx.Request.QueryString["path"] ?? "";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "File not found: " + filePath });
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = filePath,
            UseShellExecute = true
        });

        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    private async Task OpenFolder(HttpListenerContext ctx)
    {
        var filePath = ctx.Request.QueryString["path"] ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Path is empty" });
            return;
        }

        var dir = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : filePath;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Directory not found: " + dir });
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = dir,
            UseShellExecute = true
        });

        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── Scan folder ────────────────────────────────────────────────────────────

    private async Task ScanFolder(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var row = db.Get("executor,script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(row))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { });
            return;
        }

        var parts = row.Split('¦');
        var executor = parts.Length > 0 ? parts[0] : "";
        var scriptPath = parts.Length > 1 ? parts[1] : "";
        var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";

        var result = new
        {
            has_config = false,
            config_path = "",
            has_requirements = false,
            req_path = "",
            has_package_json = false,
            package_json_path = ""
        };

        if (IsJs(executor))
        {
            var cfg = Path.Combine(folder, "config.json");
            var pkg = Path.Combine(folder, "package.json");
            result = new
            {
                has_config = File.Exists(cfg),
                config_path = cfg,
                has_requirements = false,
                req_path = "",
                has_package_json = File.Exists(pkg),
                package_json_path = pkg
            };
        }
        else if (IsPy(executor))
        {
            var cfg = Path.Combine(folder, "config.py");
            var req = Path.Combine(folder, "requirements.txt");
            result = new
            {
                has_config = File.Exists(cfg),
                config_path = cfg,
                has_requirements = File.Exists(req),
                req_path = req,
                has_package_json = false,
                package_json_path = ""
            };
        }

        await HttpHelpers.WriteJson(ctx.Response, result);
    }

    // ── Package scripts ────────────────────────────────────────────────────────

    private async Task PackageScripts(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var row = db.Get("executor,script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(row))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, scripts = new { } });
            return;
        }

        var parts = row.Split('¦');
        var executor = parts.Length > 0 ? parts[0] : "";
        var scriptPath = parts.Length > 1 ? parts[1] : "";

        if (!IsJs(executor))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, scripts = new { } });
            return;
        }

        var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";
        var pkgPath = Path.Combine(folder, "package.json");

        if (!File.Exists(pkgPath))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, scripts = new { }, missing = true });
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(pkgPath);
            var pkg = JsonSerializer.Deserialize<JsonElement>(json);
            var scripts = pkg.TryGetProperty("scripts", out var s) ? s : new JsonElement();
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, scripts });
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, scripts = new { }, error = ex.Message });
        }
    }

    // ── Config file ────────────────────────────────────────────────────────────

    private async Task GetConfigFile(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        var type = ctx.Request.QueryString["type"] ?? "config";

        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var row = db.Get("executor,script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(row))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Schedule not found" });
            return;
        }

        var parts = row.Split('¦');
        var executor = parts.Length > 0 ? parts[0] : "";
        var scriptPath = parts.Length > 1 ? parts[1] : "";

        string configPath;
        bool found;

        if (type == "package" && IsJs(executor))
        {
            var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";
            configPath = Path.Combine(folder, "package.json");
            found = File.Exists(configPath);
        }
        else
        {
            (configPath, found) = ResolveConfigPath(executor, scriptPath);
        }

        if (!found)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, missing = true, path = configPath, content = "" });
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath);
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, path = configPath, content });
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
        }
    }

    private async Task SaveConfigFile(HttpListenerContext ctx)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var path = json.Value.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var content = json.Value.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(path))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, content);
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
        }
    }

    // ── Terminal config ────────────────────────────────────────────────────────

    private async Task TerminalConfig(HttpListenerContext ctx)
    {
        var terminal = Config.Terminal ?? "cmd";
        var terminalPath = Config.TerminalPath ?? "";
        var gitbashFound = FindGitBash() ?? "";

        await HttpHelpers.WriteJson(ctx.Response, new
        {
            terminal,
            terminal_path = terminalPath,
            gitbash_found = gitbashFound
        });
    }

    // ── Detect venv ────────────────────────────────────────────────────────────

    private async Task DetectVenv(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var row = db.Get("script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(row))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, venvs = new List<object>() });
            return;
        }

        var scriptPath = row;
        var venvs = DetectVenvFolders(scriptPath);
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true, venvs });
    }

    // ── Open terminal ──────────────────────────────────────────────────────────

    private async Task OpenTerminal(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var row = db.Get("script_path,terminal_override,terminal_init_cmd", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(row))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Schedule not found" });
            return;
        }

        var parts = row.Split('¦');
        var scriptPath = parts.Length > 0 ? parts[0] : "";
        var termOverride = parts.Length > 1 ? parts[1] : "";
        var termInitCmd = parts.Length > 2 ? parts[2] : "";

        var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = $"Folder not found: {folder}" });
            return;
        }

        try
        {
            var error = LaunchTerminal(folder, termOverride, termInitCmd);
            if (error != null)
            {
                await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error });
            }
            else
            {
                await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
            }
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
        }
    }

    // ── Helper methods ─────────────────────────────────────────────────────────

    private static bool IsJs(string executor) => executor is "node" or "ts-node";
    private static bool IsPy(string executor) => executor == "python";

    private static (string path, bool found) ResolveConfigPath(string executor, string scriptPath)
    {
        if (IsJs(executor))
        {
            var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";
            var cfg = Path.Combine(folder, "config.json");
            return (cfg, File.Exists(cfg));
        }
        else if (IsPy(executor))
        {
            var folder = Path.GetDirectoryName(scriptPath) ?? "";
            var cfg = Path.Combine(folder, "config.py");
            return (cfg, File.Exists(cfg));
        }
        return ("", false);
    }

    private static string? FindGitBash()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            @"C:\Git\bin\bash.exe"
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static List<object> DetectVenvFolders(string scriptPath)
    {
        var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";
        if (!Directory.Exists(folder))
            return new List<object>();

        var venvCandidates = new[] { ".venv", "venv", "env", ".env" };
        var results = new List<object>();

        foreach (var venvName in venvCandidates)
        {
            var venvPath = Path.Combine(folder, venvName);
            if (!Directory.Exists(venvPath))
                continue;

            var pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
            if (File.Exists(pythonExe))
            {
                results.Add(new
                {
                    name = venvName,
                    path = venvPath,
                    python = pythonExe
                });
            }
        }

        return results;
    }

    private static string? LaunchTerminal(string cwd, string termOverride, string initCmd)
    {
        var terminal = !string.IsNullOrWhiteSpace(termOverride) ? termOverride : (Config.Terminal ?? "cmd");

        if (terminal == "cmd")
        {
            var cdCmd = $"cd /d {cwd}";
            var full = !string.IsNullOrWhiteSpace(initCmd) ? $"{cdCmd} && {initCmd}" : cdCmd;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K {full}",
                UseShellExecute = true,
                CreateNoWindow = false
            });
        }
        else if (terminal == "powershell")
        {
            var cdCmd = $"Set-Location '{cwd}'";
            var full = !string.IsNullOrWhiteSpace(initCmd) ? $"{cdCmd}; {initCmd}" : cdCmd;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"{full}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            });
        }
        else if (terminal == "gitbash")
        {
            var bash = FindGitBash();
            if (bash == null)
                return "Git Bash not found. Install Git for Windows.";

            var cdExpr = $"cd '{cwd}'";
            var full = !string.IsNullOrWhiteSpace(initCmd)
                ? $"{cdExpr} && {initCmd} && exec bash"
                : $"{cdExpr} && exec bash";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bash,
                Arguments = $"--login -c \"{full}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            });
        }
        else if (terminal == "third_party")
        {
            var termPath = Config.TerminalPath ?? "";
            if (string.IsNullOrWhiteSpace(termPath))
                return "TERMINAL_PATH is not set in config";
            if (!File.Exists(termPath))
                return $"Terminal not found: {termPath}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = termPath,
                WorkingDirectory = cwd,
                UseShellExecute = true
            });
        }
        else
        {
            return $"Unknown terminal: {terminal}";
        }

        return null;
    }
}