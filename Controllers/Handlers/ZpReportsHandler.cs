using System.Net;

namespace z3n8;

// GET /report/api/projects-db
// Возвращает данные по всем таблицам __* из БД в том же формате что и /report/api/all → projects
// { "projects": { "ProjectName": { "name": "...", "timestamp": "...", "accounts": { "id": { "status", "timestamp", "completionSec", "report" } } } } }

internal sealed class ProjectReportHandler
{
    private readonly DbConnectionService _dbService;

    public ProjectReportHandler(DbConnectionService dbService)
    {
        _dbService = dbService;
    }

    public bool Matches(string path, string method) =>
        method == "GET" && path == "/report/api/projects-db";

    public async Task Handle(HttpListenerContext ctx)
    {
        if (!_dbService.TryGetDb(out var db))
        {
            ctx.Response.StatusCode = 503;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "Database not connected" });
            return;
        }

        var projects = ReadProjectsFromDb(db!);
        await HttpHelpers.WriteJson(ctx.Response, new { projects });
    }

    private static Dictionary<string, object> ReadProjectsFromDb(Db db)
    {
        var result = new Dictionary<string, object>();

        var allTables = db.GetTables();
        var projectTables = allTables
            .Where(t => t.StartsWith("__") && !t.StartsWith("__|"))
            .ToList();

        foreach (var tableName in projectTables)
        {
            var projectName = tableName.Replace("__", "");
            var accounts = ReadProjectAccounts(db, tableName);

            result[projectName] = new
            {
                name      = projectName,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                accounts
            };
        }

        return result;
    }

    private static Dictionary<string, object> ReadProjectAccounts(Db db, string tableName)
    {
        var accounts = new Dictionary<string, object>();

        var lines = db.GetLines(
            "id, last",
            tableName: tableName,
            where: "\"last\" LIKE '+ %' OR \"last\" LIKE '- %'"
        );

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var columns = line.Split('¦');
            if (columns.Length < 2) continue;

            var id       = columns[0].Trim();
            var lastData = columns[1];

            if (string.IsNullOrWhiteSpace(lastData)) continue;

            var rows  = lastData.Split('\n');
            var parts = rows[0].Split(' ');
            if (parts.Length < 2) continue;

            accounts[id] = new
            {
                status        = parts[0].Trim(),
                timestamp     = parts.Length >= 2 ? parts[1].Trim() : "",
                completionSec = parts.Length >= 3 ? parts[2].Trim() : "",
                report        = rows.Length > 1 ? string.Join("\n", rows.Skip(1)).Trim() : ""
            };
        }

        return accounts;
    }
}