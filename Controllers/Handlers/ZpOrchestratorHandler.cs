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
    
    private static readonly Dictionary<string, string> CommandsSchema = new()
    {
        { "id",         "TEXT PRIMARY KEY" },
        { "name",       "TEXT DEFAULT ''" },  // ← добавить
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
        db.PrepareTable(CommandsSchema, DbSchema.Commands);
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
        var rows = db.GetLines("_id,name,machine,_json_b64", DbSchema.Tasks, where: "\"_id\" IS NOT NULL");
        if (rows.Count == 0) { await WriteJson(ctx.Response, new List<object>()); return; }

        var result = new List<Dictionary<string, object>>();
        foreach (var row in rows)
        {
            var parts   = row.Split('¦');
            var taskId  = parts.Length > 0 ? parts[0] : "";
            var name    = parts.Length > 1 ? parts[1] : "";
            var machine = parts.Length > 2 ? parts[2] : "";
            var jsonB64 = parts.Length > 3 ? parts[3] : "";
            
            if (string.IsNullOrEmpty(taskId)) continue;

            var dict = new Dictionary<string, object>
            {
                { "Id",      taskId },
                { "name",    name },
                { "machine", machine }  
            };

            if (!string.IsNullOrEmpty(jsonB64))
            {
                try
                {
                    var json   = Encoding.UTF8.GetString(Convert.FromBase64String(jsonB64));
                    var parsed = JsonSerializer.Deserialize<JsonElement>(json);
                    FlattenJson(parsed, "", dict);
                }
                catch { }
            }

            result.Add(dict);
        }

        await WriteJson(ctx.Response, result);
    }

    /// <summary>
    /// Разворачивает вложенный JsonElement в плоский словарь с _ как разделителем.
    /// {"ExecutionSettings":{"Status":"Stop"}} → {"ExecutionSettings_Status":"Stop"}
    /// </summary>
    private static void FlattenJson(JsonElement el, string prefix, Dictionary<string, object> result)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}_{prop.Name}";
                FlattenJson(prop.Value, key, result);
            }
        }
        else
        {
            result[prefix] = el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : el.ToString();
        }
    }
    private async Task GetSettings(HttpListenerContext ctx, Db db)
    {
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var columns = db.GetTableColumns(DbSchema.Settings);
        if (columns.Count == 0)
        {
            await WriteJson(ctx.Response, new object());
            return;
        }

        var where = string.IsNullOrEmpty(taskId)
            ? "\"_id\" != ''"
            : $"\"_id\" = '{taskId}'";

        var rows = db.GetLines(string.Join(",", columns), DbSchema.Settings, where: where);
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task GetCommands(HttpListenerContext ctx, Db db)
    {
        var status = ctx.Request.QueryString["status"] ?? "";
        var taskId = ctx.Request.QueryString["task_id"] ?? "";

        var conditions = new List<string> { "\"id\" != ''" };
        if (!string.IsNullOrEmpty(status)) conditions.Add($"\"status\" = '{status}'");
        if (!string.IsNullOrEmpty(taskId)) conditions.Add($"\"task_id\" = '{taskId}'");

        var columns = new List<string> { "id", "name","task_id", "action", "payload", "status", "result", "created_at" };
        var rows    = db.GetLines(string.Join(",", columns), DbSchema.Commands, where: string.Join(" AND ", conditions));
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task PostCommand(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var id      = Guid.NewGuid().ToString();
        var taskId  = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        var action  = json.Value.TryGetProperty("action",  out var a) ? a.GetString() ?? "" : "";
        var payload = json.Value.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "";
        var machine = json.Value.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "";
        var name = json.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name))
            name = db.Get("name", DbSchema.Tasks, where: $"\"_id\" = '{taskId}'") ?? "";


        db.InsertDic(new Dictionary<string, string>
        {
            { "id",         id },
            { "name",       name },
            { "task_id",    taskId },
            { "action",     action },
            { "payload",    payload },
            { "machine",    machine },
            { "status",     "pending" },
            { "result",     "" },
            { "created_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
        }, DbSchema.Commands);

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
               DbSchema.Commands, where: $"\"id\" = '{id}'");

        await WriteJson(ctx.Response, new { ok = true });
    }

    private async Task ClearCommands(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var scope = json.Value.TryGetProperty("scope", out var s) ? s.GetString() ?? "done" : "done";
        var where = scope == "all" ? "\"id\" != ''" : "\"status\" = 'done'";

        db.Del(DbSchema.Commands, where: where);
        await WriteJson(ctx.Response, new { ok = true, scope });
    }
    private async Task GetSettingsXml(HttpListenerContext ctx, Db db)
    {
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var machine = ctx.Request.QueryString["machine"]  ?? "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }

        var where = string.IsNullOrEmpty(machine)
            ? $"\"_id\" = '{taskId}'"
            : $"\"_id\" = '{taskId}' AND \"machine\" = '{machine}'";

        var row    = db.Get("_xml_b64,_json_b64", DbSchema.Settings, where: where);
        var parts  = row?.Split('¦');
        var xmlB64 = parts?.Length > 0 ? parts[0] : "";

        if (string.IsNullOrWhiteSpace(xmlB64))
        {
            await WriteJson(ctx.Response, new { fields = new List<object>() });
            return;
        }

        string xml;
        try   { xml = Encoding.UTF8.GetString(Convert.FromBase64String(xmlB64)); }
        catch { await WriteError(ctx.Response, 500, "Failed to decode _xml_b64"); return; }

        // Текущие значения берём из _json_b64
        var jsonB64 = parts?.Length > 1 ? parts[1] : "";
        Dictionary<string, string> currentValues = new();
        if (!string.IsNullOrEmpty(jsonB64))
        {
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(jsonB64));
                currentValues = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                ?? new Dictionary<string, string>();
            }
            catch { }
        }

        await WriteJson(ctx.Response, new
        {
            task_id = taskId,
            fields  = ParseInputSettingsXml(xml, currentValues)
        });
    }

    private async Task PostSettingsXml(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var taskId  = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        var machine = json.Value.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }

        if (!json.Value.TryGetProperty("fields", out var fieldsEl))
        { await WriteError(ctx.Response, 400, "fields required"); return; }

        var where = string.IsNullOrEmpty(machine)
            ? $"\"_id\" = '{taskId}'"
            : $"\"_id\" = '{taskId}' AND \"machine\" = '{machine}'";

        // Читаем текущий _json_b64
        var existing = db.Get("_json_b64", DbSchema.Settings, where: where) ?? "";
        Dictionary<string, string> currentDict = new();
        if (!string.IsNullOrEmpty(existing))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(existing));
                currentDict = JsonSerializer.Deserialize<Dictionary<string, string>>(decoded)
                              ?? new Dictionary<string, string>();
            }
            catch { }
        }

        // Патчим значениями из fields
        foreach (var prop in fieldsEl.EnumerateObject())
            currentDict[prop.Name] = prop.Value.GetString() ?? "";

        // Пишем обратно
        var newJson   = JsonSerializer.Serialize(currentDict);
        var newJsonB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson))
                               .Replace("'", "''");

        db.Query($"UPDATE \"{DbSchema.Settings}\" SET \"_json_b64\" = '{newJsonB64}' WHERE {where}");
        var taskName = db.Get("name", DbSchema.Tasks, where: $"\"_id\" = '{taskId}'") ?? "";

        // Команда на клиент — подтянуть настройки из БД
        var cmdId = Guid.NewGuid().ToString();
        db.InsertDic(new Dictionary<string, string>
        {
            { "id",         cmdId },
            { "name",       taskName },
            { "task_id",    taskId },
            { "action",     "update_settings" },
            { "payload",    "" },
            { "machine",    machine },
            { "status",     "pending" },
            { "result",     "" },
            { "created_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
        }, DbSchema.Commands);

        await WriteJson(ctx.Response, new { ok = true, command_id = cmdId });
    }

    private static List<object> ParseInputSettingsXml(string xml,
        Dictionary<string, string>? currentValues = null)
    {
        var result = new List<object>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var el in doc.Descendants("InputSetting"))
            {
                var type      = el.Element("Type")?.Value ?? "Text";
                var name      = el.Element("Name")?.Value ?? "";
                var xmlValue  = el.Element("Value")?.Value ?? "";
                var outputVar = el.Element("OutputVariable")?.Value ?? "";
                var help      = el.Element("Help")?.Value ?? "";
                var key       = outputVar.Replace("{-Variable.", "").Replace("-}", "").Trim();
                var label     = System.Text.RegularExpressions.Regex
                    .Replace(System.Net.WebUtility.HtmlDecode(name), "<[^>]+>", "").Trim();

                // Берём актуальное значение из JSON если есть, иначе из XML
                var value = (currentValues != null && currentValues.TryGetValue(key, out var cv))
                    ? cv
                    : xmlValue;

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