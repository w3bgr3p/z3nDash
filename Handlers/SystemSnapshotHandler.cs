// SystemSnapshotHandler.cs
//
// Endpoints:
//   POST   /system-snapshot/capture        → native C# collection, returns { raw }
//   POST   /system-snapshot/save           { raw } → { id }
//   POST   /system-snapshot/read-file      { path } → { raw }
//   GET    /system-snapshot/list           → { snapshots: [{id, ts, host}] }
//   GET    /system-snapshot/get?id=N       → { id, ts, host, raw }
//   DELETE /system-snapshot/delete?id=N   → { ok }
//   POST   /system-snapshot/ai-audit       { model, raw } → { analysis, model, ts }
//   GET    /system-snapshot/ai-cache       → { entry } | { entry: null }
//   DELETE /system-snapshot/ai-cache       → { ok }

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace z3nIO;

internal sealed class SystemSnapshotHandler
{
    private readonly DbConnectionService _dbService;
    private readonly AiClient _aiClient;

    private const string SnapshotTable = "_system_snapshots";
    private const string AiCacheTable  = "_system_snapshot_ai_cache";
    private const string Lang          = "russian";

    public SystemSnapshotHandler(DbConnectionService dbService, AiClient aiClient)
    {
        _dbService = dbService;
        _aiClient  = aiClient;
    }

    public bool Matches(string path) => path.StartsWith("/system-snapshot");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "POST"   && path == "/system-snapshot/capture")   { await Capture(ctx);       return; }
        if (method == "POST"   && path == "/system-snapshot/save")      { await Save(ctx);          return; }
        if (method == "POST"   && path == "/system-snapshot/read-file") { await ReadFile(ctx);      return; }
        if (method == "GET"    && path == "/system-snapshot/list")      { await List(ctx);          return; }
        if (method == "GET"    && path == "/system-snapshot/get")       { await Get(ctx);           return; }
        if (method == "DELETE" && path == "/system-snapshot/delete")    { await Delete(ctx);        return; }
        if (method == "POST"   && path == "/system-snapshot/ai-audit")  { await AiAudit(ctx);       return; }
        if (method == "GET"    && path == "/system-snapshot/ai-cache")  { await AiCacheGet(ctx);    return; }
        if (method == "DELETE" && path == "/system-snapshot/ai-cache")  { await AiCacheDelete(ctx); return; }

        ctx.Response.StatusCode = 404;
        await HttpHelpers.WriteJson(ctx.Response, new { error = "Not found" });
    }

    // ── POST /system-snapshot/capture ─────────────────────────────────────────

    private async Task Capture(HttpListenerContext ctx)
    {
        try
        {
            var raw = await Task.Run(CollectSnapshot);
            await HttpHelpers.WriteJson(ctx.Response, new { raw });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── Snapshot collector ────────────────────────────────────────────────────

    private static string CollectSnapshot()
    {
        var sb = new StringBuilder(1 << 20);
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var hr = new string('=', 72);

        void Line(string s)    => sb.AppendLine(s);
        void Section(string t) { Line(""); Line(hr); Line($"## {t}"); Line(hr); }

        var allProcs = Process.GetProcesses();

        var pidName = new Dictionary<int, string>(allProcs.Length);
        foreach (var p in allProcs)
            pidName[p.Id] = p.ProcessName;

        var tcpRows   = PlatformSnapshot.GetTcpRowsWithPid();
        var connByPid = new Dictionary<int, int>();
        foreach (var r in tcpRows) { connByPid.TryGetValue(r.Pid, out var c); connByPid[r.Pid] = c + 1; }

        System.Net.IPEndPoint[] udpListeners;
        try   { udpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners(); }
        catch { udpListeners = Array.Empty<System.Net.IPEndPoint>(); }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Line("SYSTEM SNAPSHOT FOR LLM ANALYSIS");
        Line($"Captured : {ts}");
        Line($"Hostname : {Environment.MachineName}");
        Line($"OS       : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Line($"Uptime   : {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m");

        Section("SYSTEM MEMORY SUMMARY");
        var mem = PlatformSnapshot.GetMemoryInfo();
        Line($"Total    : {mem.TotalGb} GB");
        Line($"Used     : {mem.UsedGb} GB  ({mem.UsedPct}%)");
        Line($"Free     : {mem.FreeGb} GB");

        Section("CPU SUMMARY");
        Line($"Logical CPUs : {Environment.ProcessorCount}");
        var cpuLoad = PlatformSnapshot.GetCpuLoad();
        Line($"Load         : {cpuLoad}");

        Section("PROCESS AGGREGATION BY NAME (ALL INSTANCES SUMMED)");
        Line($"{"NAME",-35} {"TOTAL_MEM_MB",-12} {"INSTANCES",-10} {"TCP_CONNS",-12} AVG_MEM_MB");
        Line(new string('-', 80));

        foreach (var g in allProcs.GroupBy(p => p.ProcessName)
            .Select(g => {
                long mem2 = 0;
                foreach (var p in g) try { mem2 += p.WorkingSet64; } catch { }
                var totalMB = Math.Round(mem2 / 1048576.0, 1);
                var tcp     = g.Sum(p => connByPid.TryGetValue(p.Id, out var c) ? c : 0);
                return (Name: g.Key, TotalMB: totalMB, Count: g.Count(), Tcp: tcp, AvgMB: Math.Round(totalMB / g.Count(), 1));
            })
            .OrderByDescending(x => x.TotalMB))
        {
            var name = g.Name.Length > 35 ? g.Name[..35] : g.Name;
            Line($"{name,-35} {g.TotalMB,-12} {g.Count,-10} {g.Tcp,-12} {g.AvgMB}");
        }

        Section("ALL PROCESSES (PID | NAME | MEM_MB | CPU_SEC | THREADS | START_TIME)");
        Line($"{"PID",-8} {"NAME",-35} {"MEM_MB",-10} {"CPU_SEC",-12} {"THREADS",-8} STARTED");
        Line(new string('-', 90));

        foreach (var p in allProcs.OrderBy(p => p.ProcessName))
        {
            var mem2 = 0.0; var cpu = 0.0; var thr = 0; var started = "n/a";
            try { mem2    = Math.Round(p.WorkingSet64 / 1048576.0, 1); }             catch { }
            try { cpu     = Math.Round(p.TotalProcessorTime.TotalSeconds, 1); }      catch { }
            try { thr     = p.Threads.Count; }                                        catch { }
            try { started = p.StartTime.ToString("HH:mm:ss"); }                       catch { }
            var name = p.ProcessName.Length > 35 ? p.ProcessName[..35] : p.ProcessName;
            Line($"{p.Id,-8} {name,-35} {mem2,-10} {cpu,-12} {thr,-8} {started}");
        }

        Section("ACTIVE NETWORK CONNECTIONS (TCP + UDP)");
        Line($"{"PID",-10} {"PROTO",-7} {"LOCAL",-26} {"REMOTE",-26} {"STATE",-14} PROCESS");
        Line(new string('-', 100));

        foreach (var r in tcpRows.OrderBy(r => r.LocalPort))
        {
            var proc   = pidName.TryGetValue(r.Pid, out var pn) ? pn : "?";
            var local  = $"{r.LocalAddr}:{r.LocalPort}";
            var remote = r.RemotePort > 0 ? $"{r.RemoteAddr}:{r.RemotePort}" : "-";
            Line($"{r.Pid,-10} {"TCP",-7} {local,-26} {remote,-26} {r.State,-14} {proc}");
        }
        foreach (var ep in udpListeners.OrderBy(e => e.Port))
            Line($"{"?",-10} {"UDP",-7} {ep.Address}:{ep.Port,-20} {"-",-26} {"LISTEN",-14} ?");

        Section("LISTENING PORTS SUMMARY (TCP)");
        Line($"{"PID",-8} {"PORT",-8} {"BIND_ADDR",-20} PROCESS");
        Line(new string('-', 55));
        foreach (var r in tcpRows.Where(r => r.State == "Listen").OrderBy(r => r.LocalPort))
            Line($"{r.Pid,-8} {r.LocalPort,-8} {r.LocalAddr,-20} {(pidName.TryGetValue(r.Pid, out var pn) ? pn : "?")}");

        Section("ESTABLISHED TCP CONNECTIONS");
        Line($"{"PID",-10} {"LOCAL",-26} {"REMOTE",-26} PROCESS");
        Line(new string('-', 80));
        foreach (var r in tcpRows.Where(r => r.State == "Established").OrderBy(r => r.Pid))
            Line($"{r.Pid,-10} {r.LocalAddr}:{r.LocalPort,-20} {r.RemoteAddr}:{r.RemotePort,-20} {(pidName.TryGetValue(r.Pid, out var pn) ? pn : "?")}");

        Section("RUNNING SERVICES");
        Line($"{"NAME",-50} {"STATUS",-12} DISPLAY");
        Line(new string('-', 100));
        foreach (var svc in PlatformSnapshot.GetRunningServices())
            Line($"{svc.Name,-50} {"Running",-12} {svc.Display}");

        Section("DISK USAGE");
        Line($"{"DRIVE",-6} {"TOTAL_GB",-12} {"USED_GB",-12} {"FREE_GB",-12} PCT_USED");
        Line(new string('-', 55));
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Network) continue;
                if (!drive.IsReady) continue;
                var total = Math.Round(drive.TotalSize      / 1073741824.0, 1);
                var free  = Math.Round(drive.TotalFreeSpace / 1073741824.0, 1);
                var used  = Math.Round(total - free, 1);
                Line($"{drive.Name.TrimEnd('\\', '/'),-6} {total,-12} {used,-12} {free,-12} {(total > 0 ? Math.Round(used / total * 100, 1) : 0)}%");
            }
            catch { }
        }

        Section("ENVIRONMENT MARKERS");
        Line($"USERNAME    : {Environment.UserName}");
        Line($"USERPROFILE : {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        Line($"TEMP        : {Path.GetTempPath()}");
        Line($"CLR ver     : {Environment.Version}");
        Line($"OS 64-bit   : {Environment.Is64BitOperatingSystem}");

        Section("PATH ENTRIES");
        Line($"{"IDX",-5} PATH");
        Line(new string('-', 80));
        // Windows использует ';', Linux использует ':'
        var pathSep = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? ';' : ':';
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(pathSep, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pathEntries.Length; i++)
            Line($"{i + 1,-5} {pathEntries[i]}");
        Line($"\nTotal entries: {pathEntries.Length}");

        Line(""); Line(hr); Line("END OF SNAPSHOT"); Line(hr);

        return sb.ToString();
    }

    // ── POST /system-snapshot/save ────────────────────────────────────────────

    private async Task Save(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }

        string raw;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            raw = json.TryGetProperty("raw", out var rp) ? rp.GetString() ?? "" : "";
        }
        catch { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" }); return; }

        if (string.IsNullOrWhiteSpace(raw)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "empty raw" }); return; }

        EnsureSnapshotTable(db!);
        var ts   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var host = ExtractField(raw, "Hostname");
        db!.Query($"INSERT INTO \"{SnapshotTable}\" (\"ts\", \"host\", \"raw\") VALUES ('{Esc(ts)}', '{Esc(host)}', '{Esc(raw)}')");
        db.Query($"DELETE FROM \"{SnapshotTable}\" WHERE id NOT IN (SELECT id FROM \"{SnapshotTable}\" ORDER BY id DESC LIMIT 20)");

        var idRow = db.GetLines("id", tableName: SnapshotTable, where: $"ts = '{Esc(ts)}'").FirstOrDefault() ?? "0";
        _ = int.TryParse(idRow.Trim(), out var newId);
        await HttpHelpers.WriteJson(ctx.Response, new { id = newId });
    }

    // ── POST /system-snapshot/read-file ───────────────────────────────────────

    private async Task ReadFile(HttpListenerContext ctx)
    {
        string path;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            path = json.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
        }
        catch { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" }); return; }

        if (string.IsNullOrWhiteSpace(path)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "path required" }); return; }
        if (!File.Exists(path))              { ctx.Response.StatusCode = 404; await HttpHelpers.WriteJson(ctx.Response, new { error = $"file not found: {path}" }); return; }

        try { await HttpHelpers.WriteJson(ctx.Response, new { raw = await File.ReadAllTextAsync(path, Encoding.UTF8) }); }
        catch (Exception ex) { ctx.Response.StatusCode = 500; await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message }); }
    }

    // ── GET /system-snapshot/list ─────────────────────────────────────────────

    private async Task List(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        EnsureSnapshotTable(db!);

        var snapshots = db!.GetLines("id, ts, host", tableName: SnapshotTable, where: "1=1")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => { var c = l.Split('|');
                return new { id = int.TryParse(c.ElementAtOrDefault(0)?.Trim(), out var i) ? i : 0,
                             ts = c.ElementAtOrDefault(1)?.Trim() ?? "", host = c.ElementAtOrDefault(2)?.Trim() ?? "" }; })
            .Where(s => s.id > 0).OrderByDescending(s => s.id).ToList();

        await HttpHelpers.WriteJson(ctx.Response, new { snapshots });
    }

    // ── GET /system-snapshot/get?id=N ─────────────────────────────────────────

    private async Task Get(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        if (!int.TryParse(ctx.Request.QueryString["id"], out var id)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "id required" }); return; }

        EnsureSnapshotTable(db!);
        var line = db!.GetLines("id, ts, host, raw", tableName: SnapshotTable, where: $"id = {id}")
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (line == null) { ctx.Response.StatusCode = 404; await HttpHelpers.WriteJson(ctx.Response, new { error = "not found" }); return; }

        var i1 = line.IndexOf('|'); var i2 = i1 >= 0 ? line.IndexOf('|', i1+1) : -1; var i3 = i2 >= 0 ? line.IndexOf('|', i2+1) : -1;
        await HttpHelpers.WriteJson(ctx.Response, new {
            id   = i1 > 0 ? int.TryParse(line[..i1].Trim(), out var si) ? si : 0 : 0,
            ts   = i1 >= 0 && i2 > i1 ? line[(i1+1)..i2].Trim() : "",
            host = i2 >= 0 && i3 > i2 ? line[(i2+1)..i3].Trim() : "",
            raw  = i3 >= 0 ? line[(i3+1)..] : ""
        });
    }

    // ── DELETE /system-snapshot/delete?id=N ───────────────────────────────────

    private async Task Delete(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        if (!int.TryParse(ctx.Request.QueryString["id"], out var id)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "id required" }); return; }
        EnsureSnapshotTable(db!);
        db!.Del(tableName: SnapshotTable, where: $"id = {id}");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── POST /system-snapshot/ai-audit ────────────────────────────────────────

    private async Task AiAudit(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        if (!_aiClient.IsEnabled)             { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "ai disabled" }); return; }

        string model, raw;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            model = json.TryGetProperty("model", out var mp) ? mp.GetString() ?? "" : "";
            raw   = json.TryGetProperty("raw",   out var rp) ? rp.GetString() ?? "" : "";
        }
        catch { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" }); return; }

        if (string.IsNullOrWhiteSpace(raw)) { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "empty raw" }); return; }
        if (string.IsNullOrEmpty(model)) model = "deepseek-ai/DeepSeek-V3.2";

        var systemPrompt =
            "You are a system auditor. Analyze the provided system snapshot and identify: " +
            "1) Memory pressure — which processes or process groups consume the most RAM (total, not just per-instance). " +
            "2) Network anomalies — unusually high connection counts per process, suspicious listening ports, unexpected established connections. " +
            "3) Disk pressure — drives above 80% utilization. " +
            "4) CPU load assessment relative to process count. " +
            "5) Top 3 actionable recommendations based solely on the data. " +
            $"Use exact numbers. State 'none found' for empty sections. Max 1200 words. Language: {Lang}.";

        string result;
        try   { result = await _aiClient.CompleteAsync(model, systemPrompt, BuildAuditPrompt(raw), temp: 0.2, maxTokens: 4000, timeoutSec: 300); }
        catch (Exception ex) { await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message }); return; }

        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        SaveAiCache(db!, model, result, ts);
        await HttpHelpers.WriteJson(ctx.Response, new { analysis = result, model, ts });
    }

    // ── GET /system-snapshot/ai-cache ─────────────────────────────────────────

    private async Task AiCacheGet(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        EnsureAiCacheTable(db!);
        foreach (var line in db!.GetLines("model, ts, report", tableName: AiCacheTable, where: "1=1"))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('|');
            if (cols.Length < 3) continue;
            await HttpHelpers.WriteJson(ctx.Response, new { entry = new { model = cols[0].Trim(), ts = cols[1].Trim(), analysis = cols[2].Trim() } });
            return;
        }
        await HttpHelpers.WriteJson(ctx.Response, new { entry = (object?)null });
    }

    // ── DELETE /system-snapshot/ai-cache ──────────────────────────────────────

    private async Task AiCacheDelete(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db)) { ctx.Response.StatusCode = 503; await HttpHelpers.WriteJson(ctx.Response, new { error = "db" }); return; }
        EnsureAiCacheTable(db!);
        db!.Del(tableName: AiCacheTable, where: "1=1");
        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    // ── Prompt ────────────────────────────────────────────────────────────────

    private static string BuildAuditPrompt(string raw)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"System audit snapshot. Host: {ExtractField(raw, "Hostname")}, Captured: {ExtractField(raw, "Captured")}, Uptime: {ExtractField(raw, "Uptime")}");
        sb.AppendLine();
        foreach (var s in new[] { "SYSTEM MEMORY SUMMARY", "CPU SUMMARY", "PROCESS AGGREGATION BY NAME",
                                   "LISTENING PORTS SUMMARY", "ESTABLISHED TCP CONNECTIONS", "DISK USAGE", "ENVIRONMENT MARKERS" })
            AppendSection(sb, raw, s);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string raw, string title)
    {
        var start = raw.IndexOf($"## {title}", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return;
        var end     = raw.IndexOf("\n## ", start + 4, StringComparison.OrdinalIgnoreCase);
        var section = end > 0 ? raw[start..end] : raw[start..];
        sb.AppendLine(string.Join('\n', section.Split('\n').Take(120)));
        sb.AppendLine();
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private static void EnsureSnapshotTable(Db db) =>
        db.CreateTable(new Dictionary<string, string>
            { ["id"] = "INTEGER PRIMARY KEY", ["ts"] = "TEXT", ["host"] = "TEXT", ["raw"] = "TEXT" }, SnapshotTable);

    private static void EnsureAiCacheTable(Db db) =>
        db.CreateTable(new Dictionary<string, string>
            { ["id"] = "INTEGER PRIMARY KEY", ["model"] = "TEXT", ["ts"] = "TEXT", ["report"] = "TEXT" }, AiCacheTable);

    private static void SaveAiCache(Db db, string model, string analysis, string ts)
    {
        EnsureAiCacheTable(db);
        db.Del(tableName: AiCacheTable, where: "1=1");
        db.Query($"INSERT INTO \"{AiCacheTable}\" (\"model\", \"ts\", \"report\") VALUES ('{Esc(model)}', '{Esc(ts)}', '{Esc(analysis)}')");
    }

    private static string ExtractField(string raw, string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(raw, $@"{label}\s*:\s*(.+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string Esc(string s) => s.Replace("'", "''");
}

// ── Платформенная абстракция ──────────────────────────────────────────────────
//
// Все Windows-specific вызовы изолированы здесь.
// На Linux используются /proc/net/tcp, /proc/meminfo, /proc/stat.

internal static class PlatformSnapshot
{
    internal record TcpRow(int Pid, string LocalAddr, int LocalPort, string RemoteAddr, int RemotePort, string State);
    internal record MemInfo(double TotalGb, double UsedGb, double FreeGb, double UsedPct);
    internal record ServiceInfo(string Name, string Display);

    // ── TCP rows ──────────────────────────────────────────────────────────────

    internal static List<TcpRow> GetTcpRowsWithPid()
    {
#if WINDOWS
        return GetTcpRowsWindows();
#else
        return GetTcpRowsLinux();
#endif
    }

#if WINDOWS
    private static List<TcpRow> GetTcpRowsWindows()
    {
        var rows = new List<TcpRow>();
        try
        {
            int size = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 4, 0);
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buf, ref size, false, 2, 4, 0) != 0) return rows;
                int count       = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                const int rowSz = 24;
                for (int i = 0; i < count; i++)
                {
                    var ptr   = IntPtr.Add(buf, 4 + i * rowSz);
                    var state = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 0);
                    var lAddr = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 4);
                    var lPort = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 8);
                    var rAddr = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 12);
                    var rPort = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 16);
                    var pid   = System.Runtime.InteropServices.Marshal.ReadInt32(ptr, 20);
                    rows.Add(new TcpRow(pid, Ip(lAddr), Ntohs(lPort), Ip(rAddr), Ntohs(rPort), TcpStateWin(state)));
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return rows;
    }

    private static string Ip(int raw)    { var b = BitConverter.GetBytes(raw); return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}"; }
    private static int    Ntohs(int raw) { var b = BitConverter.GetBytes(raw); return (b[2] << 8) | b[3]; }

    private static string TcpStateWin(int s) => s switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived",
        5 => "Established", 6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait",
        9 => "Closing", 10 => "LastAck", 11 => "TimeWait", 12 => "DeleteTcb",
        _ => "Unknown"
    };
#else
    // /proc/net/tcp формат:
    // sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
    // Адреса в little-endian hex, порты в big-endian hex, state hex.
    // PID недоступен напрямую — определяется через /proc/<pid>/net/tcp или /proc/<pid>/fd (требует root).
    // Для не-root возвращаем Pid=0.
    private static List<TcpRow> GetTcpRowsLinux()
    {
        var rows = new List<TcpRow>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    var localHex  = parts[1];
                    var remoteHex = parts[2];
                    var stateHex  = parts[3];

                    var (lAddr, lPort) = ParseHexEndpoint(localHex);
                    var (rAddr, rPort) = ParseHexEndpoint(remoteHex);
                    var state          = TcpStateLinux(Convert.ToInt32(stateHex, 16));

                    rows.Add(new TcpRow(0, lAddr, lPort, rAddr, rPort, state));
                }
            }
            catch { }
        }
        return rows;
    }

    private static (string Addr, int Port) ParseHexEndpoint(string hex)
    {
        // формат: XXXXXXXX:PPPP  (адрес:порт в hex)
        var sep = hex.IndexOf(':');
        if (sep < 0) return ("0.0.0.0", 0);

        var addrHex = hex[..sep];
        var portHex = hex[(sep + 1)..];

        int port = Convert.ToInt32(portHex, 16);

        // IPv4: 8 hex chars, little-endian 32-bit
        if (addrHex.Length == 8)
        {
            var val = Convert.ToUInt32(addrHex, 16);
            var b0  = (val)       & 0xFF;
            var b1  = (val >> 8)  & 0xFF;
            var b2  = (val >> 16) & 0xFF;
            var b3  = (val >> 24) & 0xFF;
            return ($"{b0}.{b1}.{b2}.{b3}", port);
        }

        // IPv6: 32 hex chars — вернуть сокращённо
        return (addrHex, port);
    }

    private static string TcpStateLinux(int s) => s switch
    {
        0x01 => "Established", 0x02 => "SynSent",  0x03 => "SynReceived",
        0x04 => "FinWait1",    0x05 => "FinWait2",  0x06 => "TimeWait",
        0x07 => "Closed",      0x08 => "CloseWait", 0x09 => "LastAck",
        0x0A => "Listen",      0x0B => "Closing",
        _ => "Unknown"
    };
#endif

    // ── Memory ────────────────────────────────────────────────────────────────

    internal static MemInfo GetMemoryInfo()
    {
#if WINDOWS
        var s = new NativeMethods.MEMORYSTATUSEX { dwLength = 64 };
        if (!NativeMethods.GlobalMemoryStatusEx(ref s)) return new MemInfo(0, 0, 0, 0);
        var total = Math.Round(s.ullTotalPhys / 1073741824.0, 2);
        var free  = Math.Round(s.ullAvailPhys / 1073741824.0, 2);
        var used  = Math.Round(total - free, 2);
        return new MemInfo(total, used, free, total > 0 ? Math.Round(used / total * 100, 1) : 0);
#else
        // /proc/meminfo: значения в kB
        try
        {
            var lines  = File.ReadAllLines("/proc/meminfo");
            long total = ParseMemInfoKb(lines, "MemTotal");
            long avail = ParseMemInfoKb(lines, "MemAvailable");
            if (avail == 0) avail = ParseMemInfoKb(lines, "MemFree");
            var totalGb = Math.Round(total / 1048576.0, 2);
            var freeGb  = Math.Round(avail / 1048576.0, 2);
            var usedGb  = Math.Round(totalGb - freeGb, 2);
            return new MemInfo(totalGb, usedGb, freeGb, totalGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0);
        }
        catch { return new MemInfo(0, 0, 0, 0); }
#endif
    }

#if !WINDOWS
    private static long ParseMemInfoKb(string[] lines, string key)
    {
        foreach (var l in lines)
        {
            if (!l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : 0;
        }
        return 0;
    }
#endif

    // ── CPU load ──────────────────────────────────────────────────────────────

    internal static string GetCpuLoad()
    {
#if WINDOWS
        try
        {
            using var c = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            c.NextValue();
            System.Threading.Thread.Sleep(400);
            return $"{Math.Round(c.NextValue(), 1)}%";
        }
        catch { return "n/a"; }
#else
        // /proc/stat: первая строка — суммарное время cpu
        // cpu  user nice system idle iowait irq softirq steal guest guest_nice
        // load = 1 - idle/total, взять две точки с паузой
        try
        {
            static long[] ReadStat()
            {
                var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
                if (line == null) return Array.Empty<long>();
                return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).Select(v => long.TryParse(v, out var x) ? x : 0).ToArray();
            }

            var a = ReadStat();
            System.Threading.Thread.Sleep(400);
            var b = ReadStat();

            if (a.Length < 5 || b.Length < 5) return "n/a";

            var totalA = a.Sum(); var idleA = a[3];
            var totalB = b.Sum(); var idleB = b[3];
            var totalD = totalB - totalA;
            var idleD  = idleB  - idleA;

            if (totalD == 0) return "n/a";
            return $"{Math.Round((1.0 - (double)idleD / totalD) * 100, 1)}%";
        }
        catch { return "n/a"; }
#endif
    }

    // ── Services ──────────────────────────────────────────────────────────────

    internal static List<ServiceInfo> GetRunningServices()
    {
#if WINDOWS
        var result = new List<ServiceInfo>();
        try
        {
            foreach (var s in System.ServiceProcess.ServiceController.GetServices()
                .Where(s => s.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                .OrderBy(s => s.ServiceName))
            {
                var name    = s.ServiceName.Length > 50 ? s.ServiceName[..50] : s.ServiceName;
                var display = s.DisplayName.Length  > 60 ? s.DisplayName[..60]  : s.DisplayName;
                result.Add(new ServiceInfo(name, display));
            }
        }
        catch { }
        return result;
#else
        // systemctl list-units --type=service --state=running --no-pager --no-legend
        var result = new List<ServiceInfo>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl",
                "list-units --type=service --state=running --no-pager --no-legend")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return result;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var name    = parts[0].Length > 50 ? parts[0][..50] : parts[0];
                var display = parts.Length > 1 ? (parts[1].Length > 60 ? parts[1][..60] : parts[1]) : "";
                result.Add(new ServiceInfo(name, display));
            }
        }
        catch { }
        return result;
#endif
    }
}

// ── P/Invoke (Windows only) ───────────────────────────────────────────────────

#if WINDOWS
internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int dwSize, bool sort,
        int ipVersion, int tableClass, int reserved);

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedUdpTable(IntPtr pUdpTable, ref int dwSize, bool sort,
        int ipVersion, int tableClass, int reserved);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
#endif