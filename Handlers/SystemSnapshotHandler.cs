// SystemSnapshotHandler.cs
//
// Endpoints:
//   POST   /system-snapshot/capture        → native C# collection, returns { raw }
//   POST   /system-snapshot/ai-audit       { model, raw } → { analysis, model, ts }
//   GET    /system-snapshot/ai-cache       → { entry } | { entry: null }
//   DELETE /system-snapshot/ai-cache       → { ok }
//   GET    /system-snapshot/process?pid=N  → { pid, name, path, started, memMb, cmdLine, sysInformer } | { error }
//   POST   /system-snapshot/kill           { pid, name } → { ok } | { error }
//   POST   /system-snapshot/reveal         { pid, name } → { ok } | { error }   (Explorer, exe selected)
//   POST   /system-snapshot/sysinformer    { pid, name } → { ok } | { error }   (System Informer, process selected)

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace z3nDash;

internal sealed class SystemSnapshotHandler
{
    private readonly DbConnectionService _dbService;
    private readonly AiClient _aiClient;

    private static string AiCacheTable  => DbSchema.SystemSnapshotAiCache.Name;
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
        if (method == "POST"   && path == "/system-snapshot/ai-audit")  { await AiAudit(ctx);       return; }
        if (method == "GET"    && path == "/system-snapshot/ai-cache")  { await AiCacheGet(ctx);    return; }
        if (method == "DELETE" && path == "/system-snapshot/ai-cache")  { await AiCacheDelete(ctx); return; }
        if (method == "GET"    && path == "/system-snapshot/process")   { await ProcessInfo(ctx);   return; }
        if (method == "POST"   && path == "/system-snapshot/kill")      { await ProcessKill(ctx);   return; }
        if (method == "POST"   && path == "/system-snapshot/reveal")    { await ProcessReveal(ctx); return; }
        if (method == "POST"   && path == "/system-snapshot/sysinformer") { await OpenSystemInformer(ctx); return; }

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
        // Table row: every column but the last is padded to its width and columns are joined
        // with two spaces, so a value longer than its width never merges with the next one.
        // system.html splits rows on 2+ spaces and relies on this.
        void Row(params (object? V, int W)[] cols) =>
            Line(string.Join("  ", cols.Select((c, i) =>
                i == cols.Length - 1 ? $"{c.V}" : $"{c.V}".PadRight(c.W))).TrimEnd());

        var allProcs = Process.GetProcesses();

        var pidName = new Dictionary<int, string>(allProcs.Length);
        foreach (var p in allProcs)
            pidName[p.Id] = p.ProcessName;
        string ProcName(int pid) => pidName.TryGetValue(pid, out var pn) ? pn : "?";
        static string Ep(string addr, int port) => addr.Contains(':') ? $"[{addr}]:{port}" : $"{addr}:{port}";

        var tcpRows   = PlatformSnapshot.GetTcpRowsWithPid();
        var udpRows   = PlatformSnapshot.GetUdpRowsWithPid();
        var connByPid = new Dictionary<int, int>();
        foreach (var r in tcpRows) { connByPid.TryGetValue(r.Pid, out var c); connByPid[r.Pid] = c + 1; }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Line("SYSTEM SNAPSHOT FOR LLM ANALYSIS");
        Line($"Captured : {ts}");
        Line($"Hostname : {Environment.MachineName}");
        Line($"OS       : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Line($"Uptime   : {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m");

        Section("SYSTEM MEMORY SUMMARY");
        var mem = PlatformSnapshot.GetMemoryInfo();
        Line(FormattableString.Invariant($"Total    : {mem.TotalGb} GB"));
        Line(FormattableString.Invariant($"Used     : {mem.UsedGb} GB  ({mem.UsedPct}%)"));
        Line(FormattableString.Invariant($"Free     : {mem.FreeGb} GB"));
        if (mem.Commit is { } commit)
        {
            Line($"Commit used bytes : {commit.UsedBytes}");
            Line($"Commit limit bytes : {commit.LimitBytes}");
            Line($"Commit peak bytes : {commit.PeakBytes}");
            Line($"Paged pool bytes : {commit.PagedPoolBytes}");
            Line($"Nonpaged pool bytes : {commit.NonpagedPoolBytes}");
        }

        Section("CPU SUMMARY");
        Line($"Logical CPUs : {Environment.ProcessorCount}");
        var cpuLoad = PlatformSnapshot.GetCpuLoad();
        Line($"Load         : {cpuLoad}");

        var processGroups = allProcs.GroupBy(p => p.ProcessName)
            .Select(g => {
                long mem2 = 0, privateBytes = 0;
                int unreadable = 0;
                foreach (var p in g)
                {
                    try { mem2 += p.WorkingSet64; } catch { }
                    try { privateBytes += p.PrivateMemorySize64; } catch { unreadable++; }
                }
                var totalMB = Math.Round(mem2 / 1048576.0, 1);
                var tcp     = g.Sum(p => connByPid.TryGetValue(p.Id, out var c) ? c : 0);
                return (Name: g.Key, TotalMB: totalMB, PrivateMB: Math.Round(privateBytes / 1048576.0, 1),
                    Count: g.Count(), Tcp: tcp, AvgMB: Math.Round(totalMB / g.Count(), 1), Unreadable: unreadable);
            })
            .ToArray();

        Section("MEMORY ALLOCATION");
        Row(("NAME", 35), ("PRIVATE_MB", 12), ("RAM_MB", 12), ("INSTANCES", 10), ("UNREADABLE", 0));
        Line(new string('-', 90));
        foreach (var g in processGroups.OrderByDescending(x => x.PrivateMB))
            Row((g.Name, 35), (g.PrivateMB.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), 12),
                (g.TotalMB.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), 12),
                (g.Count, 10), (g.Unreadable, 0));

        Section("PROCESS AGGREGATION BY NAME (ALL INSTANCES SUMMED)");
        Row(("NAME", 35), ("TOTAL_MEM_MB", 12), ("INSTANCES", 10), ("TCP_CONNS", 12), ("AVG_MEM_MB", 0));
        Line(new string('-', 80));
        foreach (var g in processGroups.OrderByDescending(x => x.TotalMB))
        {
            Row((g.Name, 35), (g.TotalMB, 12), (g.Count, 10), (g.Tcp, 12), (g.AvgMB, 0));
        }

        Section("ALL PROCESSES (PID | NAME | MEM_MB | CPU_SEC | THREADS | START_TIME)");
        Row(("PID", 8), ("NAME", 35), ("MEM_MB", 10), ("CPU_SEC", 12), ("THREADS", 8), ("STARTED", 0));
        Line(new string('-', 90));

        foreach (var p in allProcs.OrderBy(p => p.ProcessName))
        {
            var mem2 = 0.0; var cpu = 0.0; var thr = 0; var started = "n/a";
            try { mem2    = Math.Round(p.WorkingSet64 / 1048576.0, 1); }             catch { }
            try { cpu     = Math.Round(p.TotalProcessorTime.TotalSeconds, 1); }      catch { }
            try { thr     = p.Threads.Count; }                                        catch { }
            try { started = p.StartTime.ToString("HH:mm:ss"); }                       catch { }
            Row((p.Id, 8), (p.ProcessName, 35), (mem2, 10), (cpu, 12), (thr, 8), (started, 0));
        }

        Section("ACTIVE NETWORK CONNECTIONS (TCP + UDP)");
        Row(("PID", 8), ("PROTO", 5), ("LOCAL", 26), ("REMOTE", 26), ("STATE", 12), ("PROCESS", 0));
        Line(new string('-', 100));

        foreach (var r in tcpRows.Concat(udpRows).OrderBy(r => r.LocalPort))
        {
            var remote = r.RemotePort > 0 ? Ep(r.RemoteAddr, r.RemotePort) : "-";
            Row((r.Pid, 8), (r.Proto, 5), (Ep(r.LocalAddr, r.LocalPort), 26), (remote, 26), (r.State, 12), (ProcName(r.Pid), 0));
        }

        Section("LISTENING PORTS SUMMARY (TCP + UDP)");
        Row(("PID", 8), ("PROTO", 5), ("PORT", 6), ("BIND_ADDR", 20), ("PROCESS", 0));
        Line(new string('-', 60));
        foreach (var r in tcpRows.Where(r => r.State == "Listen").Concat(udpRows).OrderBy(r => r.LocalPort))
            Row((r.Pid, 8), (r.Proto, 5), (r.LocalPort, 6), (r.LocalAddr, 20), (ProcName(r.Pid), 0));

        Section("ESTABLISHED TCP CONNECTIONS");
        Row(("PID", 8), ("LOCAL", 26), ("REMOTE", 26), ("PROCESS", 0));
        Line(new string('-', 80));
        foreach (var r in tcpRows.Where(r => r.State == "Established").OrderBy(r => r.Pid))
            Row((r.Pid, 8), (Ep(r.LocalAddr, r.LocalPort), 26), (Ep(r.RemoteAddr, r.RemotePort), 26), (ProcName(r.Pid), 0));

        Section("RUNNING SERVICES");
        Row(("NAME", 50), ("STATUS", 12), ("DISPLAY", 0));
        Line(new string('-', 100));
        foreach (var svc in PlatformSnapshot.GetRunningServices())
            Row((svc.Name, 50), ("Running", 12), (svc.Display, 0));

        Section("DISK USAGE");
        Row(("DRIVE", 6), ("TOTAL_GB", 12), ("USED_GB", 12), ("FREE_GB", 12), ("PCT_USED", 0));
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
                Row((drive.Name.TrimEnd('\\', '/'), 6), (total, 12), (used, 12), (free, 12), ($"{(total > 0 ? Math.Round(used / total * 100, 1) : 0)}%", 0));
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
        Row(("IDX", 5), ("PATH", 0));
        Line(new string('-', 80));
        // Windows использует ';', Linux использует ':'
        var pathSep = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? ';' : ':';
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(pathSep, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pathEntries.Length; i++)
            Row((i + 1, 5), (pathEntries[i], 0));

        Line(""); Line(hr); Line("END OF SNAPSHOT"); Line(hr);

        return sb.ToString();
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
            "1) Memory pressure — distinguish physical RAM from system commit used/limit/headroom. " +
            "Commit exhaustion can cause allocation failures even with available RAM. Compare process groups by private allocation and RAM; " +
            "private process totals do not account for all system commit, and UNREADABLE marks incomplete measurements. " +
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

    // ── Process actions ───────────────────────────────────────────────────────

    private async Task ProcessInfo(HttpListenerContext ctx)
    {
        if (!int.TryParse(ctx.Request.QueryString["pid"], out var pid))
        { ctx.Response.StatusCode = 400; await HttpHelpers.WriteJson(ctx.Response, new { error = "pid required" }); return; }

        try
        {
            using var p = Process.GetProcessById(pid);
            string? exe = null, started = null; double? memMb = null;
            try { exe     = p.MainModule?.FileName; }                          catch { }
            try { started = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }     catch { }
            try { memMb   = Math.Round(p.WorkingSet64 / 1048576.0, 1); }       catch { }
            var cmdLine = GetCommandLine(pid);
            await HttpHelpers.WriteJson(ctx.Response, new { pid, name = p.ProcessName, path = exe, started, memMb, cmdLine,
                                                            sysInformer = FindSystemInformer() != null });
        }
        catch (Exception ex)
        {
            await HttpHelpers.WriteJson(ctx.Response, new { error = $"step=process-info | {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // null when WMI returns nothing (access denied for elevated/protected processes, or the process is gone).
    private static string? GetCommandLine(int pid)
    {
#if WINDOWS
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject item in searcher.Get())
                using (item) return item["CommandLine"]?.ToString();
        }
        catch { }
        return null;
#else
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); }
        catch { return null; }
#endif
    }

    // The snapshot is a few seconds old and PIDs get reused, so the caller passes the name it saw
    // and the action is refused when the live process under that PID has a different name.
    private static async Task<(Process? proc, string? error)> ResolveProcess(HttpListenerContext ctx, string step)
    {
        int pid; string name;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            pid  = json.GetProperty("pid").GetInt32();
            name = json.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        }
        catch (Exception ex) { return (null, $"step={step} | invalid body: {ex.GetType().Name}: {ex.Message}"); }

        if (pid <= 4 || pid == Environment.ProcessId)
            return (null, $"step={step} | refused: pid {pid} is a system process or z3nDash itself");

        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (Exception ex) { return (null, $"step={step} | {ex.GetType().Name}: {ex.Message}"); }

        if (name != "" && !string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
        {
            var live = p.ProcessName; p.Dispose();
            return (null, $"step={step} | refused: pid {pid} is now '{live}', not '{name}'");
        }
        return (p, null);
    }

    private async Task ProcessKill(HttpListenerContext ctx)
    {
        var (p, err) = await ResolveProcess(ctx, "kill");
        if (p == null) { await HttpHelpers.WriteJson(ctx.Response, new { error = err }); return; }
        using (p)
        {
            try
            {
                p.Kill();
                var exited = p.WaitForExit(5000);
                await HttpHelpers.WriteJson(ctx.Response, exited
                    ? new { ok = true, error = (string?)null }
                    : new { ok = false, error = (string?)$"step=kill | process {p.Id} still running after 5s" });
            }
            catch (Exception ex)
            {
                await HttpHelpers.WriteJson(ctx.Response, new { error = $"step=kill | {ex.GetType().Name}: {ex.Message}" });
            }
        }
    }

    private async Task ProcessReveal(HttpListenerContext ctx)
    {
        var (p, err) = await ResolveProcess(ctx, "reveal");
        if (p == null) { await HttpHelpers.WriteJson(ctx.Response, new { error = err }); return; }
        using (p)
        {
            try
            {
                var exe = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                { await HttpHelpers.WriteJson(ctx.Response, new { error = $"step=reveal | no executable path for pid {p.Id}" }); return; }
#if WINDOWS
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true })?.Dispose();
                await HttpHelpers.WriteJson(ctx.Response, new { ok = true, path = exe });
#else
                await HttpHelpers.WriteJson(ctx.Response, new { error = $"step=reveal | not supported on this OS, path: {exe}" });
#endif
            }
            catch (Exception ex)
            {
                await HttpHelpers.WriteJson(ctx.Response, new { error = $"step=reveal | {ex.GetType().Name}: {ex.Message}" });
            }
        }
    }

    // Opens the System Informer properties window of the process, in two steps (source:
    // winsiderss/systeminformer, SystemInformer/main.c + mainwnd.c; checked live on this machine):
    //   1. `SystemInformer.exe -selectpid N` — when an instance is running, the new process sends it
    //      WM_PH_ACTIVATE (selects the row, synchronously) and exits; otherwise it becomes the instance.
    //   2. WM_COMMAND ID_PROCESS_PROPERTIES (10006) to the main window — properties of the selected row.
    // Success = a System Informer window titled "<name>.exe (<pid>)" shows up. An elevated System
    // Informer only lets WM_PH_ACTIVATE through UIPI, so step 2 fails there and the row stays selected.
    private const int SiPropertiesCmd = 10006;

    private async Task OpenSystemInformer(HttpListenerContext ctx)
    {
        var (p, err) = await ResolveProcess(ctx, "sysinformer");
        if (p == null) { await HttpHelpers.WriteJson(ctx.Response, new { error = err }); return; }
        using (p)
        {
            var exe = FindSystemInformer();
            if (exe == null)
            { await HttpHelpers.WriteJson(ctx.Response, new { error = "step=sysinformer | SystemInformer.exe not found (App Paths registry key)" }); return; }
#if WINDOWS
            var pid = p.Id;
            var stage = "sysinformer-selectpid";
            try
            {
                var wasRunning = Process.GetProcessesByName("SystemInformer").Length > 0;
                using (var helper = Process.Start(new ProcessStartInfo(exe, $"-selectpid {pid}") { UseShellExecute = true }))
                {
                    if (wasRunning && helper != null && !await Task.Run(() => helper.WaitForExit(6000)))
                    { await HttpHelpers.WriteJson(ctx.Response, new { error = $"step={stage} | SystemInformer.exe -selectpid did not exit within 6s" }); return; }
                }

                stage = "sysinformer-mainwindow";
                IntPtr main = IntPtr.Zero;
                for (int i = 0; i < 40 && main == IntPtr.Zero; i++)   // a fresh start needs time to build the window
                {
                    main = SiWindows().FirstOrDefault(w => w.Cls == "MainWindowClassName").Hwnd;
                    if (main == IntPtr.Zero) await Task.Delay(250);
                }
                if (main == IntPtr.Zero)
                { await HttpHelpers.WriteJson(ctx.Response, new { error = $"step={stage} | no visible MainWindowClassName window of SystemInformer.exe within 10s" }); return; }
                if (!wasRunning) await Task.Delay(1500);             // fresh start: the process list fills in after the window

                stage = "sysinformer-properties";
                if (!NativeMethods.PostMessage(main, 0x0111 /* WM_COMMAND */, (IntPtr)SiPropertiesCmd, IntPtr.Zero))
                {
                    var code = Marshal.GetLastWin32Error();
                    await HttpHelpers.WriteJson(ctx.Response, new { ok = true, properties = false,
                        error = $"step={stage} | PostMessage WM_COMMAND failed, Win32 error {code}; process is selected in the list" });
                    return;
                }

                var suffix = $"({pid})";
                for (int i = 0; i < 12; i++)
                {
                    if (SiWindows().Any(w => w.Title.EndsWith(suffix)))
                    { await HttpHelpers.WriteJson(ctx.Response, new { ok = true, properties = true }); return; }
                    await Task.Delay(250);
                }
                await HttpHelpers.WriteJson(ctx.Response, new { ok = true, properties = false,
                    error = $"step={stage} | no window titled '*{suffix}' within 3s after WM_COMMAND {SiPropertiesCmd}" });
            }
            catch (Exception ex)
            {
                await HttpHelpers.WriteJson(ctx.Response, new { error = $"step={stage} | {ex.GetType().Name}: {ex.Message}" });
            }
#else
            await HttpHelpers.WriteJson(ctx.Response, new { error = "step=sysinformer | not supported on this OS" });
#endif
        }
    }

#if WINDOWS
    private static List<(IntPtr Hwnd, string Cls, string Title)> SiWindows()
    {
        var pids = new HashSet<int>();
        foreach (var sp in Process.GetProcessesByName("SystemInformer")) { pids.Add(sp.Id); sp.Dispose(); }
        var list = new List<(IntPtr, string, string)>();
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out var wpid);
            if (!pids.Contains((int)wpid) || !NativeMethods.IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256); NativeMethods.GetClassName(h, cls, cls.Capacity);
            var ttl = new StringBuilder(512); NativeMethods.GetWindowText(h, ttl, ttl.Capacity);
            list.Add((h, cls.ToString(), ttl.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }
#endif

    // The installer registers App Paths\SystemInformer.exe; a portable copy is not found.
    private static string? FindSystemInformer()
    {
#if WINDOWS
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SystemInformer.exe";
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var k = root.OpenSubKey(key);
                if (k?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
            }
            catch { }
        }
#endif
        return null;
    }

    // ── Prompt ────────────────────────────────────────────────────────────────

    private static string BuildAuditPrompt(string raw)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"System audit snapshot. Host: {ExtractField(raw, "Hostname")}, Captured: {ExtractField(raw, "Captured")}, Uptime: {ExtractField(raw, "Uptime")}");
        sb.AppendLine();
        foreach (var s in new[] { "SYSTEM MEMORY SUMMARY", "MEMORY ALLOCATION", "CPU SUMMARY", "PROCESS AGGREGATION BY NAME",
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

    private static void EnsureAiCacheTable(Db db) =>
        db.CreateTable(DbSchema.SystemSnapshotAiCache.Columns, AiCacheTable);

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
    internal record TcpRow(string Proto, int Pid, string LocalAddr, int LocalPort, string RemoteAddr, int RemotePort, string State);
    internal record CommitInfo(ulong UsedBytes, ulong LimitBytes, ulong PeakBytes,
        ulong PagedPoolBytes, ulong NonpagedPoolBytes);
    internal record MemInfo(double TotalGb, double UsedGb, double FreeGb, double UsedPct, CommitInfo? Commit = null);
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

    internal static List<TcpRow> GetUdpRowsWithPid()
    {
#if WINDOWS
        return GetUdpRowsWindows();
#else
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Select(ep => new TcpRow("UDP", 0, ep.Address.ToString(), ep.Port, "", 0, "Listen")).ToList();
        }
        catch { return new List<TcpRow>(); }
#endif
    }

#if WINDOWS
    private const int AfInet  = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;   // TCP_TABLE_OWNER_PID_ALL: listeners + connections
    private const int UdpTableOwnerPid    = 1;   // UDP_TABLE_OWNER_PID

    private static List<TcpRow> GetTcpRowsWindows()
    {
        var rows = new List<TcpRow>();
        // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (24 bytes)
        ReadTable(NativeMethods.GetExtendedTcpTable, AfInet, TcpTableOwnerPidAll, 24, p => rows.Add(new TcpRow("TCP",
            Marshal.ReadInt32(p, 20), Ip4(p, 4), Port(p, 8), Ip4(p, 12), Port(p, 16), TcpStateWin(Marshal.ReadInt32(p, 0)))));
        // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid (56 bytes)
        ReadTable(NativeMethods.GetExtendedTcpTable, AfInet6, TcpTableOwnerPidAll, 56, p => rows.Add(new TcpRow("TCP6",
            Marshal.ReadInt32(p, 52), Ip6(p, 0, 16), Port(p, 20), Ip6(p, 24, 40), Port(p, 44), TcpStateWin(Marshal.ReadInt32(p, 48)))));
        return rows;
    }

    private static List<TcpRow> GetUdpRowsWindows()
    {
        var rows = new List<TcpRow>();
        // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid (12 bytes)
        ReadTable(NativeMethods.GetExtendedUdpTable, AfInet, UdpTableOwnerPid, 12, p => rows.Add(new TcpRow("UDP",
            Marshal.ReadInt32(p, 8), Ip4(p, 0), Port(p, 4), "", 0, "Listen")));
        // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid (28 bytes)
        ReadTable(NativeMethods.GetExtendedUdpTable, AfInet6, UdpTableOwnerPid, 28, p => rows.Add(new TcpRow("UDP6",
            Marshal.ReadInt32(p, 24), Ip6(p, 0, 16), Port(p, 20), "", 0, "Listen")));
        return rows;
    }

    private delegate int TableFn(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

    // Table layout: DWORD dwNumEntries, then rows of rowSz bytes. The table can grow between the
    // size query and the read (ERROR_INSUFFICIENT_BUFFER = 122) — retry with the new size.
    private static void ReadTable(TableFn fn, int af, int tableClass, int rowSz, Action<IntPtr> onRow)
    {
        try
        {
            int size = 0;
            fn(IntPtr.Zero, ref size, false, af, tableClass, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var rc = fn(buf, ref size, false, af, tableClass, 0);
                    if (rc == 122) continue;
                    if (rc != 0) return;
                    int count = Marshal.ReadInt32(buf);
                    for (int i = 0; i < count; i++)
                        onRow(IntPtr.Add(buf, 4 + i * rowSz));
                    return;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { }
    }

    private static string Ip4(IntPtr p, int off) =>
        $"{Marshal.ReadByte(p, off)}.{Marshal.ReadByte(p, off + 1)}.{Marshal.ReadByte(p, off + 2)}.{Marshal.ReadByte(p, off + 3)}";

    private static string Ip6(IntPtr p, int off, int scopeOff)
    {
        var b = new byte[16];
        Marshal.Copy(IntPtr.Add(p, off), b, 0, 16);
        return new IPAddress(b, (uint)Marshal.ReadInt32(p, scopeOff)).ToString();
    }

    // Port is in network byte order in the low 16 bits of the DWORD (first two bytes in memory).
    private static int Port(IntPtr p, int off) => (Marshal.ReadByte(p, off) << 8) | Marshal.ReadByte(p, off + 1);

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

                    rows.Add(new TcpRow(file.EndsWith("6") ? "TCP6" : "TCP", 0, lAddr, lPort, rAddr, rPort, state));
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
        var perf = new NativeMethods.PERFORMANCE_INFORMATION();
        perf.cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>();
        if (NativeMethods.GetPerformanceInfo(ref perf, perf.cb))
        {
            var pageSize = perf.PageSize.ToUInt64();
            ulong Bytes(UIntPtr pages) => pages.ToUInt64() * pageSize;
            var totalGb = Bytes(perf.PhysicalTotal) / 1073741824.0;
            var freeGb = Bytes(perf.PhysicalAvailable) / 1073741824.0;
            var usedGb = totalGb - freeGb;
            return new MemInfo(Math.Round(totalGb, 2), Math.Round(usedGb, 2), Math.Round(freeGb, 2),
                totalGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0,
                new CommitInfo(Bytes(perf.CommitTotal), Bytes(perf.CommitLimit), Bytes(perf.CommitPeak),
                    Bytes(perf.KernelPaged), Bytes(perf.KernelNonpaged)));
        }
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
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak;
        public UIntPtr PhysicalTotal, PhysicalAvailable, SystemCache;
        public UIntPtr KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);

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

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
#endif
