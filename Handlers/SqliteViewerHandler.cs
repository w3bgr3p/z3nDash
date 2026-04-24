using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace z3nIO;

internal sealed class SqliteViewerHandler
{
    public bool Matches(string path) => path.StartsWith("/sqlite-viewer");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/sqlite-viewer/tables" && method == "POST") { await HandleTables(ctx); return; }
        if (path == "/sqlite-viewer/query"  && method == "POST") { await HandleQuery(ctx);  return; }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // POST /sqlite-viewer/tables  body = raw sqlite bytes
    // → { "tables": ["t1","t2",...] }
    private static async Task HandleTables(HttpListenerContext ctx)
    {
        var tmp = await WriteTempFile(ctx);
        if (tmp == null) { await Error(ctx, "empty body"); return; }
        try
        {
            var tables = new List<string>();
            using var conn = Open(tmp);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) tables.Add(rdr.GetString(0));
            await HttpHelpers.WriteJson(ctx.Response, new { tables });
        }
        catch (Exception ex) { await Error(ctx, ex.Message); }
        finally { TryDelete(tmp); }
    }

    // POST /sqlite-viewer/query?sql=SELECT...  body = raw sqlite bytes
    // → { "columns": [...], "rows": [[...]] }
    private static async Task HandleQuery(HttpListenerContext ctx)
    {
        var sql = ctx.Request.QueryString["sql"] ?? "";
        if (string.IsNullOrWhiteSpace(sql)) { await Error(ctx, "missing sql"); return; }

        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH",   StringComparison.OrdinalIgnoreCase))
        {
            await Error(ctx, "only SELECT allowed"); return;
        }

        var tmp = await WriteTempFile(ctx);
        if (tmp == null) { await Error(ctx, "empty body"); return; }
        try
        {
            var columns = new List<string>();
            var rows    = new List<List<object?>>();

            using var conn = Open(tmp);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = sql;
            using var rdr = cmd.ExecuteReader();

            for (int i = 0; i < rdr.FieldCount; i++) columns.Add(rdr.GetName(i));

            while (rdr.Read())
            {
                var row = new List<object?>();
                for (int i = 0; i < rdr.FieldCount; i++)
                    row.Add(rdr.IsDBNull(i) ? null : rdr.GetValue(i)?.ToString());
                rows.Add(row);
            }

            await HttpHelpers.WriteJson(ctx.Response, new { columns, rows });
        }
        catch (Exception ex) { await Error(ctx, ex.Message); }
        finally { TryDelete(tmp); }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task<string?> WriteTempFile(HttpListenerContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms);
        if (ms.Length == 0) return null;

        var tmp = Path.Combine(Path.GetTempPath(), $"z3n_sqlite_{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(tmp, ms.ToArray());
        return tmp;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static async Task Error(HttpListenerContext ctx, string msg)
    {
        ctx.Response.StatusCode = 400;
        await HttpHelpers.WriteJson(ctx.Response, new { error = msg });
    }
}