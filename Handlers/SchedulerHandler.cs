using System.Net;
using System.Text;
using System.Text.Json;
using z3nDash;

namespace z3nDash;

/// <summary>
/// Маршруты:
///   GET  /tasker          — HTML страница
///   GET  /tasker/list     — список расписаний из БД
///   POST /tasker/save     — создать / обновить запись
///   POST /tasker/delete   — удалить по id
///   POST /tasker/run      — запустить вручную немедленно
///   POST /tasker/stop     — Kill процесса по id
///   GET  /tasker/output   — last_output из БД по ?id=
///   GET  /tasker/pick     — системный диалог выбора файла или каталога
/// </summary>
public sealed class SchedulerHandler : IScriptHandler
{
    public string PathPrefix => "/tasker";

    private readonly DbConnectionService _dbService;
    private readonly SchedulerService    _scheduler;
    private readonly string              _wwwrootPath;

    private static string Table => DbSchema.Schedules.Name;

    private static readonly List<string> Columns = new()
    {
        "id", "name", "executor", "script_path", "args", "enabled",
        "cron", "interval_minutes", "fixed_time", "on_overlap", "max_threads",
        "status", "last_run", "last_exit", "last_output",
        "payload_schema", "payload_values",
        "runs_total", "runs_success", "schedule_tag", "last_run_id",
        "use_venv", "schedule_mode", "schedule_json", "sched_runs", "sched_started_at",
        "browser_json"
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

        if (!path.StartsWith("/tasker")) return false;

        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            await HttpHelpers.WriteJson(context.Response, new { error = "DB not connected" });
            return true;
        }

        try
        {
            if (path == "/tasker" || path == "/tasker/" || path == "/tasker.html")           { await ServePage(context.Response);          return true; }
            if (path == "/tasker/list"    && method == "GET")         { await List(context, db);                    return true; }
            if (path == "/tasker/save"    && method == "POST")        { await Save(context, db);                    return true; }
            if (path == "/tasker/delete"  && method == "POST")        { await Delete(context, db);                  return true; }
            if (path == "/tasker/run"     && method == "POST")        { await RunNow(context, db);                  return true; }
            if (path == "/tasker/defer" && method == "POST")
            {
                var id = context.Request.QueryString["id"] ?? "";
                var (until, reason) = await TaskControlHandler.ReadDefer(context.Request);
                await HttpHelpers.WriteJson(context.Response, _scheduler.DeferTask(id, until, reason));
                return true;
            }
            if ((path == "/tasker/pause" || path == "/tasker/resume") && method == "POST")
            {
                var json = await ReadJson(context.Request);
                var id = json?.GetProperty("id").GetString() ?? "";
                await HttpHelpers.WriteJson(context.Response, _scheduler.PauseTask(id, path.EndsWith("/pause")));
                return true;
            }
            if (path == "/tasker/stop"    && method == "POST")        { await Stop(context);                        return true; }
            if (path == "/tasker/output"       && method == "GET")  { await Output(context, db);    return true; }
            if (path == "/tasker/live-output"  && method == "GET")  { await LiveOutput(context);     return true; }
            if (path == "/tasker/clear-output" && method == "POST") { await ClearOutput(context, db); return true; }
            if (path == "/tasker/payload"      && method == "GET")  { await GetPayload(context, db); return true; }
            if (path == "/tasker/pick"         && method == "GET")  { await Pick(context);           return true; }
            if (path == "/tasker/payload" && method == "POST")        { await SavePayload(context, db);             return true; }
            if (path == "/tasker/process-stats" && method == "GET") { await ProcessStats(context); return true; }
            if (path == "/tasker/instances"     && method == "GET")  { await Instances(context); return true; }
            if (path == "/tasker/kill-instance" && method == "POST") { await KillInstance(context); return true; }
            if (path == "/tasker/queue"         && method == "GET")  { await QueueItems(context, db); return true; }
            if (path == "/tasker/clear-queue"   && method == "POST") { await ClearQueue(context, db); return true; }
            if (path == "/tasker/output/stream" && method == "GET")
            {
                var sid    = context.Request.QueryString["id"] ?? "";
                var events = _scheduler.GetLiveEvents(sid);
                await SseHub.SubscribeOutput(context.Response, sid, GetDisconnectToken(context),
                                             events.Count == 0 ? null : events);
                return true;
            }
            if (path == "/tasker/build"         && method == "POST") { await Build(context, db); return true; }
            if (path == "/tasker/open-file"   && method == "GET") { await OpenFile(context);   return true; }
            if (path == "/tasker/open-folder" && method == "GET") { await OpenFolder(context); return true; }
            if (path == "/tasker/scan-folder" && method == "GET") { await ScanFolder(context, db); return true; }
            if (path == "/tasker/package-scripts" && method == "GET") { await PackageScripts(context, db); return true; }
            if (path == "/tasker/config-file" && method == "GET") { await GetConfigFile(context, db); return true; }
            if (path == "/tasker/config-file" && method == "POST") { await SaveConfigFile(context); return true; }
            if (path == "/tasker/ensure-venv" && method == "POST") { await EnsureVenv(context, db); return true; }
            if (path == "/tasker/internal-tasks" && method == "GET") { await InternalTasks(context); return true; }
            if (path == "/tasker/schedule-preview" && method == "POST") { await SchedulePreview(context); return true; }
            if (path == "/tasker/install/stream" && method == "GET") { await InstallStream(context, db); return true; }
            if (path == "/tasker/open-terminal" && method == "GET") { await OpenTerminal(context, db); return true; }

        }
        catch (Exception ex)
        {
            context.Response.StatusCode = ex is ArgumentException or JsonException ? 400 : ex is KeyNotFoundException ? 404 : 500;
            await HttpHelpers.WriteJson(context.Response, new { error = ex.Message });
        }

        return true;
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private async Task List(HttpListenerContext ctx, Db db)
    {
        var cols = db.GetTableColumns(Table);
        if (cols.Count == 0) { await HttpHelpers.WriteJson(ctx.Response, new List<object>()); return; }
        
        cols = cols.Where(c => c != "last_output").ToList();
        var rows = db.GetLines(string.Join(",", cols), Table, where: "\"id\" != ''");

        var tasks = RowsToList(rows, cols);
        foreach (var task in tasks)
            task["defer_reason"] = SchedulerService.DecodeReason(task.GetValueOrDefault("defer_reason", ""));
        await HttpHelpers.WriteJson(ctx.Response, tasks);
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

        // «Начать сразу» должно значить сразу, а не «на ближайшем минутном тике».
        _scheduler.EvaluateNow(id, db);

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
        string filePath = Path.Combine(_wwwrootPath, "tasker.html");
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
            var bytes = Encoding.UTF8.GetBytes($"tasker.html not found at: {filePath}");
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

    // ── venv ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Создаёт venv рядом со скриптом, если его ещё нет. Вызывается по галке
    /// Use venv, чтобы каталог появился до первого запуска, а не во время него.
    /// </summary>
    private async Task EnsureVenv(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        var id   = json?.TryGetProperty("id", out var eid) == true ? eid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }

        var scriptPath = db.Get("script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Schedule not found" });
            return;
        }

        var lines = new List<string>();
        var interpreter = await Task.Run(() => PythonEnv.Ensure(scriptPath, lines.Add));
        var created = File.Exists(interpreter);

        await HttpHelpers.WriteJson(ctx.Response, new { ok = created, interpreter, log = lines });
    }

    // ── Установка зависимостей ─────────────────────────────────────────────────

    /// <summary>
    /// npm install / pip install с потоковым выводом. Для python установка идёт
    /// интерпретатором venv, если галка Use venv включена, иначе системным.
    /// </summary>
    private async Task InstallStream(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        var row = db.Get("executor,script_path,use_venv", Table, where: $"\"id\" = '{id}'");

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.Headers.Add("X-Accel-Buffering", "no");
        ctx.Response.StatusCode = 200;
        var output = ctx.Response.OutputStream;

        if (string.IsNullOrWhiteSpace(row))
        {
            await SendInstallLine(output, "[ERR] schedule not found", "ERROR");
            await FinishInstall(ctx, output);
            return;
        }

        var parts      = row.Split('¦');
        var executor   = parts.Length > 0 ? parts[0] : "";
        var scriptPath = parts.Length > 1 ? parts[1] : "";
        var useVenv    = parts.Length > 2 && parts[2] == "true";
        var folder     = PythonEnv.FolderOf(scriptPath);

        if (!Directory.Exists(folder))
        {
            await SendInstallLine(output, $"[ERR] folder not found: {folder}", "ERROR");
            await FinishInstall(ctx, output);
            return;
        }

        string fileName, arguments;
        if (IsJs(executor))
        {
            fileName  = OperatingSystem.IsWindows() ? "cmd.exe" : "npm";
            arguments = OperatingSystem.IsWindows() ? "/c npm install" : "install";
        }
        else
        {
            var requirements = Path.Combine(folder, "requirements.txt");
            if (!File.Exists(requirements))
            {
                await SendInstallLine(output, "[ERR] requirements.txt not found", "ERROR");
                await FinishInstall(ctx, output);
                return;
            }
            var lines = new List<string>();
            fileName = useVenv ? PythonEnv.Ensure(scriptPath, lines.Add) : "python";
            foreach (var line in lines) await SendInstallLine(output, line, "INFO");
            arguments = "-m pip install -r requirements.txt";
        }

        await RunInstallProcess(output, fileName, arguments, folder);
        await FinishInstall(ctx, output);
    }

    private static async Task RunInstallProcess(Stream output, string fileName, string arguments, string cwd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                await SendInstallLine(output, $"[ERR] cannot start {fileName}", "ERROR");
                return;
            }

            while (await proc.StandardOutput.ReadLineAsync() is { } line)
                await SendInstallLine(output, line, "INFO");

            var stderr = await proc.StandardError.ReadToEndAsync();
            foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await SendInstallLine(output, line.TrimEnd(), "ERROR");

            await proc.WaitForExitAsync();
            await SendInstallLine(output, $"exit code {proc.ExitCode}", proc.ExitCode == 0 ? "INFO" : "ERROR");
        }
        catch (Exception ex)
        {
            await SendInstallLine(output, "[ERR] " + ex.Message, "ERROR");
        }
    }

    private static async Task SendInstallLine(Stream output, string line, string level)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { line, level });
        var bytes   = System.Text.Encoding.UTF8.GetBytes($"event: output\ndata: {payload}\n\n");
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static async Task FinishInstall(HttpListenerContext ctx, Stream output)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("event: done\ndata: {}\n\n");
        await output.WriteAsync(bytes);
        await output.FlushAsync();
        ctx.Response.Close();
    }

    // ── Предпросмотр расписания ────────────────────────────────────────────────

    /// <summary>
    /// Ближайшие расчётные запуски по несохранённым настройкам формы —
    /// упрощённый отладчик расписания. Ошибки настроек возвращаются списком,
    /// по ним форма подсвечивает поля и блокирует включение.
    /// </summary>
    private static async Task SchedulePreview(HttpListenerContext ctx)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { ctx.Response.StatusCode = 400; return; }

        var mode = json.Value.TryGetProperty("mode", out var m) ? m.GetString() ?? "off" : "off";
        var now  = DateTime.UtcNow;

        if (mode == "cron")
        {
            var cron = json.Value.TryGetProperty("cron", out var c) ? c.GetString() ?? "" : "";
            try
            {
                var expr  = Cronos.CronExpression.Parse(cron);
                var times = new List<string>();
                var cursor = now;
                for (var i = 0; i < 20; i++)
                {
                    var next = expr.GetNextOccurrence(cursor, TimeZoneInfo.Utc);
                    if (!next.HasValue) break;
                    times.Add(next.Value.ToString("yyyy-MM-dd HH:mm"));
                    cursor = next.Value;
                }
                await HttpHelpers.WriteJson(ctx.Response, new { ok = true, errors = Array.Empty<string>(), times });
            }
            catch (Exception ex)
            {
                await HttpHelpers.WriteJson(ctx.Response, new { ok = false, errors = new[] { ex.Message }, times = Array.Empty<string>() });
            }
            return;
        }

        var raw  = json.Value.TryGetProperty("schedule_json", out var sj) ? sj.GetString() ?? "" : "";
        var spec = ZpSchedule.Parse(raw);
        if (!spec.IsValid)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, errors = spec.Errors, times = Array.Empty<string>() });
            return;
        }

        var preview = ZpSchedule.Preview(spec, now)
                                .Select(t => t.ToString("yyyy-MM-dd HH:mm"))
                                .ToList();
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true, errors = Array.Empty<string>(), times = preview });
    }

    // ── Internal tasks ─────────────────────────────────────────────────────────

    /// <summary>Список зарегистрированных internal-задач для выпадашки в Settings.</summary>
    private async Task InternalTasks(HttpListenerContext ctx)
        => await HttpHelpers.WriteJson(ctx.Response, new { tasks = _scheduler.InternalTaskNames });

    // ── Open terminal ──────────────────────────────────────────────────────────

    private async Task OpenTerminal(HttpListenerContext ctx, Db db)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var scriptPath = db.Get("script_path", Table, where: $"\"id\" = '{id}'");
        if (string.IsNullOrEmpty(scriptPath))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "Schedule not found" });
            return;
        }

        var folder = Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = $"Folder not found: {folder}" });
            return;
        }

        try
        {
            var error = LaunchTerminal(folder);
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

    private static bool IsJs(string executor) => executor is "node" or "ts-node" or "npm";
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

    /// <summary>
    /// Открыть терминал в папке задачи. Настройки нет: на Windows это
    /// PowerShell, на Linux — первый найденный системный эмулятор терминала.
    /// </summary>
    private static string? LaunchTerminal(string cwd)
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoExit -Command \"Set-Location '{cwd}'\"",
                UseShellExecute = true,
                CreateNoWindow  = false,
            });
            return null;
        }

        string[] candidates =
        [
            Environment.GetEnvironmentVariable("TERMINAL") ?? "",
            "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "xterm",
        ];

        foreach (var term in candidates.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName         = term,
                    WorkingDirectory = cwd,
                    UseShellExecute  = true,
                });
                return null;
            }
            catch { /* следующий кандидат */ }
        }

        return "No terminal emulator found. Set $TERMINAL.";
    }

    /// <summary>
    /// Системный диалог выбора пути. Из страницы полный путь получить нельзя:
    /// браузер отдаёт только имя файла, а планировщику нужен абсолютный путь.
    /// Поэтому диалог открывает само приложение.
    ///
    /// Параметры: mode=file|folder, ext — подсказка для фильтра, start — откуда
    /// начать. Отмена — не ошибка, возвращается пустой путь.
    /// </summary>
    private static async Task Pick(HttpListenerContext ctx)
    {
        var mode  = ctx.Request.QueryString["mode"]  ?? "file";
        var ext   = ctx.Request.QueryString["ext"]   ?? "";
        var start = ctx.Request.QueryString["start"] ?? "";

        string picked;
        try
        {
            picked = ShowPicker(mode, ext, start);
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
            return;
        }

        await HttpHelpers.WriteJson(ctx.Response, new { ok = true, path = picked });
    }

#if WINDOWS
    /// <summary>
    /// Диалоги WinForms требуют STA, а запрос обрабатывается в потоке пула,
    /// поэтому окно поднимается на отдельном потоке и мы ждём его закрытия.
    /// Владелец — скрытая форма поверх остальных: иначе диалог уходит за окно
    /// приложения и выглядит как зависание.
    /// </summary>
    internal static string ShowPicker(string mode, string ext, string start)
    {
        var result = "";
        var thread = new Thread(() =>
        {
            using var owner = new System.Windows.Forms.Form
            {
                TopMost       = true,
                ShowInTaskbar = false,
                Size          = new System.Drawing.Size(0, 0),
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            };
            owner.Show();
            owner.Hide();

            if (mode == "folder")
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                if (Directory.Exists(start)) dialog.SelectedPath = start;
                if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    result = dialog.SelectedPath;
            }
            else
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    CheckFileExists = true,
                    Filter          = FilterFor(ext),
                };
                var dir = Directory.Exists(start) ? start
                        : File.Exists(start)      ? Path.GetDirectoryName(start)
                        : null;
                if (!string.IsNullOrEmpty(dir)) dialog.InitialDirectory = dir;

                if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    result = dialog.FileName;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>Фильтр по типу задачи — чтобы не искать .xml среди всего подряд.</summary>
    private static string FilterFor(string ext) => ext switch
    {
        "xml"     => "Шаблон ZennoPoster (*.xml)|*.xml|Все файлы (*.*)|*.*",
        "csx"     => "Скрипт C# (*.csx)|*.csx|Все файлы (*.*)|*.*",
        "py"      => "Python (*.py)|*.py|Все файлы (*.*)|*.*",
        "js"      => "JavaScript (*.js;*.ts)|*.js;*.ts|Все файлы (*.*)|*.*",
        "ps1"     => "PowerShell (*.ps1)|*.ps1|Все файлы (*.*)|*.*",
        "exe"     => "Программа (*.exe)|*.exe|Все файлы (*.*)|*.*",
        "dll"     => "Библиотека .NET (*.dll)|*.dll",
        "cmd"     => "Пакетный файл (*.cmd;*.bat)|*.cmd;*.bat|Все файлы (*.*)|*.*",
        "sh"      => "Shell (*.sh)|*.sh|Все файлы (*.*)|*.*",
        _         => "Все файлы (*.*)|*.*",
    };
#else
    internal static string ShowPicker(string mode, string ext, string start)
        => throw new PlatformNotSupportedException(
            "Выбор пути через системный диалог доступен только в сборке под Windows.");
#endif

}
