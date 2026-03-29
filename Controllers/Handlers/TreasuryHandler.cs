// TreasuryHandler.cs
// Handler для EmbeddedServer.
//
// Endpoints:
//   POST /treasury/update?maxId=1000&minValue=0.001&concurrency=5
//   GET  /treasury/status
//   GET  /treasury/data?maxId=1000
//   POST /treasury/ai-analyze   { model, data: [...] }  -> { analysis, model, ts }
//   GET  /treasury/ai-cache                             -> { entry } | { entry: null }
//   DELETE /treasury/ai-cache                           -> { ok }
//
// Регистрация в EmbeddedServer:
//   Добавить поле: private readonly TreasuryHandler _treasuryHandler;
//   В конструкторе:  _treasuryHandler = new TreasuryHandler(dbService);
//   В ProcessRequest: if (path.StartsWith("/treasury")) { await _treasuryHandler.Handle(ctx); return; }

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace z3n8;

internal sealed class TreasuryHandler
{
    private readonly DbConnectionService _dbService;

    private volatile bool   _running;
    private volatile int    _processed;
    private volatile int    _total;
    private volatile string _lastError = "";

    private const string AiCacheTable = "_treasury_ai_cache";
    private const string AiioUrl      = "https://api.intelligence.io.solutions/api/v1/chat/completions";
    private const string Lang          = "russian";

    public TreasuryHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool Matches(string path) => path.StartsWith("/treasury");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "POST" && path == "/treasury/update")        { await StartUpdate(ctx);    return; }
        if (method == "GET"  && path == "/treasury/status")        { await GetStatus(ctx);      return; }
        if (method == "GET"  && path == "/treasury/data")          { await GetData(ctx);         return; }
        if (method == "POST" && path == "/treasury/ai-analyze")    { await AiAnalyze(ctx);       return; }
        if (method == "GET"  && path == "/treasury/ai-cache")      { await AiCacheGet(ctx);      return; }
        if (method == "DELETE" && path == "/treasury/ai-cache")    { await AiCacheDelete(ctx);   return; }

        ctx.Response.StatusCode = 404;
        await HttpHelpers.WriteJson(ctx.Response, new { error = "Not found" });
    }

    // ── POST /treasury/update ─────────────────────────────────────────────────

    private async Task StartUpdate(HttpListenerContext ctx)
    {
        if (_running)
        {
            ctx.Response.StatusCode = 409;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "Update already running", processed = _processed, total = _total });
            return;
        }

        var qs           = ctx.Request.QueryString;
        int maxId        = int.TryParse(qs["maxId"],       out var m)  ? m  : 1000;
        decimal minValue = decimal.TryParse(qs["minValue"], System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0.001m;
        int concurrency  = int.TryParse(qs["concurrency"], out var c)  ? Math.Clamp(c, 1, 20) : 5;

        var task = Task.Run(() => RunUpdate(maxId, minValue, concurrency));
        task.ContinueWith(t => Console.WriteLine($"[treasury] task faulted: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);

        await HttpHelpers.WriteJson(ctx.Response, new { started = true, maxId, minValue, concurrency });
    }

    // ── GET /treasury/status ──────────────────────────────────────────────────

    private async Task GetStatus(HttpListenerContext ctx)
    {
        await HttpHelpers.WriteJson(ctx.Response, new
        {
            running   = _running,
            processed = _processed,
            total     = _total,
            lastError = _lastError,
        });
    }

    // ── GET /treasury/data ────────────────────────────────────────────────────

    private async Task GetData(HttpListenerContext ctx)
    {
        var db    = _dbService.GetDb();
        var qs    = ctx.Request.QueryString;
        int maxId = int.TryParse(qs["maxId"], out var m) ? m : 1000;

        var columns   = db.GetTableColumns("_treasury");
        var chainCols = columns.Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();

        var result = new List<object>();

        for (int id = 1; id <= maxId; id++)
        {
            var address = db.Get("evm", "_addresses", where: $"id = {id}");
            Console.WriteLine($"[treasury] {id} address='{address}' len={address?.Length}");

            if (string.IsNullOrEmpty(address)) continue;

            var chainData = new Dictionary<string, List<DeBankClient.TokenInfo>>();
            decimal total = 0;

            foreach (var col in chainCols)
            {
                var json = db.Get(col, "_treasury", where: $"id = {id}");
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    var tokens = JsonSerializer.Deserialize<List<DeBankClient.TokenInfo>>(json);
                    if (tokens is { Count: > 0 })
                    {
                        chainData[col] = tokens;
                        total += tokens.Sum(t => t.ValueUsd);
                    }
                }
                catch { }
            }

            result.Add(new { id, address, chainData, totalUsd = total });
        }

        await HttpHelpers.WriteJson(ctx.Response, result);
    }

    // ── POST /treasury/ai-analyze ─────────────────────────────────────────────
    // Body: { model: string, data: [ { id, address, totalUsd, chainData: { chain: [ { symbol, valueUsd, amount } ] } } ] }

    private async Task AiAnalyze(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db))
        {
            ctx.Response.StatusCode = 503;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "db" });
            return;
        }

        string? apiKey = GetApiKey(db!);
        if (string.IsNullOrEmpty(apiKey))
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "aiio key not found" });
            return;
        }

        string model;
        JsonElement dataEl;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            model  = json.TryGetProperty("model",  out var mp) ? mp.GetString() ?? "" : "";
            dataEl = json.TryGetProperty("data",   out var dp) ? dp : default;
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" });
            return;
        }

        if (string.IsNullOrEmpty(model)) model = "deepseek-ai/DeepSeek-V3.2";

        var prompt = BuildTreasuryPrompt(dataEl);

        string result;
        try
        {
            result = await CallAiio(apiKey, model, prompt);
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
            return;
        }

        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        SaveAiCache(db!, model, result, ts);

        await HttpHelpers.WriteJson(ctx.Response, new { analysis = result, model, ts });
    }

    // ── GET /treasury/ai-cache ────────────────────────────────────────────────

    private async Task AiCacheGet(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        EnsureAiCacheTable(db!);
        var lines = db!.GetLines("model, ts, report", tableName: AiCacheTable, where: "1=1");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('|');
            if (cols.Length < 3) continue;
            await HttpHelpers.WriteJson(ctx.Response, new
            {
                entry = new
                {
                    model    = cols[0].Trim(),
                    ts       = cols[1].Trim(),
                    analysis = cols[2].Trim()
                }
            });
            return;
        }

        await HttpHelpers.WriteJson(ctx.Response, new { entry = (object?)null });
    }

    // ── DELETE /treasury/ai-cache ─────────────────────────────────────────────

    private async Task AiCacheDelete(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        EnsureAiCacheTable(db!);
        db!.Del(tableName: AiCacheTable, where: "1=1");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── cache helpers ─────────────────────────────────────────────────────────

    private static void EnsureAiCacheTable(Db db)
    {
        db.CreateTable(new Dictionary<string, string>
        {
            ["id"]     = "INTEGER PRIMARY KEY",
            ["model"]  = "TEXT",
            ["ts"]     = "TEXT",
            ["report"] = "TEXT"
        }, AiCacheTable);
    }

    private static void SaveAiCache(Db db, string model, string analysis, string ts)
    {
        EnsureAiCacheTable(db);
        db.Del(tableName: AiCacheTable, where: "1=1");

        var rEsc = analysis.Replace("'", "''");
        var mEsc = model.Replace("'", "''");
        var tEsc = ts.Replace("'", "''");
        db.Query($"INSERT INTO \"{AiCacheTable}\" (\"model\", \"ts\", \"report\") VALUES ('{mEsc}', '{tEsc}', '{rEsc}')");
    }

    // ── api key (same pattern as AiReportHandler) ─────────────────────────────

    private static string? GetApiKey(Db db)
    {
        var now   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var lines = db.GetLines(
            "api",
            tableName: "__aiio",
            where: $"(\"expire\" = '' OR \"expire\" IS NULL OR \"expire\" > '{now}')"
        );
        var keys = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (keys.Count == 0) return null;
        return keys[new Random().Next(keys.Count)].Trim();
    }

    // ── prompt builder ────────────────────────────────────────────────────────

    private static string BuildTreasuryPrompt(JsonElement dataEl)
    {
        // Агрегируем данные из JSON, переданного фронтендом
        var tokenAgg  = new Dictionary<string, (double val, int accs)>(StringComparer.OrdinalIgnoreCase);
        var chainAgg  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var zeroAccs  = new List<int>();
        var largeAccs = new List<(int id, double val)>();
        var dustTokens = new List<(string sym, double val, string chain)>();

        double totalPortfolio = 0;
        int    accountCount   = 0;

        if (dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var acc in dataEl.EnumerateArray())
            {
                accountCount++;
                int    id       = acc.TryGetProperty("id",       out var idp)  ? idp.GetInt32()  : 0;
                double accTotal = acc.TryGetProperty("totalUsd", out var tp)   ? tp.GetDouble()  : 0;
                totalPortfolio += accTotal;

                if (accTotal == 0) zeroAccs.Add(id);
                if (accTotal > 10000) largeAccs.Add((id, accTotal));

                if (!acc.TryGetProperty("chainData", out var chainData) || chainData.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var chainProp in chainData.EnumerateObject())
                {
                    var chain = chainProp.Name;
                    if (chainProp.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (var tok in chainProp.Value.EnumerateArray())
                    {
                        var sym = GetStr(tok, "symbol", "Symbol");
                        var val = GetDbl(tok, "valueUsd", "ValueUsd");

                        chainAgg.TryGetValue(chain, out var cv);
                        chainAgg[chain] = cv + val;

                        if (!tokenAgg.TryGetValue(sym, out var ta)) ta = (0, 0);
                        tokenAgg[sym] = (ta.val + val, ta.accs + 1);

                        // dust: есть значение, но < $1 и не нативный газ (нет смысла свапать 0.001$)
                        if (val > 0.01 && val < 1.0)
                            dustTokens.Add((sym, val, chain));
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Treasury portfolio snapshot. Accounts: {accountCount}, Total: ${totalPortfolio:F2}");
        sb.AppendLine();

        // Топ токены
        sb.AppendLine("=== Top tokens by value ===");
        foreach (var (sym, (val, accs)) in tokenAgg.OrderByDescending(x => x.Value.val).Take(20))
            sb.AppendLine($"{sym}: ${val:F2} across {accs} accounts ({val / totalPortfolio * 100:F1}%)");

        // Цепочки
        sb.AppendLine();
        sb.AppendLine("=== Chain distribution ===");
        foreach (var (chain, val) in chainAgg.OrderByDescending(x => x.Value).Take(15))
            sb.AppendLine($"{chain}: ${val:F2} ({val / totalPortfolio * 100:F1}%)");

        // Аномалии
        sb.AppendLine();
        sb.AppendLine("=== Anomalies ===");
        if (zeroAccs.Count > 0)
            sb.AppendLine($"Zero-balance accounts ({zeroAccs.Count}): ids {string.Join(", ", zeroAccs.Take(20))}");
        if (largeAccs.Count > 0)
        {
            sb.AppendLine($"Large accounts (>$10k): {largeAccs.Count}");
            foreach (var (id, val) in largeAccs.OrderByDescending(x => x.val).Take(10))
                sb.AppendLine($"  #{id}: ${val:F2}");
        }

        // Пыль
        sb.AppendLine();
        sb.AppendLine($"=== Dust tokens (<$1, >$0.01): {dustTokens.Count} positions ===");
        var dustByToken = dustTokens.GroupBy(d => d.sym)
            .Select(g => (sym: g.Key, total: g.Sum(x => x.val), count: g.Count()))
            .OrderByDescending(x => x.total).Take(15);
        foreach (var (sym, total, count) in dustByToken)
            sb.AppendLine($"  {sym}: ${total:F3} in {count} positions");

        return sb.ToString();
    }

    private static string GetStr(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var p)) return p.GetString() ?? "?";
        return "?";
    }

    private static double GetDbl(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var p)) return p.GetDouble();
        return 0;
    }

    // ── aiio call ─────────────────────────────────────────────────────────────

    private static async Task<string> CallAiio(string apiKey, string model, string prompt)
    {
        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts","treasury.txt");
        
        var systemPrompt = File.ReadAllText(promptPath) + $" Language: {Lang}.";
        
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages    = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = prompt } },
            temperature = 0.3,
            top_p       = 0.9,
            stream      = false,
            max_tokens  = 900
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

    // ── background update (unchanged) ─────────────────────────────────────────

    private async Task RunUpdate(int maxId, decimal minValue, int concurrency)
    {
        _running   = true;
        _processed = 0;
        _total     = maxId;
        _lastError = "";

        var db        = _dbService.GetDb();
        var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var tasks     = new List<Task>();

        try
        {
            for (int id = 1; id <= maxId; id++)
            {
                var capturedId = id;
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try   { await ProcessAccount(db, capturedId, minValue); }
                    catch (Exception ex) { Console.WriteLine($"[treasury] {capturedId} ERR {ex.Message}"); }
                    finally
                    {
                        Interlocked.Increment(ref _processed);
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    private async Task ProcessAccount(Db db, int id, decimal minValue)
    {
        var address = db.Get("evm", "_addresses", where: $"id = {id}");
        if (string.IsNullOrEmpty(address)) return;

        var proxy = db.GetRandom("proxy", "_instance");
        if (string.IsNullOrEmpty(proxy)) return;

        try
        {
            using var client = new DeBankClient(proxy);
            var tokens = await client.GetTokens(address);

            if (tokens.Count == 0) { Console.WriteLine($"[treasury] {id} no tokens"); return; }

            var byChain = tokens
                .Where(t => t.ValueUsd >= minValue)
                .GroupBy(t => t.Chain)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (chain, chainTokens) in byChain)
            {
                db.AddColumn(chain, "_treasury");
                var json = JsonSerializer.Serialize(chainTokens);
                db.Upd($"{chain} = '{json.Replace("'", "''")}'", "_treasury", where: $"id = {id}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[treasury] {id} ERR {ex.GetType().Name}: {ex.Message}");
        }
    }
}