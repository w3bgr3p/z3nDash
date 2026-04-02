using System.Net;
using System.Text;
using z3nIO;

public class EmbeddedServer
{
    private readonly HttpListener _listener = new();
    private readonly HttpListener _replayListener = new();
    private readonly int _port;
    private bool _isRunning;
    private bool _debug = false;
    private readonly string _wwwrootPath;

    private readonly List<IScriptHandler> _scriptHandlers = new();

    private readonly LogHandler        _logHandler;
    private readonly HttpLogHandler    _httpLogHandler;
    //private readonly TrafficHandler    _trafficHandler;
    private readonly ReportHandler     _reportHandler;
    private readonly HttpReplayHandler _replayHandler;
    private readonly ConfigHandler     _configHandler; 
    private readonly ZbHandler _zbHandler;
    private readonly AiClient _aiClient;
    private readonly AiReportHandler _aiReportHandler;
    private readonly TreasuryHandler _treasuryHandler;
    private readonly SystemSnapshotHandler _snapshotHandler;
    private readonly JsonAnalyzerHandler _jsonAnalyzerHandler;

    private const int DefaultPort = 10993;

    public EmbeddedServer(LogsConfig config, DbConnectionService dbService)
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
        {
            // fallback: найти любой свободный порт
            for (int fallback = 10993; fallback < 11100; fallback++)
            {
                try
                {
                    var test = new HttpListener();
                    test.Prefixes.Add($"http://*:{fallback}/");
                    test.Start(); test.Stop(); test.Close();
                    _listener.Prefixes.Add($"http://*:{fallback}/");
                    listeningPorts.Add(fallback);
                    break;
                }
                catch { }
            }
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
        //_trafficHandler = new TrafficHandler(logPath);
        _reportHandler = new ReportHandler(reportsPath, _wwwrootPath, dbService);
        _zbHandler = new ZbHandler();
        _replayHandler  = new HttpReplayHandler();
        _aiClient            = new AiClient(dbService);
        _configHandler       = new ConfigHandler(logPath, _listener, _port, dbService, _aiClient);
        _aiReportHandler     = new AiReportHandler(dbService, _aiClient);
        _treasuryHandler     = new TreasuryHandler(dbService, _aiClient);
        _snapshotHandler     = new SystemSnapshotHandler(dbService, _aiClient);
        _jsonAnalyzerHandler = new JsonAnalyzerHandler(dbService, _aiClient);

        
        
        int replayPort = int.TryParse(config.ReplayPort, out var rp) ? rp : _port + 1;
        try
        {
            //_replayListener.Prefixes.Add($"http://*:{replayPort}/");
            _replayListener.Prefixes.Add($"http://localhost:{replayPort}/");

            _replayListener.Start();
            Console.WriteLine($"Replay port: {replayPort}");
        }
        catch (Exception ex)  { $"Replay port {replayPort} unavailable: {ex.Message}".Debug(); }
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
        if (_replayListener.IsListening) Task.Run(ListenReplay);
        Console.WriteLine($"Listening {_port}");
    }

    public void Stop()
    {
        _isRunning = false;
        if (_listener.IsListening) _listener.Stop();
        if (_replayListener.IsListening) _replayListener.Stop();
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

    private async Task ListenReplay()
    {
        while (_isRunning)
        {
            try
            {
                var context = await _replayListener.GetContextAsync();
                _ = Task.Run(() => ProcessReplayRequest(context));
            }
            catch { if (!_isRunning) break; }
        }
    }

    private async Task ProcessReplayRequest(HttpListenerContext context)
    {
        context.Response.Headers.Add("Access-Control-Allow-Origin",  "*");
        context.Response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (context.Request.HttpMethod == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        try   { await _replayHandler.Handle(context); }
        catch { }
        finally { try { context.Response.Close(); } catch { } }
    }

    private async Task ProcessRequest(HttpListenerContext context)
    {
        var request  = context.Request;
        var response = context.Response;

        response.Headers.Add("Access-Control-Allow-Origin",  "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
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
                    if (_debug )  $"[handler] Script:{handler.PathPrefix} → {path}".Debug();
                    await handler.HandleRequest(context);
                    return;
                }
            }

            // Docs
            if (method == "GET" && path.StartsWith("/docs"))
            {
                if (path == "/docs")
                {
                    response.StatusCode = 301;
                    response.Headers["Location"] = "/docs/";
                    response.Close();
                    return;
                }

                var relative = Uri.UnescapeDataString(path.TrimStart('/'));
                var filePath = Path.Combine(_wwwrootPath, relative).Replace("\\", "/");

                // 1. Точное совпадение
                if (!File.Exists(filePath))
                    filePath = Path.Combine(_wwwrootPath, relative, "index.html").Replace("\\", "/");

                // 2. Quartz кладёт страницы как Slug.html рядом, без подпапки
                if (!File.Exists(filePath))
                    filePath = Path.Combine(_wwwrootPath, relative + ".html").Replace("\\", "/");

                if (!File.Exists(filePath))
                {
                    response.StatusCode = 404;
                    var msg = Encoding.UTF8.GetBytes($"Not found: {filePath}");
                    await response.OutputStream.WriteAsync(msg);
                    response.Close();
                    return;
                }

                var rel = Path.GetRelativePath(_wwwrootPath, filePath).Replace("\\", "/");
                
                if (filePath.EndsWith(".html"))
                {
                    var html = await File.ReadAllTextAsync(filePath);
                    var inject = """
                                 <link rel="stylesheet" href="/css/themes.css">
                                 <link rel="stylesheet" href="/css/quartz-bridge.css">
                                 <script src="/js/theme.js"></script>
                                 <script>
                                   (function() {
                                     function applyTheme() {
                                       var theme = localStorage.getItem('zp-theme') || 'dark';
                                       var quartzTheme = theme === 'light' ? 'light' : 'dark';
                                       localStorage.setItem('theme', quartzTheme);
                                       document.documentElement.setAttribute('data-theme', theme);
                                       document.documentElement.setAttribute('saved-theme', quartzTheme);
                                     }
                                     applyTheme();
                                     document.addEventListener('nav', applyTheme);

                                     document.addEventListener('nav', function() {
                                       var old = document.getElementById('zp-dock-wrap');
                                       var oldZone = document.getElementById('zp-dock-zone');
                                       var oldOtp = document.getElementById('zp-otp-overlay');
                                       if (old) old.remove();
                                       if (oldZone) oldZone.remove();
                                       if (oldOtp) oldOtp.remove();
                                       var s = document.createElement('script');
                                       s.src = '/js/nav.js?' + Date.now();
                                       document.body.appendChild(s);
                                     });
                                   })();
                                 </script>
                                 """;
                    
                    var before = html.Contains("<script src=\"./prescript.js\"");

                    var prescriptTag = html.Contains("<script src=\"./prescript.js\"")
                        ? "<script src=\"./prescript.js\""
                        : "<script src=\"../prescript.js\"";

                    html = html.Replace(prescriptTag, inject + prescriptTag);
                    
                    html = html.Replace("</body>", "<script src=\"/js/nav.js\"></script></body>");

                    var after = html.Contains(inject.Substring(0, 20));
                    $"[docs inject] found={before} injected={after} path={filePath}".Debug();
                    
                    var bytes = Encoding.UTF8.GetBytes(html);
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes);
                    response.Close();
                    return;
                }

                await ServeFile(response, rel, _wwwrootPath);
                return;
            }
// Pages
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                var page = request.QueryString["page"] ?? "home";
                if (_debug )  $"[handler] Page → {page}".Debug();
                await ServePage(response, page);
                return;
            }

// Domain handlers
            if (path.StartsWith("/report"))
            {
                if (_debug )  $"[handler] ReportHandler → {path}".Debug();
                await _reportHandler.Handle(context, path);
                return;
            }
            if (path.StartsWith("/config") || path == "/clear-all-logs")
            {
                if (_debug )  $"[handler] ConfigHandler → {path}".Debug();
                await _configHandler.Handle(context);
                return;
            }
            if (_zbHandler.Matches(path)) 
            {
                $"[handler] ZbHandler → {method} {path}".Debug();
                await _zbHandler.Handle(context);
                 return;
            }
            if (_logHandler.Matches(path, method))
            {
                if (_debug )  $"[handler] LogHandler → {method} {path}".Debug();
                await _logHandler.Handle(context);
                return;
            }
            if (_httpLogHandler.Matches(path, method))
            {
                if (_debug )  $"[handler] HttpLogHandler → {method} {path}".Debug();
                await _httpLogHandler.Handle(context);
                return;
            }
            if (_zbHandler.Matches(path))
            {
                 $"[handler] ZbHandler → {method} {path}".Debug();
                 await _zbHandler.Handle(context);
                 return;
            }
            if (_aiReportHandler.Matches(path))
            {
                await _aiReportHandler.Handle(context);
                return;
            }
            if (path.StartsWith("/treasury"))
            {
                await _treasuryHandler.Handle(context);
                return;
            }

            if (_snapshotHandler.Matches(path))
            {
                await _snapshotHandler.Handle(context); 
                return;
            }
            if (_jsonAnalyzerHandler.Matches(path))
            {
                await _jsonAnalyzerHandler.Handle(context);
                return;
            }
            
            
// Static (wwwroot)
            if (method == "GET")
            {
                if (_debug ) $"[handler] StaticFile → {request.Url!.AbsolutePath}".Debug();
                await ServeFile(response, request.Url!.AbsolutePath.TrimStart('/'), _wwwrootPath);
                return;
            }

            if (_debug ) $"[handler] 404 → {method} {path}".Debug();
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