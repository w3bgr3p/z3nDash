namespace DevDeck
{
    
    public class TableSchema
    {
        public string Name   { get; set; }
        public Dictionary<string, string> Columns { get; set; }
    }
    
    /// <summary>
    /// Централизованное хранилище схем таблиц, которые DevDeck использует напрямую.
    /// </summary>
    public static class DbSchema
    {
        public static readonly TableSchema ZpNodes = new()
        {
            Name = "zp_nodes",
            Columns = new()
            {
                { "machine",    "TEXT PRIMARY KEY" },
                { "host",       "TEXT DEFAULT ''"  },
                { "port",       "TEXT DEFAULT ''"  },
                { "updated_at", "TEXT DEFAULT ''"  },
            }
        };

        public static readonly TableSchema Schedules = new()
        {
            Name = "schedules",
            Columns = new()
            {
                { "id",               "TEXT PRIMARY KEY" },
                { "name",             "TEXT DEFAULT ''" },
                { "executor",         "TEXT DEFAULT 'internal'" },
                { "script_path",      "TEXT DEFAULT ''" },
                { "args",             "TEXT DEFAULT ''" },
                { "enabled",          "TEXT DEFAULT 'true'" },
                { "cron",             "TEXT DEFAULT ''" },
                { "interval_minutes", "TEXT DEFAULT '0'" },
                { "fixed_time",       "TEXT DEFAULT ''" },
                { "on_overlap",       "TEXT DEFAULT 'skip'" },
                { "max_threads",      "TEXT DEFAULT '1'" },
                { "status",           "TEXT DEFAULT 'idle'" },
                { "last_run",         "TEXT DEFAULT ''" },
                { "last_exit",        "TEXT DEFAULT ''" },
                { "last_output",      "TEXT DEFAULT ''" },
                { "payload_schema",   "TEXT DEFAULT ''" },
                { "payload_values",   "TEXT DEFAULT ''" },
                { "runs_total",       "TEXT DEFAULT '0'" },
                { "runs_success",     "TEXT DEFAULT '0'" },
                { "schedule_tag",     "TEXT DEFAULT ''" },
                { "last_run_id",      "TEXT DEFAULT ''" },
                { "use_venv",         "TEXT DEFAULT 'false'" },
                { "schedule_mode",    "TEXT DEFAULT 'off'" },
                { "schedule_json",    "TEXT DEFAULT ''" },
                { "sched_runs",       "TEXT DEFAULT '0'" },
                { "sched_started_at", "TEXT DEFAULT ''" },
                { "browser_json",     "TEXT DEFAULT ''" },
            }
        };

        public static readonly TableSchema ScheduleQueue = new()
        {
            Name = "schedule_queue",
            Columns = new()
            {
                { "uuid",        "TEXT PRIMARY KEY" },
                { "schedule_id", "TEXT DEFAULT ''" },
                { "queued_at",   "TEXT DEFAULT ''" },
                { "status",      "TEXT DEFAULT 'pending'" },
                { "priority",    "TEXT DEFAULT '10'" },
                { "run_id",      "TEXT DEFAULT ''" },
                { "args_b64",    "TEXT DEFAULT ''" },
            }
        };

        public static readonly TableSchema Clips = new()
        {
            Name = "clips",
            Columns = new()
            {
                { "id",         "TEXT PRIMARY KEY" },
                { "path",       "TEXT DEFAULT ''" },
                { "title",      "TEXT DEFAULT ''" },
                { "content",    "TEXT DEFAULT ''" },
                { "created_at", "TEXT DEFAULT ''" },
            }
        };

        public static readonly TableSchema JsonAnalyzerCache = new()
        {
            Name = "ai_json_cache",
            Columns = new()
            {
                { "key",      "TEXT PRIMARY KEY" },
                { "analysis", "TEXT" },
                { "model",    "TEXT" },
                { "ts",       "TEXT" },
            }
        };

        public static readonly TableSchema SystemSnapshots = new()
        {
            Name = "system_snapshots",
            Columns = new()
            {
                { "id",   "INTEGER PRIMARY KEY" },
                { "ts",   "TEXT" },
                { "host", "TEXT" },
                { "raw",  "TEXT" },
            }
        };

        public static readonly TableSchema SystemSnapshotAiCache = new()
        {
            Name = "system_snapshot_ai_cache",
            Columns = new()
            {
                { "id",     "INTEGER PRIMARY KEY" },
                { "model",  "TEXT" },
                { "ts",     "TEXT" },
                { "report", "TEXT" },
            }
        };

        public static readonly TableSchema TreasuryAiCache = new()
        {
            Name = "treasury_ai_cache",
            Columns = new()
            {
                { "id",     "INTEGER PRIMARY KEY" },
                { "model",  "TEXT" },
                { "ts",     "TEXT" },
                { "report", "TEXT" },
            }
        };

        public static readonly TableSchema Instance = new()
        {
            Name = "instance",
            Columns = new()
            {
                { "id",      "INTEGER PRIMARY KEY" },
                { "proxy",   "TEXT DEFAULT ''" },
                { "cookies", "TEXT DEFAULT ''" },
                { "webgl",   "TEXT DEFAULT ''" },
                { "zb_id",   "TEXT DEFAULT ''" },
            }
        };

        public static readonly TableSchema Addresses = new()
        {
            Name = "addresses",
            Columns = new()
            {
                { "id",       "INTEGER PRIMARY KEY" },
                { "evm_pk",   "TEXT DEFAULT ''" },
                { "sol_pk",   "TEXT DEFAULT ''" },
                { "apt_pk",   "TEXT DEFAULT ''" },
                { "evm_seed", "TEXT DEFAULT ''" },
            }
        };

    }
}
