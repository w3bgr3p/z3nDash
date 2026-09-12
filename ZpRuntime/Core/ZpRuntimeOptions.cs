namespace z3nDash;

/// <summary>
/// Настройки ZpRuntime, которые проставляет хост-приложение при старте.
/// Нужны, чтобы рантайм не зависел от <see cref="Config"/> — иначе получается
/// цикл ZpRuntime → z3nDash → ZpRuntime.
/// </summary>
public static class ZpRuntimeOptions
{
    private static string _logsFolder = string.Empty;

    /// <summary>
    /// Каталог логов. Пусто → &lt;BaseDirectory&gt;/logs, тот же запасной путь,
    /// что считает EmbeddedServer.
    /// </summary>
    public static string LogsFolder
    {
        get => string.IsNullOrWhiteSpace(_logsFolder)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : _logsFolder;
        set => _logsFolder = value ?? string.Empty;
    }
}
