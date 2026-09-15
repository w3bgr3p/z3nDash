using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Обработчик ZP-роутов.
///
/// Роуты:
///   GET  /zp/nodes       — список зарегистрированных node-сервисов
///   GET  /zp/traffic     — HTTP-трафик с ноды (хвост trafficLog.jsonl)
///   GET  /zp/version     — версии z3n7, ZennoPoster и рантайма на node
///   GET  /zp/state       — состояние конкретного node
///   GET  /zp/state/all   — агрегированное состояние всех node
///   GET  /zp/task/settings — input settings конкретной задачи
///   POST /zp/commands    — отправить команду напрямую node
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
        db.PrepareTable(DbSchema.ZpNodes.Columns, DbSchema.ZpNodes.Name);
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
            if (path == "/zp/commands"       && method == "POST")   { await PostCommand(context, db);     return true; }
            if (path == "/zp/nodes"          && method == "GET")    { await GetNodes(context, db);        return true; }
            if (path == "/zp/nodes"          && method == "POST")   { await UpsertNode(context, db);      return true; }
            if (path == "/zp/nodes"          && method == "DELETE") { await DeleteNode(context, db);      return true; }
            if (path == "/zp/log"            && method == "GET")    { await GetLog(context, db);          return true; }
            if (path == "/zp/traffic"        && method == "GET")    { await GetTraffic(context, db);      return true; }
            if (path == "/zp/task/settings"  && method == "GET")    { await GetTaskSettings(context, db); return true; }
            if (path == "/zp/version"        && method == "GET")    { await GetVersion(context, db);      return true; }
            if (path == "/zp/state"  && method == "GET")  { await GetState(context, db);  return true; }
            if (path == "/zp/state/all" && method == "GET") { await GetStateAll(context, db); return true; }
        }
        catch (Exception ex)
        {
            await WriteError(context.Response, 500, ex.Message);
        }

        return true;
    }
    
    private static readonly System.Net.Http.HttpClient _http = new();
    private static readonly TimeSpan NodeProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NodeStateTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NodeTrafficTimeout = TimeSpan.FromSeconds(20);

    private static ZpNodeRow? GetNode(Db db, string machine)
        => new ZpNodeStore(db).GetAll()
            .FirstOrDefault(item => item.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase));

    private static string NodeUrl(ZpNodeRow node) => $"http://{node.Host}:{node.Port}";

    /// <summary>
    /// Запрос к узлу с токеном в заголовке. Пустой токен заголовок не ставит:
    /// узел ответит 401, и это честнее, чем послать «Bearer » без значения.
    /// </summary>
    private static HttpRequestMessage NodeRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    /// <summary>
    /// Текст на 401 от узла: это наблюдение («узел ответил 401») плюс действие,
    /// которое его снимает. Причину — сменился токен, не тот узел — не гадаем.
    /// </summary>
    private static string UnauthorizedHint(string machine)
        => $"Node {machine} answered 401 unauthorized — re-add the node string printed by ZennoPoster";

    // ── Handlers ──────────────────────────────────────────────────────────────
    // GET /zp/nodes
    private async Task GetNodes(HttpListenerContext ctx, Db db)
    {
        var store = new ZpNodeStore(db);
        if (ctx.Request.QueryString["probe"] == "false")
        {
            await WriteJson(ctx.Response, store.GetAll().Select(node => new
            {
                machine = node.Machine,
                host = node.Host,
                port = node.Port,
                updated_at = node.UpdatedAt,
            }));
            return;
        }
        var probes = store.GetAll().Select(async node =>
        {
            var probe = await ProbeNode(node);
            var version = probe == ZpNodeProbe.Ok ? await FetchVersion(node) : null;
            return new
            {
                machine = node.Machine,
                host = node.Host,
                port = node.Port,
                updated_at = node.UpdatedAt,
                status = probe.ToString().ToLowerInvariant(),
                available = probe == ZpNodeProbe.Ok,
                z3n7 = version?.Z3n7 ?? "",
                zennoposter = version?.ZennoPoster ?? "",
                framework = version?.Framework ?? "",
            };
        });

        var result = await Task.WhenAll(probes);
        await WriteJson(ctx.Response, result);
    }

    private static async Task UpsertNode(HttpListenerContext ctx, Db db)
    {
        var raw = await ReadBody(ctx.Request);
        if (!ZpNodeStore.TryParse(raw, out var node, out var error))
        {
            await WriteError(ctx.Response, 400, error);
            return;
        }

        var (reachableHost, probe) = await new ZpNodeRegistrar(new ZpNodeStore(db)).RegisterAsync(
            node,
            Environment.MachineName,
            ProbeAddress);
        if (reachableHost == null)
        {
            await WriteError(ctx.Response, 502, probe == ZpNodeProbe.Unauthorized
                ? "node answered 401 unauthorized — the token in this string does not match the one the node expects"
                : "node unreachable on loopback, local and external addresses");
            return;
        }

        await WriteJson(ctx.Response, new { ok = true, machine = node.Machine, host = reachableHost });
    }

    private static async Task DeleteNode(HttpListenerContext ctx, Db db)
    {
        var machine = ctx.Request.QueryString["machine"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(machine))
        {
            await WriteError(ctx.Response, 400, "machine required");
            return;
        }

        new ZpNodeStore(db).Delete(machine);
        await WriteJson(ctx.Response, new { ok = true });
    }

    private static async Task<ZpNodeProbe> ProbeNode(ZpNodeRow node)
        => await ProbeAddress(node.Host, node.Port, node.Token);

    private static async Task<ZpNodeProbe> ProbeAddress(string host, int port, string token)
    {
        using var timeout = new CancellationTokenSource(NodeProbeTimeout);
        try
        {
            using var request = NodeRequest(HttpMethod.Get, $"http://{host}:{port}/state", token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode) return ZpNodeProbe.Ok;
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? ZpNodeProbe.Unauthorized
                : ZpNodeProbe.Unreachable;
        }
        catch
        {
            return ZpNodeProbe.Unreachable;
        }
    }

    /// <summary>
    /// Версии с узла. Пустой результат — не «узел мёртв»: сборка z3n7 без роута
    /// /version отвечает 404, и это ровно тот случай, когда версию показать
    /// нечем, а всё остальное на узле работает.
    /// </summary>
    private static async Task<ZpNodeVersion?> FetchVersion(ZpNodeRow node)
    {
        using var timeout = new CancellationTokenSource(NodeProbeTimeout);
        try
        {
            using var request = NodeRequest(HttpMethod.Get, $"{NodeUrl(node)}/version", node.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var info = JsonSerializer.Deserialize<JsonElement>(body);
            if (info.ValueKind != JsonValueKind.Object) return null;

            return new ZpNodeVersion(
                ReadVersionField(info, "z3n7"),
                ReadVersionField(info, "zennoposter"),
                ReadVersionField(info, "framework"));
        }
        catch
        {
            return null;
        }
    }

    private static string ReadVersionField(JsonElement info, string name)
        => info.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // GET /zp/version?machine=PC
    private async Task GetVersion(HttpListenerContext ctx, Db db)
    {
        var machine = ctx.Request.QueryString["machine"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(machine))
        {
            await WriteError(ctx.Response, 400, "machine required");
            return;
        }

        var node = GetNode(db, machine);
        if (node == null)
        {
            await WriteError(ctx.Response, 404, $"Node not found: {machine}");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = NodeRequest(HttpMethod.Get, $"{NodeUrl(node)}/version", node.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            ctx.Response.StatusCode = (int)response.StatusCode;
            await WriteRaw(ctx.Response, body);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx.Response, 504, "Node version request timed out");
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
        }
    }

    private async Task GetLog(HttpListenerContext ctx, Db db)
    {
        var query = ctx.Request.QueryString;
        var machine = query["machine"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(machine))
        {
            await WriteError(ctx.Response, 400, "machine required");
            return;
        }

        if (!ZpLogRequest.TryCreate(
                query["kind"], query["process"], query["n"], query["project"],
                out var logRequest, out var error))
        {
            await WriteError(ctx.Response, 400, error);
            return;
        }

        var node = GetNode(db, machine);
        if (node == null)
        {
            await WriteError(ctx.Response, 404, $"Node not found: {machine}");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = NodeRequest(HttpMethod.Get, NodeUrl(node) + logRequest.BuildPath(), node.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            ctx.Response.StatusCode = (int)response.StatusCode;
            await WriteRaw(ctx.Response, body);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx.Response, 504, "Node log request timed out");
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
        }
    }

    // GET /zp/traffic?machine=PC&tail=200&project=…&task_id=…
    //
    // Проброс к ноде один-в-один, как GetLog. Таймаут больше: в записи трафика
    // лежат тела запросов и ответов, страница на 2000 записей весит заметно
    // больше страницы текстового лога.
    private async Task GetTraffic(HttpListenerContext ctx, Db db)
    {
        var query = ctx.Request.QueryString;
        var machine = query["machine"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(machine))
        {
            await WriteError(ctx.Response, 400, "machine required");
            return;
        }

        if (!ZpTrafficRequest.TryCreate(query["tail"], query["project"], query["task_id"],
                out var trafficRequest, out var error))
        {
            await WriteError(ctx.Response, 400, error);
            return;
        }

        var node = GetNode(db, machine);
        if (node == null)
        {
            await WriteError(ctx.Response, 404, $"Node not found: {machine}");
            return;
        }

        using var timeout = new CancellationTokenSource(NodeTrafficTimeout);
        try
        {
            using var request = NodeRequest(HttpMethod.Get, NodeUrl(node) + trafficRequest.BuildPath(), node.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            ctx.Response.StatusCode = (int)response.StatusCode;
            await WriteRaw(ctx.Response, body);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx.Response, 504, "Node traffic request timed out");
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
        }
    }

    private async Task GetTaskSettings(HttpListenerContext ctx, Db db)
    {
        var query = ctx.Request.QueryString;
        var machine = query["machine"]?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(machine))
        {
            await WriteError(ctx.Response, 400, "machine required");
            return;
        }

        if (!ZpTaskSettingsRequest.TryCreate(query["task_id"], out var settingsRequest, out var error))
        {
            await WriteError(ctx.Response, 400, error);
            return;
        }

        var node = GetNode(db, machine);
        if (node == null)
        {
            await WriteError(ctx.Response, 404, $"Node not found: {machine}");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = NodeRequest(HttpMethod.Get, NodeUrl(node) + settingsRequest.BuildPath(), node.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            ctx.Response.StatusCode = (int)response.StatusCode;
            await WriteRaw(ctx.Response, body);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx.Response, 504, "Node settings request timed out");
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
        }
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

        var node = GetNode(db, machine);
        if (node == null) { await WriteError(ctx.Response, 404, $"Node not found: {machine}"); return; }

        var body = JsonSerializer.Serialize(new { action, task_id = taskId, payload });

        try
        {
            using var request = NodeRequest(HttpMethod.Post, $"{NodeUrl(node)}/command", node.Token);
            request.Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(request);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            var text = await resp.Content.ReadAsStringAsync();
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await WriteRaw(ctx.Response, text);
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
        }
    }
        // GET /zp/state?machine=MACHINENAME
    private async Task GetState(HttpListenerContext ctx, Db db)
    {
        var machine = ctx.Request.QueryString["machine"] ?? "";
        if (string.IsNullOrEmpty(machine)) { await WriteError(ctx.Response, 400, "machine required"); return; }

        var node = GetNode(db, machine);
        if (node == null) { await WriteError(ctx.Response, 404, $"Node not found: {machine}"); return; }

        string raw;
        try
        {
            using var timeout = new CancellationTokenSource(NodeStateTimeout);
            using var request = NodeRequest(HttpMethod.Get, $"{NodeUrl(node)}/state", node.Token);
            using var resp = await _http.SendAsync(request, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                await WriteError(ctx.Response, 401, UnauthorizedHint(machine));
                return;
            }
            resp.EnsureSuccessStatusCode();
            raw = await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 502, $"Node unreachable: {ex.Message}");
            return;
        }

        var state   = JsonSerializer.Deserialize<JsonElement>(raw);
        var tasksB64 = state.TryGetProperty("tasks_b64", out var tb) ? tb : default;

        var tasks = new List<Dictionary<string, object>>();
        if (tasksB64.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tasksB64.EnumerateArray())
            {
                var b64 = item.GetString() ?? "";
                if (string.IsNullOrEmpty(b64)) continue;
                try
                {
                    var xml  = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    var doc  = System.Xml.Linq.XDocument.Parse("<root>" + xml + "</root>");
                    var jObj = Newtonsoft.Json.Linq.JObject.Parse(
                        Newtonsoft.Json.JsonConvert.SerializeXNode(doc))["root"];

                    var dict = new Dictionary<string, object>();
                    var el   = JsonSerializer.Deserialize<JsonElement>(jObj.ToString());
                    FlattenJson(el, "", dict);

                    var idRaw = dict.TryGetValue("Id", out var idVal) ? idVal?.ToString() ?? "" : "";
                    dict["id"]      = $"{machine}|{idRaw}";
                    dict["guid"]    = idRaw;
                    dict["machine"] = machine;

                    tasks.Add(dict);
                }
                catch { }
            }
        }

        await WriteJson(ctx.Response, new
        {
            machine   = machine,
            tasks     = tasks,
            processes = state.TryGetProperty("processes", out var p) ? p : default,
        });
    }

    private async Task GetStateAll(HttpListenerContext ctx, Db db)
    {
        var nodes = new ZpNodeStore(db).GetAll();

        var fetches = nodes.Select(async node =>
        {
            try
            {
                using var request = NodeRequest(HttpMethod.Get, $"{NodeUrl(node)}/state", node.Token);
                using var resp = await _http.SendAsync(request);
                if (!resp.IsSuccessStatusCode) return (machine: node.Machine, raw: "", ok: false);
                var raw = await resp.Content.ReadAsStringAsync();
                return (machine: node.Machine, raw, ok: true);
            }
            catch
            {
                return (machine: node.Machine, raw: "", ok: false);
            }
        });

        var results = await Task.WhenAll(fetches);

        var allTasks  = new List<object>();
        var allProcs  = new List<object>();
        var deadNodes = new List<string>();

        foreach (var (machine, raw, ok) in results)
        {
            if (!ok) { deadNodes.Add(machine); continue; }

            try
            {
                var state    = JsonSerializer.Deserialize<JsonElement>(raw);
                var tasksB64 = state.TryGetProperty("tasks_b64", out var tb) ? tb : default;

                if (tasksB64.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in tasksB64.EnumerateArray())
                    {
                        var b64 = item.GetString() ?? "";
                        if (string.IsNullOrEmpty(b64)) continue;
                        try
                        {
                            var xml  = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                            var doc  = System.Xml.Linq.XDocument.Parse("<root>" + xml + "</root>");
                            var jObj = Newtonsoft.Json.Linq.JObject.Parse(
                                Newtonsoft.Json.JsonConvert.SerializeXNode(doc))["root"];

                            var dict = new Dictionary<string, object>();
                            var el   = JsonSerializer.Deserialize<JsonElement>(jObj.ToString());
                            FlattenJson(el, "", dict);

                            var idRaw = dict.TryGetValue("Id", out var idVal) ? idVal?.ToString() ?? "" : "";
                            dict["id"]      = $"{machine}|{idRaw}";
                            dict["guid"]    = idRaw;
                            dict["machine"] = machine;

                            allTasks.Add(dict);
                        }
                        catch { }
                    }
                }

                if (state.TryGetProperty("processes", out var procs) && procs.ValueKind == JsonValueKind.Array)
                    foreach (var p in procs.EnumerateArray())
                        allProcs.Add(p);
            }
            catch { deadNodes.Add(machine); }
        }

        await WriteJson(ctx.Response, new
        {
            tasks      = allTasks,
            processes  = allProcs,
            dead_nodes = deadNodes,
        });
    }

    private static async Task WriteRaw(HttpListenerResponse res, string json)
    {
        res.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static async Task<JsonElement?> ReadJson(HttpListenerRequest request)
    {
        var body = await ReadBody(request);
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    private static async Task<string> ReadBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        return await reader.ReadToEndAsync();
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
