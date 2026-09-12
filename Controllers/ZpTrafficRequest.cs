namespace z3nDash;

/// <summary>
/// Запрос трафика к ноде z3n7: GET /traffic?tail=N&amp;project=&amp;task_id=
///
/// Режим только tail — «последние N записей». Форвардная пагинация по байтовому
/// оффсету у ноды осталась, но оркестратору она не нужна: модалка и панель задачи
/// каждый раз просят хвост, как это делает allLogs для текстовых логов.
/// </summary>
public sealed record ZpTrafficRequest(int Limit, string Project, string TaskId)
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    public static bool TryCreate(
        string? limit,
        string? project,
        string? taskId,
        out ZpTrafficRequest request,
        out string error)
    {
        request = new ZpTrafficRequest(DefaultLimit, "", "");
        error = "";

        if (!string.IsNullOrWhiteSpace(limit) && !int.TryParse(limit, out _))
        {
            error = "tail must be a number";
            return false;
        }

        var parsedLimit = int.TryParse(limit, out var value) ? value : DefaultLimit;
        parsedLimit = Math.Clamp(parsedLimit, 1, MaxLimit);

        request = new ZpTrafficRequest(parsedLimit, project?.Trim() ?? "", taskId?.Trim() ?? "");
        return true;
    }

    public string BuildPath()
    {
        var path = $"/traffic?tail={Limit}";
        if (!string.IsNullOrEmpty(Project)) path += $"&project={Uri.EscapeDataString(Project)}";
        if (!string.IsNullOrEmpty(TaskId)) path += $"&task_id={Uri.EscapeDataString(TaskId)}";
        return path;
    }
}
