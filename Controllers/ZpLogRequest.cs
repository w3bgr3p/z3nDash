namespace z3nDash;

public sealed record ZpLogRequest(string Kind, string Process, int Limit, string Project)
{
    public static bool TryCreate(
        string? kind,
        string? process,
        string? limit,
        string? project,
        out ZpLogRequest request,
        out string error)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "execution" : kind.Trim().ToLowerInvariant();
        var normalizedProcess = string.IsNullOrWhiteSpace(process) ? "ZennoPoster" : process.Trim();

        request = new ZpLogRequest("execution", "ZennoPoster", 200, "");
        error = "";

        if (normalizedKind is not ("execution" or "errors" or "critical"))
        {
            error = "kind must be execution, errors or critical";
            return false;
        }

        if (!normalizedProcess.Equals("ZennoPoster", StringComparison.OrdinalIgnoreCase)
            && !normalizedProcess.Equals("ProjectMaker", StringComparison.OrdinalIgnoreCase))
        {
            error = "process must be ZennoPoster or ProjectMaker";
            return false;
        }

        var parsedLimit = int.TryParse(limit, out var value) ? value : 200;
        parsedLimit = Math.Clamp(parsedLimit, 1, 2000);
        normalizedProcess = normalizedProcess.Equals("ProjectMaker", StringComparison.OrdinalIgnoreCase)
            ? "ProjectMaker"
            : "ZennoPoster";

        var normalizedProject = normalizedKind == "execution" ? project?.Trim() ?? "" : "";
        request = new ZpLogRequest(normalizedKind, normalizedProcess, parsedLimit, normalizedProject);
        return true;
    }

    public string BuildPath()
    {
        var path = $"/log?kind={Uri.EscapeDataString(Kind)}&process={Uri.EscapeDataString(Process)}&n={Limit}";
        if (!string.IsNullOrEmpty(Project)) path += $"&project={Uri.EscapeDataString(Project)}";
        return path;
    }
}
