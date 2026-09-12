using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Журнал HTTP-трафика ZpRuntime. Перенесено из z3n7/Server/ZpTraffic.cs —
/// формат файла и семантика чтения те же, чтобы записи от ноды и от рантайма
/// разбирались одним кодом на фронте.
///
/// Отличие одно: путь берётся из <see cref="ZpRuntimeOptions.LogsFolder"/>, а не
/// из LogOptions проекта — у рантайма нет ZennoPoster'овских настроек лога.
/// Форвардная пагинация по оффсету не перенесена: локальному читателю нужен
/// только хвост.
/// </summary>
public static class ZpTraffic
{
    /// <summary>Длина файла, после которой он уезжает в trafficLog_&lt;дата&gt;.jsonl.</summary>
    private const long RotateBytes = 50L * 1024 * 1024;

    /// <summary>Сколько ротированных файлов держим; остальные удаляются.</summary>
    private const int KeepRotated = 5;

    /// <summary>Размер куска при чтении с конца. Записи длиннее не теряются.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Потолок сканирования в Tail — иначе фильтр без совпадений читает файл целиком.</summary>
    private const long MaxScanBytes = 64L * 1024 * 1024;

    public static string FilePath()
        => Path.GetFullPath(Path.Combine(ZpRuntimeOptions.LogsFolder, "trafficLog.jsonl"));

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Дописывает одну JSONL-строку. Писать могут разные потоки и — при запуске
    /// нескольких копий — разные процессы, поэтому запись под именованным мьютексом.
    /// </summary>
    public static void Append(string json)
    {
        var path = FilePath();
        var mutexName = "Local\\z3nDash.Traffic." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Traffic file is busy: " + path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Rotate(path);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    /// <summary>
    /// Отправляет переросший файл в trafficLog_&lt;дата&gt;.jsonl. Вызывается только
    /// под мьютексом Append. Сбой ротации не имеет права ронять запись трафика —
    /// тогда просто продолжаем писать в текущий файл.
    /// </summary>
    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= RotateBytes) return;

            var target = Path.Combine(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(path));

            // Вторая ротация в ту же секунду — оставляем как есть, дорастёт.
            if (File.Exists(target)) return;

            File.Move(path, target);

            var rotated = Rotated(path);
            for (var i = KeepRotated; i < rotated.Count; i++)
                try { File.Delete(rotated[i]); } catch { }
        }
        catch { }
    }

    /// <summary>Ротированные файлы от новых к старым. Имя содержит дату, поэтому сортировка по имени = по времени.</summary>
    private static List<string> Rotated(string path)
    {
        try
        {
            var files = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "_*" + Path.GetExtension(path));
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(files);
            return [.. files];
        }
        catch { return []; }
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Последние max записей, подходящих под фильтры. Файл читается с конца
    /// кусками, границы строк ищутся назад — поэтому запись любого размера
    /// (тело ответа бывает в мегабайт) приезжает целиком.
    /// </summary>
    public static Page Tail(int max, string? project, string? taskId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        var path = FilePath();
        var page = new Page { file = path };
        if (!File.Exists(path)) return page;

        var found = new List<JsonElement>();   // от новых к старым
        long scanned = 0;

        var files = new List<string> { path };
        files.AddRange(Rotated(path));

        foreach (var file in files)
        {
            if (found.Count >= max || scanned >= MaxScanBytes) break;
            ScanBackwards(file, max, project, taskId, found, ref scanned);
        }

        page.truncated = found.Count < max && scanned >= MaxScanBytes;
        found.Reverse();
        page.entries.AddRange(found);
        return page;
    }

    /// <summary>Один файл с конца. Дополняет found, пока не набрано max или не исчерпан бюджет.</summary>
    private static void ScanBackwards(string file, int max, string? project, string? taskId,
                                      List<JsonElement> found, ref long scanned)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var position = stream.Length;
            var buffer = new byte[ChunkBytes];

            // Начало строки, уехавшее в уже прочитанный (более поздний) кусок.
            var pending = new List<byte>();

            while (position > 0)
            {
                if (found.Count >= max || scanned >= MaxScanBytes) return;

                var size = (int)Math.Min(ChunkBytes, position);
                position -= size;
                stream.Position = position;
                stream.ReadExactly(buffer, 0, size);
                scanned += size;

                var end = size;
                for (var i = size - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    Collect(Line(buffer, i + 1, end - i - 1, pending), project, taskId, found);
                    pending.Clear();
                    end = i;
                    if (found.Count >= max) return;
                }

                if (end > 0)
                {
                    var head = new byte[end];
                    Buffer.BlockCopy(buffer, 0, head, 0, end);
                    pending.InsertRange(0, head);
                }
            }

            // Первая строка файла — перед ней нет '\n'.
            if (pending.Count > 0 && found.Count < max)
                Collect([.. pending], project, taskId, found);
        }
        catch { }
    }

    private static byte[] Line(byte[] buffer, int start, int length, List<byte> pending)
    {
        var line = new byte[length + pending.Count];
        if (length > 0) Buffer.BlockCopy(buffer, start, line, 0, length);
        pending.CopyTo(line, length);
        return line;
    }

    /// <summary>Битая строка — не повод ронять выдачу: пропускаем её молча.</summary>
    private static void Collect(byte[] line, string? project, string? taskId, List<JsonElement> found)
    {
        if (line.Length == 0) return;
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(line));
            var entry = document.RootElement;
            if (Matches(entry, "project", project) && Matches(entry, "task_id", taskId))
                found.Add(entry.Clone());
        }
        catch { }
    }

    private static bool Matches(JsonElement entry, string field, string? filter)
        => string.IsNullOrEmpty(filter) || (entry.TryGetProperty(field, out var value)
            && string.Equals(value.GetString(), filter, StringComparison.OrdinalIgnoreCase));

    public sealed class Page
    {
        public string file { get; set; } = "";
        public int count => entries.Count;
        public List<JsonElement> entries { get; } = [];

        /// <summary>Tail упёрся в потолок сканирования: записей меньше запрошенного не потому, что их нет.</summary>
        public bool truncated { get; set; }
    }
}
