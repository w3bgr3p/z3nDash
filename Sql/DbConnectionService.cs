

namespace z3n8;

    

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

    public void Connect(DbConfig config, Logger logger = null)
    {
        
        lock (_lock)
        {
            try
            {
                _db = new Db(config,logger);
                _config = config;
                Console.WriteLine($"✅ {config.Mode} database connected ");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Database connection failed: {ex.Message}");
                _db = null;
                _config = null;
                throw;
            }
        }
    }
    

    public void Disconnect()
    {
        lock (_lock)
        {
            _db = null;
            _config = null;
            Console.WriteLine("🔌 Database disconnected");
        }
    }
}



