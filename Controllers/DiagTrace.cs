using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Диагностический режим: куда уходит время между действием в интерфейсе и
/// ответом сервера. Пишет jsonl в logs/diag — по строке на событие:
///   req  — HTTP-запрос: когда принят, когда взят в работу, сколько выполнялся,
///          сколько в нём заняли БД, ожидание блокировок и запись в консоль;
///   s    — замер раз в 250 мс: пул потоков, задержка постановки в пул, GC, CPU,
///          фоновая работа с БД;
///   db / lock / span / con — отдельные медленные операции;
///   c*   — события браузера, их присылает wwwroot/js/diag.js.
/// Пока режим выключен, всё сводится к проверке одного флага.
/// </summary>
public static partial class DiagTrace
{
    private static volatile bool _enabled;
    public static bool Enabled => _enabled;

    public static string? FilePath { get; private set; }

    private static readonly object _switch = new();
    private static BlockingCollection<string>? _queue;
    private static Thread? _writer;
    private static Thread? _sampler;
    private static TextWriter? _originalOut;

    private static readonly AsyncLocal<RequestScope?> _scope = new();
    private static readonly ConcurrentDictionary<long, RequestScope> _inflight = new();
    private static long _requestSeq;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Folder =>
        Path.Combine(string.IsNullOrEmpty(ZpRuntimeOptions.LogsFolder)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : ZpRuntimeOptions.LogsFolder, "diag");

    /// <summary>Unix-время в миллисекундах — та же шкала, что Date.now() в браузере.</summary>
    public static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    private static double Ms(long fromTimestamp) => Stopwatch.GetElapsedTime(fromTimestamp).TotalMilliseconds;

    private static double Round(double v) => Math.Round(v, 1);

    // ── Включение и запись ────────────────────────────────────────────────────

    public static string Start(string dbMode)
    {
        lock (_switch)
        {
            if (_enabled && FilePath != null) return FilePath;

            Directory.CreateDirectory(Folder);
            var path  = Path.Combine(Folder, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            var queue = new BlockingCollection<string>(200_000);
            _queue    = queue;
            FilePath  = path;

            // Свой поток, а не пул: запись дампа не должна зависеть от того,
            // что она как раз и меряет.
            _writer = new Thread(() => WriteLoop(path, queue)) { IsBackground = true, Name = "diag-writer" };
            _writer.Start();

            ResetCounters();
            _enabled = true;

            ThreadPool.GetMinThreads(out var minWorker, out var minIo);
            ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
            Emit(new
            {
                k = "start", t = Now(), db = dbMode, cores = Environment.ProcessorCount,
                tp_min = minWorker, tp_min_io = minIo, tp_max = maxWorker, tp_max_io = maxIo,
                pid = Environment.ProcessId,
                ver = typeof(DiagTrace).Assembly.GetName().Version?.ToString() ?? ""
            });

            Db.QueryObserver = OnQuery;
            HookConsole();

            _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "diag-sampler" };
            _sampler.Start();
            return path;
        }
    }

    public static string? Stop()
    {
        lock (_switch)
        {
            if (!_enabled) return FilePath;
            Emit(new { k = "stop", t = Now() });
            _enabled = false;
            Db.QueryObserver = null;
            UnhookConsole();
            _queue?.CompleteAdding();
            _writer?.Join(3000);
            _sampler?.Join(1000);
            _queue = null;
            _stoppedAt = DateTime.UtcNow;
            return FilePath;
        }
    }

    private static void WriteLoop(string path, BlockingCollection<string> queue)
    {
        try
        {
            using var w = new StreamWriter(path, append: true, new UTF8Encoding(false));
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var line, 500))
                {
                    w.WriteLine(line);
                    if (queue.Count == 0) w.Flush();
                }
                else w.Flush();
            }
            w.Flush();
        }
        catch (Exception ex)
        {
            // Писать некуда, кроме исходной консоли: перехват консоли идёт через Emit.
            try { (_originalOut ?? Console.Out).WriteLine($"[diag] writer stopped: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    private static void Emit(object record)
    {
        var q = _queue;
        if (q == null || q.IsAddingCompleted) return;
        try { q.TryAdd(JsonSerializer.Serialize(record, Json)); } catch { }
    }

    /// <summary>Строки из браузера: каждая — готовый JSON-объект.</summary>
    /// <remarks>
    /// Пачка может прийти уже после Stop: при занятом пуле потоков запрос браузера
    /// ждёт обработки дольше, чем человек ждёт кнопку. Такие строки дописываются
    /// в последний дамп напрямую — писатель к этому моменту файл уже закрыл.
    /// </remarks>
    public static int AppendClient(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
        var lines = doc.RootElement.EnumerateArray()
                       .Where(e => e.ValueKind == JsonValueKind.Object)
                       .Select(e => e.GetRawText()).ToList();

        var q = _queue;
        if (_enabled && q != null)
            return lines.Count(l => { try { return q.TryAdd(l); } catch { return false; } });

        lock (_switch)
        {
            if (_enabled || FilePath == null || DateTime.UtcNow - _stoppedAt > LateWindow) return 0;
            File.AppendAllLines(FilePath, lines, new UTF8Encoding(false));
            return lines.Count;
        }
    }

    private static DateTime _stoppedAt = DateTime.MinValue;
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(10);

    /// <summary>Сколько инстансов задач идёт сейчас; задаёт SchedulerService.</summary>
    public static Func<int>? RunningInstances;
}
