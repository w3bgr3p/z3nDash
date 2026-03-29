using System.Net.Http.Headers;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using z3n8;

//using System.Text.Json;


public class ZB
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private static string _baseUrl = "http://localhost:8160/v1/";

    public ZB(string apiKey)
    {
        _apiKey = apiKey;

        var baseHandler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
        };

        var debugHandler = new HttpDebugHandler("ZB")
        {
            InnerHandler = baseHandler
        };

        _httpClient = new HttpClient(debugHandler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<string> ZBGetAsync(string apiCommand)
    {
        var request = new HttpRequestMessage
        {
            Method = System.Net.Http.HttpMethod.Get,
            RequestUri = new Uri(_baseUrl + apiCommand),
            Headers =
            {
                { "Api-Token", _apiKey },
            },
        };

        using (var response = await _httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return body;
        }
    }

    public async Task<string> ZBPostAsync(string apiCommand)
    {
        var request = new HttpRequestMessage
        {
            Method = System.Net.Http.HttpMethod.Post,
            RequestUri = new Uri(_baseUrl + apiCommand),
            Headers =
            {
                { "Api-Token", _apiKey },
            },
        };

        using (var response = await _httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return body;
        }
    }

    public async Task<string> ZBDeleteAsync(string apiCommand)
    {
        var request = new HttpRequestMessage
        {
            Method = System.Net.Http.HttpMethod.Delete,
            RequestUri = new Uri(_baseUrl + apiCommand),
            Headers =
            {
                { "Api-Token", _apiKey },
            },
        };

        using (var response = await _httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return body;
        }
    }

    public string ZBGet(string apiCommand)
    {
        return ZBGetAsync(apiCommand).GetAwaiter().GetResult();
        // или .Result — разницы почти нет
    }
    public string ZBPost(string apiCommand)
    {
        return ZBPostAsync(apiCommand).GetAwaiter().GetResult();
    }
    public string ZBDel(string apiCommand)
    {
        return ZBDeleteAsync(apiCommand).GetAwaiter().GetResult();
    }


    public async Task<string> RunProfile(string profileId)
    {
        string rawResponse =
            await ZBPostAsync(
                $"browser_instances/create?profileId={profileId}&workspaceId=-1&desktopName=&threadToken=");
        var data = JObject.Parse(rawResponse);

        // Достаем строку (используем оператор ? на случай если поля нет)
        string wsEndpoint = data["connectionString"]?.ToString();

        return wsEndpoint;
    }
    public async Task<string> ProfileDown(string profileId)
    {
        string rawResponse = await ZBDeleteAsync($"browser_instances/{profileId}?workspaceId=-1");

        // ✅ Проверяем, что ответ не пустой
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return "Profile stopped successfully (no response body)";
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);

            // Возвращаем весь JSON или конкретное поле, если оно есть
            if (doc.RootElement.TryGetProperty("message", out var msg))
            {
                return msg.GetString();
            }

            return rawResponse; // Вернуть весь JSON
        }
        catch (JsonException)
        {
            // Если это не JSON, вернуть как есть
            return rawResponse;
        }
    }
    
    
}