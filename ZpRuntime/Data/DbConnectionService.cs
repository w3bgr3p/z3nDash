

namespace z3nDash;

    

public class DbConnectionService
{
    
    private Db? _db;
    private DbConfig? _config;
    private readonly object _lock = new object();
    public bool debug { get; set; } =  false;
    

    public bool IsConnected => _db != null;
    public Db GetDb()
    {
        lock (_lock)
        {
            if (_db == null)
            {
                throw new InvalidOperationException("Database not configured. Please configure database settings first.");
            }
            return _db;
        }
    }

    public bool TryGetDb(out Db? db)
    {
        lock (_lock)
        {
            db = _db;
            return _db != null;
        }
    }

    /// <summary>Текст последней неудачной попытки подключения; пусто, пока подключение живо.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// Срабатывает после каждого успешного Connect. Подписчики создают свои таблицы:
    /// базу меняют и на лету со страницы Config, а не только на старте.
    /// Исключение подписчика уходит вызывающему, подключение при этом остаётся.
    /// </summary>
    public event Action<Db>? Connected;

    /// <summary>
    /// Открыть базу и выполнить пробный запрос. Конструктор Db для SQLite к файлу
    /// не обращается, и без пробы битый путь всплыл бы только на первой записи.
    /// </summary>
    public static Db Open(DbConfig config, Logger logger = null)
    {
        var db = new Db(config, logger);
        try
        {
            db.Query("SELECT 1", thrw: true);
        }
        catch (AggregateException ae)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.GetBaseException()).Throw();
        }
        return db;
    }

    public void Connect(DbConfig config, Logger logger = null)
    {
        Db db;
        lock (_lock)
        {
            try
            {
                _db = Open(config, logger);
                _config = config;
                LastError = "";
                db = _db;
                Console.WriteLine($"✅ {config.Mode} database connected ");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Database connection failed: {ex.Message}");
                _db = null;
                _config = null;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
        }
        Connected?.Invoke(db);
    }
    

    public void Disconnect()
    {
        lock (_lock)
        {
            _db = null;
            _config = null;
            LastError = "";
            Console.WriteLine("🔌 Database disconnected");
        }
    }
}



