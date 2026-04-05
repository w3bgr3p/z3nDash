using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nIO;

/// <summary>
/// Обработчик ZP-роутов.
///
/// Схема id: "{machine}|{guid}" — TEXT PRIMARY KEY во всех трёх таблицах.
/// Фильтрация по машине: WHERE "id" LIKE '{machine}|%'
/// Извлечение guid: id.Split('|')[1]
///
/// Роуты:
///   GET  /zp/tasks            — список задач из _tasks
///   GET  /zp/settings         — настройки из _settings (опц. ?task_id=&amp;machine=)
///   GET  /zp/commands         — очередь команд (опц. ?status=&amp;task_id=&amp;machine=)
///   POST /zp/commands         — создать команду { task_id, machine, action, payload }
///   POST /zp/commands/done    — отметить выполненной { id, result, status }
///   POST /zp/commands/clear   — очистить команды { scope: "done"|"all" }
///   GET  /zp/settings-xml     — InputSettings поля для UI (?task_id=&amp;machine=)
///   POST /zp/settings-xml     — сохранить поля и создать update_settings команду
/// </summary>
public class ZpOrchestratorHandler : IScriptHandler
{
    public string PathPrefix => "/zp";

    private readonly DbConnectionService _dbService;
    

    public ZpOrchestratorHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public void Init()
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;
        db.PrepareTable(DbSchema.Settings.Columns,  DbSchema.Settings.Name);
        db.PrepareTable(DbSchema.Tasks.Columns,     DbSchema.Tasks.Name);
        db.PrepareTable(DbSchema.Commands.Columns,  DbSchema.Commands.Name);
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
        var machine = ctx.Request.QueryString["machine"] ?? "";
        var where   = string.IsNullOrEmpty(machine)
            ? "1=1"
            : $"\"id\" LIKE '{machine}|%'";

        var rows   = db.GetLines("id,name,_json_b64", DbSchema.Tasks.Name, where: where);
        var result = new List<Dictionary<string, object>>();

        foreach (var row in rows)
        {
            var parts   = row.Split('¦');
            var id      = parts.Length > 0 ? parts[0] : "";
            var name    = parts.Length > 1 ? parts[1] : "";
            var jsonB64 = parts.Length > 2 ? parts[2] : "";
            if (string.IsNullOrEmpty(id)) continue;

            var idParts  = id.Split('|');
            var taskGuid = idParts.Length > 1 ? idParts[1] : id;
            var machine_ = idParts.Length > 1 ? idParts[0] : "";

            var dict = new Dictionary<string, object>
            {
                { "id",      id       },
                { "guid",    taskGuid },
                { "machine", machine_ },
                { "name",    name     },
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

    private async Task GetSettings(HttpListenerContext ctx, Db db)
    {
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var machine = ctx.Request.QueryString["machine"]  ?? "";

        var where = BuildIdFilter(taskId, machine);

        var columns = db.GetTableColumns(DbSchema.Settings.Name);
        if (columns.Count == 0) { await WriteJson(ctx.Response, new object()); return; }

        var rows = db.GetLines(string.Join(",", columns), DbSchema.Settings.Name, where: where);
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task GetCommands(HttpListenerContext ctx, Db db)
    {
        var status  = ctx.Request.QueryString["status"]  ?? "";
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var machine = ctx.Request.QueryString["machine"] ?? "";

        var conditions = new List<string> { "1=1" };
        if (!string.IsNullOrEmpty(status))  conditions.Add($"\"status\" = '{status}'");
        if (!string.IsNullOrEmpty(taskId))  conditions.Add($"\"task_id\" = '{taskId}'");
        if (!string.IsNullOrEmpty(machine)) conditions.Add($"\"id\" LIKE '{machine}|%'");

        var columns = new List<string> { "id", "task_id", "action", "payload", "status", "result", "created_at" };
        var rows    = db.GetLines(string.Join(",", columns), DbSchema.Commands.Name, where: string.Join(" AND ", conditions));
        await WriteJson(ctx.Response, RowsToJson(rows, columns));
    }

    private async Task PostCommand(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var taskId  = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        var action  = json.Value.TryGetProperty("action",  out var a) ? a.GetString() ?? "" : "";
        var payload = json.Value.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "";
        var machine = json.Value.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(machine)) { await WriteError(ctx.Response, 400, "machine required"); return; }

        var cmdId = $"{machine}|{Guid.NewGuid()}";

        db.Query($"INSERT INTO \"{DbSchema.Commands.Name}\" " +
                 $"(\"id\", \"task_id\", \"action\", \"payload\", \"status\", \"result\", \"created_at\") VALUES " +
                 $"('{cmdId}', '{taskId}', '{action}', '{payload.Replace("'", "''")}', 'pending', '', '{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}') " +
                 $"ON CONFLICT (\"id\") DO UPDATE SET " +
                 $"\"task_id\" = EXCLUDED.\"task_id\", \"action\" = EXCLUDED.\"action\", \"payload\" = EXCLUDED.\"payload\", " +
                 $"\"status\" = EXCLUDED.\"status\", \"result\" = EXCLUDED.\"result\", \"created_at\" = EXCLUDED.\"created_at\"");

        await WriteJson(ctx.Response, new { id = cmdId, status = "pending" });
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
               DbSchema.Commands.Name, where: $"\"id\" = '{id}'");

        await WriteJson(ctx.Response, new { ok = true });
    }

    private async Task ClearCommands(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var scope   = json.Value.TryGetProperty("scope",   out var s) ? s.GetString() ?? "done" : "done";
        var machine = json.Value.TryGetProperty("machine", out var m) ? m.GetString() ?? ""     : "";

        var conditions = new List<string>();
        conditions.Add(scope == "all" ? "1=1" : "\"status\" = 'done'");
        if (!string.IsNullOrEmpty(machine)) conditions.Add($"\"id\" LIKE '{machine}|%'");

        db.Del(DbSchema.Commands.Name, where: string.Join(" AND ", conditions));
        await WriteJson(ctx.Response, new { ok = true, scope });
    }

    private async Task GetSettingsXml(HttpListenerContext ctx, Db db)
    {
        var taskId  = ctx.Request.QueryString["task_id"] ?? "";
        var machine = ctx.Request.QueryString["machine"] ?? "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }

        var where  = BuildIdFilter(taskId, machine);
        var row    = db.Get("_xml_b64,_json_b64", DbSchema.Settings.Name, where: where);
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

        Dictionary<string, string> currentValues = new();
        var jsonB64 = parts?.Length > 1 ? parts[1] : "";
        if (!string.IsNullOrEmpty(jsonB64))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(jsonB64));
                currentValues = JsonSerializer.Deserialize<Dictionary<string, string>>(decoded) ?? new();
            }
            catch { }
        }

        await WriteJson(ctx.Response, new { task_id = taskId, fields = ParseInputSettingsXml(xml, currentValues) });
    }

    private async Task PostSettingsXml(HttpListenerContext ctx, Db db)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null) { await WriteError(ctx.Response, 400, "Invalid JSON"); return; }

        var taskId  = json.Value.TryGetProperty("task_id", out var t) ? t.GetString() ?? "" : "";
        var machine = json.Value.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(taskId)) { await WriteError(ctx.Response, 400, "task_id required"); return; }
        if (string.IsNullOrEmpty(machine)) { await WriteError(ctx.Response, 400, "machine required"); return; }

        if (!json.Value.TryGetProperty("fields", out var fieldsEl))
        { await WriteError(ctx.Response, 400, "fields required"); return; }

        var where = BuildIdFilter(taskId, machine);

        var existing = db.Get("_json_b64", DbSchema.Settings.Name, where: where) ?? "";
        Dictionary<string, string> currentDict = new();
        if (!string.IsNullOrEmpty(existing))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(existing));
                currentDict = JsonSerializer.Deserialize<Dictionary<string, string>>(decoded) ?? new();
            }
            catch { }
        }

        foreach (var prop in fieldsEl.EnumerateObject())
            currentDict[prop.Name] = prop.Value.GetString() ?? "";

        var newJsonB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(currentDict)))
                                .Replace("'", "''");

        db.Query($"UPDATE \"{DbSchema.Settings}\" SET \"_json_b64\" = '{newJsonB64}' WHERE {where}");

        var cmdId = $"{machine}|{Guid.NewGuid()}";
// PostSettingsXml — строки 305–307
        db.Query($"INSERT INTO \"{DbSchema.Commands.Name}\" " +
                 $"(\"id\", \"task_id\", \"action\", \"payload\", \"status\", \"result\", \"created_at\") VALUES " +
                 $"('{cmdId}', '{taskId}', 'update_settings', '', 'pending', '', '{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}') " +
                 $"ON CONFLICT (\"id\") DO UPDATE SET " +
                 $"\"task_id\" = EXCLUDED.\"task_id\", \"action\" = EXCLUDED.\"action\", \"payload\" = EXCLUDED.\"payload\", " +
                 $"\"status\" = EXCLUDED.\"status\", \"result\" = EXCLUDED.\"result\", \"created_at\" = EXCLUDED.\"created_at\"");

        await WriteJson(ctx.Response, new { ok = true, command_id = cmdId });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Строит WHERE-фильтр по task_id и/или machine.
    /// task_id — чистый guid, machine — имя машины.
    /// Если оба заданы: точное совпадение по PK.
    /// Если только task_id: LIKE '%|{task_id}' — ищет по всем машинам.
    /// Если только machine: LIKE '{machine}|%' — все задачи машины.
    private static string BuildIdFilter(string taskId, string machine)
    {
        if (!string.IsNullOrEmpty(machine) && !string.IsNullOrEmpty(taskId))
            return $"\"id\" = '{machine}|{taskId}'";
        if (!string.IsNullOrEmpty(taskId))
            return $"\"id\" LIKE '%|{taskId}'";
        if (!string.IsNullOrEmpty(machine))
            return $"\"id\" LIKE '{machine}|%'";
        return "1=1";
    }

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
            result[prefix] = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }
    }

    private static List<object> ParseInputSettingsXml(string xml, Dictionary<string, string>? currentValues = null)
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
                var value     = currentValues != null && currentValues.TryGetValue(key, out var cv) ? cv : xmlValue;
                result.Add(new { type, key, label, value, outputVar, help });
            }
        }
        catch { }
        return result;
    }

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

    private static async Task<JsonElement?> ReadJson(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
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