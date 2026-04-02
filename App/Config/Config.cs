using Microsoft.Extensions.Configuration;

namespace z3nIO;

public class Config
{
    public static bool IsConfigured { get; private set; } = false;
    
    
    
    public static DbConfig DbConfig { get; private set; } = new();
    public static LogsConfig LogsConfig { get; private set; } = new();
    public static ApiConfig ApiConfig { get; private set; } = new();
    
    public static SecurityConfig SecurityConfig { get; private set; } = new();
    public static AiConfig       AiConfig       { get; private set; } = new();

    public static Dictionary<string, CrxItem> Crx { get; private set; } = new();
    public static void Init()
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.secrets.json");
        if (!File.Exists(cfgPath))
        {
            IsConfigured = false;
            return; // стартуем с дефолтами
        }
        
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory) // Указываем путь к папке с приложением
            .AddJsonFile("appsettings.secrets.json", optional: false, reloadOnChange: true)
            .Build();
        
        DbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? new();
        LogsConfig = config.GetSection("LogsConfig").Get<LogsConfig>() ?? new();
        ApiConfig = config.GetSection("ApiConfig").Get<ApiConfig>() ?? new();
        SecurityConfig = config.GetSection("SecurityConfig").Get<SecurityConfig>() ?? new();
        AiConfig       = config.GetSection("AiConfig").Get<AiConfig>()             ?? new();

        Crx = config.GetSection("Crx").Get<Dictionary<string, CrxItem>>() ?? new();
        IsConfigured = true;
    }
}




