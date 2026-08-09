namespace DevDeck;

/// <summary>
/// Настройки ZpRuntime, которые проставляет хост-приложение при старте.
/// Нужны, чтобы рантайм не зависел от <see cref="Config"/> — иначе получается
/// цикл ZpRuntime → DevDeck → ZpRuntime.
/// </summary>
public static class ZpRuntimeOptions
{
    /// <summary>Endpoint для http-лога трафика. Пусто → используется дефолт.</summary>
    public static string TrafficHost { get; set; } = string.Empty;
}
