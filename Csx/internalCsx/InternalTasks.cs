// InternalTasks.cs

using Newtonsoft.Json;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nIO;

public static partial class InternalTasks
{
    private static List<Action<SchedulerService, DbConnectionService, LogsConfig>> _registrations;

    
    private static string _jVarsPath =>
        !string.IsNullOrWhiteSpace(Config.SecurityConfig.JVarsPath)
            ? Config.SecurityConfig.JVarsPath
            : Path.Combine(AppContext.BaseDirectory, "jvars.dat");

    private static string _jVars = "";
    public static bool IsUnlocked => !string.IsNullOrEmpty(_jVars);
    public static string JVars    => _jVars;


    public static void Load()
    {
        if (!File.Exists(_jVarsPath))
        {
            Console.WriteLine($"[InternalTasks.Load] jVars not found: {_jVarsPath}");
            return;
        }
        _jVars = File.ReadAllText(_jVarsPath).Trim();
        Console.WriteLine($"[InternalTasks.Load] loaded {_jVars[..20]}...");
    }

    private static bool RegisterSelf(Action<SchedulerService, DbConnectionService, LogsConfig> reg)
    {
        _registrations ??= new();
        _registrations.Add(reg);
        return true;
    }

    public static void Register(SchedulerService scheduler, DbConnectionService dbService, LogsConfig logsConfig)
    {
        Load();
        foreach (var reg in _registrations ?? Enumerable.Empty<Action<SchedulerService, DbConnectionService, LogsConfig>>())
            reg(scheduler, dbService, logsConfig);
    }
}