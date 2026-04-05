using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace z3nIO;

internal sealed class DocsGraphHandler
{
    private static readonly string TemplatePath = Path.Combine(
        AppContext.BaseDirectory, "Templates", "docs_graph_template.html");
    
    private static readonly string ExportTemplatePath = Path.Combine(
        AppContext.BaseDirectory, "Templates", "docs_graph_export_template.html");

    private string? _lastHtml;
    private string? _lastGraphJson;

    public bool Matches(string path) => path.StartsWith("/docs-graph");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET" && (path == "/docs-graph" || path == "/docs" || path == "/docs/"))
        { await Serve(ctx); return; }
        
        if (method == "POST" && path == "/docs-graph/generate") 
        { await Generate(ctx); return; }
        
        if (method == "GET" && path == "/docs-graph/export")
        {
            if (_lastGraphJson is null)
            {
                ctx.Response.StatusCode = 404;
                await HttpHelpers.WriteJson(ctx.Response, new { error = "No graph generated yet" });
                return;
            }

            var template = await File.ReadAllTextAsync(ExportTemplatePath);
            var export   = template.Replace("DOCS_GRAPH_DATA_PLACEHOLDER", _lastGraphJson);

            var bytes = Encoding.UTF8.GetBytes(export);
            ctx.Response.StatusCode      = 200;
            ctx.Response.ContentType     = "text/html; charset=utf-8";
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"docs.html\"";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ── GET /docs-graph ───────────────────────────────────────────────────────

    private async Task Serve(HttpListenerContext ctx)
    {
        string html;
        var template = await File.ReadAllTextAsync(TemplatePath);
        if (_lastHtml is null)
        {
            html = template
                .Replace("DOCS_GRAPH_DATA_PLACEHOLDER", "{\"nodes\":[],\"edges\":[]}")
                .Replace("vault path…", "vault path…\" value=\"docs-vault");
        }
        else
        {
            html = _lastHtml;
        }
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode      = 200;
        ctx.Response.ContentType     = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ── POST /docs-graph/generate?vaultPath=... ───────────────────────────────

    private async Task Generate(HttpListenerContext ctx)
    {
        var vaultPath = ctx.Request.QueryString["vaultPath"] ?? "";

        if (string.IsNullOrWhiteSpace(vaultPath))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "vaultPath is required" });
            return;
        }
        if (!Path.IsPathRooted(vaultPath))
            vaultPath = Path.Combine(AppContext.BaseDirectory, vaultPath);
            vaultPath = Path.Combine(AppContext.BaseDirectory, vaultPath);

        if (!Directory.Exists(vaultPath))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = $"Directory not found: {vaultPath}" });
            return;
        }

        if (!File.Exists(TemplatePath))
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = $"Template not found: {TemplatePath}" });
            return;
        }

        try
        {
            var graphJson = BuildGraphJson(vaultPath);
            
            graphJson = graphJson.Replace("</script>", "<\\/script>");

            var template  = await File.ReadAllTextAsync(TemplatePath);
            _lastGraphJson = graphJson;
            _lastHtml      = template.Replace("DOCS_GRAPH_DATA_PLACEHOLDER", graphJson);

            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, vaultPath });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    private static readonly Regex WikiLinkRx  = new(@"\[\[([^\]|#\n]+)", RegexOptions.Compiled);
    private static readonly Regex TagLineRx   = new(@"^tags\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TagItemRx   = new(@"[-\s]*(\S+)", RegexOptions.Compiled);
    private static readonly Regex TitleLineRx = new(@"^title\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static string BuildGraphJson(string vaultPath)
    {
        var mdFiles = Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".obsidian" + Path.DirectorySeparatorChar))
            .ToList();

        // pass 1: build id map  label → id
        var labelToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in mdFiles)
        {
            var id = Path.GetRelativePath(vaultPath, file).Replace('\\', '/');
            var label = Path.GetFileNameWithoutExtension(file);
            labelToId.TryAdd(label, id);
        }

        var nodes   = new List<object>();
        var edgeSet = new HashSet<string>();

        // pass 2: parse each file
        foreach (var file in mdFiles)
        {
            var id      = Path.GetRelativePath(vaultPath, file).Replace('\\', '/');
            var label   = Path.GetFileNameWithoutExtension(file);
            var raw     = File.ReadAllText(file);

            var (frontmatter, body) = SplitFrontmatter(raw);

            var title = string.IsNullOrEmpty(frontmatter) ? null
                : TitleLineRx.Match(frontmatter) is { Success: true } tm
                    ? tm.Groups[1].Value.Trim().Trim('"')
                    : null;

            var tags = ParseTags(frontmatter);

            nodes.Add(new
            {
                id,
                label,
                title,
                tags,
                path  = id,
                content = raw
            });

            // wikilink edges
            foreach (Match m in WikiLinkRx.Matches(body))
            {
                var target = m.Groups[1].Value.Trim();
                if (labelToId.TryGetValue(target, out var targetId) && targetId != id)
                    edgeSet.Add(id + "|" + targetId + "|wikilink");
            }

            // tag edges
            foreach (var tag in tags)
                edgeSet.Add("tag::" + tag + "|" + id + "|tag");
            
            // folder edges
            var parts = id.Split('/');
            if (parts.Length > 1)
            {
                var folderPath = string.Join("/", parts[..^1]);
                var folderId   = "folder::" + folderPath;
                edgeSet.Add(folderId + "|" + id + "|folder");
            }
            
            
        }

        // tag nodes (virtual)
        var tagNodes = edgeSet
            .Where(e => e.StartsWith("tag::"))
            .Select(e => e.Split('|')[0])
            .Distinct()
            .Select(tid => (object)new { id = tid, label = tid.Replace("tag::", "#"), title = (string?)null, tags = Array.Empty<string>(), path = "", content = "" });

        var folderNodes = edgeSet
            .Where(e => e.StartsWith("folder::"))
            .Select(e => e.Split('|')[0])
            .Distinct()
            .Select(fid => (object)new {
                id      = fid,
                label   = fid.Replace("folder::", "").Split('/').Last(),
                title   = (string?)null,
                tags    = Array.Empty<string>(),
                path    = "",
                content = ""
            });

        var allNodes = nodes.Concat(tagNodes).Concat(folderNodes).ToList();

        var edges = edgeSet.Select(e =>
        {
            var p = e.Split('|');
            return new { source = p[0], target = p[1], kind = p[2] };
        });

        return JsonSerializer.Serialize(new { nodes = allNodes, edges });
    }

    private static (string frontmatter, string body) SplitFrontmatter(string raw)
    {
        if (!raw.StartsWith("---")) return ("", raw);
        var end = raw.IndexOf("\n---", 3);
        if (end < 0) return ("", raw);
        return (raw[3..end].Trim(), raw[(end + 4)..]);
    }

    private static List<string> ParseTags(string frontmatter)
    {
        if (string.IsNullOrEmpty(frontmatter)) return [];

        var m = TagLineRx.Match(frontmatter);
        if (!m.Success) return [];

        var val = m.Groups[1].Value.Trim();

        // inline: tags: [a, b, c]
        if (val.StartsWith('['))
            return val.Trim('[', ']').Split(',')
                .Select(t => t.Trim().Trim('"'))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

        // multiline list follows — scan lines after "tags:"
        var idx   = frontmatter.IndexOf(m.Value);
        var after = frontmatter[(idx + m.Value.Length)..];
        var tags  = new List<string>();
        foreach (var line in after.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith('-')) break;
            var tag = l.TrimStart('-').Trim().Trim('"');
            if (!string.IsNullOrEmpty(tag)) tags.Add(tag);
        }
        return tags.Count > 0 ? tags : [];
    }
}