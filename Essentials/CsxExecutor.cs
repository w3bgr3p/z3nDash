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
    private sealed record CacheEntry(Script<object> Script, string Hash);

    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

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
                "ZennoLab.CommandCenter"
            );
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static async Task<List<string>> CompileAsync(string scriptPath)
    {
        if (!File.Exists(scriptPath))
            return new List<string> { $"File not found: {scriptPath}" };

        try
        {
            var code = await File.ReadAllTextAsync(scriptPath);
            var hash = ComputeCompositeHash(scriptPath, code);

            if (_cache.TryGetValue(scriptPath, out var entry) && entry.Hash == hash)
                return new List<string>();

            var script = CSharpScript.Create<object>(
                code,
                BuildOptions(scriptPath),
                globalsType: typeof(CsxGlobals));

            var diagnostics = script.Compile();
            var errors = diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(e => e.ToString())
                .ToList();

            if (errors.Count == 0)
                _cache[scriptPath] = new CacheEntry(script, hash);

            return errors;
        }
        catch (Exception ex)
        {
            return new List<string> { ex.Message };
        }
    }

    public static async Task<ScriptRunResult> RunAsync(string scriptPath, CsxGlobals globals, CancellationToken ct)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"CSX script not found: {scriptPath}");

        var code   = await File.ReadAllTextAsync(scriptPath, ct);
        var hash   = ComputeCompositeHash(scriptPath, code);
        var script = GetOrCompile(scriptPath, code, hash);

        try
        {
            await script.RunAsync(globals: globals, catchException: null, cancellationToken: ct);
            return ScriptRunResult.Ok();
        }
        catch (Exception ex)
        {
            var snippet = ExtractSourceSnippet(ex);
            return ScriptRunResult.Fail(ex, snippet);
        }
    }
    private static SourceSnippet? ExtractSourceSnippet(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, true);
        foreach (var frame in trace.GetFrames() ?? [])
        {
            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            if (string.IsNullOrEmpty(file) || line <= 0) continue;
            if (!File.Exists(file)) continue;

            var lines   = File.ReadAllLines(file);
            var from    = Math.Max(0, line - 10);
            var to      = Math.Min(lines.Length - 1, line + 10);
            var snippet = lines[from..(to + 1)];

            return new SourceSnippet(file, line, from + 1, snippet);
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
                int lineNo = StartLine + i;
                string marker = lineNo == ErrorLine ? ">>>" : "   ";
                sb.AppendLine($"{marker} {lineNo,4}: {Lines[i]}");
            }
            return sb.ToString();
        }
    }

    public record ScriptRunResult(bool Success, Exception? Exception, SourceSnippet? Snippet)
    {
        public static ScriptRunResult Ok()                                    => new(true,  null, null);
        public static ScriptRunResult Fail(Exception ex, SourceSnippet? s)   => new(false, ex,   s);
    }
    
    // ── Cache ─────────────────────────────────────────────────────────────────

    private static Script<object> GetOrCompile(string path, string code, string hash)
    {
        if (_cache.TryGetValue(path, out var entry) && entry.Hash == hash)
            return entry.Script;

        var script = CSharpScript.Create<object>(
            code,
            BuildOptions(path),
            globalsType: typeof(CsxGlobals));

        var diagnostics = script.Compile();
        var errors = diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"CSX compile errors in {Path.GetFileName(path)}:\n" +
                string.Join("\n", errors.Select(e => e.ToString())));

        _cache[path] = new CacheEntry(script, hash);
        return script;
    }

    // ── Hash — учитывает содержимое всех #load файлов ─────────────────────────

    /// <summary>
    /// SHA256 от содержимого самого скрипта + всех файлов подключённых через #load.
    /// Изменение ClashOfCoins.cs инвалидирует кэш скрипта который его #load-ит.
    /// </summary>
    private static string ComputeCompositeHash(string scriptPath, string code)
    {
        var scriptDir = Path.GetDirectoryName(scriptPath)!;
        using var sha = SHA256.Create();

        var sb = new StringBuilder();
        sb.Append(code);

        foreach (var loadPath in ResolveLoadPaths(code, scriptDir))
        {
            if (File.Exists(loadPath))
                sb.Append(File.ReadAllText(loadPath));
        }

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

            var relative = trimmed.Substring(start + 1, end - start - 1);
            yield return Path.GetFullPath(Path.Combine(baseDir, relative));
        }
    }

    // ── Cache management ──────────────────────────────────────────────────────

    public static void Invalidate(string scriptPath)    => _cache.TryRemove(scriptPath, out _);
    public static void InvalidateAll()                  => _cache.Clear();
}