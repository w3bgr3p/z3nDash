using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nIO;

/// <summary>
/// AI Agent chat handler with SSE streaming support.
/// Routes:
///   POST /ai/chat          - SSE stream chat
///   POST /ai/interrupt     - Interrupt active chat
///   GET  /ai/providers     - Get available models
///   GET  /ai/health        - Health check
/// </summary>
internal sealed class AiAgentHandler
{
    private readonly DbConnectionService _dbService;
    private readonly AiClient _aiClient;

    // Active chat sessions (chatId -> cancellation token)
    private static readonly Dictionary<string, CancellationTokenSource> _activeSessions = new();

    public AiAgentHandler(DbConnectionService dbService, AiClient aiClient)
    {
        _dbService = dbService;
        _aiClient = aiClient;
    }

    public bool Matches(string path) => path.StartsWith("/ai/");

    public async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.ToLower() ?? "";
        var method = ctx.Request.HttpMethod;

        try
        {
            if (path == "/ai/chat" && method == "POST")
            {
                await HandleChat(ctx);
                return;
            }

            if (path == "/ai/interrupt" && method == "POST")
            {
                await HandleInterrupt(ctx);
                return;
            }

            if (path == "/ai/providers" && method == "GET")
            {
                await HandleProviders(ctx);
                return;
            }

            if (path == "/ai/health" && method == "GET")
            {
                await HandleHealth(ctx);
                return;
            }

            if (path == "/ai/cwd" && method == "GET")
            {
                await HandleCwd(ctx);
                return;
            }

            ctx.Response.StatusCode = 404;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "not found" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ai] error: {ex.Message}");
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── POST /ai/chat ──────────────────────────────────────────────────────────

    private async Task HandleChat(HttpListenerContext ctx)
    {
        if (!_aiClient.IsEnabled)
        {
            ctx.Response.StatusCode = 503;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "AI not enabled" });
            return;
        }

        string chatId, message, cwd, model;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);

            chatId = json.TryGetProperty("chatId", out var c) ? c.GetString() ?? "" : "";
            message = json.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            cwd = json.TryGetProperty("cwd", out var w) ? w.GetString() ?? "" : "";
            model = json.TryGetProperty("model", out var md) ? md.GetString() ?? "" : "";
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" });
            return;
        }

        if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(message))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "chatId and message required" });
            return;
        }

        if (string.IsNullOrEmpty(model))
            model = "kr/claude-sonnet-4.5";

        // Setup SSE
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.Headers.Add("X-Accel-Buffering", "no");
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.StatusCode = 200;

        var cts = new CancellationTokenSource();
        _activeSessions[chatId] = cts;

        try
        {
            await StreamResponse(ctx.Response.OutputStream, message, model, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await SendSSE(ctx.Response.OutputStream, "error", new { error = "interrupted" });
        }
        catch (Exception ex)
        {
            await SendSSE(ctx.Response.OutputStream, "error", new { error = ex.Message });
        }
        finally
        {
            _activeSessions.Remove(chatId);
            cts.Dispose();
        }

        await SendSSE(ctx.Response.OutputStream, "done", new { });
        ctx.Response.Close();
    }

    private async Task StreamResponse(Stream output, string message, string model, CancellationToken ct)
    {
        // Simple streaming: call AI and stream back the response
        // For now, we'll simulate streaming by chunking the response
        // In a real implementation, you'd integrate with an actual streaming API

        var systemPrompt = "You are a helpful AI assistant with access to files and tools. " +
                          "Provide clear, concise answers. When working with code, be specific and actionable.";

        try
        {
            // Call AI (non-streaming for now)
            var response = await _aiClient.CompleteAsync(
                model: model,
                systemPrompt: systemPrompt,
                userPrompt: message,
                temp: 0.7,
                maxTokens: 4000,
                timeoutSec: 120
            );

            // Simulate streaming by sending chunks
            var chunkSize = 50;
            for (int i = 0; i < response.Length; i += chunkSize)
            {
                ct.ThrowIfCancellationRequested();

                var chunk = response.Substring(i, Math.Min(chunkSize, response.Length - i));
                await SendSSE(output, "delta", new { text = chunk });
                await Task.Delay(20, ct); // Small delay to simulate streaming
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await SendSSE(output, "error", new { error = ex.Message });
        }
    }

    private static async Task SendSSE(Stream output, string eventType, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var message = $"event: {eventType}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(message);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    // ── POST /ai/interrupt ─────────────────────────────────────────────────────

    private async Task HandleInterrupt(HttpListenerContext ctx)
    {
        string chatId;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            chatId = json.TryGetProperty("chatId", out var c) ? c.GetString() ?? "" : "";
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "invalid body" });
            return;
        }

        if (string.IsNullOrEmpty(chatId))
        {
            ctx.Response.StatusCode = 400;
            await HttpHelpers.WriteJson(ctx.Response, new { error = "chatId required" });
            return;
        }

        if (_activeSessions.TryGetValue(chatId, out var cts))
        {
            cts.Cancel();
            await HttpHelpers.WriteJson(ctx.Response, new { ok = true });
        }
        else
        {
            await HttpHelpers.WriteJson(ctx.Response, new { ok = false, error = "session not found" });
        }
    }

    // ── GET /ai/providers ──────────────────────────────────────────────────────

    private async Task HandleProviders(HttpListenerContext ctx)
    {
        try
        {
            // Try to get models from omniroute
            var models = new List<object>();

            if (Config.AiConfig.Provider == "omniroute")
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var url = Config.AiConfig.OmniRouteHost.TrimEnd('/') + "/api/models";
                    var response = await http.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var data = JsonSerializer.Deserialize<JsonElement>(json);

                        if (data.TryGetProperty("models", out var modelsArray))
                        {
                            foreach (var model in modelsArray.EnumerateArray())
                            {
                                var fullModel = model.TryGetProperty("fullModel", out var fm) ? fm.GetString() : "";
                                var name = model.TryGetProperty("name", out var n) ? n.GetString() : fullModel;
                                var available = model.TryGetProperty("available", out var a) && a.GetBoolean();

                                if (!string.IsNullOrEmpty(fullModel))
                                {
                                    models.Add(new { fullModel, name, available });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ai] failed to fetch models from omniroute: {ex.Message}");
                }
            }

            // Fallback to default models
            if (models.Count == 0)
            {
                models.Add(new { fullModel = "kr/claude-sonnet-4.5", name = "Claude Sonnet 4.5", available = true });
                models.Add(new { fullModel = "deepseek-ai/DeepSeek-V3.2", name = "DeepSeek V3.2", available = true });
            }

            await HttpHelpers.WriteJson(ctx.Response, new { models });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await HttpHelpers.WriteJson(ctx.Response, new { error = ex.Message });
        }
    }

    // ── GET /ai/health ─────────────────────────────────────────────────────────

    private async Task HandleHealth(HttpListenerContext ctx)
    {
        var health = new
        {
            ok = _aiClient.IsEnabled,
            provider = Config.AiConfig.Provider,
            activeSessions = _activeSessions.Count
        };

        await HttpHelpers.WriteJson(ctx.Response, health);
    }

    // ── GET /ai/cwd ────────────────────────────────────────────────────────────

    private async Task HandleCwd(HttpListenerContext ctx)
    {
        // Return the application root directory
        var cwd = AppContext.BaseDirectory;
        await HttpHelpers.WriteJson(ctx.Response, new { cwd });
    }
}
