namespace z3nDash;

public sealed record ZpTaskSettingsRequest(Guid TaskId)
{
    public static bool TryCreate(
        string? taskId,
        out ZpTaskSettingsRequest request,
        out string error)
    {
        request = new ZpTaskSettingsRequest(Guid.Empty);
        error = "";

        if (!Guid.TryParse(taskId, out var parsedTaskId))
        {
            error = "task_id must be GUID";
            return false;
        }

        request = new ZpTaskSettingsRequest(parsedTaskId);
        return true;
    }

    public string BuildPath()
        => $"/task/settings?task_id={Uri.EscapeDataString(TaskId.ToString())}";
}
