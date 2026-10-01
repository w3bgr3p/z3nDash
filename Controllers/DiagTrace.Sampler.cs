using System.Diagnostics;
using System.Text;

namespace z3nDash;

public static partial class DiagTrace
{
    // ── Консоль ───────────────────────────────────────────────────────────────

    private static long _conTicks;

    /// <summary>
    /// Console.WriteLine синхронный: пока одна запись висит, следующие ждут.
    /// Меряем, сколько длится каждая запись в исходный поток консоли.
    /// </summary>
    private static void HookConsole()
    {
        try
        {
            _originalOut = Console.Out;
            Console.SetOut(new TimedWriter(_originalOut));
        }
        catch { _originalOut = null; }
    }

    private static void UnhookConsole()
    {
        try { if (_originalOut != null) Console.SetOut(_originalOut); } catch { }
    }

    private static void NoteConsole(long t0)
    {
        if (!_enabled) return;
        var elapsed = Stopwatch.GetElapsedTime(t0);
        Interlocked.Add(ref _conTicks, elapsed.Ticks);
        var ms    = elapsed.TotalMilliseconds;
        var scope = Current();
        if (scope != null) lock (scope.Sync) scope.ConMs += ms;
        if (ms >= 50) Emit(new { k = "con", t = Now(), ms = Round(ms), rid = scope?.Id ?? 0 });
    }

    private sealed class TimedWriter : TextWriter
    {
        private readonly TextWriter _inner;
        public TimedWriter(TextWriter inner) => _inner = inner;
        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)                         { var t = Stopwatch.GetTimestamp(); _inner.Write(value); NoteConsole(t); }
        public override void Write(string? value)                      { var t = Stopwatch.GetTimestamp(); _inner.Write(value); NoteConsole(t); }
        public override void Write(char[] buffer, int index, int count){ var t = Stopwatch.GetTimestamp(); _inner.Write(buffer, index, count); NoteConsole(t); }
        public override void WriteLine(string? value)                  { var t = Stopwatch.GetTimestamp(); _inner.WriteLine(value); NoteConsole(t); }
        public override void WriteLine()                               { var t = Stopwatch.GetTimestamp(); _inner.WriteLine(); NoteConsole(t); }
        public override void Flush() => _inner.Flush();
    }

    // ── Сэмплер ───────────────────────────────────────────────────────────────

    private static int    _probeOutstanding;
    private static long   _probePostedTs;
    private static double _probeLagMs;
    private static double _probeLagMaxMs;

    private static int RunningCount()
    {
        try { return RunningInstances?.Invoke() ?? -1; } catch { return -1; }
    }

    private static void ResetCounters()
    {
        _bgDbN = 0; _bgDbTicks = 0; _conTicks = 0;
        _probeOutstanding = 0; _probeLagMs = 0; _probeLagMaxMs = 0;
    }

    /// <summary>
    /// Раз в 250 мс. Главная цифра — tp_lag: сколько работа, поставленная в пул
    /// потоков, ждёт исполнения. Обработчик каждого HTTP-запроса тоже идёт через пул.
    /// </summary>
    private static void SampleLoop()
    {
        var proc     = Process.GetCurrentProcess();
        var lastCpu  = proc.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        var lastGc   = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var lastPause    = GC.GetTotalPauseDuration();
        var lastContend  = Monitor.LockContentionCount;
        var lastDone     = ThreadPool.CompletedWorkItemCount;
        long lastBgN = 0, lastBgTicks = 0, lastCon = 0;

        while (_enabled)
        {
            try
            {
                Thread.Sleep(250);
                if (!_enabled) break;

                double stuck = 0;
                if (Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    Volatile.Write(ref _probePostedTs, Stopwatch.GetTimestamp());
                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        var lag = Ms(Volatile.Read(ref _probePostedTs));
                        _probeLagMs = lag;
                        if (lag > _probeLagMaxMs) _probeLagMaxMs = lag;
                        Volatile.Write(ref _probeOutstanding, 0);
                    }, null);
                }
                else stuck = Ms(Volatile.Read(ref _probePostedTs));

                proc.Refresh();
                var cpu  = proc.TotalProcessorTime;
                var wall = Stopwatch.GetTimestamp();
                var cpuPct = (cpu - lastCpu).TotalMilliseconds
                             / Math.Max(1, Stopwatch.GetElapsedTime(lastWall, wall).TotalMilliseconds)
                             / Environment.ProcessorCount * 100;
                lastCpu = cpu; lastWall = wall;

                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                var pause = GC.GetTotalPauseDuration();
                var contend = Monitor.LockContentionCount;
                var done = ThreadPool.CompletedWorkItemCount;
                var bgN = Interlocked.Read(ref _bgDbN);
                var bgTicks = Interlocked.Read(ref _bgDbTicks);
                var con = Interlocked.Read(ref _conTicks);

                var lagMax = _probeLagMaxMs;
                _probeLagMaxMs = 0;

                Emit(new
                {
                    k = "s", t = Now(),
                    tp_thr = ThreadPool.ThreadCount, tp_q = ThreadPool.PendingWorkItemCount,
                    tp_done = done - lastDone,
                    tp_lag = Round(_probeLagMs), tp_lag_max = Round(lagMax), tp_stuck = Round(stuck),
                    inflight = _inflight.Count,
                    running = RunningCount(),
                    inflight_sse = _inflight.Values.Count(r => r.Stream),
                    cpu = Round(cpuPct), ws_mb = proc.WorkingSet64 / 1024 / 1024,
                    gc0 = gc[0] - lastGc[0], gc1 = gc[1] - lastGc[1], gc2 = gc[2] - lastGc[2],
                    gc_pause = Round((pause - lastPause).TotalMilliseconds),
                    lock_cont = contend - lastContend,
                    bg_db_n = bgN - lastBgN, bg_db_ms = Round(TimeSpan.FromTicks(bgTicks - lastBgTicks).TotalMilliseconds),
                    con_ms = Round(TimeSpan.FromTicks(con - lastCon).TotalMilliseconds),
                });

                lastGc = gc; lastPause = pause; lastContend = contend; lastDone = done;
                lastBgN = bgN; lastBgTicks = bgTicks; lastCon = con;
            }
            catch (Exception ex)
            {
                Emit(new { k = "err", t = Now(), at = "sampler", e = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }
    }
}
