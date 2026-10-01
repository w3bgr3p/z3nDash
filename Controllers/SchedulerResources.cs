using System.Diagnostics;
#if WINDOWS
using System.Management;
#endif

namespace z3nDash;

/// <summary>
/// Суммарное потребление памяти задачами, запущенными оркестратором:
/// по каждой задаче и по всем сразу. Считается по дереву процессов каждого
/// инстанса (python и его дочерние — браузеры, прокси-хелперы).
/// </summary>
public sealed partial class SchedulerService
{
    public sealed record TaskResources(int Instances, int Processes, long MemoryMB);

    public sealed record ResourceSnapshot(
        Dictionary<string, TaskResources> Tasks,
        int  Instances,
        int  Processes,
        long MemoryMB,
        int  InProcessRuns,
        long MasterMB);

    private readonly object _resLock = new();
    private ResourceSnapshot? _resCache;
    private DateTime _resCachedAt;

    /// <summary>
    /// Снимок берётся не чаще раза в 3 секунды: WMI-запрос по всем процессам
    /// дорог, а UI опрашивает его из нескольких вкладок.
    /// </summary>
    public ResourceSnapshot GetResources()
    {
        lock (_resLock)
        {
            if (_resCache != null && (DateTime.UtcNow - _resCachedAt).TotalSeconds < 3)
                return _resCache;
            _resCache   = BuildResources();
            _resCachedAt = DateTime.UtcNow;
            return _resCache;
        }
    }

    private ResourceSnapshot BuildResources()
    {
        // instanceKey = "<taskId>:<runId>"; остальной код сервиса режет по
        // id + ":", поэтому id задачи двоеточия не содержит.
        var live = _running
            .Where(kv => !kv.Value.HasExited)
            .Select(kv =>
            {
                var cut = kv.Key.IndexOf(':');
                return (TaskId: cut > 0 ? kv.Key[..cut] : kv.Key, Pid: kv.Value.Pid);
            })
            .ToList();

        var table = SnapshotProcessTable();

        var tasks = new Dictionary<string, (int inst, int procs, long bytes)>();
        var counted  = new HashSet<int>();   // процесс не должен попасть в сумму дважды
        int inProc   = 0;

        foreach (var (taskId, pid) in live)
        {
            tasks.TryGetValue(taskId, out var acc);
            acc.inst++;

            // Прогон без внешнего процесса (xml-шаблон) идёт внутри Master:
            // его память не отделить от памяти самого приложения.
            if (pid <= 0) { inProc++; tasks[taskId] = acc; continue; }

            foreach (var p in WalkTree(pid, table))
            {
                if (!counted.Add(p)) continue;
                if (!table.Memory.TryGetValue(p, out var bytes)) continue;
                acc.procs++;
                acc.bytes += bytes;
            }
            tasks[taskId] = acc;
        }

        var perTask = tasks.ToDictionary(
            kv => kv.Key,
            kv => new TaskResources(kv.Value.inst, kv.Value.procs, kv.Value.bytes / 1024 / 1024));

        long master = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            master = me.WorkingSet64 / 1024 / 1024;
        }
        catch { }

        return new ResourceSnapshot(
            perTask,
            live.Count,
            tasks.Values.Sum(t => t.procs),
            tasks.Values.Sum(t => t.bytes) / 1024 / 1024,
            inProc,
            master);
    }

    private sealed record ProcessTable(
        Dictionary<int, List<int>> Children,
        Dictionary<int, long>      Memory);

    private static ProcessTable SnapshotProcessTable()
    {
        var children = new Dictionary<int, List<int>>();
        var memory   = new Dictionary<int, long>();

#if WINDOWS
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, WorkingSetSize FROM Win32_Process");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var id     = Convert.ToInt32((uint)item["ProcessId"]);
                    var parent = Convert.ToInt32((uint)item["ParentProcessId"]);
                    memory[id] = item["WorkingSetSize"] is ulong ws ? (long)ws : 0;
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = new List<int>();
                    list.Add(id);
                }
            }
            return new ProcessTable(children, memory);
        }
        catch { }
#endif

        // Без WMI дерево недоступно: считаем только корневые процессы инстансов.
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { memory[p.Id] = p.WorkingSet64; } catch { }
                }
            }
        }
        catch { }
        return new ProcessTable(children, memory);
    }

    private static IEnumerable<int> WalkTree(int root, ProcessTable table)
    {
        var seen    = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!seen.Add(id)) continue;
            yield return id;
            if (table.Children.TryGetValue(id, out var kids))
                foreach (var k in kids) pending.Enqueue(k);
        }
    }
}
