using System.Net;
using System.Text;
using System.Text.Json;
using z3n8;

namespace z3n8;

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
        "cron", "interval_minutes", "fixed_time", "on_overlap",
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
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 400; return; }
        var output = _scheduler.GetLiveOutput(id);
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
}