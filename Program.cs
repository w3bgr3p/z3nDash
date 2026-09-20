/*
 * Copyright (C) 2026 [w3bgr3p]
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using z3nDash;
using System.Diagnostics;
using System.Text;

// Логи содержат кириллицу и «ёлочки» из ZpRuntime. Без явной кодировки
// Console пишет в кодовой странице, доставшейся от родительского терминала,
// и всё непредставимое в ней подменяется на «?». Ставим UTF-8 до первой
// строки вывода. В try: у перенаправленного stdout консоли может не быть.
try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
try { Console.InputEncoding  = new UTF8Encoding(false); } catch { }

// Аналог -SelfTest из clipboard-heget.ps1: проверка преобразования без буфера,
// без хоткеев и без запуска сервера. Результат дублируется в файл: у WinExe
// stdout не всегда доходит до вызывающей консоли.
if (args.Contains("--clipconv-selftest"))
{
    var selfTestLines = new List<string>();
    int selfTestFailed = 0;
    foreach (var c in HeSelectorConverter.SelfTest())
    {
        selfTestLines.Add($"{(c.Passed ? "PASS" : "FAIL")}  {c.Name}");
        if (!c.Passed)
        {
            selfTestFailed++;
            selfTestLines.Add($"  expected: {c.Expected}");
            selfTestLines.Add($"  actual:   {c.Actual}");
        }
    }
    selfTestLines.Add(selfTestFailed == 0 ? "Self-test passed" : $"Self-test FAILED: {selfTestFailed}");

    foreach (var l in selfTestLines) Console.WriteLine(l);
    try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "clipconv-selftest.log"), selfTestLines); } catch { }
    return selfTestFailed == 0 ? 0 : 1;
}

try
{
    
    Config.Init();
    var logsConfig = Config.IsConfigured ? Config.LogsConfig : new LogsConfig { DashboardPort = "33333" };

    var dbConnectionService = new DbConnectionService();
    var _log = new Logger(logLevel: LogLevel.Error);

    if (Config.IsConfigured)
    {
        dbConnectionService.Connect(Config.DbConfig, _log);
    }

    var dashboardService = new EmbeddedServer(logsConfig, dbConnectionService);
    if (Config.IsConfigured)
    {
        dashboardService.RegisterHandler(new ZpOrchestratorHandler(dbConnectionService));
    }

    var schedulerService = new SchedulerService(dbConnectionService, _log);
    schedulerService.ApiBaseUrl = $"http://localhost:{dashboardService.Port}";
    InternalTasks.Load();


    var watchdogService = new MemoryWatchdogService(_log);
    var clipboardService = new ClipboardConverterService(_log);

    dashboardService.RegisterHandler(new ZpOrchestratorHandler(dbConnectionService));
    dashboardService.RegisterHandler(new SchedulerHandler(dbConnectionService, schedulerService, dashboardService.WwwrootPath));
    dashboardService.RegisterHandler(new TaskControlHandler(schedulerService));
    dashboardService.RegisterHandler(new ZpDebugHandler());
    dashboardService.RegisterHandler(new ImportHandler(dbConnectionService));
    dashboardService.RegisterHandler(new CliplatesHandler(dbConnectionService));
    dashboardService.RegisterHandler(new MemoryWatchdogHandler(watchdogService));
    dashboardService.RegisterHandler(new ClipboardConverterHandler(clipboardService));

    dashboardService.Start();
    schedulerService.Init();

    int port = dashboardService.Port;
    string startUrl = Config.IsConfigured
        ? $"http://localhost:{port}/?page=tasker"
        : $"http://localhost:{port}/?page=config";

#if WINDOWS
    DashboardOverlay.Open(startUrl);
#else
    OpenBrowser(startUrl);
#endif

    if (Config.IsConfigured)
    {
        var db = dbConnectionService.GetDb();
    }

    var exitTcs = new TaskCompletionSource();

    _ = Task.Run(() =>
    {
        Console.ReadKey(true);
        exitTcs.SetResult();
    });

    await exitTcs.Task;
    schedulerService.Dispose();
    watchdogService.Dispose();
    clipboardService.Dispose();
}
catch (Exception ex)
{
    var crashLog = Path.Combine(AppContext.BaseDirectory, "crash.log");
    File.WriteAllText(crashLog, $"{DateTime.Now}\n{ex}");

#if WINDOWS
    System.Windows.Forms.MessageBox.Show(
        $"Startup failed:\n{ex.Message}\n\nDetails: {crashLog}",
        "z3nDash",
        System.Windows.Forms.MessageBoxButtons.OK,
        System.Windows.Forms.MessageBoxIcon.Error);
#else
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    Console.Error.WriteLine($"Details: {crashLog}");
#endif
}

// Ключ --clipconv-selftest возвращает код явно, поэтому точка входа стала int-возвращающей:
// компилятор требует возврат на всех путях (CS0161). Ноль — прежнее поведение.
return 0;

#if !WINDOWS
static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = "xdg-open",
            Arguments       = url,
            UseShellExecute = false
        });
    }
    catch { }
    Console.WriteLine($"Dashboard: {url}");
}
#endif
