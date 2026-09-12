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
    InternalTasks.Register(schedulerService, dbConnectionService, logsConfig);


    var watchdogService = new MemoryWatchdogService(_log);

    dashboardService.RegisterHandler(new ZpOrchestratorHandler(dbConnectionService));
    dashboardService.RegisterHandler(new SchedulerHandler(dbConnectionService, schedulerService, dashboardService.WwwrootPath));
    dashboardService.RegisterHandler(new TaskControlHandler(schedulerService));
    dashboardService.RegisterHandler(new ImportHandler(dbConnectionService));
    dashboardService.RegisterHandler(new CliplatesHandler(dbConnectionService));
    dashboardService.RegisterHandler(new MemoryWatchdogHandler(watchdogService));

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
