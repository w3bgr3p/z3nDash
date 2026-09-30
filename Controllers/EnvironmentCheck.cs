using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace z3nDash;

/// <summary>
/// Что стоит на машине из того, чем z3nDash запускает задачи: python, node, npm,
/// dotnet, git bash, PowerShell, Postgres, WebView2 и прочее.
/// Каждая строка — наблюдение: что запустили или где посмотрели и что получили.
/// Причин «почему нет» здесь не выводим — их читает человек по Detail.
/// </summary>
internal static class EnvironmentCheck
{
    /// <summary>found — нашли и получили версию; missing — не нашли или команда упала; info — просто значение.</summary>
    public sealed record Item(
        [property: JsonPropertyName("group")]   string Group,
        [property: JsonPropertyName("name")]    string Name,
        [property: JsonPropertyName("state")]   string State,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("path")]    string Path,
        [property: JsonPropertyName("detail")]  string Detail,
        // Ключ рецепта EnvironmentInstall; кнопку показываем только при state=missing.
        [property: JsonPropertyName("install")] string Install = "");

    private const string Found   = "found";
    private const string Missing = "missing";
    private const string Info    = "info";

    static EnvironmentCheck()
    {
        // cmd.exe и where.exe пишут в OEM-кодировке (866 на русской Windows).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<List<Item>> RunAsync(DbConnectionService dbService)
    {
        RefreshPath();

        var probes = new List<Task<Item>>
        {
            Can(Tool("System", "winget", "winget", "--version"), "winget"),

            // Python: задачи запускаются именем "python" из PATH (PythonEnv.Resolve).
            Can(Tool("Python", "python", "python", "--version"), "python"),
            Can(Tool("Python", "py launcher", "py", "-0p", multiline: true), "python"),
            Can(Tool("Python", "pip", "python", "-m pip --version", whereName: "python"), "pip"),

            Can(Tool("JavaScript", "node", "node", "--version"), "node"),
            Can(Tool("JavaScript", "npm", "npm", "--version", viaCmd: true), "node"),
            Can(Tool("JavaScript", "npx", "npx", "--version", viaCmd: true), "node"),
            // --no: не ставить пакет, если его нет, а упасть. "--" обязателен: без него
            // --version забирает сам npx и печатает свою версию (проверено: 10.9.3 вместо v10.9.2).
            Can(Tool("JavaScript", "ts-node (npx)", "npx", "--no -- ts-node --version", viaCmd: true, timeoutMs: 20000), "ts-node"),

            Can(Tool(".NET", "dotnet SDKs", "dotnet", "--list-sdks", multiline: true), "dotnet-sdk"),
            Can(Tool(".NET", "dotnet runtimes", "dotnet", "--list-runtimes", multiline: true), "dotnet-sdk"),
            Can(Tool(".NET", "dotnet-script", "dotnet-script", "--version"), "dotnet-script"),

            Can(Tool("Shell", "git", "git", "--version"), "git"),
            Can(GitBash(), "git"),
            Tool("Shell", "Windows PowerShell", "powershell", "-NoProfile -Command $PSVersionTable.PSVersion.ToString()"),
            Can(Tool("Shell", "PowerShell 7 (pwsh)", "pwsh", "--version"), "pwsh"),

            // psql без кнопки: установщик EDB bin в PATH не добавляет, это правка PATH, а не установка.
            Tool("Database", "psql (client in PATH)", "psql", "--version"),
            Can(Task.Run(PostgresInstallations), "postgres"),
            Task.Run(PostgresServices),
            PortOpen("Database", "localhost:5432", "localhost", 5432),
            Task.Run(() => ConnectedDb(dbService)),
            Task.Run(BundledSqlite),
            Can(Task.Run(SqliteOdbcDrivers), "sqlite-odbc"),

            Can(Task.Run(WebView2), "webview2"),
            Can(Task.Run(Chrome), "chrome"),
            Can(PlaywrightBrowsers(), "playwright"),
        };

        var items = new List<Item>(SystemItems());
        items.AddRange(await Task.WhenAll(probes));
        return items;
    }

    private static async Task<Item> Can(Task<Item> probe, string installKey)
        => (await probe) with { Install = installKey };

    /// <summary>
    /// Установщик пишет PATH в реестр, а процесс держит копию со своего старта: без
    /// перечитывания только что поставленный node не нашёлся бы ни здесь, ни у задач
    /// до перезапуска дашборда. Порядок — как у нового процесса (Machine, затем User),
    /// записи, которых в реестре нет, сохраняются в хвосте.
    /// </summary>
    internal static void RefreshPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            static IEnumerable<string> Split(string? v) => (v ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var fresh = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
                .Concat(Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
                .Select(Environment.ExpandEnvironmentVariables)
                .ToList();
            if (fresh.Count == 0) return;

            var merged = fresh
                .Concat(Split(Environment.GetEnvironmentVariable("PATH")))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            Environment.SetEnvironmentVariable("PATH", string.Join(';', merged));
        }
        catch (Exception ex) { Console.WriteLine($"[env] PATH refresh skipped: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── System ────────────────────────────────────────────────────────────────

    private static IEnumerable<Item> SystemItems()
    {
        // В колонку версии — только номер: длинные строки выпирали из неё.
        yield return new Item("System", "OS", Info, Environment.OSVersion.Version.ToString(), "",
            $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, machine {Environment.MachineName}");

        var app = Assembly.GetEntryAssembly();
        var appVersion = app?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? app?.GetName().Version?.ToString() ?? "";
        // "1.0.3.1+<полный хэш>": номер — в колонку, хэш коммита — в подробности.
        var plus   = appVersion.IndexOf('+');
        var commit = plus < 0 ? "" : appVersion[(plus + 1)..];
        yield return new Item("System", "z3nDash", Info, plus < 0 ? appVersion : appVersion[..plus], AppContext.BaseDirectory,
            RuntimeInformation.FrameworkDescription + (commit.Length > 0 ? $", commit {commit[..Math.Min(12, commit.Length)]}" : ""));

        yield return new Item("System", "Run as administrator", Info, IsAdmin(), "", $"user {Environment.UserName}");

        // Config пишется рядом с exe: в Program Files без прав администратора запись не пройдёт.
        var probe = Path.Combine(AppContext.BaseDirectory, $".write-probe-{Guid.NewGuid():N}");
        string writable, detail = "";
        try { File.WriteAllText(probe, ""); File.Delete(probe); writable = "yes"; }
        catch (Exception ex) { writable = "no"; detail = $"{ex.GetType().Name}: {ex.Message}"; }
        yield return new Item("System", "App folder writable", writable == "yes" ? Found : Missing, writable,
            AppContext.BaseDirectory, detail);
    }

    private static string IsAdmin()
    {
#if WINDOWS
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "yes" : "no";
#else
        return "n/a";
#endif
    }

    // ── Command-line tools ────────────────────────────────────────────────────

    /// <summary>
    /// Запустить команду так же, как её запускают задачи, и записать ответ.
    /// Path — первая строка where.exe: видно, какой именно файл подхватился из PATH
    /// (например, заглушка Microsoft Store вместо настоящего python).
    /// </summary>
    private static async Task<Item> Tool(string group, string name, string exe, string args,
        bool viaCmd = false, bool multiline = false, string? whereName = null, int timeoutMs = 10000)
    {
        var path = await WhereFirst(whereName ?? exe);
        // npm и npx — .cmd-обёртки, их запускает cmd.exe, как и экзекутор npm.
        var r = viaCmd ? await Run("cmd.exe", $"/c {exe} {args}", timeoutMs) : await Run(exe, args, timeoutMs);
        var output = (r.Out + "\n" + r.Err).Trim();

        if (r.Error == null && r.Exit == 0 && output.Length > 0)
            return new Item(group, name, Found, VersionOf(output), path, multiline ? output : FirstLine(output));
        return new Item(group, name, Missing, "", path, Observed(r, output));
    }

    private static async Task<Item> GitBash()
    {
        // Тот же поиск, что у экзекутора bash.
        var bash = SchedulerService.ResolveGitBash();
        var path = bash == "bash" ? await WhereFirst("bash") : bash;
        var r = await Run(bash, "--version");
        var output = (r.Out + "\n" + r.Err).Trim();
        return r.Error == null && r.Exit == 0
            ? new Item("Shell", "bash (executor bash)", Found, VersionOf(output), path, FirstLine(output))
            : new Item("Shell", "bash (executor bash)", Missing, "", path, Observed(r, output));
    }

    private static async Task<string> WhereFirst(string name)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var r = await Run("where.exe", name, 5000);
        return r.Error == null && r.Exit == 0 ? FirstLine(r.Out) : "";
    }

    private sealed record RunResult(int Exit, string Out, string Err, string? Error);

    /// <summary>Процесс с таймаутом: зависший инструмент не должен вешать всю проверку.</summary>
    private static async Task<RunResult> Run(string file, string args, int timeoutMs = 10000)
    {
        // Консольные утилиты Windows пишут в OEM-кодировке, остальные — в UTF-8.
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var enc  = name is "cmd" or "where" or "powershell" ? Oem() : Encoding.UTF8;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = enc,
                    StandardErrorEncoding  = enc,
                },
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new RunResult(-1, "", "", $"timeout after {timeoutMs} ms: {file} {args}");
            }
            return new RunResult(p.ExitCode, (await outTask).Replace("\r", ""), (await errTask).Replace("\r", ""), null);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "", "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    private static Encoding Oem()
    {
        try { return OperatingSystem.IsWindows() ? Encoding.GetEncoding(GetOEMCP()) : Encoding.UTF8; }
        catch { return Encoding.UTF8; }
    }

    private static string Observed(RunResult r, string output)
        => r.Error ?? (output.Length > 0 ? $"exit={r.Exit} | {Short(output)}" : $"exit={r.Exit}, no output");

    private static string VersionOf(string text)
    {
        var m = Regex.Match(text, @"\d+(\.\d+)+");
        return m.Success ? m.Value : "";
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static string Short(string text, int limit = 400)
    {
        var s = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= limit ? s : s[..limit] + "...";
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>Записи установщика EDB. Postgres из docker, scoop и т. п. здесь не появится.</summary>
    private static Item PostgresInstallations()
    {
        const string name = "PostgreSQL server (installer registry)";
#if WINDOWS
        const string key  = @"SOFTWARE\PostgreSQL\Installations";
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if (root == null) return new Item("Database", name, Missing, "", "", $@"no key HKLM\{key}");

            var rows = root.GetSubKeyNames().Select(sub =>
            {
                using var k = root.OpenSubKey(sub);
                return (Ver: k?.GetValue("Version")?.ToString() ?? "",
                        Dir: k?.GetValue("Base Directory")?.ToString() ?? "",
                        Svc: k?.GetValue("Service ID")?.ToString() ?? "");
            }).ToList();
            if (rows.Count == 0) return new Item("Database", name, Missing, "", "", $@"HKLM\{key} has no entries");

            return new Item("Database", name, Found, string.Join(", ", rows.Select(r => r.Ver)), rows[0].Dir,
                string.Join("\n", rows.Select(r => $"{r.Ver}  {r.Dir}  service={r.Svc}")));
        }
        catch (Exception ex) { return new Item("Database", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
#else
        return new Item("Database", name, Info, "n/a", "", "not Windows");
#endif
    }

    private static Item PostgresServices()
    {
        const string name = "PostgreSQL services";
#if WINDOWS
        try
        {
            var services = System.ServiceProcess.ServiceController.GetServices()
                .Where(s => s.ServiceName.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase))
                .Select(s => $"{s.ServiceName}: {s.Status}")
                .ToList();
            return services.Count == 0
                ? new Item("Database", name, Missing, "", "", "no service named postgresql*")
                : new Item("Database", name, Found, "", "", string.Join("\n", services));
        }
        catch (Exception ex) { return new Item("Database", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
#else
        return new Item("Database", name, Info, "n/a", "", "not Windows");
#endif
    }

    private static async Task<Item> PortOpen(string group, string name, string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(2000);
            await client.ConnectAsync(host, port, cts.Token);
            return new Item(group, name, Found, "", "", "TCP connect succeeded");
        }
        catch (OperationCanceledException) { return new Item(group, name, Missing, "", "", "TCP connect timeout after 2000 ms"); }
        catch (Exception ex) { return new Item(group, name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>База из Config: чем ответил сервер, к которому подключён дашборд.</summary>
    private static Item ConnectedDb(DbConnectionService dbService)
    {
        const string name = "Configured DB (Config page)";
        if (!dbService.TryGetDb(out var db) || db == null)
            return new Item("Database", name, Missing, "", "",
                string.IsNullOrEmpty(dbService.LastError) ? "not connected" : $"not connected | {dbService.LastError}");
        try
        {
            var sql = db.Mode == dbMode.Postgre ? "SELECT version()" : "SELECT sqlite_version()";
            var answer = db.Query(sql, thrw: true);
            return new Item("Database", name, Found, VersionOf(answer), "", $"{db.Mode}: {answer}");
        }
        catch (Exception ex)
        {
            var e = ex is AggregateException ae ? ae.GetBaseException() : ex;
            return new Item("Database", name, Missing, "", "", $"{db.Mode} | {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Движок, который приложение несёт с собой (Microsoft.Data.Sqlite).</summary>
    private static Item BundledSqlite()
    {
        const string name = "SQLite engine (bundled)";
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version()";
            var ver = cmd.ExecuteScalar()?.ToString() ?? "";
            var lib = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version;
            return new Item("Database", name, Found, ver, "", $"Microsoft.Data.Sqlite {lib}");
        }
        catch (Exception ex) { return new Item("Database", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Нужен шаблонам ZP, которые ходят в БД через FastDb (ZennoPoster.Db.ExecuteQuery → ODBC).</summary>
    private static Item SqliteOdbcDrivers()
    {
        const string name = "SQLite ODBC driver (ZP FastDb)";
#if WINDOWS
        const string key  = @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers";
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            var drivers = k?.GetValueNames().Where(n => n.Contains("SQLite", StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
            return drivers.Count == 0
                ? new Item("Database", name, Missing, "", "", $@"no SQLite entries in HKLM\{key}")
                : new Item("Database", name, Found, "", "", string.Join("\n", drivers));
        }
        catch (Exception ex) { return new Item("Database", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
#else
        return new Item("Database", name, Info, "n/a", "", "not Windows");
#endif
    }

    // ── Browsers ──────────────────────────────────────────────────────────────

    /// <summary>Окно дашборда (DashboardOverlay) рисуется в WebView2.</summary>
    private static Item WebView2()
    {
        const string name = "WebView2 Runtime";
#if WINDOWS
        try
        {
            var ver = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            return new Item("Browser", name, Found, ver, "", "");
        }
        catch (Exception ex) { return new Item("Browser", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
#else
        return new Item("Browser", name, Info, "n/a", "", "not Windows");
#endif
    }

    /// <summary>Нужен режиму браузера с channel=chrome.</summary>
    private static Item Chrome()
    {
        const string name = "Google Chrome";
#if WINDOWS
        const string key  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
        try
        {
            string? path = null;
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var k = hive.OpenSubKey(key);
                path = k?.GetValue("")?.ToString();
                if (!string.IsNullOrEmpty(path)) break;
            }
            if (string.IsNullOrEmpty(path)) return new Item("Browser", name, Missing, "", "", $@"no App Paths entry {key} in HKLM or HKCU");
            if (!File.Exists(path))         return new Item("Browser", name, Missing, "", path, "App Paths points to a missing file");
            return new Item("Browser", name, Found, FileVersionInfo.GetVersionInfo(path).FileVersion ?? "", path, "");
        }
        catch (Exception ex) { return new Item("Browser", name, Missing, "", "", $"{ex.GetType().Name}: {ex.Message}"); }
#else
        return new Item("Browser", name, Info, "n/a", "", "not Windows");
#endif
    }

    /// <summary>
    /// Chromium той ревизии, которую ждёт драйвер рядом с exe. Ревизию и каталог
    /// называет сам драйвер: «cli.js install --dry-run chromium» ничего не качает,
    /// только печатает Install location (проверено 2026-09-28).
    /// </summary>
    private static async Task<Item> PlaywrightBrowsers()
    {
        const string name = "Playwright Chromium";
        var (node, cli) = EnvironmentInstall.PlaywrightDriver();
        if (!File.Exists(node) || !File.Exists(cli))
            return new Item("Browser", name, Missing, "", "", $"driver not found: {node} | {cli}");

        var r = await Run(node, $"\"{cli}\" install --dry-run chromium", 15000);
        var output = (r.Out + "\n" + r.Err).Trim();
        if (r.Error != null || r.Exit != 0) return new Item("Browser", name, Missing, "", "", Observed(r, output));

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head  = Array.FindIndex(lines, l => l.Contains("playwright chromium v", StringComparison.OrdinalIgnoreCase));
        var loc   = head < 0 ? null : lines.Skip(head).FirstOrDefault(l => l.StartsWith("Install location:", StringComparison.OrdinalIgnoreCase));
        if (loc == null) return new Item("Browser", name, Missing, "", "", $"no chromium Install location in dry-run output | {Short(output)}");

        var dir = loc["Install location:".Length..].Trim();
        var rev = Regex.Match(lines[head], @"chromium v(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
        return Directory.Exists(dir)
            ? new Item("Browser", name, Found, rev, dir, lines[head])
            : new Item("Browser", name, Missing, "", dir, $"{lines[head]} | folder does not exist");
    }
}
