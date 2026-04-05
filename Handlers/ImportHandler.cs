using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace z3nIO;

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

        if (!InternalTasks.IsUnlocked && path != "/import/structure")
        {
            ctx.Response.StatusCode = 403;
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "jVars not loaded" });
            return true;
        }

        string pin = ResolvePinFromJVars();

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        try
        {
            var result = path switch
            {
                "/import/structure" => await ImportStructure(db),
                "/import/wallets"   => ImportWallets(db, pin, body),
                "/import/proxy"     => ImportProxy(db, body),
                "/import/addresses" => ImportAddresses(db, body),
                "/import/social"    => ImportSocial(db, body),
                "/import/bio"       => ImportBio(db, body),
                "/import/mail"      => ImportMail(db, body),
                "/import/rpc"       => ImportRpc(db, body),
                "/import/deposits"  => ImportDeposits(db, body),
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

    // ── PIN из jVars ──────────────────────────────────────────────────────────

    static string ResolvePinFromJVars()
    {
        var jVarsJson = SAFU.DecryptHWIDOnly(InternalTasks.JVars);
        if (string.IsNullOrEmpty(jVarsJson)) return "";
        var json  = jVarsJson.TrimStart().StartsWith("{") ? jVarsJson
            : Encoding.UTF8.GetString(Convert.FromBase64String(jVarsJson));
        var vars  = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
        return vars?.GetValueOrDefault("cfgPin", "") ?? "";
    }

    // ── Wallets → _wlt ────────────────────────────────────────────────────────

    ImportResult ImportWallets(Db db, string pin, string body)
    {
        var req   = Parse<WalletsRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");

        const string table = "_wlt";
        EnsureWltTable(db);

        int startId = NextId(db, table);
        db.AddRange(table, lines.Count);
        int imported = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            int id = startId + i;

            string col = req.Type switch
            {
                "seed" => "bip39",
                "sol"  => "base58",
                _      => "secp256k1"
            };

            var encrypted = SAFU.Encode(line, pin, id.ToString());
            db.Upd($"\"{col}\" = '{Esc(encrypted)}'", table, id: id);
            imported++;
        }

        return new(imported, null);
    }

    // ── Proxy → _instance ─────────────────────────────────────────────────────

    ImportResult ImportProxy(Db db, string body)
    {
        var req   = Parse<LinesRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");

        const string table = "_instance";
        EnsureTable(db, table, new() { ["id"] = "INTEGER PRIMARY KEY", ["proxy"] = "TEXT DEFAULT ''", ["cookies"] = "TEXT DEFAULT ''", ["webgl"] = "TEXT DEFAULT ''", ["zb_id"] = "TEXT DEFAULT ''" });

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

        const string table = "_addresses";
        EnsureTable(db, table, new() { ["id"] = "INTEGER PRIMARY KEY", ["evm_pk"] = "TEXT DEFAULT ''", ["sol_pk"] = "TEXT DEFAULT ''", ["apt_pk"] = "TEXT DEFAULT ''", ["evm_seed"] = "TEXT DEFAULT ''" });

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

    // ── Social → _twitter/_discord/etc ───────────────────────────────────────

    ImportResult ImportSocial(Db db, string body)
    {
        var req   = Parse<SocialRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");
        if (string.IsNullOrWhiteSpace(req.Table)) return new(0, "table required");
        if (req.Mask == null || req.Mask.Count == 0) return new(0, "mask required");

        string sep = string.IsNullOrEmpty(req.Separator) ? ":" : req.Separator;

        var cols = SocialColumns(req.Table);
        EnsureTable(db, req.Table, cols);

        int startId = NextId(db, req.Table);
        db.AddRange(req.Table, lines.Count);
        int imported = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(sep);
            var sets  = new List<string>();

            for (int m = 0; m < req.Mask.Count && m < parts.Length; m++)
            {
                var field = req.Mask[m];
                if (string.IsNullOrWhiteSpace(field)) continue;
                sets.Add($"\"{field}\" = '{Esc(parts[m].Trim())}'");
            }

            if (sets.Count == 0) continue;
            db.Upd(string.Join(", ", sets), req.Table, id: startId + i);
            imported++;
        }

        return new(imported, null);
    }

    // ── Bio → _profile ────────────────────────────────────────────────────────

    ImportResult ImportBio(Db db, string body)
    {
        var req = Parse<BioRequest>(body);
        const string table = "_profile";
        EnsureTable(db, table, new() { ["id"] = "INTEGER PRIMARY KEY", ["nickname"] = "TEXT DEFAULT ''", ["bio"] = "TEXT DEFAULT ''", ["brsr_score"] = "TEXT DEFAULT ''" });

        int imported = 0;
        imported += BulkImport(db, table, "nickname", ParseLines(req.Nicknames));
        imported += BulkImport(db, table, "bio",      ParseLines(req.Bios));
        return new(imported, null);
    }

    // ── Mail → _mail ──────────────────────────────────────────────────────────

    ImportResult ImportMail(Db db, string body)
    {
        var req = Parse<MailRequest>(body);
        const string table = "_mail";
        EnsureTable(db, table, new() { ["id"] = "INTEGER PRIMARY KEY", ["google"] = "TEXT DEFAULT ''", ["icloud"] = "TEXT DEFAULT ''", ["firstmail"] = "TEXT DEFAULT ''" });

        int imported = 0;
        imported += BulkImport(db, table, "google",    ParseLines(req.Google));
        imported += BulkImport(db, table, "icloud",    ParseLines(req.Icloud));
        imported += BulkImport(db, table, "firstmail", ParseLines(req.Firstmail));
        return new(imported, null);
    }

    // ── RPC → _rpc ────────────────────────────────────────────────────────────

    ImportResult ImportRpc(Db db, string body)
    {
        var req   = Parse<LinesRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");

        const string table = "_rpc";
        EnsureTable(db, table, new() { ["id"] = "TEXT PRIMARY KEY", ["rpc"] = "TEXT DEFAULT ''", ["explorer"] = "TEXT DEFAULT ''", ["explorer_api"] = "TEXT DEFAULT ''" });

        int imported = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(';');
            if (p.Length < 2) continue;
            string id  = p[0].ToLower().Trim();
            string rpc = p.Length > 1 ? p[1].Trim() : "";
            string exp = p.Length > 2 ? p[2].Trim() : "";
            string api = p.Length > 3 ? p[3].Trim() : "";
            db.Query($"INSERT INTO \"{table}\" (id, rpc, explorer, explorer_api) VALUES ('{Esc(id)}','{Esc(rpc)}','{Esc(exp)}','{Esc(api)}') ON CONFLICT(id) DO UPDATE SET rpc=excluded.rpc, explorer=excluded.explorer, explorer_api=excluded.explorer_api");
            imported++;
        }

        return new(imported, null);
    }

    // ── Deposits → _deposits ──────────────────────────────────────────────────

    ImportResult ImportDeposits(Db db, string body)
    {
        var req   = Parse<DepositsRequest>(body);
        var lines = ParseLines(req.Lines);
        if (lines.Count == 0) return new(0, "No lines");
        if (string.IsNullOrWhiteSpace(req.Chain) || string.IsNullOrWhiteSpace(req.Cex)) return new(0, "chain and cex required");

        const string table  = "_deposits";
        string col          = $"{req.Cex.ToLower()}_{req.Chain.ToLower()}";

        EnsureTable(db, table, new() { ["id"] = "INTEGER PRIMARY KEY" });
        if (!db.ColumnExists(col, table)) db.AddColumn(col, table);

        int startId = NextId(db, table);
        db.AddRange(table, lines.Count);
        int imported = 0;

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

    static void EnsureWltTable(Db db) =>
        EnsureTable(db, "_wlt", new() { ["id"] = "BIGINT PRIMARY KEY", ["secp256k1"] = "TEXT DEFAULT ''", ["base58"] = "TEXT DEFAULT ''", ["bip39"] = "TEXT DEFAULT ''" });

    static int BulkImport(Db db, string table, string col, List<string> lines)
    {
        if (lines.Count == 0) return 0;
        int startId = NextId(db, table);
        db.AddRange(table, lines.Count);
        int imported = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            db.Upd($"\"{col}\" = '{Esc(line)}'", table, id: startId + i);
            imported++;
        }
        return imported;
    }

    static Dictionary<string, string> SocialColumns(string table) => table switch
    {
        "_twitter" => new() { ["id"] = "INTEGER PRIMARY KEY", ["status"] = "TEXT DEFAULT ''", ["last"] = "TEXT DEFAULT ''", ["cookies"] = "TEXT DEFAULT ''", ["token"] = "TEXT DEFAULT ''", ["login"] = "TEXT DEFAULT ''", ["password"] = "TEXT DEFAULT ''", ["otpsecret"] = "TEXT DEFAULT ''", ["otpbackup"] = "TEXT DEFAULT ''", ["email"] = "TEXT DEFAULT ''", ["emailpass"] = "TEXT DEFAULT ''" },
        "_discord" => new() { ["id"] = "INTEGER PRIMARY KEY", ["status"] = "TEXT DEFAULT ''", ["last"] = "TEXT DEFAULT ''", ["token"] = "TEXT DEFAULT ''", ["login"] = "TEXT DEFAULT ''", ["password"] = "TEXT DEFAULT ''", ["otpsecret"] = "TEXT DEFAULT ''", ["otpbackup"] = "TEXT DEFAULT ''", ["email"] = "TEXT DEFAULT ''", ["emailpass"] = "TEXT DEFAULT ''", ["recovery_phone"] = "TEXT DEFAULT ''" },
        "_google"  => new() { ["id"] = "INTEGER PRIMARY KEY", ["status"] = "TEXT DEFAULT ''", ["last"] = "TEXT DEFAULT ''", ["cookies"] = "TEXT DEFAULT ''", ["login"] = "TEXT DEFAULT ''", ["password"] = "TEXT DEFAULT ''", ["otpsecret"] = "TEXT DEFAULT ''", ["otpbackup"] = "TEXT DEFAULT ''", ["recoveryemail"] = "TEXT DEFAULT ''", ["recovery_phone"] = "TEXT DEFAULT ''" },
        "_github"  => new() { ["id"] = "INTEGER PRIMARY KEY", ["status"] = "TEXT DEFAULT ''", ["last"] = "TEXT DEFAULT ''", ["cookies"] = "TEXT DEFAULT ''", ["token"] = "TEXT DEFAULT ''", ["login"] = "TEXT DEFAULT ''", ["password"] = "TEXT DEFAULT ''", ["otpsecret"] = "TEXT DEFAULT ''", ["otpbackup"] = "TEXT DEFAULT ''", ["email"] = "TEXT DEFAULT ''", ["emailpass"] = "TEXT DEFAULT ''" },
        _          => new() { ["id"] = "INTEGER PRIMARY KEY" }
    };

    // ── Structure ─────────────────────────────────────────────────────────────

    static readonly HashSet<string> TextPrimaryKeyTables = new() { "_api", "_rpc" };

    static async Task<ImportResult> ImportStructure(Db db)
    {
        //const string url = "https://raw.githubusercontent.com/z3nFarm/z3n/refs/heads/master/templates/db_template.json";

        Console.WriteLine($"[ImportStructure] downloading template...");

        //string json;
        //using var http = new System.Net.Http.HttpClient();
        //http.DefaultRequestHeaders.Add("User-Agent", "z3nIO");
        //try   { json = await http.GetStringAsync(url); }
        //catch (Exception ex) { return new(0, $"Download failed: {ex.Message}"); }
        
        string templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", "db_template.json");
        string json = await File.ReadAllTextAsync(templatePath);
        
        Console.WriteLine($"[ImportStructure] downloaded {json.Length} chars");

        Dictionary<string, List<string>>? template;
        try   { template = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json); }
        catch (Exception ex) { return new(0, $"Parse failed: {ex.Message}"); }


        
        
        if (template == null || template.Count == 0)
            return new(0, "Template is empty");

        Console.WriteLine($"[ImportStructure] {template.Count} tables in template");

        int count = 0;
        foreach (var (tableName, columns) in template)
        {
            string idType = TextPrimaryKeyTables.Contains(tableName) ? "TEXT PRIMARY KEY" : "INTEGER PRIMARY KEY";

            var structure = new Dictionary<string, string> { ["id"] = idType };
            foreach (var col in columns)
            {
                var c = col.Trim();
                if (!string.IsNullOrEmpty(c) && c.ToLower() != "id")
                    structure.TryAdd(c, "TEXT DEFAULT ''");
            }

            Console.WriteLine($"[ImportStructure] → {tableName} cols:[{string.Join(", ", structure.Keys)}]");
            try
            {
                db.PrepareTable(structure, tableName, log: true, rearrange: false);
                Console.WriteLine($"[ImportStructure] ✓ {tableName}");
                count++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImportStructure] ✗ {tableName}: {ex.Message}");
            }
        }

        Console.WriteLine($"[ImportStructure] done: {count}/{template.Count}");
        return new(count, null);
    }

    // ── Request models ────────────────────────────────────────────────────────

    record ImportResult(int Count, string? Error);

    record WalletsRequest   { public string Type { get; init; } = "evm"; public string Lines { get; init; } = ""; }
    record LinesRequest     { public string Lines { get; init; } = ""; }
    record AddressesRequest { public string Type { get; init; } = "evm"; public string Lines { get; init; } = ""; }
    record SocialRequest    { public string Table { get; init; } = ""; public List<string> Mask { get; init; } = new(); public string Separator { get; init; } = ":"; public string Lines { get; init; } = ""; }
    record BioRequest       { public string Nicknames { get; init; } = ""; public string Bios { get; init; } = ""; }
    record MailRequest      { public string Google { get; init; } = ""; public string Icloud { get; init; } = ""; public string Firstmail { get; init; } = ""; }
    record DepositsRequest  { public string Chain { get; init; } = ""; public string Cex { get; init; } = ""; public string Lines { get; init; } = ""; }
}