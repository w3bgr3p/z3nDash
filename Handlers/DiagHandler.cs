using System.Net;
using System.Text;

namespace z3nDash;

/// <summary>
/// Маршруты диагностического режима:
///   GET  /diag/state  — включён ли, куда пишет
///   POST /diag/start  — включить, новый файл в logs/diag
///   POST /diag/stop   — выключить и собрать отчёт рядом с дампом (.txt)
///   POST /diag/client — пачка событий браузера от js/diag.js
///   GET  /diag/report — отчёт по текущему или последнему дампу (?file= — имя файла в logs/diag)
/// </summary>
public sealed class DiagHandler
{
    private readonly DbConnectionService _dbService;

    public DiagHandler(DbConnectionService dbService) => _dbService = dbService;

    public static bool Matches(string path) => path.StartsWith("/diag/");

    public async Task Handle(HttpListenerContext ctx, string path, string method)
    {
        if (path == "/diag/state" && method == "GET")
        {
            await HttpHelpers.WriteJson(ctx.Response, new { enabled = DiagTrace.Enabled, file = DiagTrace.FilePath ?? "" });
            return;
        }
        if (path == "/diag/start" && method == "POST")
        {
            var mode = _dbService.TryGetDb(out var db) && db != null ? db.Mode.ToString() : "none";
            var file = DiagTrace.Start(mode);
            await HttpHelpers.WriteJson(ctx.Response, new { enabled = true, file });
            return;
        }
        if (path == "/diag/stop" && method == "POST")
        {
            var file = DiagTrace.Stop();
            var report = "";
            if (file != null)
            {
                report = Path.ChangeExtension(file, ".txt");
                await File.WriteAllTextAsync(report, DiagReport.Build(file), new UTF8Encoding(false));
            }
            await HttpHelpers.WriteJson(ctx.Response, new { enabled = false, file = file ?? "", report });
            return;
        }
        if (path == "/diag/client" && method == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var n = DiagTrace.AppendClient(await reader.ReadToEndAsync());
            await HttpHelpers.WriteJson(ctx.Response, new { accepted = n });
            return;
        }
        if (path == "/diag/report" && method == "GET")
        {
            var file = PickFile(ctx.Request.QueryString["file"]);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await HttpHelpers.WriteText(ctx.Response, file == null ? "No diagnostic dumps in " + DiagTrace.Folder : DiagReport.Build(file));
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    /// <summary>Имя из запроса — только как имя файла внутри logs/diag; иначе текущий или самый свежий дамп.</summary>
    private static string? PickFile(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var p = Path.Combine(DiagTrace.Folder, Path.GetFileName(name));
            return File.Exists(p) ? p : null;
        }
        if (DiagTrace.FilePath != null && File.Exists(DiagTrace.FilePath)) return DiagTrace.FilePath;
        if (!Directory.Exists(DiagTrace.Folder)) return null;
        return new DirectoryInfo(DiagTrace.Folder).GetFiles("diag-*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }
}
