using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

internal sealed class AiReportHandler
{
    private readonly DbConnectionService _dbService;

    private static List<string>? _modelsCache;

    private const string CacheTable = "_ai_reports";
    private const string Lang = "russian";
    public AiReportHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool Matches(string path) => path.StartsWith("/ai-report");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (path == "/ai-report/api/models" && method == "GET")       { await HandleModels(ctx);      return; }
        if (path == "/ai-report/api/analyze" && method == "POST")     { await HandleAnalyze(ctx);     return; }
        if (path == "/ai-report/api/analyze-all" && method == "POST") { await HandleAnalyzeAll(ctx);  return; }
        if (path == "/ai-report/api/cache" && method == "GET")        { await HandleCacheGet(ctx);    return; }
        if (path == "/ai-report/api/cache" && method == "DELETE")     { await HandleCacheDelete(ctx); return; }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ── api key ────────────────────────────────────────────────────────────────

    private static string? GetApiKey(Db db)
    {
        var now  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var lines = db.GetLines(
            "api",
            tableName: "__aiio",
            where: $"(\"expire\" = '' OR \"expire\" IS NULL OR \"expire\" > '{now}')"
        );
        var keys = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (keys.Count == 0) return null;
        return keys[new Random().Next(keys.Count)].Trim();
    }

    // ── cache table ────────────────────────────────────────────────────────────

    private static void EnsureCacheTable(Db db)
    {
        db.CreateTable(new Dictionary<string, string>
        {
            ["project"] = "TEXT PRIMARY KEY",
            ["report"]  = "TEXT",
            ["ts"]      = "TEXT",
            ["model"]   = "TEXT"
        }, CacheTable);
    }

    private static void SaveCache(Db db, string project, string model, string analysis)
    {
        EnsureCacheTable(db);
        var ts      = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var escaped = analysis.Replace("'", "''");
        var mEsc    = model.Replace("'", "''");
        var pEsc    = project.Replace("'", "''");

        // upsert
        db.Query(
            $"INSERT INTO \"{CacheTable}\" (\"project\", \"report\", \"ts\", \"model\") " +
            $"VALUES ('{pEsc}', '{escaped}', '{ts}', '{mEsc}') " +
            $"ON CONFLICT (\"project\") DO UPDATE SET \"report\" = excluded.\"report\", \"ts\" = excluded.\"ts\", \"model\" = excluded.\"model\""
        );
    }

    // ── GET /ai-report/api/cache ───────────────────────────────────────────────

    private async Task HandleCacheGet(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        EnsureCacheTable(db!);

        var lines = db!.GetLines("project, model, ts, report", tableName: CacheTable, where: "1=1");
        var entries = new List<object>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('¦');
            if (cols.Length < 4) continue;
            entries.Add(new
            {
                project  = cols[0].Trim(),
                model    = cols[1].Trim(),
                ts       = cols[2].Trim(),
                analysis = cols[3].Trim()
            });
        }

        await HttpHelpers.WriteJson(ctx.Response, new { entries });
    }

    // ── DELETE /ai-report/api/cache?project=X ─────────────────────────────────

    private async Task HandleCacheDelete(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        var project = ctx.Request.QueryString["project"] ?? "";
        if (string.IsNullOrEmpty(project)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "project required" }); return; }

        EnsureCacheTable(db!);
        db!.Del(tableName: CacheTable, where: $"\"project\" = '{project.Replace("'", "''")}'");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── models ─────────────────────────────────────────────────────────────────

    private async Task HandleModels(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        if (_modelsCache != null) { await HttpHelpers.WriteJson(ctx.Response, new { models = _modelsCache }); return; }

        string? apiKey = GetApiKey(db!);
        if (string.IsNullOrEmpty(apiKey)) { ctx.Response.StatusCode = 500; await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" }); return; }

        try
        {
            using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.intelligence.io.solutions/api/v1/models?page=1&page_size=200");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var response = await http.SendAsync(request);
            var raw  = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)response.StatusCode}\n{raw}");

            var json   = JsonSerializer.Deserialize<JsonElement>(raw);
            var models = json.GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id)
                .ToList();

            _modelsCache = models;
            await HttpHelpers.WriteJson(ctx.Response, new { models });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── single project ─────────────────────────────────────────────────────────

    private async Task HandleAnalyze(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        string? apiKey = GetApiKey(db!);
        if (string.IsNullOrEmpty(apiKey)) { ctx.Response.StatusCode = 500; await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" }); return; }

        string projectName, model;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            projectName = json.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "";
            model       = json.TryGetProperty("model",   out var m) ? m.GetString() ?? "" : "";
        }
        catch { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" }); return; }

        if (string.IsNullOrEmpty(projectName)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "project required" }); return; }
        if (string.IsNullOrEmpty(model)) model = _modelsCache?.FirstOrDefault() ?? "deepseek-ai/DeepSeek-V3.2";

        var accounts = ReadAccounts(db!, $"__{projectName}");
        if (accounts.Count == 0) { await HttpHelpers.WriteJson(ctx.Response, new { project = projectName, analysis = "No data." }); return; }

        string result;
        try   { result = await CallAiio(apiKey, model, BuildPrompt(projectName, accounts)); }
        catch (Exception ex) { await HttpHelpers.WriteJson(ctx.Response, new { project = projectName, model, error = ex.Message }); return; }

        SaveCache(db!, projectName, model, result);
        await HttpHelpers.WriteJson(ctx.Response, new { project = projectName, model, analysis = result });
    }

    // ── cross-project summary ──────────────────────────────────────────────────

    private async Task HandleAnalyzeAll(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        string? apiKey = GetApiKey(db!);
        if (string.IsNullOrEmpty(apiKey)) { ctx.Response.StatusCode = 500; await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" }); return; }

        string model;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            model = json.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        }
        catch { model = ""; }

        if (string.IsNullOrEmpty(model)) model = _modelsCache?.FirstOrDefault() ?? "deepseek-ai/DeepSeek-V3.2";

        var projectTables = db!.GetTables()
            .Where(t => t.StartsWith("__") && !t.StartsWith("__|") && t != CacheTable)
            .ToList();

        var byAccount  = new Dictionary<string, List<(string Project, string Status, string Report)>>();
        var perProject = new List<(string Name, int Total, int Ok, int Fail)>();

        foreach (var table in projectTables)
        {
            var projectName = table.Substring(2);
            var accounts    = ReadAccountsWithId(db!, table);
            if (accounts.Count == 0) continue;

            int ok   = accounts.Values.Count(a => a.Status == "+");
            int fail = accounts.Count - ok;
            perProject.Add((projectName, accounts.Count, ok, fail));

            foreach (var (id, acc) in accounts)
            {
                if (!byAccount.ContainsKey(id))
                    byAccount[id] = new List<(string, string, string)>();
                byAccount[id].Add((projectName, acc.Status, acc.Report ?? ""));
            }
        }

        if (perProject.Count == 0) { await HttpHelpers.WriteJson(ctx.Response, new { project = "__all__", analysis = "No data." }); return; }

        string result;
        try
        {
            result = await CallAiio(apiKey, model, BuildCrossPrompt(perProject, byAccount), systemPrompt:
                "You are a concise automation analyst. You receive a cross-project account farm report. " +
                "Each account failure entry includes the project's own failRate. " +
                "IMPORTANT DISTINCTION: " +
                "- If an account fails across projects where ALL those projects have high failRate (>70%) — this is a PROJECT-LEVEL issue (maintenance, ban wave, infra), not the account. " +
                "- Account-level issue (proxy, linked resource, ban) is only likely when the account fails in a project that has a LOW failRate (<30%). " +
                "Tasks: 1) overall health summary 2) identify project-level failure clusters 3) identify genuine account-level suspects with justification 4) recurring error patterns. " +
                "Name specific account IDs, project names, error types. Max 400 words." +
                $"Response language: {Lang}");
        }
        catch (Exception ex) { await HttpHelpers.WriteJson(ctx.Response, new { project = "__all__", model, error = ex.Message }); return; }

        SaveCache(db!, "__all__", model, result);
        await HttpHelpers.WriteJson(ctx.Response, new { project = "__all__", model, analysis = result });
    }

    // ── data access ────────────────────────────────────────────────────────────

    private static List<AccountEntry> ReadAccounts(Db db, string tableName)
    {
        var result = new List<AccountEntry>();
        var lines  = db.GetLines("id, last", tableName: tableName, where: "\"last\" LIKE '+ %' OR \"last\" LIKE '- %'");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('¦');
            if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[1])) continue;

            var rows  = cols[1].Split('\n');
            var parts = rows[0].Split(' ');
            if (parts.Length < 2) continue;

            double.TryParse(parts.Length >= 3 ? parts[2].Trim() : "0",
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double sec);

            result.Add(new AccountEntry(parts[0].Trim(), parts[1].Trim(), sec,
                rows.Length > 1 ? string.Join("\n", rows.Skip(1)).Trim() : ""));
        }
        return result;
    }

    private static Dictionary<string, AccountEntry> ReadAccountsWithId(Db db, string tableName)
    {
        var result = new Dictionary<string, AccountEntry>();
        var lines  = db.GetLines("id, last", tableName: tableName, where: "\"last\" LIKE '+ %' OR \"last\" LIKE '- %'");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('¦');
            if (cols.Length < 2 || string.IsNullOrWhiteSpace(cols[1])) continue;

            var id    = cols[0].Trim();
            var rows  = cols[1].Split('\n');
            var parts = rows[0].Split(' ');
            if (parts.Length < 2) continue;

            double.TryParse(parts.Length >= 3 ? parts[2].Trim() : "0",
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double sec);

            result[id] = new AccountEntry(parts[0].Trim(), parts[1].Trim(), sec,
                rows.Length > 1 ? string.Join("\n", rows.Skip(1)).Trim() : "");
        }
        return result;
    }

    // ── prompts ────────────────────────────────────────────────────────────────

    private static string BuildPrompt(string projectName, List<AccountEntry> accounts)
    {
        int total   = accounts.Count;
        int success = accounts.Count(a => a.Status == "+");
        int failed  = total - success;
        double rate = total > 0 ? success * 100.0 / total : 0;

        var okTimes   = accounts.Where(a => a.Status == "+" && a.Sec > 0).Select(a => a.Sec).ToList();
        var failTimes = accounts.Where(a => a.Status != "+" && a.Sec > 0).Select(a => a.Sec).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine($"Total: {total} | OK: {success} | FAIL: {failed} | Rate: {rate:F1}%");
        if (okTimes.Count > 0)   sb.AppendLine($"OK timing   — min:{okTimes.Min():F0}s avg:{okTimes.Average():F0}s max:{okTimes.Max():F0}s");
        if (failTimes.Count > 0) sb.AppendLine($"FAIL timing — min:{failTimes.Min():F0}s avg:{failTimes.Average():F0}s max:{failTimes.Max():F0}s");
        sb.AppendLine();

        var rnd        = new Random();
        var okSample   = accounts.Where(a => a.Status == "+").OrderBy(_ => rnd.Next()).Take(15);
        var failSample = accounts.Where(a => a.Status != "+").OrderBy(_ => rnd.Next()).Take(15);

        foreach (var acc in failSample.Concat(okSample))
            sb.AppendLine($"[{(acc.Status == "+" ? "OK" : "FAIL")}] {acc.Sec:F0}s | {acc.Report?.Replace('\n', ' ').Trim()}");

        return sb.ToString();
    }

    private static string BuildCrossPrompt(
        List<(string Name, int Total, int Ok, int Fail)> perProject,
        Dictionary<string, List<(string Project, string Status, string Report)>> byAccount)
    {
        var projectFailRate = perProject.ToDictionary(
            p => p.Name,
            p => p.Total > 0 ? p.Fail * 100.0 / p.Total : 0.0);

        var sb = new StringBuilder();
        sb.AppendLine($"Cross-project analysis. Projects: {perProject.Count}");
        sb.AppendLine();
        sb.AppendLine("=== Per-project summary ===");
        foreach (var (name, total, ok, fail) in perProject.OrderByDescending(p => p.Fail))
        {
            double rate = total > 0 ? ok * 100.0 / total : 0;
            sb.AppendLine($"{name}: total={total} ok={ok} fail={fail} failRate={100 - rate:F1}%");
        }

        sb.AppendLine();
        sb.AppendLine("=== Accounts failing in multiple projects ===");
        sb.AppendLine("NOTE: failRate shown per project. High failRate (>70%) in ALL projects = project-level issue, not account.");

        var multiFailAccounts = byAccount
            .Where(kv => kv.Value.Count(e => e.Status != "+") >= 2)
            .OrderByDescending(kv => kv.Value.Count(e => e.Status != "+"))
            .Take(30).ToList();

        if (multiFailAccounts.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            foreach (var (id, entries) in multiFailAccounts)
            {
                var fails = entries.Where(e => e.Status != "+").ToList();
                sb.AppendLine($"Account #{id} — fails in {fails.Count} projects:");
                foreach (var (proj, _, report) in fails)
                {
                    var fr = projectFailRate.TryGetValue(proj, out var v) ? $"failRate={v:F1}%" : "";
                    var r  = (report ?? "").Replace('\n', ' ').Trim();
                    if (r.Length > 120) r = r.Substring(0, 120);
                    sb.AppendLine($"  [{proj} {fr}] {r}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== Recurring error patterns (fail reports sample) ===");
        var rnd      = new Random();
        var allFails = byAccount.Values
            .SelectMany(v => v.Where(e => e.Status != "+" && !string.IsNullOrWhiteSpace(e.Report)))
            .OrderBy(_ => rnd.Next()).Take(40).ToList();

        foreach (var (proj, _, report) in allFails)
        {
            var r = report.Replace('\n', ' ').Trim();
            if (r.Length > 150) r = r.Substring(0, 150);
            sb.AppendLine($"[{proj}] {r}");
        }

        return sb.ToString();
    }

    // ── aiio ───────────────────────────────────────────────────────────────────

    private const string AiioUrl = "https://api.intelligence.io.solutions/api/v1/chat/completions";

    private static async Task<string> CallAiio(string apiKey, string model, string prompt,
        string systemPrompt =
            "You are a concise automation analyst. Analyze the provided account farm run report. " +
            "Output: 1) status summary 2) key issues from failed accounts 3) performance notes. " +
            "Be specific. Max 300 words." +
            $"Response language: {Lang}")
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages   = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = prompt } },
            temperature = 0.3,
            top_p       = 0.9,
            stream      = false,
            max_tokens  = 800
        });

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var request = new HttpRequestMessage(HttpMethod.Post, AiioUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)response.StatusCode}\n{raw}");

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            return json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "No response";
        }
        catch (Exception ex)
        {
            throw new Exception($"{ex.Message}\nRAW:\n{raw}");
        }
    }

    private record AccountEntry(string Status, string Timestamp, double Sec, string Report);
}