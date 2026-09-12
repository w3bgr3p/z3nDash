using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace z3nDash;

public sealed partial class SchedulerService
{
    private readonly object _controlGate = new();
    private readonly ConcurrentDictionary<string, TaskRunContext> _runContexts = new();
    public string ApiBaseUrl { get; set; } = "";

    private TaskRunContext RegisterRun(string taskId)
    {
        var context = new TaskRunContext(ApiBaseUrl, taskId, Guid.NewGuid().ToString("N")[..12],
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        _runContexts[context.Token] = context;
        return context;
    }

    public bool TryGetRun(string token, out TaskRunContext? context)
        => _runContexts.TryGetValue(token, out context);

    private static string SqlValue(string value) => value.Replace("'", "''");

    private static DateTimeOffset? DeferredUntil(Dictionary<string, string> record)
        => DateTimeOffset.TryParse(record.GetValueOrDefault("deferred_until"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var until) ? until : null;

    private static bool IsHeld(Dictionary<string, string> record, DateTime now)
        => record.GetValueOrDefault("schedule_paused") == "true" || DeferredUntil(record) > now;

    private Dictionary<string, string> ReadControlRecord(Db db, string id)
    {
        var cols = new List<string> { "id", "name", "enabled", "schedule_mode", "schedule_paused", "deferred_until", "defer_reason" };
        var select = string.Join(",", cols.Select(c => $"\"{c}\""));
        var row = db.Query($"SELECT {select} FROM \"{Table}\" WHERE \"id\" = '{SqlValue(id)}'", thrw: true);
        if (string.IsNullOrEmpty(row)) throw new KeyNotFoundException("Task not found");
        return ParseRow(row, cols);
    }

    private bool AutomaticLaunchAllowed(Db db, string id)
    {
        try
        {
            var record = ReadControlRecord(db, id);
            return record.GetValueOrDefault("enabled") == "true" && !IsHeld(record, DateTime.UtcNow);
        }
        catch (KeyNotFoundException) { return false; }
    }

    public TaskControlState GetControlState(string id, string? runId = null)
    {
        lock (_controlGate)
            return ControlState(ReadControlRecord(_dbService.GetDb(), id), runId);
    }

    private static TaskControlState ControlState(Dictionary<string, string> record, string? runId)
        => new(record["id"], runId, record.GetValueOrDefault("name", ""),
            record.GetValueOrDefault("enabled") == "true", record.GetValueOrDefault("schedule_paused") == "true",
            DeferredUntil(record), DecodeReason(record.GetValueOrDefault("defer_reason", "")),
            record.GetValueOrDefault("schedule_mode", "off"));

    public TaskControlState DeferTask(string id, DateTimeOffset until, string reason, string? runId = null)
    {
        if (until <= DateTimeOffset.UtcNow) throw new ArgumentException("until must be in the future");
        if (reason.Length > 2000) throw new ArgumentException("reason must be at most 2000 characters");
        lock (_controlGate)
        {
            var db = _dbService.GetDb();
            var record = ReadControlRecord(db, id);
            // Concurrent failing instances can extend, but never shorten, the hold.
            if (DeferredUntil(record) is not { } previous || until > previous)
            {
                var encodedReason = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(reason));
                db.Query($"UPDATE \"{Table}\" SET \"deferred_until\" = '{until.UtcDateTime:o}', \"defer_reason\" = '{encodedReason}' WHERE \"id\" = '{SqlValue(id)}'", thrw: true);
            }
            return ControlState(ReadControlRecord(db, id), runId);
        }
    }

    public TaskControlState PauseTask(string id, bool paused, string? runId = null)
    {
        lock (_controlGate)
        {
            var db = _dbService.GetDb();
            ReadControlRecord(db, id);
            // Resume explicitly clears both the indefinite pause and temporary hold.
            var clear = paused ? "" : ", \"deferred_until\" = '', \"defer_reason\" = ''";
            db.Query($"UPDATE \"{Table}\" SET \"schedule_paused\" = '{(paused ? "true" : "false")}'{clear} WHERE \"id\" = '{SqlValue(id)}'", thrw: true);
            return ControlState(ReadControlRecord(db, id), runId);
        }
    }

    // Db's row protocol uses separators; store free text as base64 to preserve any reason.
    internal static string DecodeReason(string reason)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(reason)); }
        catch (FormatException) { return reason; }
    }
}
