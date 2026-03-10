using System.Net;
using System.Text;
using z3n8;

public class EmbeddedServer
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private bool _isRunning;
    private bool _debug = false;
    private readonly string _wwwrootPath;

    private readonly List<IScriptHandler> _scriptHandlers = new();

    private readonly LogHandler        _logHandler;
    private readonly HttpLogHandler    _httpLogHandler;
    private readonly TrafficHandler    _trafficHandler;
    private readonly ReportHandler     _reportHandler;
    private readonly HttpReplayHandler _replayHandler;
    private readonly ConfigHandler     _configHandler;

    private const int DefaultPort = 10993;

    public EmbeddedServer(LogsConfig config)
    {
        _port = int.TryParse(config.DashboardPort, out var p) ? p : DefaultPort;

        var ports = new HashSet<int> { _port };
        if (Uri.TryCreate(config.LogHost,     UriKind.Absolute, out var logUri))     ports.Add(logUri.Port);
        if (Uri.TryCreate(config.TrafficHost, UriKind.Absolute, out var trafficUri)) ports.Add(trafficUri.Port);

        var listeningPorts = new List<int>();
        foreach (var port in ports)
        {
            try
            {
                var test = new HttpListener();
                test.Prefixes.Add($"http://*:{port}/");
                test.Start(); test.Stop(); test.Close();
                _listener.Prefixes.Add($"http://*:{port}/");
                listeningPorts.Add(port);
            }
            catch { Console.WriteLine($"Port {port} already in use, skipping"); }
        }

        if (listeningPorts.Count == 0)
            throw new InvalidOperationException("No ports available to listen on");

        Console.WriteLine($"Listening ports: {string.Join(", ", listeningPorts)}");

        string logPath = !string.IsNullOrEmpty(config.LogsFolder)
            ? config.LogsFolder
            : Path.Combine(AppContext.BaseDirectory, "logs");
        EnsureDir(logPath, "LogsFolder");

        string reportsPath = !string.IsNullOrEmpty(config.ReportsFolder)
            ? config.ReportsFolder
            : Path.Combine(AppContext.BaseDirectory, "reports");
        EnsureDir(reportsPath, "ReportsFolder");

        _wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        EnsureDir(_wwwrootPath, "Wwwroot");

        _logHandler     = new LogHandler(logPath);
        _httpLogHandler = new HttpLogHandler(logPath);
        _trafficHandler = new TrafficHandler(logPath);
        _reportHandler  = new ReportHandler(reportsPath, _wwwrootPath);
        _replayHandler  = new HttpReplayHandler();
        _configHandler  = new ConfigHandler(logPath, _listener, _port);
    }

    public string WwwrootPath => _wwwrootPath;

    public void RegisterHandler(IScriptHandler handler)
    {
        handler.Init();
        _scriptHandlers.Add(handler);
    }

    public void Start()
    {
        _isRunning = true;
        _listener.Start();
        Task.Run(Listen);
        Console.WriteLine($"Listening {_port}");
    }

    public void Stop()
    {
        _isRunning = false;
        if (_listener.IsListening) _listener.Stop();
    }

    // ── Core loop ──────────────────────────────────────────────────────────────

    private async Task Listen()
    {
        while (_isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequest(context));
            }
            catch { if (!_isRunning) break; }
        }
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var request  = context.Request;
        var response = context.Response;

        response.Headers.Add("Access-Control-Allow-Origin",  "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }
        
        if (_debug ) request.RawUrl.Debug();


        try
        {
            string path   = request.Url?.AbsolutePath.ToLower() ?? "";
            string method = request.HttpMethod;

            // Script handlers (/zp, /py, /node, ...)
            foreach (var handler in _scriptHandlers)
            {
                if (path.StartsWith(handler.PathPrefix))
                {
                    await handler.HandleRequest(context);
                    return;
                }
            }

            // Pages
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                await ServePage(response, request.QueryString["page"] ?? "home");
                return;
            }

            // Domain handlers
            if (path.StartsWith("/report"))                              { await _reportHandler.Handle(context, path);  return; }
            if (path.StartsWith("/config") || path == "/clear-all-logs"){ await _configHandler.Handle(context);        return; }
            if (path == "/http-replay")                                  { await _replayHandler.Handle(context);        return; }
            if (_logHandler.Matches(path, method))                       { await _logHandler.Handle(context);           return; }
            if (_httpLogHandler.Matches(path, method))                   { await _httpLogHandler.Handle(context);       return; }
            if (_trafficHandler.Matches(path, method))                   { await _trafficHandler.Handle(context);       return; }

            // Static (wwwroot)
            if (method == "GET") { await ServeFile(response, request.Url!.AbsolutePath.TrimStart('/'), _wwwrootPath); return; }

            response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            var err = Encoding.UTF8.GetBytes(ex.Message);
            response.OutputStream.Write(err, 0, err.Length);
        }
        finally { response.Close(); }
    }

    // ── Static file serving ────────────────────────────────────────────────────

    private async Task ServePage(HttpListenerResponse response, string page, string? root = null)
    {
        root ??= _wwwrootPath;
        page = Path.GetFileName(page).Replace("..", "");
        if (!page.EndsWith(".html")) page += ".html";
        await ServeFile(response, page, root);
    }

    private static async Task ServeFile(HttpListenerResponse response, string relativePath, string root)
    {
        relativePath = relativePath.Replace("..", "").Replace("\\", "/").TrimStart('/');
        string filePath = Path.Combine(root, relativePath);

        if (!File.Exists(filePath) && string.IsNullOrEmpty(Path.GetExtension(filePath)))
            filePath += ".html";

        if (!File.Exists(filePath))
        {
            response.StatusCode  = 404;
            response.ContentType = "text/plain; charset=utf-8";
            var msg = Encoding.UTF8.GetBytes($"Not found: {filePath}");
            await response.OutputStream.WriteAsync(msg);
            Console.WriteLine($"[404] Not found: {filePath}");
            return;
        }

        response.ContentType = Path.GetExtension(filePath).ToLower() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png"  => "image/png",
            ".ico"  => "image/x-icon",
            _       => "application/octet-stream"
        };

        byte[] buf = await File.ReadAllBytesAsync(filePath);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf);
    }

    // ── Util ───────────────────────────────────────────────────────────────────

    private static void EnsureDir(string path, string label)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        Console.WriteLine($"{label}: {path}");
    }
}