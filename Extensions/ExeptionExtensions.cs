namespace z3nIO;

internal static class ExceptionExtensions
{
    internal static void LogErr(this SchedulerService.RunningProcess rp, Exception ex)
    {
        var scriptFrames = ex.StackTrace?
            .Split(" at ", StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.Contains("Submission#0") || f.Contains("InternalTasks") || f.Contains("SchedulerService"))
            .Select(f => "at " + f.Trim());

        var line = "[ERR] " + ex.Message;
        foreach (var frame in scriptFrames ?? Enumerable.Empty<string>())
            line += (" " + frame);     
        
        rp.AddLine(line);

    }
}