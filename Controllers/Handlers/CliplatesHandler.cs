using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

public class CliplatesHandler : IScriptHandler
{
    public string PathPrefix => "/clips";

    private readonly DbConnectionService _dbService;

    public CliplatesHandler(DbConnectionService dbService) => _dbService = dbService;

    private const string Table = "_clips";

    private static readonly Dictionary<string, string> Schema = new()
    {
        { "id",         "TEXT PRIMARY KEY" },
        { "path",       "TEXT DEFAULT ''" },
        { "title",      "TEXT DEFAULT ''" },
        { "content",    "TEXT DEFAULT ''" },
        { "created_at", "TEXT DEFAULT ''" }
    };

    public void Init()
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;
        db.PrepareTable(Schema, Table);
    }

    public async Task<bool> HandleRequest(HttpListenerContext ctx)
    {
        string path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        string method = ctx.Request.HttpMethod;

        if (!path.StartsWith("/clips")) return false;
        if (path == "/clips.html") return false;

        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            await WriteError(ctx.Response, 503, "DB not connected");
            return true;
        }

        try
        {
            if ((path == "/clips" || path == "/clips/") && method == "GET")
            { await GetAll(ctx.Response, db); return true; }

            if ((path == "/clips" || path == "/clips/") && method == "POST")
            { await Create(ctx, db); return true; }

            if (method == "PUT" && TryParseId(path, out string putId))
            { await Update(ctx, db, putId); return true; }

            if (method == "DELETE" && TryParseId(path, out string delId))
            { await Delete(ctx.Response, db, delId); return true; }

            await WriteError(ctx.Response, 404, "not found");
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 500, ex.Message);
        }

        return true;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private static async Task GetAll(HttpListenerResponse res, Db db)
    {
        var columns = new List<string> { "id", "path", "title", "content", "created_at" };
        var rows    = db.GetLines(string.Join(",", columns), Table, where: "\"id\" != ''");
        await WriteJson(res, RowsToList(rows, columns));
    }

    private static async Task Create(HttpListenerContext ctx, Db db)
    {
        var body = await ReadJson(ctx.Request);
        if (body == null) { await WriteError(ctx.Response, 400, "invalid json"); return; }

        var title   = body.Value.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : "";
        var path    = body.Value.TryGetProperty("path",    out var p) ? p.GetString() ?? "" : "";
        var content = body.Value.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(title)) { await WriteError(ctx.Response, 400, "title required"); return; }

        var id = Guid.NewGuid().ToString();

        db.InsertDic(new Dictionary<string, string>
        {
            { "id",         id },
            { "path",       path },
            { "title",      title },
            { "content",    content },
            { "created_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
        }, Table);

        await WriteJson(ctx.Response, new { ok = true, id });
    }

    private static async Task Update(HttpListenerContext ctx, Db db, string id)
    {
        var body = await ReadJson(ctx.Request);
        if (body == null) { await WriteError(ctx.Response, 400, "invalid json"); return; }

        var title   = body.Value.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : "";
        var path    = body.Value.TryGetProperty("path",    out var p) ? p.GetString() ?? "" : "";
        var content = body.Value.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(title)) { await WriteError(ctx.Response, 400, "title required"); return; }

        db.Upd(
            $"path = '{Esc(path)}', title = '{Esc(title)}', content = '{Esc(content)}'",
            Table, where: $"\"id\" = '{id}'"
        );

        await WriteJson(ctx.Response, new { ok = true });
    }

    private static async Task Delete(HttpListenerResponse res, Db db, string id)
    {
        db.Del(Table, where: $"\"id\" = '{id}'");
        await WriteJson(res, new { ok = true });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Esc(string s) => s.Replace("'", "''");

    private static bool TryParseId(string path, out string id)
    {
        var segments = path.TrimEnd('/').Split('/');
        if (segments.Length >= 3 && !string.IsNullOrEmpty(segments[2]))
        {
            id = segments[2];
            return true;
        }
        id = "";
        return false;
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

    private static async Task<JsonElement?> ReadJson(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream);
        var body = await reader.ReadToEndAsync();
        try   { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    private static async Task WriteJson(HttpListenerResponse res, object data)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        res.ContentType     = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task WriteError(HttpListenerResponse res, int code, string message)
    {
        res.StatusCode = code;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
        res.ContentType     = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}