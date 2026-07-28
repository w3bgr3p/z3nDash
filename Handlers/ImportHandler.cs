using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace DevDeck;

public sealed class ImportHandler : IScriptHandler
{
    public string PathPrefix => "/import";

    private readonly DbConnectionService _dbService;

    public ImportHandler(DbConnectionService dbService) => _dbService = dbService;

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method != "POST" || !path.StartsWith("/import/")) return false;

        if (!_dbService.TryGetDb(out var db) || db == null)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "DB not connected" });
            return true;
        }

        if (!InternalTasks.IsUnlocked)
        {
            ctx.Response.StatusCode = 403;
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "jVars not loaded" });
            return true;
        }

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        try
        {
            var result = path switch
            {
                "/import/proxy"     => ImportProxy(db, body),
                "/import/addresses" => ImportAddresses(db, body),
                _                   => new ImportResult(0, $"Unknown import type: {path}")
            };

            await HttpHelpers.WriteJson(ctx.Response, new { ok = result.Error == null, imported = result.Count, error = result.Error });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = ex.Message });
        }

        return true;
    }

    // ── Proxy → _instance ─────────────────────────────────────────────────────

    ImportResult ImportProxy(Db db, string body)
    {
        var req   = Parse<LinesRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");

        var table = DbSchema.Instance.Name;
        EnsureTable(db, table, DbSchema.Instance.Columns);

        int startId = NextId(db, table);
        db.AddRange(table, lines.Count);
        int imported = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            db.Upd($"\"proxy\" = '{Esc(line)}'", table, id: startId + i);
            imported++;
        }

        return new(imported, null);
    }

    // ── Addresses → _addresses ────────────────────────────────────────────────

    ImportResult ImportAddresses(Db db, string body)
    {
        var req   = Parse<AddressesRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");

        var table = DbSchema.Addresses.Name;
        EnsureTable(db, table, DbSchema.Addresses.Columns);

        int startId = NextId(db, table);
        db.AddRange(table, lines.Count);
        int imported = 0;

        string col = req.Type == "sol" ? "sol_pk" : "evm_pk";

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            db.Upd($"\"{col}\" = '{Esc(line)}'", table, id: startId + i);
            imported++;
        }

        return new(imported, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static T Parse<T>(string body) => JsonConvert.DeserializeObject<T>(body)!;

    static List<string> ParseLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? new()
        : raw.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    static string Esc(string s) => s.Replace("'", "''");

    static int NextId(Db db, string table)
    {
        var raw = db.Query($"SELECT COALESCE(MAX(id), 0) FROM \"{table}\"");
        return int.TryParse(raw, out var n) ? n + 1 : 1;
    }

    static void EnsureTable(Db db, string table, Dictionary<string, string> cols)
    {
        if (!db.TableExists(table)) db.CreateTable(cols, table);
        else db.AddColumns(cols.Where(c => c.Key != "id").ToDictionary(c => c.Key, c => c.Value), table);
    }

    // ── Request models ────────────────────────────────────────────────────────

    record ImportResult(int Count, string? Error);

    record LinesRequest     { public string Lines { get; init; } = ""; }
    record AddressesRequest { public string Type { get; init; } = "evm"; public string Lines { get; init; } = ""; }
}
