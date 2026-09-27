using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Параметры командной строки python-скрипта. Скрипт не запускается: его
/// разбирает sdk/python/_z3n_script_params.py через ast, наружу — JSON как есть.
/// </summary>
public static class ScriptParams
{
    private const int TimeoutMs = 15_000;

    public static string InspectorPath
        => Path.Combine(AppContext.BaseDirectory, "sdk", "python", "_z3n_script_params.py");

    public static async Task<JsonElement> InspectAsync(string scriptPath, bool useVenv)
    {
        if (!File.Exists(InspectorPath))
            return Fail("inspector", $"file not found: {InspectorPath}");
        if (!File.Exists(scriptPath))
            return Fail("script", $"file not found: {scriptPath}");

        var psi = new ProcessStartInfo
        {
            // Тот же интерпретатор, что и при запуске: синтаксис зависит от версии python.
            FileName               = PythonEnv.Resolve(scriptPath, useVenv),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        // -I: без PYTHONPATH и пользовательского site — разборщику хватает стандартной библиотеки.
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(InspectorPath);
        psi.ArgumentList.Add(scriptPath);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return Fail("start", $"{ex.GetType().Name}: {ex.Message} ({psi.FileName})"); }
        if (proc == null) return Fail("start", $"Process.Start returned null ({psi.FileName})");

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeoutMs);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
                return Fail("timeout", $"no result after {TimeoutMs / 1000} s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(stdout);
            }
            catch (JsonException)
            {
                var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return Fail("output", $"exit {proc.ExitCode}: {Short(text)}");
            }
        }
    }

    private static JsonElement Fail(string step, string error)
        => JsonSerializer.SerializeToElement(new { ok = false, step, error });

    private static string Short(string text, int limit = 1500)
    {
        text = text.Trim();
        return text.Length <= limit ? text : text[..limit] + "...";
    }
}
