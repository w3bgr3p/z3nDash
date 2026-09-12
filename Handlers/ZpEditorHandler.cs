/*
 * Copyright (C) 2026 [w3bgr3p]
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Net;
using System.Text;
using System.Text.Json;

namespace z3nDash;

/// <summary>
/// Handler для ZP Flow Editor — интерактивного редактора ZennoPoster XML схем.
///
/// Endpoints:
///   GET  /zp-editor/xml?path=...  — загрузить XML из файла
///   POST /zp-editor/xml           — сохранить XML в файл { path, xml }
/// </summary>
public class ZpEditorHandler : IScriptHandler
{
    public string PathPrefix => "/zp-editor";

    public void Init() { }

    public async Task<bool> HandleRequest(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.ToLower() ?? "";
        string method = context.Request.HttpMethod;

        // Обрабатываем только API endpoints, остальное пропускаем к статике
        if (!path.StartsWith("/zp-editor/xml")) return false;

        try
        {
            if (path == "/zp-editor/xml" && method == "GET")
            {
                await GetXml(context);
                return true;
            }

            if (path == "/zp-editor/xml" && method == "POST")
            {
                await PostXml(context);
                return true;
            }
        }
        catch (Exception ex)
        {
            await WriteError(context.Response, 500, ex.Message);
        }

        return false; // Пропустить к статике если не наш endpoint
    }

    // ── GET /zp-editor/xml?path=... ────────────────────────────────────────────

    private async Task GetXml(HttpListenerContext ctx)
    {
        var filePath = ctx.Request.QueryString["path"] ?? "";

        if (string.IsNullOrEmpty(filePath))
        {
            await WriteError(ctx.Response, 400, "path parameter required");
            return;
        }

        if (!File.Exists(filePath))
        {
            await WriteError(ctx.Response, 404, $"File not found: {filePath}");
            return;
        }

        try
        {
            string xml;

            // Если это .zp файл — извлекаем XML
            if (filePath.EndsWith(".zp", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                xml = Encoding.UTF8.GetString(bytes);

                // Пытаемся найти XML внутри (ZP хранит XML как текст)
                var xmlStart = xml.IndexOf("<?xml", StringComparison.Ordinal);
                if (xmlStart >= 0)
                {
                    xml = xml.Substring(xmlStart);
                }
            }
            else
            {
                // Обычный XML файл
                xml = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            }

            await WriteJson(ctx.Response, new { path = filePath, xml });
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 500, $"Failed to read file: {ex.Message}");
        }
    }

    // ── POST /zp-editor/xml ────────────────────────────────────────────────────

    private async Task PostXml(HttpListenerContext ctx)
    {
        var json = await ReadJson(ctx.Request);
        if (json == null)
        {
            await WriteError(ctx.Response, 400, "Invalid JSON");
            return;
        }

        var filePath = json.Value.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var xml = json.Value.TryGetProperty("xml", out var x) ? x.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(filePath))
        {
            await WriteError(ctx.Response, 400, "path required");
            return;
        }

        if (string.IsNullOrEmpty(xml))
        {
            await WriteError(ctx.Response, 400, "xml required");
            return;
        }

        try
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(filePath, xml, Encoding.UTF8);
            await WriteJson(ctx.Response, new { ok = true, path = filePath });
        }
        catch (Exception ex)
        {
            await WriteError(ctx.Response, 500, $"Failed to write file: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<JsonElement?> ReadJson(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    private static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task WriteError(HttpListenerResponse response, int code, string message)
    {
        response.StatusCode = code;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
