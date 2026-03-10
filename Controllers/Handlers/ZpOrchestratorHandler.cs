using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

/// <summary>
/// Обработчик ZP-роутов. Подключается к EmbeddedServer через HandleRequest().
///
/// Роуты:
///   GET  /zp                  — dashboard HTML
///   GET  /zp/tasks            — список задач из _tasks
///   GET  /zp/settings         — настройки из _settings (опц. ?task_id=)
///   GET  /zp/commands         — очередь команд (опц. ?status=&amp;task_id=)
///   POST /zp/commands         — создать команду { task_id, action, payload }
///   POST /zp/commands/done    — отметить выполненной { id, result, status }
///   POST /zp/commands/clear   — очистить команды { scope: "done"|"all" }
///   GET  /zp/settings-xml     — InputSettings поля для UI (?task_id=)
///   POST /zp/settings-xml     — сохранить поля и создать update_settings команду
/// </summary>
public class ZpOrchestratorHandler : IScriptHandler
{
    public string PathPrefix => "/zp";

    private readonly DbConnectionService _dbService;

    private const string TasksTable    = "_tasks";
    private const string SettingsTable = "_settings";
    private const string CommandsTable = "_commands";

    private static readonly Dictionary<string, string> CommandsSchema = new()
    {
        { "id",         "TEXT PRIMARY KEY" },
        { "task_id",    "TEXT DEFAULT ''" },
        { "action",     "TEXT DEFAULT ''" },
        { "payload",    "TEXT DEFAULT ''" },
        { "status",     "TEXT DEFAULT 'pending'" },
        { "result",     "TEXT DEFAULT ''" },
        { "created_at", "TEXT DEFAULT ''" }
    };

    public ZpOrchestratorHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    /// <summary>Инициализация таблицы команд. Вызывать при старте сервера.</summary>
    public void Init()
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;
        db.PrepareTable(CommandsSchema, CommandsTable);
    }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        string path   = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        string method = context.Request.HttpMethod;

        if (!path.StartsWith("/zp")) return false;

        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            await WriteError(context.Response, 503, "DB not connected");
            return true;
        }

        try
        {
            if (path == "/zp" || path == "/zp/")                    { await ServeZpDashboard(context.Response); return true; }
            if (path == "/zp/tasks"          && method == "GET")    { await GetTasks(context, db);        return true; }
            if (path == "/zp/settings"       && method == "GET")    { await GetSettings(context, db);     return true; }
            if (path == "/zp/commands"       && method == "GET")    { await GetCommands(context, db);     return true; }
            if (path == "/zp/commands"       && method == "POST")   { await PostCommand(context, db);     return true; }
            if (path == "/zp/commands/done"  && method == "POST")   { await MarkCommandDone(context, db); return true; }
            if (path == "/zp/commands/clear" && method == "POST")   { await ClearCommands(context, db);   return true; }
            if (path == "/zp/settings-xml"   && method == "GET")    { await GetSettingsXml(context, db);  return true; }
            if (path == "/zp/settings-xml"   && method == "POST")   { await PostSettingsXml(context, db); return true; }
        }
        catch (Exception ex)
        {
            await WriteError(context.Response, 500, ex.Message);
        }

        return true;
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private async Task GetTasks(HttpListenerContext ctx, Db db)
    {
        var columns = db.GetTableColumns(TasksTable);
        if (columns.Count == 0)
        {
            await WriteJson(ctx.Response, new List<object>());
            return;
        }

        var rows = db.GetLines(string.Join(",", columns), TasksTable, where: "\"id\" IS NOT NULL");
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task GetSettings(HttpListenerContext ctx, Db db)
    {
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var columns = db.GetTableColumns(SettingsTable);
        if (columns.Count == 0)
        {
            await WriteJson(ctx.Response, new object());
            return;
        }

        var where = string.IsNullOrEmpty(taskId)
            ? "\"id\" != ''"
            : $"\"Id\" = '{taskId}'";

        var rows = db.GetLines(string.Join(",", columns), SettingsTable, where: where);
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task GetCommands(HttpListenerContext ctx, Db db)
    {
        var status = ctx.Request.QueryString["status"] ?? "";
        var taskId = ctx.Request.QueryString["task_id"] ?? "";

        var conditions = new List<string> { "\"id\" != ''" };
        if (!string.IsNullOrEmpty(status)) conditions.Add($"\"status\" = '{status}'");
        if (!string.IsNullOrEmpty(taskId)) conditions.Add($"\"task_id\" = '{taskId}'");

        var columns = new List<string> { "id", "task_id", "action", "payload", "status", "result", "created_at" };
        var rows    = db.GetLines(string.Join(",", columns), CommandsTable, where: string.Join(" AND ", conditions));
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task PostCommand(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var id      = Guid.NewGuid().ToString();
        var taskId  = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        var action  = json.Value.TryGetProperty("action",  out var a) ? a.GetString() ?? "" : "";
        var payload = json.Value.TryGetProperty("payload", out var p) ? p.ToString()  ?? "" : "";

        db.InsertDic(new Dictionary<string, string>
        {
            { "id",         id },
            { "task_id",    taskId },
            { "action",     action },
            { "payload",    payload },
            { "status",     "pending" },
            { "result",     "" },
            { "created_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
        }, CommandsTable);

        await WriteJson(ctx.Response, new { id, status = "pending" });
    }

    private async Task MarkCommandDone(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var id     = json.Value.TryGetProperty("id",     out var i) ? i.GetString() ?? "" : "";
        var result = json.Value.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
        var status = json.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "done" : "done";

        if (string.IsNullOrEmpty(id)) { await WriteError(ctx.Response, 400, "id required"); return; }

        db.Upd($"status = '{status}', result = '{result.Replace("'", "''")}'",
               CommandsTable, where: $"\"id\" = '{id}'");

        await WriteJson(ctx.Response, new { ok = true });
    }

    private async Task ClearCommands(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var scope = json.Value.TryGetProperty("scope", out var s) ? s.GetString() ?? "done" : "done";
        var where = scope == "all" ? "\"id\" != ''" : "\"status\" = 'done'";

        db.Del(CommandsTable, where: where);
        await WriteJson(ctx.Response, new { ok = true, scope });
    }

    private async Task GetSettingsXml(HttpListenerContext ctx, Db db)
    {
        var taskId = ctx.Request.QueryString["task_id"] ?? "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }

        var xmlB64 = db.Get("_xml", SettingsTable, where: $"\"Id\" = '{taskId}'");
        if (string.IsNullOrWhiteSpace(xmlB64))
        {
            await WriteJson(ctx.Response, new { fields = new List<object>() });
            return;
        }

        string xml;
        try { xml = Encoding.UTF8.GetString(Convert.FromBase64String(xmlB64)); }
        catch { await WriteError(ctx.Response, 500, "Failed to decode _xml base64"); return; }

        await WriteJson(ctx.Response, new { task_id = taskId, fields = ParseInputSettingsXml(xml) });
    }

    private async Task PostSettingsXml(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var taskId = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }

        if (!json.Value.TryGetProperty("fields", out var fieldsEl))
        { await WriteError(ctx.Response, 400, "fields required"); return; }

        var setParts = new List<string>();
        foreach (var prop in fieldsEl.EnumerateObject())
        {
            var col = prop.Name.Replace("'", "''");
            var val = prop.Value.GetString()?.Replace("'", "''") ?? "";
            setParts.Add($"\"{col}\" = '{val}'");
        }

        if (setParts.Count > 0)
            db.Query($"UPDATE \"{SettingsTable}\" SET {string.Join(", ", setParts)} WHERE \"Id\" = '{taskId}'");

        var cmdId = Guid.NewGuid().ToString();
        db.InsertDic(new Dictionary<string, string>
        {
            { "id",         cmdId },
            { "task_id",    taskId },
            { "action",     "update_settings" },
            { "payload",    "" },
            { "status",     "pending" },
            { "result",     "" },
            { "created_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
        }, CommandsTable);

        await WriteJson(ctx.Response, new { ok = true, command_id = cmdId });
    }

    private static List<object> ParseInputSettingsXml(string xml)
    {
        var result = new List<object>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var el in doc.Descendants("InputSetting"))
            {
                var type      = el.Element("Type")?.Value ?? "Text";
                var name      = el.Element("Name")?.Value ?? "";
                var value     = el.Element("Value")?.Value ?? "";
                var outputVar = el.Element("OutputVariable")?.Value ?? "";
                var help      = el.Element("Help")?.Value ?? "";
                var key       = outputVar.Replace("{-Variable.", "").Replace("-}", "").Trim();
                var label     = System.Text.RegularExpressions.Regex
                    .Replace(System.Net.WebUtility.HtmlDecode(name), "<[^>]+>", "").Trim();

                result.Add(new { type, key, label, value, outputVar, help });
            }
        }
        catch { }
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<JsonElement?> ReadJson(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    /// <summary>Конвертирует строки из Db.GetLines (разделители · и ¦) в List&lt;Dictionary&gt;.</summary>
    private static List<Dictionary<string, string>> RowsToJson(List<string> rows, List<string> columns)
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

    private static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task WriteError(HttpListenerResponse response, int code, string message)
    {
        response.StatusCode = code;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task ServeZpDashboard(HttpListenerResponse response)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "zp-dashboard.html");
        if (File.Exists(path))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            response.ContentType = "text/html; charset=utf-8";
            await response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes("<h1>zp-dashboard.html not found</h1>");
            response.StatusCode = 404;
            await response.OutputStream.WriteAsync(bytes);
        }
        response.Close();
    }
}