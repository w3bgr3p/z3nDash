using System.Net;
using System.Text;
using System.Text.Json;

namespace z3n8;

internal static class HttpHelpers
{
    internal static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf);
    }

    internal static async Task WriteRawJson(HttpListenerResponse response, string json)
    {
        response.ContentType = "application/json; charset=utf-8";
        var buf = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf);
        response.Close();
    }

    internal static async Task WriteText(HttpListenerResponse response, string text)
    {
        var buf = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = buf.Length;
        await response.OutputStream.WriteAsync(buf);
    }

    internal static async Task<(bool ok, string? taskId)> ReadTaskId(HttpListenerRequest request)
    {
        using var r = new StreamReader(request.InputStream);
        var body    = await r.ReadToEndAsync();
        var json    = JsonSerializer.Deserialize<JsonElement>(body);
        var taskId  = json.TryGetProperty("task_id", out var t) ? t.GetString() : null;
        return (!string.IsNullOrEmpty(taskId), taskId);
    }
}
