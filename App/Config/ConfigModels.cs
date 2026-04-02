namespace z3nIO;

public class DbConfig
{
    public string Type { get; set; } = "";
    public string SqlitePath { get; set; } = string.Empty;
    public string PostgresHost { get; set; } = string.Empty;
    public string PostgresPort { get; set; } = string.Empty;
    public string PostgresUser { get; set; } = string.Empty;
    public string PostgresPassword { get; set; } = string.Empty;
    public string PostgresDatabase { get; set; } = "postgres";
    
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
    // "aiio" | "omniroute" | "" (disabled)
    public string Provider { get; set; } = string.Empty;
    public string OmniRouteHost { get; set; } = "http://localhost:20128";
}

public class CrxItem
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}