using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cronos;
using z3n8;

namespace z3n8;

public sealed class SchedulerService : IDisposable
{
    private readonly DbConnectionService _dbService;
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<string, RunningProcess> _running = new();
    private readonly Logger? _log;
    private readonly ConcurrentDictionary<string, Func<Dictionary<string, string>, CancellationToken, Action<string>, Task<string>>> _internalTasks = new();
    public void RegisterTask(string name, Func<Dictionary<string, string>, CancellationToken, Action<string>, Task<string>> handler)
        => _internalTasks[name] = handler;

    private const string Table = "_schedules";

    private static readonly Dictionary<string, string> Schema = new()
    {
        { "id",               "TEXT PRIMARY KEY" },
        { "name",             "TEXT DEFAULT ''" },
        { "executor",         "TEXT DEFAULT 'python'" },
        { "script_path",      "TEXT DEFAULT ''" },
        { "args",             "TEXT DEFAULT ''" },
        { "enabled",          "TEXT DEFAULT 'true'" },
        { "cron",             "TEXT DEFAULT ''" },
        { "interval_minutes", "TEXT DEFAULT '0'" },
        { "fixed_time",       "TEXT DEFAULT ''" },
        { "on_overlap",       "TEXT DEFAULT 'skip'" },
        { "status",           "TEXT DEFAULT 'idle'" },
        { "last_run",         "TEXT DEFAULT ''" },
        { "last_exit",        "TEXT DEFAULT ''" },
        { "last_output",      "TEXT DEFAULT ''" },
        { "payload_schema",   "TEXT DEFAULT ''" },
        { "payload_values",   "TEXT DEFAULT ''" },
        { "runs_total",       "TEXT DEFAULT '0'" },
        { "runs_success",     "TEXT DEFAULT '0'" },
        { "schedule_tag",     "TEXT DEFAULT ''" },  // стабильный тег = имя задачи, фильтр логов
        { "last_run_id",      "TEXT DEFAULT ''" },  // run_id последнего прогона
    };

    public SchedulerService(DbConnectionService dbService, Logger? log = null)
    {
        _dbService = dbService;
        _log       = log;
        _timer = new System.Threading.Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public void Init()
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;
        db.PrepareTable(Schema, Table);
    }

    private void Tick(object? _)
    {
        if (!_dbService.TryGetDb(out var db) || db == null) return;

        var columns = db.GetTableColumns(Table);
        if (columns.Count == 0) return;

        var rows = db.GetLines(string.Join(",", columns), Table, where: "\"enabled\" = 'true'");
        var now  = DateTime.UtcNow;

        foreach (var row in rows)
        {
            var record = ParseRow(row, columns);
            if (!ShouldFire(record, now)) continue;

            var overlap = record.GetValueOrDefault("on_overlap", "skip");
            var id      = record.GetValueOrDefault("id", "");

            if (_running.TryGetValue(id, out var existing) && !existing.HasExited)
            {
                switch (overlap)
                {
                    case "skip":
                        continue;
                    case "kill_restart":
                        existing.Kill();
                        _running.TryRemove(id, out RunningProcess _);
                        break;
                }
            }

            _ = LaunchAsync(db, record, now);
        }
    }

    private async Task LaunchAsync(Db db, Dictionary<string, string> record, DateTime firedAt)
    {
        var id         = record.GetValueOrDefault("id", "");
        var name       = record.GetValueOrDefault("name", id);
        var executor   = record.GetValueOrDefault("executor", "python");
        var scriptPath = record.GetValueOrDefault("script_path", "");
        var args       = record.GetValueOrDefault("args", "");

        // ── уникальный id прогона ──────────────────────────────────────────────
        var runId       = Guid.NewGuid().ToString("N")[..12];
        var scheduleTag = BuildScheduleTag(name);

        var payloadValues = record.GetValueOrDefault("payload_values", "");
        if (!string.IsNullOrWhiteSpace(payloadValues))
        {
            var projectName = Path.GetFileName(scriptPath).Split('.')[0];
            var table       = $"__{projectName}";
            var nowIso      = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadValues)
                          ?? new Dictionary<string, object>();

            var condition = payload.TryGetValue("condition", out var cond) ? cond?.ToString() ?? "" : "";
            condition     = condition.Replace("NOW", $"'{nowIso}'");

            if (!string.IsNullOrWhiteSpace(condition))
            {
                var cols    = db.GetTableColumns(table);
                var colsSql = string.Join(", ", cols.Select(c => $"\"{c}\""));
                var rawRow  = db.Query($"SELECT {colsSql} FROM \"{table}\" WHERE {condition} LIMIT 1");

                if (!string.IsNullOrWhiteSpace(rawRow))
                {
                    var values = rawRow.Split('¦');
                    for (int i = 0; i < cols.Count && i < values.Length; i++)
                        payload[cols[i]] = values[i];
                    _log?.Info($"[{name}] account selected from {table}");
                }
                else
                {
                    _log?.Warn($"[{name}] no account found in {table} by condition: {condition}");
                }
            }

            args = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        }

        if (executor == "internal")
        {
            if (!_internalTasks.TryGetValue(scriptPath, out var handler))
            {
                _log?.Error($"[{name}] internal task not found: {scriptPath}");
                UpdateStatus(db, id, "error", firedAt, "-1", $"task not registered: {scriptPath}", runId);
                return;
            }

            UpdateStatus(db, id, "running", firedAt, "", "", runId);
            var cts = new CancellationTokenSource();
            var rp  = new RunningProcess(null, firedAt, cts);
            _running[id] = rp;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE] internal task started id={id} name={name} run={runId}");
            Console.ResetColor();

            try
            {
                var payload = string.IsNullOrWhiteSpace(args)
                    ? record
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(
                        Encoding.UTF8.GetString(Convert.FromBase64String(args))) ?? record;

                payload["__taskName"]    = name;
                payload["__runId"]       = runId;
                payload["__scheduleTag"] = scheduleTag;

                var output = await handler(payload, cts.Token, rp.AddLine);
                rp.Result = output ?? "";
                RunLogger(scheduleTag, runId)?.Info($"[{name}] done run={runId}");
                _running.TryRemove(id, out RunningProcess _);
                UpdateStatus(db, id, "idle", DateTime.UtcNow, "0", rp.Snapshot(), runId);
            }
            catch (Exception ex)
            {
                rp.AddLine("[ERR] " + ex.Message);
                var runLog = RunLogger(scheduleTag, runId);
                runLog?.Error($"[{name}] internal task failed: {ex.Message}");
                rp.Result = ex.Message;
                _running.TryRemove(id, out RunningProcess _);
                UpdateStatus(db, id, "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
            }
            finally
            {
                cts.Dispose();
            }

            return;
        }

        var (fileName, arguments) = BuildCommand(executor, scriptPath, args);

        _log?.Info($"[{name}] launch → {fileName} {Path.GetFileName(scriptPath)} run={runId}");
        UpdateStatus(db, id, "running", firedAt, "", "", runId);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = Directory.Exists(Path.GetDirectoryName(scriptPath) ?? "")
                                        ? Path.GetDirectoryName(scriptPath)!
                                        : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var rp2     = new RunningProcess(process, firedAt, null);

        process.OutputDataReceived += (_, e) => { if (e.Data != null) rp2.AddLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) rp2.AddLine("[ERR] " + e.Data); };

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[LIVE] process task starting id={id} name={name} run={runId} exe={fileName}");
        Console.ResetColor();

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _running[id] = rp2;

            await process.WaitForExitAsync();

            string output = rp2.Snapshot();
            _log?.Info($"[{name}] exit {process.ExitCode} run={runId}");
            _running.TryRemove(id, out RunningProcess _);
            UpdateStatus(db, id, "idle", DateTime.UtcNow, process.ExitCode.ToString(), output, runId);
        }
        catch (Exception ex)
        {
            _log?.Error($"[{name}] launch failed: {ex.Message}");
            _running.TryRemove(id, out RunningProcess _);
            UpdateStatus(db, id, "error", DateTime.UtcNow, "-1", ex.Message, runId);
        }
        finally
        {
            process.Dispose();
        }
    }

    // ── schedule_tag: имя задачи без пробелов, lowercase ─────────────────────
    private Logger? RunLogger(string scheduleTag, string runId)
    {
        if (_log == null) return null;
        return new Logger(
            taskId:  scheduleTag,
            session: runId,
            logHost: _log.LogHost);
    }

    private static string BuildScheduleTag(string name)
        => string.IsNullOrEmpty(name) ? "unknown" : name.Trim().Replace(" ", "_");

    private void UpdateStatus(Db db, string id, string status, DateTime lastRun, string exitCode, string output, string runId = "")
    {
        var safe = output
            .Replace("'", "''")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "");

        string incrSql = "";
        if (exitCode != "")
        {
            bool isPostgre = db.Mode == dbMode.Postgre;
            string castTotal   = isPostgre
                ? "COALESCE(NULLIF(\"runs_total\",''),'0')::int + 1"
                : "CAST(COALESCE(NULLIF(\"runs_total\",''),'0') AS INTEGER) + 1";
            string castSuccess = isPostgre
                ? "COALESCE(NULLIF(\"runs_success\",''),'0')::int + 1"
                : "CAST(COALESCE(NULLIF(\"runs_success\",''),'0') AS INTEGER) + 1";

            incrSql = isPostgre
                ? $", runs_total = ({castTotal})::text"
                : $", runs_total = CAST({castTotal} AS TEXT)";

            if (exitCode == "0")
                incrSql += isPostgre
                    ? $", runs_success = ({castSuccess})::text"
                    : $", runs_success = CAST({castSuccess} AS TEXT)";
        }

        string runIdSql = !string.IsNullOrEmpty(runId) ? $", last_run_id = '{runId}'" : "";

        db.Upd(
            $"status = '{status}', last_run = '{lastRun:yyyy-MM-dd HH:mm:ss}', last_exit = '{exitCode}', last_output = '{safe}'{incrSql}{runIdSql}",
            Table,
            where: $"\"id\" = '{id}'"
        );
    }

    private static bool ShouldFire(Dictionary<string, string> r, DateTime now)
    {
        var cron = r.GetValueOrDefault("cron", "");
        if (!string.IsNullOrWhiteSpace(cron))
        {
            try
            {
                var expr = CronExpression.Parse(cron);
                var prev = expr.GetNextOccurrence(now.AddMinutes(-1), TimeZoneInfo.Utc);
                if (prev.HasValue && prev.Value >= now.AddMinutes(-1) && prev.Value <= now)
                    return true;
            }
            catch { }
        }

        if (int.TryParse(r.GetValueOrDefault("interval_minutes", "0"), out int interval) && interval > 0)
        {
            var lastRun = r.GetValueOrDefault("last_run", "");
            if (string.IsNullOrEmpty(lastRun)) return true;
            if (DateTime.TryParse(lastRun, out var last) && (now - last).TotalMinutes >= interval)
                return true;
        }

        var fixedTime = r.GetValueOrDefault("fixed_time", "");
        if (!string.IsNullOrWhiteSpace(fixedTime) && TimeSpan.TryParse(fixedTime, out var ft))
        {
            var todayFire = now.Date + ft;
            var lastRun   = r.GetValueOrDefault("last_run", "");
            DateTime.TryParse(lastRun, out var last);
            if (now >= todayFire && now < todayFire.AddMinutes(1) && last.Date < now.Date)
                return true;
        }

        return false;
    }

    private static string ResolveGitBash()
    {
        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            @"C:\Git\bin\bash.exe",
        ];
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return "bash"; // PATH fallback
    }

        private static (string fileName, string arguments) BuildCommand(string executor, string scriptPath, string args)
    {
        static string TsNodeArgs(string path, string extraArgs)
        {
            string dir      = Path.GetDirectoryName(path) ?? ".";
            string tsconfig = Path.Combine(dir, "tsconfig.json");
            string project  = File.Exists(tsconfig) ? $"--project \"{tsconfig}\" " : "";
            return $"/c npx ts-node {project}\"{path}\" {extraArgs}".Trim();
        }

        return executor.ToLower() switch
        {
            "python"  => ("python",   $"\"{scriptPath}\" {args}".Trim()),
            "node"    => ("node",     $"\"{scriptPath}\" {args}".Trim()),
            "ts-node" => ("cmd.exe",  TsNodeArgs(scriptPath, args)),
            "csx"     => ("cmd.exe",  $"/c dotnet-script \"{scriptPath}\" {args}".Trim()),
            "exe"     => (scriptPath, args),
            "bat"     => ("cmd.exe",  $"/c \"{scriptPath}\" {args}".Trim()),
            "bash" => (ResolveGitBash(), $"-c \"stdbuf -oL -eL bash '{scriptPath}' {args}\"".Trim()),
            _         => ("python",   $"\"{scriptPath}\" {args}".Trim()),
        };
    }

    private static Dictionary<string, string> ParseRow(string row, List<string> columns)
    {
        var values = row.Split('¦');
        var dict   = new Dictionary<string, string>();
        for (int i = 0; i < columns.Count && i < values.Length; i++)
            dict[columns[i]] = values[i];
        return dict;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRunning(string id) =>
        _running.TryGetValue(id, out var p) && !p.HasExited;

    public (int pid, long uptimeSec, long memoryMB, bool running) GetProcessInfo(string id)
    {
        if (!_running.TryGetValue(id, out var rp) || rp.HasExited)
            return (-1, 0, 0, false);
        return (rp.Pid, rp.UptimeSec, rp.MemoryMB, true);
    }

    public string GetLiveOutput(string id)
    {
        var found = _running.TryGetValue(id, out var rp);
        var snap  = found ? rp!.Snapshot() : "";
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine($"[LIVE?] id={id} found={found} len={snap.Length}");
        Console.ResetColor();
        return snap;
    }

    public string GetResult(string id) =>
        _running.TryGetValue(id, out var rp) ? rp.Result : "";

    public void ClearLiveOutput(string id)
    {
        if (_running.TryGetValue(id, out var rp)) rp.Clear();
    }

    private readonly SemaphoreSlim _reserveLock = new(1, 1);

    public async Task<Dictionary<string, string>?> ReserveAccount(
        Db db, string table, string condition, Logger? log = null)
    {
        await _reserveLock.WaitAsync();
        try
        {
            var cols    = db.GetTableColumns(table);
            var colsSql = string.Join(", ", cols.Select(c => $"\"{c}\""));
            var query   = $"SELECT {colsSql} FROM \"{table}\" WHERE ({condition}) AND \"status\" != 'busy' LIMIT 1";
            var rawRow  = db.Query(query, thrw: true);

            if (string.IsNullOrWhiteSpace(rawRow))
            {
                LoggerExt.Debug($"no accs by query [{query}]");
                return null;
            }

            var values = rawRow.Split('¦');
            var record = new Dictionary<string, string>();
            for (int i = 0; i < cols.Count && i < values.Length; i++)
                record[cols[i]] = values[i];

            var keyCol = "id";
            var id     = record.GetValueOrDefault(keyCol, "");
            if (string.IsNullOrEmpty(id)) return null;

            db.Query($"UPDATE \"{table}\" SET \"status\" = 'busy' WHERE \"{keyCol}\" = '{id}'");
        
            // ── сохраняем метаданные лока ──────────────────────────────
            record["__keyCol"]   = keyCol;
            record["__table"]    = table;
            record["__lockedAt"] = DateTime.UtcNow.ToString("o");  // ISO timestamp
        
            // ── логируем какой аккаунт залочен ────────────────────────
            var acctLabel = record.GetValueOrDefault("address",
                record.GetValueOrDefault("login",
                    record.GetValueOrDefault("name", id)));
            log?.Info($"account locked  table={table} id={id} label={acctLabel}");
        
            return record;
        }
        finally
        {
            _reserveLock.Release();
        }
    }

    public void ReleaseAccount(
        Db db, string table, Dictionary<string, string> account, 
        string status, Logger? log = null)
    {
        var keyCol = account.GetValueOrDefault("__keyCol", "id");
        var id     = account.GetValueOrDefault(keyCol, "");
        if (string.IsNullOrEmpty(id)) return;

        db.Query($"UPDATE \"{table}\" SET \"status\" = '{status}' WHERE \"{keyCol}\" = '{id}'");

        // ── логируем длительность лока ────────────────────────────────
        var acctLabel = account.GetValueOrDefault("address",
            account.GetValueOrDefault("login",
                account.GetValueOrDefault("name", id)));

        var lockedSec = "";
        if (account.TryGetValue("__lockedAt", out var lockedAt) &&
            DateTime.TryParse(lockedAt, out var lockedTime))
        {
            var elapsed = DateTime.UtcNow - lockedTime;
            lockedSec = $" locked={elapsed.TotalSeconds:F1}s";
        }

        log?.Info($"account released table={table} id={id} label={acctLabel} status={status}{lockedSec}");
    }

    public void Kill(string id)
    {
        if (!_running.TryGetValue(id, out var rp)) return;
        rp.Kill();
    }

    public void FireNow(string id, Dictionary<string, string> record, Db db)
    {
        var overlap = record.GetValueOrDefault("on_overlap", "skip");

        if (_running.TryGetValue(id, out var existing) && !existing.HasExited)
        {
            switch (overlap)
            {
                case "skip":    return;
                case "kill_restart":
                    existing.Kill();
                    _running.TryRemove(id, out RunningProcess _);
                    break;
            }
        }

        _ = LaunchAsync(db, record, DateTime.UtcNow);
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var rp in _running.Values)
            rp.Kill();
    }

    // ── RunningProcess ────────────────────────────────────────────────────────

    private sealed class RunningProcess
    {
        private readonly System.Diagnostics.Process? _process;
        private readonly CancellationTokenSource?    _cts;
        private readonly List<string>                _lines = new();
        private readonly object                      _lock  = new();

        public string Result { get; set; } = "";
        public DateTime StartedAt { get; }

        public RunningProcess(System.Diagnostics.Process? process, DateTime startedAt, CancellationTokenSource? cts)
        {
            _process  = process;
            StartedAt = startedAt;
            _cts      = cts;
        }

        public int Pid =>
            _process == null ? -1 : (_process.HasExited ? -1 : _process.Id);

        public long MemoryMB
        {
            get
            {
                try
                {
                    var target = _process is { HasExited: false } ? _process : System.Diagnostics.Process.GetCurrentProcess();
                    target.Refresh();
                    return target.WorkingSet64 / 1024 / 1024;
                }
                catch { return 0; }
            }
        }

        public long UptimeSec => (long)(DateTime.UtcNow - StartedAt).TotalSeconds;

        public bool HasExited =>
            _process == null ? (_cts?.IsCancellationRequested ?? true) : _process.HasExited;

        public void Kill()
        {
            if (_process != null)
                try { _process.Kill(entireProcessTree: true); } catch { }
            _cts?.Cancel();
        }

        public void AddLine(string line)
        {
            lock (_lock) { _lines.Add(line); if (_lines.Count > 2000) _lines.RemoveAt(0); }
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE+] {line}");
            Console.ResetColor();
        }

        public string Snapshot()
        {
            lock (_lock) { return string.Join("\n", _lines); }
        }

        public void Clear()
        {
            lock (_lock) { _lines.Clear(); }
        }
    }
}