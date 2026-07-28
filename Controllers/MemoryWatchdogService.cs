using System.Diagnostics;
#if WINDOWS
using System.Management;
#endif

namespace DevDeck;

/// <summary>
/// Фоновый сторож памяти ZennoPoster.
/// Периодически суммирует WorkingSet процессов по имени и, если задан лимит
/// (LimitMb > 0) и он превышен, убивает процесс(ы) целиком (entireProcessTree).
/// Архитектурно ZennoPoster всегда один процесс, но GetProcessesByName
/// возвращает список — обрабатываем на всякий случай все совпадения.
/// </summary>
public sealed class MemoryWatchdogService : IDisposable
{
    private readonly Logger? _log;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();

    private WatchdogConfig _cfg;

    // ── последнее срабатывание (для статуса) ──────────────────────────────
    private string _lastKillTs    = "";
    private long   _lastKillMemMb = 0;

    public MemoryWatchdogService(Logger? log = null)
    {
        _log = log;
        _cfg = Config.WatchdogConfig ?? new WatchdogConfig();
        _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        ApplySchedule();
    }

    // ── перечитать конфиг и пересоздать расписание ────────────────────────
    public void Reload()
    {
        lock (_lock)
        {
            _cfg = Config.WatchdogConfig ?? new WatchdogConfig();
            ApplySchedule();
        }
        _log?.Info($"[Watchdog] reloaded enabled={_cfg.Enabled} process={_cfg.ProcessName} limit={_cfg.LimitMb}MB interval={_cfg.IntervalSec}s");
    }

    private void ApplySchedule()
    {
        if (_cfg.Enabled && _cfg.IntervalSec > 0)
        {
            var period = TimeSpan.FromSeconds(_cfg.IntervalSec);
            _timer.Change(period, period);
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    // ── измерение памяти полного дерева процесса(ов) ─────────────────────
    private static (long totalMb, int pid, int count) Measure(string processName)
    {
        var (rootPids, processIds) = FindProcessTree(processName);
        long totalBytes = 0;
        int  count      = 0;

        foreach (var processId in processIds)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId);
                process.Refresh();
                totalBytes += process.WorkingSet64;
                count++;
            }
            catch { }
            finally { try { process?.Dispose(); } catch { } }
        }

        return (totalBytes / 1024 / 1024, rootPids.FirstOrDefault(-1), count);
    }

    private static (List<int> rootPids, HashSet<int> processIds) FindProcessTree(string processName)
    {
        var normalizedName = Path.GetFileNameWithoutExtension(processName);
        var rootPids = new List<int>();
        var processIds = new HashSet<int>();

#if WINDOWS
        try
        {
            var processes = new List<(int id, int parentId, string name)>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");

            foreach (ManagementObject item in searcher.Get())
            {
                var id = Convert.ToInt32((uint)item["ProcessId"]);
                var parentId = Convert.ToInt32((uint)item["ParentProcessId"]);
                var name = Path.GetFileNameWithoutExtension(item["Name"]?.ToString() ?? "");
                processes.Add((id, parentId, name));

                if (name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                    rootPids.Add(id);
            }

            var children = processes
                .GroupBy(process => process.parentId)
                .ToDictionary(group => group.Key, group => group.Select(process => process.id).ToList());

            var pending = new Queue<int>(rootPids);
            while (pending.Count > 0)
            {
                var processId = pending.Dequeue();
                if (!processIds.Add(processId)) continue;
                if (!children.TryGetValue(processId, out var childIds)) continue;

                foreach (var childId in childIds)
                    pending.Enqueue(childId);
            }

            return (rootPids, processIds);
        }
        catch { }
#endif

        try
        {
            foreach (var process in Process.GetProcessesByName(normalizedName))
            {
                using (process)
                {
                    rootPids.Add(process.Id);
                    processIds.Add(process.Id);
                }
            }
        }
        catch { }

        return (rootPids, processIds);
    }

    // ── периодическая проверка ────────────────────────────────────────────
    private void Tick(object? _)
    {
        WatchdogConfig cfg;
        lock (_lock) { cfg = _cfg; }

        if (!cfg.Enabled || cfg.LimitMb <= 0 || string.IsNullOrWhiteSpace(cfg.ProcessName))
            return;

        var (totalMb, _, count) = Measure(cfg.ProcessName);
        if (count == 0) return;

        if (totalMb > cfg.LimitMb)
        {
            _log?.Warn($"[Watchdog] {cfg.ProcessName} memory {totalMb}MB > limit {cfg.LimitMb}MB — killing");
            var killed = KillProcesses(cfg.ProcessName);

            lock (_lock)
            {
                _lastKillTs    = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                _lastKillMemMb = totalMb;
            }

            _log?.Warn($"[Watchdog] killed {killed} process(es) of {cfg.ProcessName}");
        }
    }

    // ── убить все процессы по имени ───────────────────────────────────────
    private int KillProcesses(string processName)
    {
        int killed = 0;

        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch { return 0; }

        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex) { _log?.Error($"[Watchdog] kill failed pid={SafePid(p)}: {ex.Message}"); }
            finally { try { p.Dispose(); } catch { } }
        }

        return killed;
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    // ── статус для дашборда ────────────────────────────────────────────────
    public object GetStatus()
    {
        WatchdogConfig cfg;
        string lastKillTs;
        long   lastKillMemMb;
        lock (_lock)
        {
            cfg           = _cfg;
            lastKillTs    = _lastKillTs;
            lastKillMemMb = _lastKillMemMb;
        }

        var (totalMb, pid, count) = string.IsNullOrWhiteSpace(cfg.ProcessName)
            ? (0L, -1, 0)
            : Measure(cfg.ProcessName);

        return new
        {
            enabled       = cfg.Enabled,
            processName   = cfg.ProcessName,
            limitMb       = cfg.LimitMb,
            intervalSec   = cfg.IntervalSec,
            running       = count > 0,
            currentMemMb  = totalMb,
            pid,
            instances     = count,
            lastKillTs,
            lastKillMemMb,
        };
    }

    public void Dispose() => _timer.Dispose();
}
