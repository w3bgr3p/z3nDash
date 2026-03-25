using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

internal sealed class AiReportHandler
{
    private readonly DbConnectionService _dbService;

    private static readonly string[] Models =
    [
        "deepseek-ai/DeepSeek-V3.2",
        "meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8",
        "meta-llama/Llama-3.3-70B-Instruct",
        "mistralai/Mistral-Large-Instruct-2411",
        "zai-org/GLM-4.6",
        "openai/gpt-oss-20b",
    ];

    public AiReportHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool Matches(string path) => path.StartsWith("/ai-report");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";

        if (path == "/ai-report/api/analyze" && ctx.Request.HttpMethod == "POST")
        {
            await HandleAnalyze(ctx);
            return;
        }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ── analyze endpoint ───────────────────────────────────────────────────────

    private async Task HandleAnalyze(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db))
        {
            ctx.Response.StatusCode = 503;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "Database not connected" });
            return;
        }

        string apiKey = db!.Get("value", tableName: "api", where: "\"id\" = '__aiio'");
        if (string.IsNullOrEmpty(apiKey))
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" });
            return;
        }

        string projectName;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            projectName = json.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "";
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "Invalid request body" });
            return;
        }

        if (string.IsNullOrEmpty(projectName))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "project name required" });
            return;
        }

        var accounts = ReadAccounts(db!, $"__{projectName}");

        if (accounts.Count == 0)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { project = projectName, analysis = "No data." });
            return;
        }

        var prompt = BuildPrompt(projectName, accounts);
        var model  = Models[new Random().Next(Models.Length)];
        var result = await CallAiio(apiKey, model, prompt);

        await HttpHelpers.WriteJson(ctx.Response, new { project = projectName, model, analysis = result });
    }

    // ── data access ────────────────────────────────────────────────────────────

    private static List<AccountEntry> ReadAccounts(Db db, string tableName)
    {
        var result = new List<AccountEntry>();

        var lines = db.GetLines(
            "id, last",
            tableName: tableName,
            where: "\"last\" LIKE '+ %' OR \"last\" LIKE '- %'"
        );

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var columns = line.Split('¦');
            if (columns.Length < 2) continue;

            var lastData = columns[1];
            if (string.IsNullOrWhiteSpace(lastData)) continue;

            var rows  = lastData.Split('\n');
            var parts = rows[0].Split(' ');
            if (parts.Length < 2) continue;

            var status    = parts[0].Trim();
            var timestamp = parts.Length >= 2 ? parts[1].Trim() : "";
            var secRaw    = parts.Length >= 3 ? parts[2].Trim() : "0";
            double.TryParse(secRaw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double sec);
            var report = rows.Length > 1 ? string.Join("\n", rows.Skip(1)).Trim() : "";

            result.Add(new AccountEntry(status, timestamp, sec, report));
        }

        return result;
    }

    // ── prompt ─────────────────────────────────────────────────────────────────

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

        if (okTimes.Count > 0)
            sb.AppendLine($"OK timing   — min:{okTimes.Min():F0}s avg:{okTimes.Average():F0}s max:{okTimes.Max():F0}s");
        if (failTimes.Count > 0)
            sb.AppendLine($"FAIL timing — min:{failTimes.Min():F0}s avg:{failTimes.Average():F0}s max:{failTimes.Max():F0}s");

        sb.AppendLine();

        var rnd        = new Random();
        var okSample   = accounts.Where(a => a.Status == "+").OrderBy(_ => rnd.Next()).Take(15);
        var failSample = accounts.Where(a => a.Status != "+").OrderBy(_ => rnd.Next()).Take(15);

        foreach (var acc in failSample.Concat(okSample))
        {
            var label  = acc.Status == "+" ? "OK" : "FAIL";
            var report = acc.Report?.Replace('\n', ' ').Trim();
            sb.AppendLine($"[{label}] {acc.Sec:F0}s | {report}");
        }

        return sb.ToString();
    }

    // ── aiio ───────────────────────────────────────────────────────────────────

    private const string AiioUrl = "https://api.intelligence.io.solutions/api/v1/chat/completions";

    private static async Task<string> CallAiio(string apiKey, string model, string prompt)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new
                {
                    role    = "system",
                    content = "You are a concise automation analyst. Analyze the provided account farm run report. " +
                              "Output: 1) status summary 2) key issues from failed accounts 3) performance notes. " +
                              "Be specific. Max 300 words."
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            top_p       = 0.9,
            stream      = false,
            max_tokens  = 600
        });

        using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, AiioUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        return json
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "No response";
    }

    private record AccountEntry(string Status, string Timestamp, double Sec, string Report);
}