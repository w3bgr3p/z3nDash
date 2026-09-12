using System.Diagnostics;

namespace z3nDash;

/// <summary>
/// Каталог зависимостей рядом со скриптом. Интерпретатор остаётся системным:
/// «python -m venv» кладёт в каталог только лаунчер, а стандартную библиотеку
/// venv берёт из базовой установки по home= в pyvenv.cfg. Изоляция идёт
/// исключительно по site-packages.
/// </summary>
public static class PythonEnv
{
    private static readonly string[] Candidates = [".venv", "venv", "env"];

    public static string FolderOf(string scriptPath)
        => Directory.Exists(scriptPath) ? scriptPath : Path.GetDirectoryName(scriptPath) ?? "";

    /// <summary>Существующий venv рядом со скриптом, иначе null.</summary>
    public static string? Find(string scriptPath)
    {
        var folder = FolderOf(scriptPath);
        if (!Directory.Exists(folder)) return null;

        foreach (var name in Candidates)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(InterpreterIn(path))) return path;
        }
        return null;
    }

    /// <summary>Путь к интерпретатору внутри venv — на Windows и на POSIX он лежит по-разному.</summary>
    public static string InterpreterIn(string venvPath)
        => OperatingSystem.IsWindows()
            ? Path.Combine(venvPath, "Scripts", "python.exe")
            : Path.Combine(venvPath, "bin", "python");

    /// <summary>Чем запускать скрипт: интерпретатором venv, если он включён и найден, иначе системным.</summary>
    public static string Resolve(string scriptPath, bool useVenv)
    {
        if (!useVenv) return "python";
        var venv = Find(scriptPath);
        return venv != null ? InterpreterIn(venv) : "python";
    }

    /// <summary>
    /// Создаёт venv, если его ещё нет. Возвращает путь к интерпретатору;
    /// строки процесса уходят в log, чтобы создание было видно в output задачи.
    /// </summary>
    public static string Ensure(string scriptPath, Action<string>? log = null)
    {
        var existing = Find(scriptPath);
        if (existing != null) return InterpreterIn(existing);

        var folder = FolderOf(scriptPath);
        if (!Directory.Exists(folder))
        {
            log?.Invoke($"[ERR] folder not found: {folder}");
            return "python";
        }

        var target = Path.Combine(folder, "venv");
        log?.Invoke($"creating venv at {target}");

        var psi = new ProcessStartInfo
        {
            FileName               = "python",
            Arguments              = "-m venv venv",
            WorkingDirectory       = folder,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            log?.Invoke("[ERR] cannot start python");
            return "python";
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        foreach (var line in (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            log?.Invoke(line.TrimEnd());

        var interpreter = InterpreterIn(target);
        if (!File.Exists(interpreter))
        {
            log?.Invoke("[ERR] venv creation failed");
            return "python";
        }

        log?.Invoke("venv ready");
        return interpreter;
    }
}
