using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace z3nIO;

public sealed class TerminalHandler : IScriptHandler
{
    public string PathPrefix => "/terminal";

    private readonly string _wwwrootPath;
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    public TerminalHandler(string wwwrootPath) => _wwwrootPath = wwwrootPath;

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        var path   = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = context.Request.HttpMethod;

        if (!path.StartsWith("/terminal")) return false;

        try
        {
            if (path is "/terminal" or "/terminal/" or "/terminal.html")
                { await ServePage(context.Response); return true; }

            if (path == "/terminal/list" && method == "GET")
                { await List(context.Response); return true; }

            if (path == "/terminal/create" && method == "POST")
                { await Create(context); return true; }

            if (path == "/terminal/close" && method == "POST")
                { await Close(context); return true; }

            if (path == "/terminal/resize" && method == "POST")
                { await HttpHelpers.WriteJson(context.Response, new { ok = true }); return true; }

            if (path == "/terminal/ws" && context.Request.IsWebSocketRequest)
                { await HandleWebSocket(context); return true; }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(context.Response, new { error = ex.Message });
        }

        return true;
    }

    private async Task List(HttpListenerResponse res)
    {
        var list = _sessions.Values.Select(s => new
        {
            s.Id, s.Label, s.Shell, s.Cwd, s.CreatedAt, alive = s.IsAlive
        }).ToList();
        await HttpHelpers.WriteJson(res, list);
    }

    private async Task Create(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(body); }
        catch { ctx.Response.StatusCode = 400; return; }

        var shell = json.TryGetProperty("shell", out var s) ? s.GetString() ?? "cmd" : "cmd";
        var cwd   = json.TryGetProperty("cwd",   out var c) ? c.GetString() ?? ""    : "";
        var label = json.TryGetProperty("label", out var l) ? l.GetString() ?? shell  : shell;

        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = AppContext.BaseDirectory;

        var session = new TerminalSession(shell, cwd, label);
        _sessions[session.Id] = session;

        await HttpHelpers.WriteJson(ctx.Response, new
        {
            Id    = session.Id,
            Label = session.Label,
            Shell = session.Shell,
            Cwd   = session.Cwd
        });
    }

    private async Task Close(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(body); }
        catch { ctx.Response.StatusCode = 400; return; }

        var id = json.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
        if (_sessions.TryRemove(id, out var session)) session.Dispose();

        await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
    }

    private async Task HandleWebSocket(HttpListenerContext ctx)
    {
        var id = ctx.Request.QueryString["id"] ?? "";
        if (!_sessions.TryGetValue(id, out var session))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        await session.Attach(wsCtx.WebSocket);
    }

    private async Task ServePage(HttpListenerResponse res)
    {
        var filePath = Path.Combine(_wwwrootPath, "terminal.html");
        if (!File.Exists(filePath))
        {
            res.StatusCode = 404;
            var msg = Encoding.UTF8.GetBytes($"terminal.html not found: {filePath}");
            await res.OutputStream.WriteAsync(msg);
            res.Close();
            return;
        }
        var bytes = await File.ReadAllBytesAsync(filePath);
        res.ContentType     = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}

internal sealed class TerminalSession : IDisposable
{
    public string Id        { get; } = Guid.NewGuid().ToString();
    public string Label     { get; }
    public string Shell     { get; }
    public string Cwd       { get; }
    public string CreatedAt { get; } = DateTime.Now.ToString("HH:mm:ss");
    public bool   IsAlive   => _process is { HasExited: false };

    private static readonly Dictionary<string, (string exe, string args)> ShellMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bash"]       = (@"C:\Program Files\Git\bin\bash.exe", @"--login -c ""export COLUMNS=220 LINES=50; exec bash -i"""),
            ["cmd"]        = ("cmd.exe",        ""),
            ["powershell"] = ("powershell.exe", "-NoLogo"),
            ["pwsh"]       = ("pwsh.exe",       "-NoLogo"),
        };

    private readonly Process _process;
    private bool _disposed;

    public TerminalSession(string shell, string cwd, string label)
    {
        Shell = shell;
        Cwd   = cwd;
        Label = label;

        if (!ShellMap.TryGetValue(shell, out var info))
            info = ("cmd.exe", "");

        // bash fallback
        if (shell == "bash" && !File.Exists(info.exe))
            info = ("cmd.exe", "");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = info.exe,
                Arguments              = info.args,
                WorkingDirectory       = cwd,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            }
        };

        _process.StartInfo.EnvironmentVariables["COLUMNS"] = "220";
        _process.StartInfo.EnvironmentVariables["LINES"]   = "50";
        if (shell == "cmd")
            _process.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        _process.Start();
        _process.StandardInput.AutoFlush = true;

        if (shell == "cmd")
            _process.StandardInput.WriteLine("chcp 65001 > nul");

    }

    public async Task Attach(WebSocket ws)
    {
        using var cts = new CancellationTokenSource();

        var r1 = PipeToWs(_process.StandardOutput.BaseStream, ws, cts.Token);
        var r2 = PipeToWs(_process.StandardError.BaseStream,  ws, cts.Token);
        var r3 = WsToProcess(ws, cts.Token);
        var r4 = Task.Run(() => _process.WaitForExit(), cts.Token);

        await Task.WhenAny(r3, r4);
        cts.Cancel();
        try { await Task.WhenAll(r1, r2, r3, r4); } catch { }

        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // stdout/stderr → base64 text → ws
    private static async Task PipeToWs(Stream stream, WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (n == 0) break;

                var b64bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(buf, 0, n));
                await ws.SendAsync(new ArraySegment<byte>(b64bytes, 0, b64bytes.Length),
                    WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ws → stdin (клиент шлёт raw text)
    private async Task WsToProcess(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                await _process.StandardInput.WriteAsync(text.AsMemory(), ct);
                await _process.StandardInput.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        // stdin НЕ закрываем — процесс должен жить
    }

    public void Resize(int cols, int rows) { /* plain Process не поддерживает resize */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.Kill(true); } catch { }
        _process.Dispose();
    }
}