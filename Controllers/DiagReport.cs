using System.Globalization;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Текстовый отчёт по дампу DiagTrace. Только цифры из дампа, сгруппированные
/// по фазам; выводы о причинах отчёт не делает.
/// </summary>
public static class DiagReport
{
    private sealed record Rec(string K, double T, JsonElement E);

    public static string Build(string path)
    {
        var recs = Load(path, out var bad);
        var sb   = new StringBuilder();
        sb.AppendLine($"Diagnostic report: {Path.GetFileName(path)}");
        if (recs.Count == 0) { sb.AppendLine("Dump is empty."); return sb.ToString(); }

        var start = recs.FirstOrDefault(r => r.K == "start");
        sb.AppendLine($"Period: {Clock(recs[0].T)} – {Clock(recs[^1].T)} ({(recs[^1].T - recs[0].T) / 1000:F0} s), records: {recs.Count}, unreadable lines: {bad}");
        if (start != null)
            sb.AppendLine($"DB: {S(start.E, "db")}, cores: {D(start.E, "cores")}, thread pool min/max workers: {D(start.E, "tp_min")}/{D(start.E, "tp_max")}");
        sb.AppendLine();
        sb.AppendLine("Phases of one request:");
        sb.AppendLine("  js      — from the user action to the fetch() call in the page");
        sb.AppendLine("  wait    — fetch() called, but the browser has not sent the request yet (requestStart - startTime)");
        sb.AppendLine("  net     — request sent by the browser → accepted by the server (wall clocks of browser and server)");
        sb.AppendLine("  queue   — accepted → handler started (wait for a thread pool thread)");
        sb.AppendLine("  handler — handler running; db/lock/con are parts of it");
        sb.AppendLine();

        var reqByCid = recs.Where(r => r.K == "req" && S(r.E, "cid") != "")
                           .GroupBy(r => S(r.E, "cid")).ToDictionary(g => g.Key, g => g.First());
        var fetches  = recs.Where(r => r.K == "cf").ToList();

        Actions(sb, recs, fetches, reqByCid);
        Slowest(sb, recs, fetches, reqByCid);
        Paths(sb, recs);
        Pool(sb, recs);
        Locks(sb, recs);
        Database(sb, recs);
        ConsoleWrites(sb, recs);
        Client(sb, recs, fetches);
        return sb.ToString();
    }

    // ── Разбор одного запроса ─────────────────────────────────────────────────

    /// <summary>Полное время и разбивка по фазам. actionT — NaN, если действия нет.</summary>
    private static (double total, string line) Breakdown(Rec? f, Rec? req, double actionT)
    {
        var parts = new List<string>();
        double total = double.NaN;
        string head;

        if (f != null)
        {
            var from = double.IsNaN(actionT) ? f.T : actionT;
            var end  = D(f.E, "re");
            if (double.IsNaN(end)) end = f.T + D(f.E, "dur");
            total = end - from;
            head  = $"{Clock(f.T)} {S(f.E, "m"),-4} {S(f.E, "u")} st={S(f.E, "st")}";
            var fn = S(f.E, "fn");
            if (fn != "") head += $" [{fn}]";
            if (S(f.E, "src") != "") head += $" from {S(f.E, "src")}";
            if (!double.IsNaN(actionT)) parts.Add($"js {N(f.T - actionT)}");
            parts.Add($"wait {N(D(f.E, "stall"))}");
            if (req != null) parts.Add($"net {N(D(req.E, "acc") - D(f.E, "rs"))}");
        }
        else if (req != null)
        {
            total = D(req.E, "q_ms") + D(req.E, "h_ms");
            head  = $"{Clock(D(req.E, "acc"))} {S(req.E, "m"),-4} {S(req.E, "p")} st={S(req.E, "st")} (no browser record)";
        }
        else return (double.NaN, "");

        if (req != null)
        {
            parts.Add($"queue {N(D(req.E, "q_ms"))}");
            var inner = $"db {S(req.E, "db_n")}x={N(D(req.E, "db_ms"))}";
            if (D(req.E, "db_max") >= 50) inner += $" max {N(D(req.E, "db_max"))} {S(req.E, "db_max_q")}";
            if (D(req.E, "lock_n") > 0) inner += $", lock wait {N(D(req.E, "lock_wait"))} hold {N(D(req.E, "lock_hold"))}";
            if (D(req.E, "con_ms") >= 1) inner += $", console {N(D(req.E, "con_ms"))}";
            if (req.E.TryGetProperty("spans", out var sp) && sp.GetArrayLength() > 0)
                inner += ", " + string.Join(" ", sp.EnumerateArray().Select(x => x.GetString()));
            parts.Add($"handler {N(D(req.E, "h_ms"))} ({inner})");
            parts.Add($"pool thr={S(req.E, "tp_thr")} q={S(req.E, "tp_q")} inflight={S(req.E, "inflight")}");
        }
        if (f != null)
        {
            parts.Add($"ttfb {N(D(f.E, "ttfb"))} body {N(D(f.E, "dl"))}");
            parts.Add($"open in page: fetch={S(f.E, "busy")} sse={S(f.E, "sse")}");
            var err = S(f.E, "err");
            if (err != "") parts.Add($"error: {err}");
        }
        return (total, $"{head}\n      total {N(total)} ms = " + string.Join(" | ", parts));
    }

    private static Rec? ServerFor(Rec f, Dictionary<string, Rec> reqByCid)
        => reqByCid.TryGetValue(S(f.E, "cid"), out var r) ? r : null;

    // ── Разделы ───────────────────────────────────────────────────────────────

    private static void Actions(StringBuilder sb, List<Rec> recs, List<Rec> fetches, Dictionary<string, Rec> reqByCid)
    {
        var actions = recs.Where(r => r.K == "ca").ToList();
        sb.AppendLine($"=== User actions ({actions.Count}) and requests started within 3 s after each (setInterval polls excluded) ===");
        foreach (var a in actions.TakeLast(150))
        {
            var mine = fetches.Where(f => D(f.E, "act_t") == a.T).ToList();
            sb.AppendLine($"{Clock(a.T)} {S(a.E, "what")} \"{S(a.E, "label")}\" on {S(a.E, "page")}" + (mine.Count == 0 ? " — no requests" : ""));
            foreach (var f in mine)
                sb.AppendLine("    " + Breakdown(f, ServerFor(f, reqByCid), a.T).line.Replace("\n", "\n    "));
        }
        sb.AppendLine();
    }

    private static void Slowest(StringBuilder sb, List<Rec> recs, List<Rec> fetches, Dictionary<string, Rec> reqByCid)
    {
        var rows = new List<(double total, string line)>();
        var seen = new HashSet<string>();
        foreach (var f in fetches)
        {
            var req = ServerFor(f, reqByCid);
            if (req != null) seen.Add(S(req.E, "cid"));
            rows.Add(Breakdown(f, req, double.NaN));
        }
        foreach (var r in recs.Where(r => r.K == "req" && !B(r.E, "sse") && !seen.Contains(S(r.E, "cid"))))
            rows.Add(Breakdown(null, r, double.NaN));

        sb.AppendLine("=== 30 slowest requests (event streams excluded) ===");
        foreach (var row in rows.Where(r => !double.IsNaN(r.total)).OrderByDescending(r => r.total).Take(30))
            sb.AppendLine("  " + row.line.Replace("\n", "\n  "));
        sb.AppendLine();
    }

    private static void Paths(StringBuilder sb, List<Rec> recs)
    {
        sb.AppendLine("=== Server time per route (handler ms; event streams excluded) ===");
        sb.AppendLine($"  {"route",-44} {"n",6} {"p50",7} {"p95",7} {"max",7} {"queue max",9} {"db avg",7}");
        var groups = recs.Where(r => r.K == "req" && !B(r.E, "sse"))
                         .GroupBy(r => $"{S(r.E, "m")} {S(r.E, "p")}")
                         .OrderByDescending(g => g.Max(r => D(r.E, "h_ms")));
        foreach (var g in groups.Take(40))
        {
            var h = g.Select(r => D(r.E, "h_ms")).OrderBy(x => x).ToList();
            sb.AppendLine($"  {g.Key,-44} {g.Count(),6} {N(Pct(h, .5)),7} {N(Pct(h, .95)),7} {N(h[^1]),7} " +
                          $"{N(g.Max(r => D(r.E, "q_ms"))),9} {N(g.Average(r => D(r.E, "db_ms"))),7}");
        }
        sb.AppendLine();
    }

    private static void Pool(StringBuilder sb, List<Rec> recs)
    {
        var s = recs.Where(r => r.K == "s").ToList();
        sb.AppendLine($"=== Thread pool, sampled every 250 ms ({s.Count} samples) ===");
        if (s.Count == 0) { sb.AppendLine(); return; }

        // Худшая задержка пула в окне: завершившаяся проба или ещё висящая.
        double Worst(Rec r) => Math.Max(Z(D(r.E, "tp_lag_max")), Z(D(r.E, "tp_stuck")));
        var lags = s.Select(Worst).OrderBy(x => x).ToList();
        sb.AppendLine("  tp_lag = how long a work item queued to the pool waited before it ran");
        sb.AppendLine($"  tp_lag p50 {N(Pct(lags, .5))} ms, p95 {N(Pct(lags, .95))} ms, max {N(lags[^1])} ms; " +
                      $"samples over 100 ms: {lags.Count(x => x > 100)}, over 1000 ms: {lags.Count(x => x > 1000)}");
        sb.AppendLine($"  pool threads min/max: {N(s.Min(r => D(r.E, "tp_thr")))}/{N(s.Max(r => D(r.E, "tp_thr")))}, " +
                      $"running task instances min/max: {N(s.Min(r => D(r.E, "running")))}/{N(s.Max(r => D(r.E, "running")))}, " +
                      $"queued items max: {N(s.Max(r => D(r.E, "tp_q")))}, process CPU max: {N(s.Max(r => D(r.E, "cpu")))}%, " +
                      $"GC gen2: {N(s.Sum(r => D(r.E, "gc2")))}, GC pause total: {N(s.Sum(r => D(r.E, "gc_pause")))} ms");
        sb.AppendLine("  worst samples:");
        foreach (var r in s.OrderByDescending(Worst).Take(12).OrderBy(r => r.T))
            sb.AppendLine($"    {Clock(r.T)} lag {N(Worst(r))} thr {S(r.E, "tp_thr")} running {S(r.E, "running")} queued {S(r.E, "tp_q")} done/250ms {S(r.E, "tp_done")} " +
                          $"inflight {S(r.E, "inflight")} (sse {S(r.E, "inflight_sse")}) cpu {S(r.E, "cpu")}% " +
                          $"bg db {S(r.E, "bg_db_n")}x={S(r.E, "bg_db_ms")} console {S(r.E, "con_ms")} gc pause {S(r.E, "gc_pause")}");
        sb.AppendLine();
    }

    private static void Locks(StringBuilder sb, List<Rec> recs)
    {
        var l = recs.Where(r => r.K == "lock").ToList();
        sb.AppendLine($"=== Scheduler lock: waits >= 50 ms or holds >= 200 ms ({l.Count}) ===");
        foreach (var r in l.OrderByDescending(r => Math.Max(D(r.E, "wait"), D(r.E, "hold"))).Take(20).OrderBy(r => r.T))
            sb.AppendLine($"  {Clock(r.T)} {S(r.E, "name")} in {S(r.E, "at")}: wait {N(D(r.E, "wait"))} hold {N(D(r.E, "hold"))}" +
                          (S(r.E, "rid") != "0" ? $" (request #{S(r.E, "rid")})" : " (background)"));
        sb.AppendLine();
    }

    private static void Database(StringBuilder sb, List<Rec> recs)
    {
        var q = recs.Where(r => r.K == "db").ToList();
        sb.AppendLine($"=== DB queries >= 100 ms or retried ({q.Count}) ===");
        foreach (var g in q.GroupBy(r => S(r.E, "q")).OrderByDescending(g => g.Sum(r => D(r.E, "ms"))).Take(20))
            sb.AppendLine($"  {g.Key,-40} n {g.Count(),5}  total {N(g.Sum(r => D(r.E, "ms"))),7}  max {N(g.Max(r => D(r.E, "ms"))),6}  " +
                          $"retried {g.Count(r => D(r.E, "att") > 1)}  in requests {g.Count(r => S(r.E, "rid") != "0")}");
        var s = recs.Where(r => r.K == "s").ToList();
        if (s.Count > 0)
            sb.AppendLine($"  background (not inside an HTTP request): {N(s.Sum(r => D(r.E, "bg_db_n")))} queries, " +
                          $"{N(s.Sum(r => D(r.E, "bg_db_ms")))} ms total, max per 250 ms window {N(s.Max(r => D(r.E, "bg_db_ms")))} ms");
        sb.AppendLine();
    }

    private static void ConsoleWrites(StringBuilder sb, List<Rec> recs)
    {
        var c = recs.Where(r => r.K == "con").ToList();
        var total = recs.Where(r => r.K == "s").Sum(r => Z(D(r.E, "con_ms")));
        sb.AppendLine($"=== Console output: total write time {N(total)} ms, writes >= 50 ms: {c.Count} ===");
        foreach (var r in c.OrderByDescending(r => D(r.E, "ms")).Take(10).OrderBy(r => r.T))
            sb.AppendLine($"  {Clock(r.T)} {N(D(r.E, "ms"))} ms" + (S(r.E, "rid") != "0" ? $" (request #{S(r.E, "rid")})" : ""));
        sb.AppendLine();
    }

    private static void Client(StringBuilder sb, List<Rec> recs, List<Rec> fetches)
    {
        sb.AppendLine("=== Browser ===");
        var stalled = fetches.Where(f => D(f.E, "stall") > 100).ToList();
        sb.AppendLine($"  fetches: {fetches.Count}, waited in the browser > 100 ms before sending: {stalled.Count}" +
                      (stalled.Count > 0 ? $", max {N(stalled.Max(f => D(f.E, "stall")))} ms" : ""));
        if (fetches.Count > 0)
            sb.AppendLine($"  max open at once in one page: fetch {N(fetches.Max(f => D(f.E, "busy")))}, " +
                          $"event streams {N(fetches.Max(f => D(f.E, "sse")))}, both {N(fetches.Max(f => D(f.E, "busy") + D(f.E, "sse")))}");

        var es = recs.Where(r => r.K == "ces").ToList();
        if (es.Count > 0)
        {
            sb.AppendLine("  event streams opened:");
            foreach (var g in es.Where(r => S(r.E, "state") == "open").GroupBy(r => S(r.E, "u")).OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Key}: {g.Count()}");
        }

        var lt = recs.Where(r => r.K == "clt").ToList();
        sb.AppendLine($"  main thread blocked >= 50 ms (long tasks): {lt.Count}, total {N(lt.Sum(r => D(r.E, "ms")))} ms");
        foreach (var r in lt.OrderByDescending(r => D(r.E, "ms")).Take(10).OrderBy(r => r.T))
            sb.AppendLine($"    {Clock(r.T)} {N(D(r.E, "ms"))} ms on {S(r.E, "page")}");

        var ev = recs.Where(r => r.K == "cev").ToList();
        sb.AppendLine($"  slow input events (input → next paint >= 100 ms): {ev.Count}");
        foreach (var r in ev.OrderByDescending(r => D(r.E, "ms")).Take(10).OrderBy(r => r.T))
            sb.AppendLine($"    {Clock(r.T)} {S(r.E, "type")} \"{S(r.E, "label")}\" {N(D(r.E, "ms"))} ms " +
                          $"(input delay {N(D(r.E, "delay"))}, handlers {N(D(r.E, "proc"))})");
        sb.AppendLine();
    }

    private static List<Rec> Load(string path, out int bad)
    {
        bad = 0;
        var list = new List<Rec>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rd = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = rd.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement.Clone();
                list.Add(new Rec(S(e, "k"), D(e, "t"), e));
            }
            catch (JsonException) { bad++; }
        }
        return list.OrderBy(r => r.T).ToList();
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static double D(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    private static bool B(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string S(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static string Clock(double unixMs)
        => double.IsNaN(unixMs) ? "?" : DateTime.UnixEpoch.AddMilliseconds(unixMs).ToLocalTime().ToString("HH:mm:ss.fff");

    private static double Z(double v) => double.IsNaN(v) ? 0 : v;

    private static string N(double v) => double.IsNaN(v) ? "?" : v.ToString("F0", CultureInfo.InvariantCulture);

    private static double Pct(List<double> sorted, double p)
        => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
}
