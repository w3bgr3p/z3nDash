using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

internal sealed class ReportHandler
{
    private readonly string _reportsPath;
    private readonly string _wwwrootPath;

    public ReportHandler(string reportsPath, string wwwrootPath)
    {
        _reportsPath = reportsPath;
        _wwwrootPath = wwwrootPath;
    }

    public async Task Handle(HttpListenerContext ctx, string path)
    {
        if (path == "/report" || path == "/report/")
        {
            await ServePage(ctx.Response, "report");
            return;
        }

        if (path.StartsWith("/report/api/") && ctx.Request.HttpMethod == "GET")
        {
            await HandleApi(ctx, path);
            return;
        }

        if (path.StartsWith("/report/") && ctx.Request.HttpMethod == "GET")
        {
            await ServeFile(ctx.Response, ctx.Request.Url!.AbsolutePath.Substring("/report/".Length), _reportsPath);
        }
    }

    private async Task HandleApi(HttpListenerContext ctx, string path)
    {
        string ExtractJsVar(string js) { int eq = js.IndexOf('='); return eq < 0 ? js : js.Substring(eq + 1).Trim().TrimEnd(';'); }
        string ReadJs(string file)     { string p = Path.Combine(_reportsPath, file); return File.Exists(p) ? ExtractJsVar(File.ReadAllText(p, Encoding.UTF8)) : "null"; }

        string apiPath = path.Substring("/report/api".Length).TrimEnd('/');

        switch (apiPath)
        {
            case "/metadata": await HttpHelpers.WriteRawJson(ctx.Response, ReadJs("metadata.js")); break;
            case "/social":   await HttpHelpers.WriteRawJson(ctx.Response, ReadJs("social.js"));   break;
            case "/projects":
            {
                string projDir = Path.Combine(_reportsPath, "projects");
                var names = Directory.Exists(projDir)
                    ? Directory.GetFiles(projDir, "*.js").Select(f => Path.GetFileNameWithoutExtension(f)).ToArray()
                    : Array.Empty<string>();
                await HttpHelpers.WriteJson(ctx.Response, names);
                break;
            }
            case "/project":
            {
                string name = (ctx.Request.QueryString["name"] ?? "").Replace("..", "").Replace("/", "").Replace("\\", "");
                await HttpHelpers.WriteRawJson(ctx.Response, ReadJs($"projects/{name}.js"));
                break;
            }
            case "/process":
            {
                string name = (ctx.Request.QueryString["name"] ?? "").Replace("..", "").Replace("/", "").Replace("\\", "");
                await HttpHelpers.WriteRawJson(ctx.Response, ReadJs($"process_{name}.js"));
                break;
            }
            case "/all":
            {
                string metaJson = ReadJs("metadata.js"), socialJson = ReadJs("social.js");
                var projectsDict = new Dictionary<string, object>(); var processesDict = new Dictionary<string, object>();
                try
                {
                    var meta = JsonSerializer.Deserialize<JsonElement>(metaJson);
                    if (meta.TryGetProperty("projects", out var pa))
                        foreach (var pn in pa.EnumerateArray()) { string n = pn.GetString() ?? ""; string j = ReadJs($"projects/{new string(n.Where(char.IsLetterOrDigit).ToArray())}.js"); if (j != "null") projectsDict[n] = JsonSerializer.Deserialize<JsonElement>(j); }
                    if (meta.TryGetProperty("machines", out var ma))
                        foreach (var mn in ma.EnumerateArray()) { string n = mn.GetString() ?? ""; string j = ReadJs($"process_{n}.js"); if (j != "null") processesDict[n] = JsonSerializer.Deserialize<JsonElement>(j); }
                }
                catch { }
                await HttpHelpers.WriteJson(ctx.Response, new { metadata = JsonSerializer.Deserialize<JsonElement>(metaJson), social = JsonSerializer.Deserialize<JsonElement>(socialJson), projects = projectsDict, processes = processesDict });
                break;
            }
            default: ctx.Response.StatusCode = 404; ctx.Response.Close(); break;
        }
    }

    private async Task ServePage(HttpListenerResponse response, string page)
    {
        page = Path.GetFileName(page).Replace("..", "");
        if (!page.EndsWith(".html")) page += ".html";
        await ServeFile(response, page, _wwwrootPath);
    }

    private static async Task ServeFile(HttpListenerResponse response, string relativePath, string root)
    {
        relativePath = relativePath.Replace("..", "").Replace("\\", "/").TrimStart('/');
        string filePath = Path.Combine(root, relativePath);

        if (!File.Exists(filePath) && string.IsNullOrEmpty(Path.GetExtension(filePath)))
            filePath += ".html";

        if (!File.Exists(filePath))
        {
            response.StatusCode = 404;
            var msg = Encoding.UTF8.GetBytes($"Not found: {filePath}");
            response.ContentType = "text/plain; charset=utf-8";
            await response.OutputStream.WriteAsync(msg);
            Console.WriteLine($"[404] Not found: {filePath}");
            return;
        }

        string ext = Path.GetExtension(filePath).ToLower();
        response.ContentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png"  => "image/png",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream"
        };

        byte[] buf = await File.ReadAllBytesAsync(filePath);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf);
    }
}
