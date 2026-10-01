using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace z3nDash;

public static partial class DiagTrace
{
    // ── HTTP-запрос ───────────────────────────────────────────────────────────

    /// <summary>Счётчики одного запроса. Пишутся из потока запроса и из Task.Run внутри Db.</summary>
    public sealed class RequestScope
    {
        public long   Id;
        public string Cid = "", Method = "", Path = "";
        public bool   Stream;
        /// <summary>Запрос закрыт. AsyncLocal уезжает и в задачи, запущенные из запроса, — их работу ему не засчитываем.</summary>
        public volatile bool Done;
        public double AcceptT, StartT;
        public long   StartTs;
        public int    TpThreads, TpPending, Inflight;
        public int    DbN, LockN;
        public double DbMs, DbMax, LockWait, LockHold, ConMs;
        public string DbMaxShape = "";
        public readonly object Sync = new();
        public readonly List<string> Spans = new();
    }

    /// <summary>
    /// Начало запроса. acceptT — когда GetContextAsync вернул контекст: разница
    /// с началом здесь — ожидание в пуле потоков перед ProcessRequest.
    /// </summary>
    public static RequestScope? BeginRequest(HttpListenerRequest request, double acceptT)
    {
        if (!_enabled) return null;
        var path = request.Url?.AbsolutePath ?? "";
        if (path.StartsWith("/diag/", StringComparison.OrdinalIgnoreCase)) return null;

        var scope = new RequestScope
        {
            Id        = Interlocked.Increment(ref _requestSeq),
            Cid       = request.Headers["X-Diag-Id"] ?? "",
            Method    = request.HttpMethod,
            Path      = path,
            Stream    = (request.Headers["Accept"] ?? "").Contains("text/event-stream"),
            AcceptT   = acceptT,
            StartT    = Now(),
            StartTs   = Stopwatch.GetTimestamp(),
            TpThreads = ThreadPool.ThreadCount,
            TpPending = (int)Math.Min(int.MaxValue, ThreadPool.PendingWorkItemCount),
        };
        scope.Inflight = _inflight.Count;
        _inflight[scope.Id] = scope;
        _scope.Value = scope;
        return scope;
    }

    public static void EndRequest(RequestScope? scope, int status)
    {
        if (scope == null) return;
        scope.Done = true;
        _inflight.TryRemove(scope.Id, out _);
        _scope.Value = null;
        if (!_enabled) return;

        List<string> spans;
        lock (scope.Sync) spans = scope.Spans.ToList();
        Emit(new
        {
            k = "req", t = Now(), rid = scope.Id, cid = scope.Cid, m = scope.Method, p = scope.Path,
            st = status, sse = scope.Stream,
            acc = Round(scope.AcceptT), beg = Round(scope.StartT),
            q_ms = Round(scope.StartT - scope.AcceptT),
            h_ms = Round(Ms(scope.StartTs)),
            tp_thr = scope.TpThreads, tp_q = scope.TpPending, inflight = scope.Inflight,
            db_n = scope.DbN, db_ms = Round(scope.DbMs), db_max = Round(scope.DbMax), db_max_q = scope.DbMaxShape,
            lock_n = scope.LockN, lock_wait = Round(scope.LockWait), lock_hold = Round(scope.LockHold),
            con_ms = Round(scope.ConMs),
            spans
        });
    }

    private static RequestScope? Current()
    {
        var s = _scope.Value;
        return s is { Done: false } ? s : null;
    }

    // ── БД ────────────────────────────────────────────────────────────────────

    private static long _bgDbN;
    private static long _bgDbTicks;

    private static readonly Regex QueryShape = new(
        @"^\s*(?:(?<verb>SELECT)\b.*?\bFROM\s+""?(?<t>[\w.]+)|(?<verb>UPDATE)\s+""?(?<t>[\w.]+)|(?<verb>INSERT\s+INTO)\s+""?(?<t>[\w.]+)|(?<verb>DELETE\s+FROM)\s+""?(?<t>[\w.]+)|(?<verb>\w+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Глагол и таблица — без значений: в запросах бывают payload и ключи.</summary>
    public static string Shape(string query)
    {
        var m = QueryShape.Match(query);
        if (!m.Success) return "?";
        var verb = Regex.Replace(m.Groups["verb"].Value.ToUpperInvariant(), @"\s+", " ");
        return m.Groups["t"].Success ? $"{verb} {m.Groups["t"].Value}" : verb;
    }

    private static void OnQuery(string query, TimeSpan elapsed, int attempts)
    {
        if (!_enabled) return;
        var ms    = elapsed.TotalMilliseconds;
        var scope = Current();
        string? shape = null;

        if (scope != null)
        {
            lock (scope.Sync)
            {
                scope.DbN++;
                scope.DbMs += ms;
                if (ms > scope.DbMax) { scope.DbMax = ms; scope.DbMaxShape = shape = Shape(query); }
            }
        }
        else
        {
            Interlocked.Increment(ref _bgDbN);
            Interlocked.Add(ref _bgDbTicks, elapsed.Ticks);
        }

        if (ms >= 100 || attempts > 1)
            Emit(new { k = "db", t = Now(), ms = Round(ms), q = shape ?? Shape(query), att = attempts, rid = scope?.Id ?? 0 });
    }

    // ── Блокировки и участки ──────────────────────────────────────────────────

    /// <summary>
    /// Замена lock(gate) с замером: сколько ждали входа и сколько держали.
    /// Выключенный режим — голые Monitor.Enter/Exit.
    /// </summary>
    public static GateHandle Gate(object gate, string name, [CallerMemberName] string caller = "")
    {
        if (!_enabled)
        {
            Monitor.Enter(gate);
            return new GateHandle(gate, null, null, 0, 0);
        }
        var t0 = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        var t1 = Stopwatch.GetTimestamp();
        return new GateHandle(gate, name, caller, t0, t1);
    }

    public readonly struct GateHandle : IDisposable
    {
        private readonly object  _gate;
        private readonly string? _name, _caller;
        private readonly long    _t0, _t1;

        internal GateHandle(object gate, string? name, string? caller, long t0, long t1)
            => (_gate, _name, _caller, _t0, _t1) = (gate, name, caller, t0, t1);

        public void Dispose()
        {
            var hold = _name != null ? Ms(_t1) : 0;
            Monitor.Exit(_gate);
            if (_name == null || !_enabled) return;

            var wait  = Stopwatch.GetElapsedTime(_t0, _t1).TotalMilliseconds;
            var scope = Current();
            if (scope != null)
                lock (scope.Sync) { scope.LockN++; scope.LockWait += wait; scope.LockHold += hold; }

            if (wait >= 50 || hold >= 200)
                Emit(new { k = "lock", t = Now(), name = _name, at = _caller, wait = Round(wait), hold = Round(hold), rid = scope?.Id ?? 0 });
        }
    }

    /// <summary>Именованный участок кода: попадает в spans запроса и, если долгий, отдельной строкой.</summary>
    public static SpanHandle Span(string name) => _enabled ? new SpanHandle(name, Stopwatch.GetTimestamp()) : default;

    public readonly struct SpanHandle : IDisposable
    {
        private readonly string? _name;
        private readonly long    _t0;

        internal SpanHandle(string name, long t0) => (_name, _t0) = (name, t0);

        public void Dispose()
        {
            if (_name == null || !_enabled) return;
            var ms    = Ms(_t0);
            var scope = Current();
            if (scope != null && ms >= 20)
                lock (scope.Sync) scope.Spans.Add($"{_name}={Round(ms)}");
            if (ms >= 100)
                Emit(new { k = "span", t = Now(), name = _name, ms = Round(ms), rid = scope?.Id ?? 0 });
        }
    }
}
