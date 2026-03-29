using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cronos;
using NBitcoin.Protocol;
using z3n8;

namespace z3n8;

public sealed class SchedulerService : IDisposable
{
    private readonly DbConnectionService _dbService;
    private readonly System.Threading.Timer _timer;
    internal readonly ConcurrentDictionary<string, RunningProcess> _running = new();
    private readonly Logger? _log;
    private readonly ConcurrentDictionary<string, Func<Dictionary<string, string>, CancellationToken, Action<string>, Task<string>>> _internalTasks = new();
    public void RegisterTask(string name, Func<Dictionary<string, string>, CancellationToken, Action<string>, Task<string>> handler)
        => _internalTasks[name] = handler;

    private const string Table = "_schedules";

    private static readonly Dictionary<string, string> Schema = new()
    {
        { "id",               "TEXT PRIMARY KEY" },
        { "name",             "TEXT DEFAULT ''" },
        { "executor",         "TEXT DEFAULT 'internal'" },
        { "script_path",      "TEXT DEFAULT ''" },
        { "args",             "TEXT DEFAULT ''" },
        { "enabled",          "TEXT DEFAULT 'true'" },
        { "cron",             "TEXT DEFAULT ''" },
        { "interval_minutes", "TEXT DEFAULT '0'" },
        { "fixed_time",       "TEXT DEFAULT ''" },
        { "on_overlap",       "TEXT DEFAULT 'skip'" },
        { "max_threads",      "TEXT DEFAULT '1'" },
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

    private const string QueueTable = "_schedule_queue";

    private static readonly Dictionary<string, string> QueueSchema = new()
    {
        { "uuid",        "TEXT PRIMARY KEY" },
        { "schedule_id", "TEXT DEFAULT ''" },
        { "queued_at",   "TEXT DEFAULT ''" },
        { "status",      "TEXT DEFAULT 'pending'" },  // pending | running | done | error
        { "priority",    "TEXT DEFAULT '10'" },        // 0=explicit, 10=cron/interval
        { "run_id",      "TEXT DEFAULT ''" },
        { "args_b64",    "TEXT DEFAULT ''" },
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
        db.PrepareTable(QueueSchema, QueueTable);
        SeedDefaults(db);
    }

private static void SeedDefaults(Db db)
{
    var baseDir = AppContext.BaseDirectory;

    var rows = new[]
    {
        (
            id:             "0d1d2d4e-d033-4661-8198-f85ad1e8d906",
            name:           "internal.0. env-check",
            executor:       "ps1",
            scriptPath:     Path.Combine(baseDir, "Tasks", "ps1", "check-env.ps1"),
            payloadSchema:  ""
        ),
        (
            id:             "4d8ead14-37ab-4081-97bf-9785929e055e",
            name:           "internal.1.env-set",
            executor:       "ps1",
            scriptPath:     Path.Combine(baseDir, "Tasks", "ps1", "setup-env.ps1"),
            payloadSchema:  ""
        ),
        (
            id:             "ab7cfd05-43ae-49b2-a102-b832fb5572b2",
            name:           "internal.GenerateClientBundle",
            executor:       "internal",
            scriptPath:     "GenerateClientBundle",
            payloadSchema:  "[{\"key\":\"clientHwid\",\"label\":\"hwID\",\"type\":\"text\",\"options\":\"\"},{\"key\":\"clientName\",\"label\":\"Roman001\",\"type\":\"text\",\"options\":\"\"},{\"key\":\"outputFolder\",\"label\":\"output\",\"type\":\"text\",\"options\":\"\"}]"
        ),
        (
            id:             "8b02699c-8198-4f19-ae8d-17ae17e8f850",
            name:           "example.csx.sysinfo",
            executor:       "csx",
            scriptPath:     Path.Combine(baseDir, "Tasks", "examples", "sysinfo.csx"),
            payloadSchema:  ""
        ),
        (
            id:             "bfd48f48-26f8-4e88-8a87-b4dbcce5e49d",
            name:           "example.zp_csx.rabby_zb",
            executor:       "csx-internal",
            scriptPath:     Path.Combine(baseDir, "Tasks", "examples", "rabby.csx"),
            payloadSchema:  ""
        ),
    };

    foreach (var r in rows)
    {
        var path   = r.scriptPath.Replace("'", "''");
        var schema = r.payloadSchema.Replace("'", "''");
        db.Query($"""
            INSERT OR IGNORE INTO "_schedules"
                ("id","name","executor","script_path","enabled","on_overlap","status","payload_schema")
            VALUES
                ('{r.id}','{r.name}','{r.executor}','{path}','true','skip','idle','{schema}')
            """);
    }
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
            var record     = ParseRow(row, columns);
            if (!ShouldFire(record, now)) continue;

            var overlap    = record.GetValueOrDefault("on_overlap", "skip");
            var id         = record.GetValueOrDefault("id", "");
            var maxThreads = int.TryParse(record.GetValueOrDefault("max_threads", "1"), out var mt) ? mt : 1;
            var active     = CountActiveInstances(id);

            switch (overlap)
            {
                case "skip":
                    if (active > 0) continue;
                    break;
                case "kill_restart":
                    if (active > 0) { KillAllInstances(id); RemoveInstancesFromRunning(id); }
                    break;
                case "parallel":
                    if (active >= maxThreads) { EnqueueItem(db, id, record, priority: 10); continue; }
                    break;
            }

            _ = LaunchAsync(db, record, now);
        }

        DrainQueue(db, now);
    }

    private int CountActiveInstances(string scheduleId)
        => _running.Count(kv => kv.Key.StartsWith(scheduleId + ":") && !kv.Value.HasExited);

    private void RemoveInstancesFromRunning(string scheduleId)
    {
        foreach (var key in _running.Keys.Where(k => k.StartsWith(scheduleId + ":")).ToList())
            _running.TryRemove(key, out _);
    }

    private void KillAllInstances(string scheduleId)
    {
        foreach (var kv in _running.Where(kv => kv.Key.StartsWith(scheduleId + ":")))
            kv.Value.Kill();
    }

    private void EnqueueItem(Db db, string scheduleId, Dictionary<string, string> record, int priority)
    {
        db.InsertDic(new Dictionary<string, string>
        {
            { "uuid",        Guid.NewGuid().ToString() },
            { "schedule_id", scheduleId },
            { "queued_at",   DateTime.UtcNow.ToString("o") },
            { "status",      "pending" },
            { "priority",    priority.ToString() },
            { "run_id",      "" },
            { "args_b64",    record.GetValueOrDefault("args", "") },
        }, QueueTable);
    }

    private void DrainQueue(Db db, DateTime now)
    {
        var schedCols = db.GetTableColumns(Table);
        if (schedCols.Count == 0) return;

        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return;

        var colsSql = string.Join(", ", qCols.Select(c => $"\"{c}\""));
        var raw = db.Query($"SELECT {colsSql} FROM \"{QueueTable}\" WHERE \"status\" = 'pending' ORDER BY \"priority\" ASC, \"queued_at\" ASC");
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var qRow in raw.Split('·').Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var qRecord    = ParseRow(qRow, qCols);
            var scheduleId = qRecord.GetValueOrDefault("schedule_id", "");
            if (string.IsNullOrEmpty(scheduleId)) continue;

            var schedRows = db.GetLines(string.Join(",", schedCols), Table, where: $"\"id\" = '{scheduleId}'");
            if (schedRows.Count == 0) continue;

            var record     = ParseRow(schedRows[0], schedCols);
            var maxThreads = int.TryParse(record.GetValueOrDefault("max_threads", "1"), out var mt) ? mt : 1;
            if (CountActiveInstances(scheduleId) >= maxThreads) continue;

            var qUuid   = qRecord.GetValueOrDefault("uuid", "");
            var argsB64 = qRecord.GetValueOrDefault("args_b64", "");
            db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = 'running' WHERE \"uuid\" = '{qUuid}'");

            if (!string.IsNullOrWhiteSpace(argsB64)) record["args"] = argsB64;
            _ = LaunchFromQueue(db, record, now, qUuid);
        }
    }

    private void TryDrainOne(Db db, string scheduleId)
    {
        var schedCols = db.GetTableColumns(Table);
        if (schedCols.Count == 0) return;

        var schedRows = db.GetLines(string.Join(",", schedCols), Table, where: $"\"id\" = '{scheduleId}'");
        if (schedRows.Count == 0) return;

        var record     = ParseRow(schedRows[0], schedCols);
        var maxThreads = int.TryParse(record.GetValueOrDefault("max_threads", "1"), out var mt) ? mt : 1;
        if (CountActiveInstances(scheduleId) >= maxThreads) return;

        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return;

        var colsSql = string.Join(", ", qCols.Select(c => $"\"{c}\""));
        var raw = db.Query($"SELECT {colsSql} FROM \"{QueueTable}\" WHERE \"status\" = 'pending' AND \"schedule_id\" = '{scheduleId}' ORDER BY \"priority\" ASC, \"queued_at\" ASC LIMIT 1");
        if (string.IsNullOrWhiteSpace(raw)) return;

        var qRecord = ParseRow(raw.Split('·')[0], qCols);
        var qUuid   = qRecord.GetValueOrDefault("uuid", "");
        var argsB64 = qRecord.GetValueOrDefault("args_b64", "");
        db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = 'running' WHERE \"uuid\" = '{qUuid}'");

        if (!string.IsNullOrWhiteSpace(argsB64)) record["args"] = argsB64;
        _ = LaunchFromQueue(db, record, DateTime.UtcNow, qUuid);
    }

    private static void FinishQueueEntry(Db db, string? queueUuid, string status, string runId)
    {
        if (string.IsNullOrEmpty(queueUuid)) return;
        db.Query($"UPDATE \"{QueueTable}\" SET \"status\" = '{status}', \"run_id\" = '{runId}' WHERE \"uuid\" = '{queueUuid}'");
    }

    private Task LaunchAsync(Db db, Dictionary<string, string> record, DateTime firedAt)
        => LaunchFromQueue(db, record, firedAt, queueUuid: null);

    private async Task LaunchFromQueue(Db db, Dictionary<string, string> record, DateTime firedAt, string? queueUuid)
    {
        var id         = record.GetValueOrDefault("id", "");
        var name       = record.GetValueOrDefault("name", id);
        var executor   = record.GetValueOrDefault("executor", "python");
        var scriptPath = record.GetValueOrDefault("script_path", "");
        var args       = record.GetValueOrDefault("args", "");


        if (executor != "internal" && !File.Exists(scriptPath))
        {
            var errMsg = $"[ERR] no script file found at {scriptPath}";
            _log.Error(errMsg);
            SseHub.BroadcastOutput(
                JsonSerializer.Serialize(new { line = errMsg, level = "ERROR" }), id);
            db.Upd(
                $"last_output = '{errMsg.Replace("'","''")}', last_exit = '1', last_run = '{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}'",
                SchedulerService.Table,
                where: $"\"id\" = '{id}'");
            return;
        }
        

        // ── уникальный id прогона ──────────────────────────────────────────────
        var runId       = Guid.NewGuid().ToString("N")[..12];
        var instanceKey = $"{id}:{runId}";
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
        
        Action<string> broadcast = line =>
            SseHub.BroadcastOutput(JsonSerializer.Serialize(new { line, level = line.StartsWith("[ERR]") ? "ERROR" : "INFO" }), id);

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

            var rp  = new RunningProcess(null, firedAt, cts, broadcast);
            _running[instanceKey] = rp;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE] internal task started id={id} name={name} run={runId} key={instanceKey}");
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
                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true }), id);
                rp.Result = output ?? "";
                RunLogger(scheduleTag, runId)?.Info($"[{name}] done run={runId}");
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle", DateTime.UtcNow, "0", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "done", runId);
            }
            catch (Exception ex)
            {
                rp.AddLine("[ERR] " + ex.Message);
                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true }), id);
                var runLog = RunLogger(scheduleTag, runId);
                runLog?.Error($"[{name}] internal task failed: {ex.Message}");
                rp.Result = ex.Message;
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "error", runId);
            }
            finally
            {
                cts.Dispose();
                TryDrainOne(db, id);
            }

            return;
        }

        if (executor == "csx-internal")
        {
            if (!File.Exists(scriptPath))
            {
                _log?.Error($"[{name}] csx script not found: {scriptPath}");
                UpdateStatus(db, id, "error", firedAt, "-1", $"script not found: {scriptPath}", runId);
                return;
            }

            UpdateStatus(db, id, "running", firedAt, "", "", runId);
            var cts = new CancellationTokenSource();
            var rp  = new RunningProcess(null, firedAt, cts, broadcast);
            _running[instanceKey] = rp;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[LIVE] csx task started id={id} name={name} run={runId} script={Path.GetFileName(scriptPath)}");
            Console.ResetColor();

            InternalTasks.TaskContext? ctx = null;
            ZB? zb                        = null;
            Microsoft.Playwright.IPlaywright? pw = null;
            var released = false;
            var zbId     = "";

            try
            {
                var payload = string.IsNullOrWhiteSpace(args)
                    ? record
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(
                        Encoding.UTF8.GetString(Convert.FromBase64String(args))) ?? record;

                payload["__taskName"]    = name;
                payload["__runId"]       = runId;
                payload["__scheduleTag"] = scheduleTag;

                var accountTable = payload.GetValueOrDefault("accountTable",
                    "__" + name.Split('.')[0]);

                ctx = await InternalTasks.PrepareTaskContext(this, _dbService, Config.LogsConfig, payload, accountTable, rp.AddLine);

                if (ctx == null)
                {
                    rp.AddLine("no account available");
                    _running.TryRemove(instanceKey, out _);
                    UpdateStatus(db, id, "idle", DateTime.UtcNow, "0", "no account available", runId);
                    FinishQueueEntry(db, queueUuid, "done", runId);
                    return;
                }

                var needsBrowser = payload.GetValueOrDefault("browser", "false") == "true";
                zbId = ctx.Project.Variables["zb_id"].Value;
                zb = needsBrowser && !string.IsNullOrWhiteSpace(zbId)
                    ? new ZB(Config.ApiConfig.ZB)
                    : null;

                z3n8.Browser.PlaywrightInstance? instance = null;

                if (zb != null)
                {
                    var wsEndpoint = await zb.RunProfile(zbId);
                    if (!string.IsNullOrWhiteSpace(wsEndpoint))
                    {
                        pw          = await Microsoft.Playwright.Playwright.CreateAsync();
                        var browser = await pw.Chromium.ConnectOverCDPAsync(wsEndpoint);
                        var context = browser.Contexts[0];
                        var page    = context.Pages.FirstOrDefault()
                                      ?? await context.NewPageAsync();
                        instance    = new z3n8.Browser.PlaywrightInstance(page);
                    }
                }


                var globals = new CsxGlobals
                {
                    project  = ctx.Project,
                    instance = instance!,
                    log      = ctx.Logger,
                };

                await CsxExecutor.RunAsync(scriptPath, globals, cts.Token);

                ctx.Release("idle");
                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true }), id);
                rp.Result = "ok";
                RunLogger(scheduleTag, runId)?.Info($"[{name}] csx done run={runId}");
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle", DateTime.UtcNow, "0", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "done", runId);
                released = true;
            }
            catch (Exception ex)
            {
                var scriptFrames = ex.StackTrace?
                    .Split(" at ", StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f.Contains("Submission#0") || f.Contains("InternalTasks") || f.Contains("SchedulerService"))
                    .Select(f => "at " + f.Trim());

                rp.AddLine("[ERR] " + ex.Message);
                rp.AddLine("[ERR] " + ex.StackTrace);  
                foreach (var frame in scriptFrames ?? Enumerable.Empty<string>())
                    rp.AddLine("[ERR] " + frame);

                SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true }), id);
                RunLogger(scheduleTag, runId)?.Error($"[{name}] csx failed: {ex.Message}");
                rp.Result = ex.Message;
                _running.TryRemove(instanceKey, out _);
                UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", rp.Snapshot(), runId);
                FinishQueueEntry(db, queueUuid, "error", runId);
            }
            finally
            {
                try { if (zb != null) await zb.ProfileDown(zbId); } catch { }
                try { pw?.Dispose(); } catch { }
                if (!released) try { ctx?.Release("fail"); } catch { }
                cts.Dispose();
                TryDrainOne(db, id);
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
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var rp2     = new RunningProcess(process, firedAt, null, broadcast);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[LIVE] process task starting id={id} name={name} run={runId} key={instanceKey} exe={fileName}");
        Console.ResetColor();

        try
        {
            process.Start();

            _running[instanceKey] = rp2;

            var stdoutTask = ReadStreamAsync(process.StandardOutput.BaseStream, rp2, prefix: "");
            var stderrTask = ReadStreamAsync(process.StandardError.BaseStream,  rp2, prefix: "[ERR] ");

            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            SseHub.BroadcastOutput(JsonSerializer.Serialize(new { done = true }), id);

            string output = rp2.Snapshot();
            _log?.Info($"[{name}] exit {process.ExitCode} run={runId}");
            _running.TryRemove(instanceKey, out _);
            UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "idle", DateTime.UtcNow, process.ExitCode.ToString(), output, runId);
            FinishQueueEntry(db, queueUuid, "done", runId);
        }
        catch (Exception ex)
        {
            _log?.Error($"[{name}] launch failed: {ex.Message}");
            _running.TryRemove(instanceKey, out _);
            UpdateStatus(db, id, CountActiveInstances(id) > 0 ? "running" : "error", DateTime.UtcNow, "-1", ex.Message, runId);
            FinishQueueEntry(db, queueUuid, "error", runId);
        }
        finally
        {
            process.Dispose();
            TryDrainOne(db, id);
        }
    }

    private static async Task ReadStreamAsync(Stream stream, RunningProcess rp, string prefix)
    {
        var buffer = new byte[1024];
        var sb     = new StringBuilder();
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

            int idx;
            while ((idx = sb.ToString().IndexOfAny(['\n', '\r'])) >= 0)
            {
                var line = sb.ToString(0, idx).Trim();
                sb.Remove(0, idx + 1);
                if (line.Length > 0)
                    rp.AddLine(prefix + line);
            }
        }

        var tail = sb.ToString().Trim();
        if (tail.Length > 0)
            rp.AddLine(prefix + tail);
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
            "bash" => (ResolveGitBash(), $"\"{scriptPath}\" {args}".Trim()),
            "ps1" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}".Trim()),
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

    public bool IsRunning(string id)
        => _running.Any(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited);

    public (int pid, long uptimeSec, long memoryMB, bool running) GetProcessInfo(string id)
    {
        var inst = _running
            .Where(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        if (inst == null) return (-1, 0, 0, false);
        return (inst.Pid, inst.UptimeSec, inst.MemoryMB, true);
    }

    // runId == null → все инстансы; задан → только этот
    public string GetLiveOutput(string id, string? runId = null)
    {
        var keys = _running.Keys
            .Where(k => k.StartsWith(id + ":"))
            .Where(k => runId == null || k == $"{id}:{runId}")
            .ToList();

        if (keys.Count == 0) return "";
        if (keys.Count == 1)
            return _running.TryGetValue(keys[0], out var single) ? single.Snapshot() : "";

        return string.Join("\n\n", keys
            .Where(k => _running.ContainsKey(k))
            .Select(k => $"── {k[(id.Length + 1)..]} ──\n{_running[k].Snapshot()}"));
    }

    public string GetResult(string id)
        => _running
            .Where(kv => kv.Key.StartsWith(id + ":"))
            .Select(kv => kv.Value.Result)
            .FirstOrDefault() ?? "";

    public void ClearLiveOutput(string id)
    {
        foreach (var kv in _running.Where(kv => kv.Key.StartsWith(id + ":")))
            kv.Value.Clear();
    }

    public List<Dictionary<string, string>> GetInstances(string id)
        => _running
            .Where(kv => kv.Key.StartsWith(id + ":") && !kv.Value.HasExited)
            .Select(kv => new Dictionary<string, string>
            {
                { "runId",     kv.Key[(id.Length + 1)..] },
                { "uptimeSec", kv.Value.UptimeSec.ToString() },
                { "pid",       kv.Value.Pid.ToString() },
                { "memoryMB",  kv.Value.MemoryMB.ToString() },
            })
            .ToList();

    public List<Dictionary<string, string>> GetQueueItems(Db db, string scheduleId)
    {
        var qCols = db.GetTableColumns(QueueTable);
        if (qCols.Count == 0) return new();
        var colsSql = string.Join(", ", qCols.Select(c => $"\"{c}\""));
        var raw = db.Query($"SELECT {colsSql} FROM \"{QueueTable}\" WHERE \"schedule_id\" = '{scheduleId}' ORDER BY \"priority\" ASC, \"queued_at\" ASC");
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split('·').Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => ParseRow(r, qCols)).ToList();
    }

    public void ClearQueue(Db db, string scheduleId)
        => db.Query($"DELETE FROM \"{QueueTable}\" WHERE \"schedule_id\" = '{scheduleId}' AND \"status\" = 'pending'");

    private readonly SemaphoreSlim _reserveLock = new(1, 1);

    public async Task<Dictionary<string, string>?> ReserveAccount(
        Db db, string table, string condition, Logger? log = null)
    {
        await _reserveLock.WaitAsync();
        try
        {
            var cols    = db.GetTableColumns(table);
            if (cols.Count == 0)
            {
                LoggerExt.Debug($"table not found: {table}");
                return null;
            }
            var colsSql = string.Join(", ", cols.Select(c => $"\"{c}\""));
            var query   = $"SELECT {colsSql} FROM \"{table}\" WHERE ({condition}) AND \"status\" != 'busy' LIMIT 1";
            var rawRow  = db.Query(query, thrw: false);

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
        KillAllInstances(id);
        RemoveInstancesFromRunning(id);
    }

    public void KillInstance(string id, string runId)
    {
        var key = $"{id}:{runId}";
        if (!_running.TryGetValue(key, out var rp)) return;
        rp.Kill();
        _running.TryRemove(key, out _);
    }

    public void FireNow(string id, Dictionary<string, string> record, Db db)
    {
        var overlap    = record.GetValueOrDefault("on_overlap", "skip");
        var maxThreads = int.TryParse(record.GetValueOrDefault("max_threads", "1"), out var mt) ? mt : 1;
        var active     = CountActiveInstances(id);

        switch (overlap)
        {
            case "skip":
                if (active > 0) return;
                break;
            case "kill_restart":
                if (active > 0) { KillAllInstances(id); RemoveInstancesFromRunning(id); }
                break;
            case "parallel":
                if (active >= maxThreads) { EnqueueItem(db, id, record, priority: 0); return; }
                break;
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

    internal sealed class RunningProcess
    {
        private readonly System.Diagnostics.Process? _process;
        private readonly CancellationTokenSource?    _cts;
        private readonly List<string>                _lines = new();
        private readonly object                      _lock  = new();
        private readonly Action<string>?             _broadcast;

        public string   Result    { get; set; } = "";
        public DateTime StartedAt { get; }

        public RunningProcess(System.Diagnostics.Process? process, DateTime startedAt, CancellationTokenSource? cts, Action<string>? broadcast = null)
        {
            _process   = process;
            StartedAt  = startedAt;
            _cts       = cts;
            _broadcast = broadcast;
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
            _broadcast?.Invoke(line);
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