using System.Net;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using z3nIO;

namespace z3nIO;

internal sealed class GraphHandler
{
    private static readonly string TemplatePath = Path.Combine(
        AppContext.BaseDirectory, "Templates", "graph_template.html");

    private string? _lastHtml;

    public bool Matches(string path) => path.StartsWith("/graph");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET"  && path == "/graph")          { await Serve(ctx);    return; }
        if (method == "POST" && path == "/graph/generate") { await Generate(ctx); return; }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // ── GET /graph ────────────────────────────────────────────────────────────

    private async Task Serve(HttpListenerContext ctx)
    {
        if (_lastHtml is null)
        {
            ctx.Response.StatusCode = 404;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "No graph generated yet. POST /graph/generate?repoPath=..." });
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(_lastHtml);
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ── POST /graph/generate?repoPath=... ─────────────────────────────────────

    private async Task Generate(HttpListenerContext ctx)
    {
        var inputPath = ctx.Request.QueryString["repoPath"] ?? "";

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "repoPath is required" });
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
            string graphJson;

            // single .dll file
            if (File.Exists(inputPath) && inputPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                graphJson = BuildGraphJsonFromDlls(new[] { inputPath });
            }
            // directory — detect by content
            else if (Directory.Exists(inputPath))
            {
                var dlls = Directory.EnumerateFiles(inputPath, "*.dll", SearchOption.TopDirectoryOnly).ToList();
                var cs   = Directory.EnumerateFiles(inputPath, "*.cs",  SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    .ToList();

                graphJson = dlls.Count > 0 && cs.Count == 0
                    ? BuildGraphJsonFromDlls(dlls)
                    : BuildGraphJson(inputPath);
            }
            else
            {
                ctx.Response.StatusCode = 400;
                await HttpHelpers.WriteJson(ctx.Response, new { error = "repoPath must be a .dll file or a directory" });
                return;
            }

            var template = await File.ReadAllTextAsync(TemplatePath);
            _lastHtml    = template.Replace("GRAPH_DATA_PLACEHOLDER", graphJson);

            await HttpHelpers.WriteJson(ctx.Response, new { ok = true, repoPath = inputPath });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── Graph builder — Roslyn (source) ───────────────────────────────────────

    private static string BuildGraphJson(string repoPath)
    {
        var sep     = Path.DirectorySeparatorChar;
        var csFiles = Directory.EnumerateFiles(repoPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep))
            .Where(f => !f.Contains(sep + "bin" + sep))
            .ToList();

        var trees = csFiles
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

        // short name → fully qualified name
        var symbolMap = new Dictionary<string, string>();
        foreach (var tree in trees)
        foreach (var cls in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var full = FullName(cls);
            symbolMap.TryAdd(cls.Identifier.Text, full);
        }

        var nodes     = new Dictionary<string, NodeInfo>();
        var edgeSet   = new HashSet<string>();

        foreach (var tree in trees)
        {
            var filePath = tree.FilePath;

            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var full = FullName(cls);
                var kind = cls is InterfaceDeclarationSyntax ? "interface"
                         : cls is StructDeclarationSyntax    ? "struct"
                         : cls is RecordDeclarationSyntax    ? "record"
                         : "class";
                var ns   = NsOf(cls);

                nodes[full] = new NodeInfo(full, cls.Identifier.Text, ns, kind,
                    Path.GetRelativePath(repoPath, filePath), null, null, null);

                // inheritance
                if (cls.BaseList != null)
                foreach (var bt in cls.BaseList.Types)
                {
                    var bn = bt.Type.ToString().Split('<')[0].Trim();
                    if (symbolMap.TryGetValue(bn, out var bfull) && bfull != full)
                        edgeSet.Add(full + "|" + bfull + "|inherits");
                }

                // fields
                foreach (var fd in cls.DescendantNodes().OfType<FieldDeclarationSyntax>())
                {
                    var t = fd.Declaration.Type.ToString().Split('<')[0].Trim();
                    if (symbolMap.TryGetValue(t, out var tf) && tf != full)
                        edgeSet.Add(full + "|" + tf + "|uses");

                    var fieldType = fd.Declaration.Type.ToString();
                    var fieldAccess = AccessOf(fd.Modifiers);
                    foreach (var v in fd.Declaration.Variables)
                    {
                        var fid = full + "::field::" + v.Identifier.Text;
                        nodes[fid] = new NodeInfo(fid, v.Identifier.Text, ns, "field",
                            Path.GetRelativePath(repoPath, filePath), full,
                            fieldType + " " + v.Identifier.Text, fieldAccess);
                        edgeSet.Add(full + "|" + fid + "|contains");
                    }
                }

                // properties
                foreach (var pd in cls.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                {
                    var t = pd.Type.ToString().Split('<')[0].Trim();
                    if (symbolMap.TryGetValue(t, out var tf) && tf != full)
                        edgeSet.Add(full + "|" + tf + "|uses");

                    var pid = full + "::field::" + pd.Identifier.Text;
                    nodes[pid] = new NodeInfo(pid, pd.Identifier.Text, ns, "field",
                        Path.GetRelativePath(repoPath, filePath), full,
                        pd.Type.ToString() + " " + pd.Identifier.Text + " { get; }", AccessOf(pd.Modifiers));
                    edgeSet.Add(full + "|" + pid + "|contains");
                }

                // methods
                foreach (var md in cls.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var rt = md.ReturnType.ToString().Split('<')[0].Trim();
                    if (symbolMap.TryGetValue(rt, out var rtf) && rtf != full)
                        edgeSet.Add(full + "|" + rtf + "|uses");

                    foreach (var p in md.ParameterList.Parameters)
                    {
                        var pt = p.Type?.ToString().Split('<')[0].Trim() ?? "";
                        if (symbolMap.TryGetValue(pt, out var ptf) && ptf != full)
                            edgeSet.Add(full + "|" + ptf + "|uses");
                    }

                    var paramStr  = string.Join(", ", md.ParameterList.Parameters
                        .Select(p => (p.Type?.ToString() ?? "") + " " + p.Identifier.Text));
                    var methodId  = full + "::method::" + md.Identifier.Text + "(" + paramStr + ")";
                    nodes[methodId] = new NodeInfo(methodId, md.Identifier.Text, ns, "method",
                        Path.GetRelativePath(repoPath, filePath), full,
                        md.ReturnType.ToString() + " " + md.Identifier.Text + "(" + paramStr + ")",
                        AccessOf(md.Modifiers));
                    edgeSet.Add(full + "|" + methodId + "|contains");
                }

                // constructors — parameter type edges only
                foreach (var cd in cls.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
                foreach (var p in cd.ParameterList.Parameters)
                {
                    var pt = p.Type?.ToString().Split('<')[0].Trim() ?? "";
                    if (symbolMap.TryGetValue(pt, out var ptf) && ptf != full)
                        edgeSet.Add(full + "|" + ptf + "|uses");
                }

                // object creations
                foreach (var oc in cls.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var t = oc.Type.ToString().Split('<')[0].Trim();
                    if (symbolMap.TryGetValue(t, out var tf) && tf != full)
                        edgeSet.Add(full + "|" + tf + "|creates");
                }
            }
        }

        var edgeList = edgeSet.Select(e => { var p = e.Split('|'); return new { source = p[0], target = p[1], kind = p[2] }; });

        return JsonSerializer.Serialize(new
        {
            nodes = nodes.Values.Select(n => new
            {
                id        = n.Id,
                label     = n.Label,
                ns        = n.Ns,
                kind      = n.Kind,
                file      = n.File,
                owner     = n.Owner,
                signature = n.Signature,
                access    = n.Access
            }),
            edges = edgeList
        });
    }

    // ── Graph builder — Reflection (dll) ─────────────────────────────────────

    private static string BuildGraphJsonFromDlls(IEnumerable<string> dllPaths)
    {
        var dllList = dllPaths.ToList();

        // resolver: dll directory + runtime directory
        var resolverPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dllList)
        {
            var dir = Path.GetDirectoryName(dll);
            if (dir != null) resolverPaths.Add(dir);
        }
        resolverPaths.Add(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

        var allPaths = resolverPaths
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolver = new PathAssemblyResolver(allPaths);

        using var mlc = new System.Reflection.MetadataLoadContext(resolver);

        // build a set of all type full-names across loaded assemblies for edge resolution
        var assemblies = dllList
            .Select(p => { try { return mlc.LoadFromAssemblyPath(p); } catch { return null; } })
            .Where(a => a != null)
            .ToList();

        var typeMap = new Dictionary<string, string>(); // short name → full name
        foreach (var asm in assemblies)
        foreach (var t in SafeGetTypes(asm!))
            typeMap.TryAdd(t.Name, t.FullName ?? t.Name);

        var nodes   = new Dictionary<string, NodeInfo>();
        var edgeSet = new HashSet<string>();

        foreach (var asm in assemblies)
        foreach (var type in SafeGetTypes(asm!))
        {
            if (type.FullName == null) continue;

            var full = type.FullName;
            var kind = type.IsInterface ? "interface"
                     : type.IsValueType ? "struct"
                     : "class";
            var ns   = type.Namespace ?? "";
            var file = asm!.GetName().Name + ".dll";

            nodes[full] = new NodeInfo(full, type.Name, ns, kind, file, null, null, null);

            // inheritance / interface implementation
            if (type.BaseType != null && type.BaseType.FullName != "System.Object")
            {
                var bn = type.BaseType.FullName ?? type.BaseType.Name;
                if (nodes.ContainsKey(bn) || typeMap.ContainsValue(bn))
                    edgeSet.Add(full + "|" + bn + "|inherits");
            }
            foreach (var iface in type.GetInterfaces())
            {
                var iname = iface.FullName ?? iface.Name;
                if (typeMap.ContainsValue(iname))
                    edgeSet.Add(full + "|" + iname + "|inherits");
            }

            // fields
            foreach (var f in type.GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static   |
                System.Reflection.BindingFlags.Public   |
                System.Reflection.BindingFlags.NonPublic))
            {
                if (f.DeclaringType?.FullName != full) continue;

                var access = FieldAccess(f);
                var fid    = full + "::field::" + f.Name;
                var ftype  = SafeTypeName(f.FieldType);
                nodes[fid] = new NodeInfo(fid, f.Name, ns, "field", file, full,
                    ftype + " " + f.Name, access);
                edgeSet.Add(full + "|" + fid + "|contains");

                var ftFull = f.FieldType.FullName ?? f.FieldType.Name;
                if (typeMap.ContainsValue(ftFull) && ftFull != full)
                    edgeSet.Add(full + "|" + ftFull + "|uses");
            }

            // properties
            foreach (var p in type.GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static   |
                System.Reflection.BindingFlags.Public   |
                System.Reflection.BindingFlags.NonPublic))
            {
                if (p.DeclaringType?.FullName != full) continue;

                var getter = p.GetGetMethod(nonPublic: true);
                var access = getter != null ? MethodAccess(getter) : "private";
                var pid    = full + "::field::" + p.Name;
                var ptype  = SafeTypeName(p.PropertyType);
                nodes[pid] = new NodeInfo(pid, p.Name, ns, "field", file, full,
                    ptype + " " + p.Name + " { get; }", access);
                edgeSet.Add(full + "|" + pid + "|contains");

                var ptFull = p.PropertyType.FullName ?? p.PropertyType.Name;
                if (typeMap.ContainsValue(ptFull) && ptFull != full)
                    edgeSet.Add(full + "|" + ptFull + "|uses");
            }

            // methods
            foreach (var m in type.GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static   |
                System.Reflection.BindingFlags.Public   |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue; // skip get_/set_/add_/remove_
                if (m.DeclaringType?.FullName != full) continue;

                var access   = MethodAccess(m);
                var paramStr = string.Join(", ", m.GetParameters()
                    .Select(p => SafeTypeName(p.ParameterType) + " " + p.Name));
                var retType  = SafeTypeName(m.ReturnType);
                var mid      = full + "::method::" + m.Name + "(" + paramStr + ")";
                nodes[mid]   = new NodeInfo(mid, m.Name, ns, "method", file, full,
                    retType + " " + m.Name + "(" + paramStr + ")", access);
                edgeSet.Add(full + "|" + mid + "|contains");

                var rtFull = m.ReturnType.FullName ?? m.ReturnType.Name;
                if (typeMap.ContainsValue(rtFull) && rtFull != full)
                    edgeSet.Add(full + "|" + rtFull + "|uses");

                foreach (var p in m.GetParameters())
                {
                    var ptFull = p.ParameterType.FullName ?? p.ParameterType.Name;
                    if (typeMap.ContainsValue(ptFull) && ptFull != full)
                        edgeSet.Add(full + "|" + ptFull + "|uses");
                }
            }
        }

        // remove edges to nodes that don't exist in our graph
        var edgeList = edgeSet
            .Select(e => { var p = e.Split('|'); return new { source = p[0], target = p[1], kind = p[2] }; })
            .Where(e => nodes.ContainsKey(e.source) && nodes.ContainsKey(e.target));

        return JsonSerializer.Serialize(new
        {
            nodes = nodes.Values.Select(n => new
            {
                id        = n.Id,
                label     = n.Label,
                ns        = n.Ns,
                kind      = n.Kind,
                file      = n.File,
                owner     = n.Owner,
                signature = n.Signature,
                access    = n.Access
            }),
            edges = edgeList
        });
    }

    private static IEnumerable<System.Reflection.TypeInfo> SafeGetTypes(System.Reflection.Assembly asm)
    {
        try { return asm.DefinedTypes.ToList(); }
        catch { return []; }
    }

    private static string SafeTypeName(System.Reflection.MemberInfo type)
    {
        try
        {
            if (type is System.Reflection.TypeInfo ti)
            {
                if (ti.IsGenericType)
                {
                    var args = string.Join(", ", ti.GenericTypeArguments.Select(a => a.Name));
                    return ti.Name.Split('`')[0] + "<" + args + ">";
                }
                return ti.Name;
            }
            return type.Name;
        }
        catch { return "?"; }
    }

    private static string SafeTypeName(System.Type type)
    {
        try
        {
            if (type.IsGenericType)
            {
                var args = string.Join(", ", type.GenericTypeArguments.Select(a => a.Name));
                return type.Name.Split('`')[0] + "<" + args + ">";
            }
            return type.Name;
        }
        catch { return "?"; }
    }

    private static string MethodAccess(System.Reflection.MethodBase m)
    {
        if (m.IsPublic)   return "public";
        if (m.IsFamily)   return "protected";
        if (m.IsAssembly) return "internal";
        return "private";
    }

    private static string FieldAccess(System.Reflection.FieldInfo f)
    {
        if (f.IsPublic)   return "public";
        if (f.IsFamily)   return "protected";
        if (f.IsAssembly) return "internal";
        return "private";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string AccessOf(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))    return "public";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return "protected";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))  return "internal";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))   return "private";
        return "private"; // default in C# for class members
    }

    private static string NsOf(BaseTypeDeclarationSyntax cls)
    {
        var parts = cls.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .Reverse();
        return string.Join(".", parts);
    }

    private static string FullName(BaseTypeDeclarationSyntax cls)
    {
        var ns = NsOf(cls);
        return ns.Length > 0 ? ns + "." + cls.Identifier.Text : cls.Identifier.Text;
    }

    private record NodeInfo(string Id, string Label, string Ns, string Kind,
        string File, string? Owner, string? Signature, string? Access);
}