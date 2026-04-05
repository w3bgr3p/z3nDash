using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nIO;

public static class CsxExecutor
{
    private sealed record CacheKey(string Path, Type GlobalsType);
    private sealed record CacheEntry(Script<object> Script, string Hash);

    private static readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();

    private static ScriptOptions BuildOptions(string scriptPath)
    {
        var scriptDir = Path.GetDirectoryName(scriptPath)!;
        return ScriptOptions.Default
            .WithFileEncoding(Encoding.UTF8)
            .WithFilePath(scriptPath)
            .WithSourceResolver(new SourceFileResolver(new[] { scriptDir }, scriptDir))
            .AddReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(System.Linq.Enumerable).Assembly,
                typeof(Newtonsoft.Json.JsonConvert).Assembly,
                typeof(Nethereum.Web3.Web3).Assembly,
                typeof(StubProject).Assembly
            )
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "Newtonsoft.Json",
                "Newtonsoft.Json.Linq",
                "z3nIO",
                "ZennoLab.InterfacesLibrary.ProjectModel",
                "ZennoLab.InterfacesLibrary.Enums.Log",
                "ZennoLab.InterfacesLibrary.Enums.Http",
                "ZennoLab.CommandCenter"
            );
    }

    public static async Task<List<string>> CompileAsync<TGlobals>(string scriptPath)
    {
        if (!File.Exists(scriptPath))
            return [$"File not found: {scriptPath}"];

        try
        {
            var code  = await File.ReadAllTextAsync(scriptPath);
            var hash  = ComputeCompositeHash(scriptPath, code);
            var key   = new CacheKey(scriptPath, typeof(TGlobals));

            if (_cache.TryGetValue(key, out var entry) && entry.Hash == hash)
                return [];

            var script = CSharpScript.Create<object>(code, BuildOptions(scriptPath), globalsType: typeof(TGlobals));
            var errors = script.Compile()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(e => e.ToString())
                .ToList();

            if (errors.Count == 0)
                _cache[key] = new CacheEntry(script, hash);

            return errors;
        }
        catch (Exception ex)
        {
            return [ex.Message];
        }
    }

    public static async Task<ScriptRunResult> RunAsync<TGlobals>(string scriptPath, TGlobals globals, CancellationToken ct)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"CSX script not found: {scriptPath}");

        var code   = await File.ReadAllTextAsync(scriptPath, ct);
        var hash   = ComputeCompositeHash(scriptPath, code);
        var script = await GetOrCompileAsync<TGlobals>(scriptPath, code, hash);
        
        try
        {
            await script.RunAsync(globals: globals, catchException: null, cancellationToken: CancellationToken.None);
            return ScriptRunResult.Ok();
        }
        catch (Exception ex)
        {
            return ScriptRunResult.Fail(ex, ExtractSourceSnippet(ex));
        }
    }

    private static async Task<Script<object>> GetOrCompileAsync<TGlobals>(string path, string code, string hash)
    {
        var key = new CacheKey(path, typeof(TGlobals));

        if (_cache.TryGetValue(key, out var entry) && entry.Hash == hash)
            return entry.Script;
        
        $"Compiling {hash}".Debug();
        var script = await Task.Run(() =>
        {
            var s = CSharpScript.Create<object>(code, BuildOptions(path), globalsType: typeof(TGlobals));

            var errors = s.Compile()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"CSX compile errors in {Path.GetFileName(path)}:\n" +
                    string.Join("\n", errors.Select(e => e.ToString())));
            return s;
        });

        _cache[key] = new CacheEntry(script, hash);
        $"Compilied {hash}".Debug();
        return script;
    }

    private static SourceSnippet? ExtractSourceSnippet(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, true);
        foreach (var frame in trace.GetFrames() ?? [])
        {
            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            if (string.IsNullOrEmpty(file) || line <= 0 || !File.Exists(file)) continue;

            var lines   = File.ReadAllLines(file);
            var from    = Math.Max(0, line - 10);
            var to      = Math.Min(lines.Length - 1, line + 10);

            return new SourceSnippet(file, line, from + 1, lines[from..(to + 1)]);
        }
        return null;
    }

    public record SourceSnippet(string File, int ErrorLine, int StartLine, string[] Lines)
    {
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"File: {File}");
            for (int i = 0; i < Lines.Length; i++)
            {
                int lineNo   = StartLine + i;
                string marker = lineNo == ErrorLine ? ">>>" : "   ";
                sb.AppendLine($"{marker} {lineNo,4}: {Lines[i]}");
            }
            return sb.ToString();
        }
    }

    public record ScriptRunResult(bool Success, Exception? Exception, SourceSnippet? Snippet)
    {
        public static ScriptRunResult Ok()                                  => new(true,  null, null);
        public static ScriptRunResult Fail(Exception ex, SourceSnippet? s) => new(false, ex,   s);
    }

    public static void Invalidate(string scriptPath)
    {
        foreach (var key in _cache.Keys.Where(k => k.Path == scriptPath).ToList())
            _cache.TryRemove(key, out _);
    }

    public static void InvalidateAll() => _cache.Clear();

    private static string ComputeCompositeHash(string scriptPath, string code)
    {
        var scriptDir = Path.GetDirectoryName(scriptPath)!;
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        sb.Append(code);
        foreach (var loadPath in ResolveLoadPaths(code, scriptDir))
            if (File.Exists(loadPath)) sb.Append(File.ReadAllText(loadPath));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static IEnumerable<string> ResolveLoadPaths(string code, string baseDir)
    {
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#load")) continue;
            var start = trimmed.IndexOf('"');
            var end   = trimmed.LastIndexOf('"');
            if (start < 0 || end <= start) continue;
            yield return Path.GetFullPath(
                Path.Combine(baseDir, trimmed.Substring(start + 1, end - start - 1)));
        }
    }
}