// InternalTasks.Context.cs

using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n8;

public static partial class InternalTasks
{
    internal sealed record TaskContext(
        StubProject                    Project,
        Logger                         Logger,
        Action<string>                 Release
    );

    // "1-100"       → ["1","2",...,"100"]
    // "1,5,9"       → ["1","5","9"]
    // "7"           → ["7"]
    private static List<string> ParseSingleRange(string group)
    {
        group = group.Trim();
        if (group.Contains(','))
            return group.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (group.Contains('-'))
        {
            var parts = group.Split('-');
            int start = int.Parse(parts[0].Trim());
            int end   = int.Parse(parts[1].Trim());
            return Enumerable.Range(start, end - start + 1).Select(i => i.ToString()).ToList();
        }
        return new List<string> { group };
    }

    // "1-50:51-100" → [["1".."50"], ["51".."100"]]
    private static List<List<string>> ParseRangeGroups(string rangeStr) =>
        rangeStr.Split(':').Select(ParseSingleRange).ToList();

    internal static async Task<TaskContext?> PrepareTaskContext(
        SchedulerService               scheduler,
        DbConnectionService            dbService,
        LogsConfig                     logsConfig,
        Dictionary<string, string>     payload,
        string                         accountTable,
        Action<string>?                sink = null)
    {
        if (!dbService.TryGetDb(out var db) || db == null)
            throw new Exception("DB not connected");

        var logger = new Logger(
            logHost: logsConfig.LogHost,
            project: payload.GetValueOrDefault("__taskName", "").Split('.')[0],
            sink:    sink);
        
        
        var condition = payload.GetValueOrDefault("condition", "1=1")
            .Replace("NOW", $"'{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}'");

        Dictionary<string, string>? account = null;

        if (payload.TryGetValue("range", out var rangeStr) && !string.IsNullOrWhiteSpace(rangeStr))
        {
            logger?.Send($"getting acc in  {rangeStr}");
            foreach (var group in ParseRangeGroups(rangeStr))
            {
                var inClause  = string.Join(",", group);
                var fullCond  = $"{condition} AND \"id\" IN ({inClause})";
                account = await scheduler.ReserveAccount(db, accountTable, fullCond);
                if (account != null) break;
            }
        }
        else
        {
            
            account = await scheduler.ReserveAccount(db, accountTable, condition);
        }

        if (account == null)
        {
            logger?.Send($"no accs by {condition}");
            return null;
        }

        try
        {


            var accId = account.GetValueOrDefault("id", "");
            logger?.Send($"running {accId} by {condition}");

            var project = new StubProject();
            project.Db = db;
            project.Name = payload.GetValueOrDefault("__taskName", "").Split('.')[0] + ".zp";

            foreach (var kv in account)
                project.Variables[kv.Key].Value = kv.Value;


            project.Variables["acc0"].Value = accId;
            project.Variables["jVars"].Value = InternalTasks._jVars;

            logger.Acc = accId;

            var instanceCols = db.GetTableColumns("_instance");
            if (instanceCols.Count > 0)
            {
                var instance = db.GetColumns(string.Join(",", instanceCols), "_instance", where: $"\"id\" = '{accId}'");
                project.VarsFromDict(instance);
            }

            var profileCols = db.GetTableColumns("folder_profile");
            if (profileCols.Count > 0)
            {
                var profile = db.GetColumns(string.Join(",", profileCols), "folder_profile",
                    where: $"\"id\" = '{accId}'");
                project.VarsFromDict(profile);
                if (profile.TryGetValue("UserAgent", out var ua) && !string.IsNullOrEmpty(ua))
                    project.Profile.UserAgent = ua;
            }

            var scheduleTag = payload.GetValueOrDefault("__scheduleTag", payload.GetValueOrDefault("__taskName", ""));
            var runId = payload.GetValueOrDefault("__runId", "");

            project.Variables["__scheduleTag"].Value = scheduleTag;
            project.Variables["__runId"].Value = runId;

            logger.TaskId = scheduleTag;
            logger.Session = runId;

            return new TaskContext(
                Project: project,
                Logger: logger,
                Release: status => scheduler.ReleaseAccount(db, accountTable, account, status)
            );
        }
        catch
        {
            scheduler.ReleaseAccount(db, accountTable, account, "fail");
            throw;
        }
    }
}