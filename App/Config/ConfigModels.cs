namespace DevDeck;

public class DbConfig
{
    public string Type { get; set; } = "";
    public string SqlitePath { get; set; } = string.Empty;
    public string PostgresConnectionString { get; set; } = string.Empty;
    
    public dbMode Mode => Type.ToLower() == "sqlite" ? dbMode.SQLite : dbMode.Postgre;

}

public class LogsConfig
{
    public string LogHost { get; set; } = string.Empty;
    public string TrafficHost { get; set; } = string.Empty;
    public string DashboardPort { get; set; } = string.Empty;
    public string ReplayPort { get; set; } = string.Empty;
    public string LogsFolder { get; set; } = string.Empty;
    public string TempFolder { get; set; } = string.Empty;
    public string ReportsFolder { get; set; } = string.Empty;

    public int MaxFileSizeMb { get; set; }
}

public class ApiConfig
{
    public string ZB { get; set; } = string.Empty;
    // ZB API base URL. Default: http://localhost:8160
    public string ZbHost { get; set; } = string.Empty;
}
public class SecurityConfig
{
    public string JVarsPath { get; set; } = string.Empty;
}

public class AiConfig
{
    public string Provider { get; set; } = "omniroute";
    public string OmniRouteHost { get; set; } = "http://localhost:20128";
}

public class WatchdogConfig
{
    // Слежение за памятью процесса ZennoPoster; при превышении лимита процесс убивается.
    public bool   Enabled     { get; set; } = false;
    public string ProcessName { get; set; } = "ZennoPoster";
    public int    LimitMb     { get; set; } = 0;   // 0 = не убивать
    public int    IntervalSec { get; set; } = 15;
}

public class CrxItem
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
