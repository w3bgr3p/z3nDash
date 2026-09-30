using System.Diagnostics;

namespace z3nDash;

/// <summary>
/// Установка того, чего не нашла EnvironmentCheck. Команды фиксированные, по ключу:
/// из запроса приходит только ключ, текст команды клиент не передаёт.
/// Установка идёт в видимом окне консоли — там видно ход, запрос UAC и итог;
/// дашборд её не ждёт. После установки человек жмёт Check.
/// </summary>
internal static class EnvironmentInstall
{
    /// <summary>console — команда в окне cmd; url — открыть страницу, где скачивать.</summary>
    public sealed record Recipe(string Title, string Kind, string Command);

    // Идентификаторы пакетов проверены 2026-09-28 через «winget show --id <id> --exact».
    private const string WingetFlags = "--exact --accept-package-agreements --accept-source-agreements";

    private static string Winget(string id, string mode = "--silent") => $"winget install --id {id} {WingetFlags} {mode}";

    public static Recipe? Get(string key) => key switch
    {
        // App Installer из Microsoft Store: в нём и живёт winget.
        "winget"        => new("winget (App Installer)", "url", "ms-windows-store://pdp/?productid=9NBLGGH4NNS1"),

        // --override заменяет тихие ключи winget целиком, поэтому /quiet здесь свой.
        // PrependPath=1 ставит python в PATH раньше заглушки Microsoft Store.
        "python"        => new("Python 3.12", "console",
                               Winget("Python.Python.3.12", "--scope user --override \"/quiet InstallAllUsers=0 PrependPath=1 Include_launcher=1 Include_pip=1\"")),
        "pip"           => new("pip", "console", "python -m ensurepip --upgrade"),
        "node"          => new("Node.js LTS", "console", Winget("OpenJS.NodeJS.LTS")),
        "ts-node"       => new("ts-node + typescript", "console", "npm install -g ts-node typescript"),
        "dotnet-sdk"    => new(".NET SDK 10", "console", Winget("Microsoft.DotNet.SDK.10")),
        "dotnet-script" => new("dotnet-script", "console", "dotnet tool install -g dotnet-script"),
        "git"           => new("Git for Windows", "console", Winget("Git.Git")),
        "pwsh"          => new("PowerShell 7", "console", Winget("Microsoft.PowerShell")),
        // Установщику нужен пароль суперпользователя: без окна его не задать.
        "postgres"      => new("PostgreSQL 17", "console", Winget("PostgreSQL.PostgreSQL.17", "--interactive")),
        "webview2"      => new("WebView2 Runtime", "console", Winget("Microsoft.EdgeWebView2Runtime")),
        "chrome"        => new("Google Chrome", "console", Winget("Google.Chrome")),
        "playwright"    => PlaywrightRecipe(),
        // В winget драйвера нет; скачивание и запуск чужого exe оставляем человеку.
        "sqlite-odbc"   => new("SQLite ODBC driver", "url", "http://www.ch-werner.de/sqliteodbc/"),
        _               => null,
    };

    /// <summary>Браузер той ревизии, которую ждёт драйвер, лежащий рядом с exe.</summary>
    private static Recipe PlaywrightRecipe()
    {
        var (node, cli) = PlaywrightDriver();
        return new("Playwright Chromium", "console", $"\"{node}\" \"{cli}\" install chromium");
    }

    public static (string Node, string Cli) PlaywrightDriver()
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".playwright");
        return (Path.Combine(root, "node", "win32_x64", "node.exe"), Path.Combine(root, "package", "cli.js"));
    }

    /// <summary>Запустить установку. Возвращает то, что запущено, — для ответа и лога.</summary>
    public static string Start(Recipe recipe)
    {
        if (recipe.Kind == "url")
        {
            Process.Start(new ProcessStartInfo(recipe.Command) { UseShellExecute = true });
            return recipe.Command;
        }

        // /s снимает только первую и последнюю кавычку — внутренние кавычки команды целы.
        // /k оставляет окно открытым: итог установки должен быть виден.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/s /k \"title z3nDash install: {recipe.Title} & {recipe.Command}\"")
        {
            UseShellExecute = true,
        });
        return recipe.Command;
    }
}
