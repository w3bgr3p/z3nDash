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
        if (path == "/sqlite-viewer/update" && method == "POST") { await HandleUpdate(ctx); return; }
        if (path == "/sqlite-viewer/delete" && method == "POST") { await HandleDelete(ctx); return; }

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
            using var conn = Open(tmp, readOnly: true);
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
    // → { "columns": [...], "rows": [[...]], "rowids": [...] }
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

        // extract table name for rowid lookup
        var table = ExtractTableName(sql);

        var tmp = await WriteTempFile(ctx);
        if (tmp == null) { await Error(ctx, "empty body"); return; }
        try
        {
            var columns = new List<string>();
            var rows    = new List<List<object?>>();
            var rowids  = new List<long?>();

            using var conn = Open(tmp, readOnly: true);

            // inject rowid into SELECT by replacing "SELECT" with "SELECT rowid,"
            // only for simple single-table queries where table name is known
            string execSql = sql;
            bool hasRowid  = false;
            if (!string.IsNullOrEmpty(table))
            {
                // replace first SELECT with SELECT rowid, (case-insensitive)
                execSql  = System.Text.RegularExpressions.Regex.Replace(
                    sql, @"(?i)^\s*SELECT\s+", "SELECT rowid, ", System.Text.RegularExpressions.RegexOptions.None);
                hasRowid = true;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = execSql;

            SqliteDataReader rdr;
            try
            {
                rdr = cmd.ExecuteReader();
            }
            catch
            {
                // rowid injection failed (e.g. JOIN, subquery) — fallback to original
                hasRowid = false;
                cmd.CommandText = sql;
                rdr = cmd.ExecuteReader();
            }

            using (rdr)
            {
                int startCol = hasRowid ? 1 : 0;
                for (int i = startCol; i < rdr.FieldCount; i++) columns.Add(rdr.GetName(i));

                while (rdr.Read())
                {
                    if (hasRowid)
                        rowids.Add(rdr.IsDBNull(0) ? null : rdr.GetInt64(0));

                    var row = new List<object?>();
                    for (int i = startCol; i < rdr.FieldCount; i++)
                        row.Add(rdr.IsDBNull(i) ? null : rdr.GetValue(i)?.ToString());
                    rows.Add(row);
                }
            }

            await HttpHelpers.WriteJson(ctx.Response, new { columns, rows, rowids, table });
        }
        catch (Exception ex) { await Error(ctx, ex.Message); }
        finally { TryDelete(tmp); }
    }

    // POST /sqlite-viewer/update?table=T&rowid=N&col=C  body = raw sqlite bytes
    // → updated file bytes (application/octet-stream)
    private static async Task HandleUpdate(HttpListenerContext ctx)
    {
        var qs    = ctx.Request.QueryString;
        var table = qs["table"] ?? "";
        var col   = qs["col"]   ?? "";
        var rowid = qs["rowid"] ?? "";
        var value = qs["value"]; // null means set NULL

        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(col) || string.IsNullOrEmpty(rowid))
        {
            await Error(ctx, "missing table/col/rowid"); return;
        }

        var tmp = await WriteTempFile(ctx);
        if (tmp == null) { await Error(ctx, "empty body"); return; }
        try
        {
            if (!long.TryParse(rowid, out var rowidVal)) { await Error(ctx, "invalid rowid"); return; }
            if (!IsIdentifier(table) || !IsIdentifier(col)) { await Error(ctx, "invalid identifier"); return; }

            using (var conn = Open(tmp, readOnly: false))
            using (var cmd  = conn.CreateCommand())
            {
                cmd.CommandText = $"UPDATE \"{Esc(table)}\" SET \"{Esc(col)}\" = @v WHERE rowid = @r";
                cmd.Parameters.AddWithValue("@r", rowidVal);
                if (value == null) cmd.Parameters.AddWithValue("@v", DBNull.Value);
                else               cmd.Parameters.AddWithValue("@v", value);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var bytes = await File.ReadAllBytesAsync(tmp);
            ctx.Response.ContentType     = "application/octet-stream";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex) { await Error(ctx, ex.Message); }
        finally { TryDelete(tmp); }
    }

    // POST /sqlite-viewer/delete?table=T&rowid=N  body = raw sqlite bytes
    // → updated file bytes (application/octet-stream)
    private static async Task HandleDelete(HttpListenerContext ctx)
    {
        var qs    = ctx.Request.QueryString;
        var table = qs["table"] ?? "";
        var rowid = qs["rowid"] ?? "";

        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(rowid))
        {
            await Error(ctx, "missing table/rowid"); return;
        }

        var tmp = await WriteTempFile(ctx);
        if (tmp == null) { await Error(ctx, "empty body"); return; }
        try
        {
            if (!long.TryParse(rowid, out var rowidVal)) { await Error(ctx, "invalid rowid"); return; }
            if (!IsIdentifier(table)) { await Error(ctx, "invalid identifier"); return; }

            using (var conn = Open(tmp, readOnly: false))
            using (var cmd  = conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM \"{Esc(table)}\" WHERE rowid = @r";
                cmd.Parameters.AddWithValue("@r", rowidVal);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var bytes = await File.ReadAllBytesAsync(tmp);
            ctx.Response.ContentType     = "application/octet-stream";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
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

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var mode = readOnly ? "ReadOnly" : "ReadWrite";
        var conn = new SqliteConnection($"Data Source={path};Mode={mode}");
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

    // basic table name extractor from "SELECT ... FROM tableName ..."
    private static string ExtractTableName(string sql)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                sql, @"\bFROM\s+[`""\[]?(\w+)[`""\]]?", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    // allow only word chars (letters, digits, underscore) — no injection via table/col names
    private static bool IsIdentifier(string s) =>
        !string.IsNullOrEmpty(s) && System.Text.RegularExpressions.Regex.IsMatch(s, @"^\w+$");

    private static string Esc(string s) => s.Replace("\"", "\"\"");
}