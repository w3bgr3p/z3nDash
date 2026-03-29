using System.Text;


public class HTTP
{
    // 1. Создаем клиента сразу при объявлении, используя статический метод
    private static readonly HttpClient _instance = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        // Создаем "двигатель" для сетевых запросов
        var networkHandler = new HttpClientHandler();

        // Оборачиваем его в твой логгер
        var debugHandler = new z3n8.HttpDebugHandler(
            projectName: "SUK",
            logHost: "http://localhost:10993/http-log"
        ) 
        { 
            InnerHandler = networkHandler 
        };

        // Создаем HttpClient ОДИН РАЗ
        return new HttpClient(debugHandler);
    }

    // 2. Метод просто возвращает уже созданный экземпляр
    public HttpClient Client()
    {
        return _instance;
    }
    
    
    
}

public static class HttpClientExtensions
{
    public static async Task<string> POST(this HttpClient client, string url, string jsonBody, string[] headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        
        if (headers != null)
        {
            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    
                    // Content headers должны идти в Content.Headers, не в request.Headers
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue; // Уже установлено через StringContent
                    
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }
        }
        
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> GET(this HttpClient client, string url, string[] headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        if (headers != null)
        {
            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
                }
            }
        }
        
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
    
    public static async Task<string> PUT(this HttpClient client, string url, string jsonBody, string[] headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        
        if (headers != null)
        {
            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }
        }
        
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> DELETE(this HttpClient client, string url, string[] headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        
        if (headers != null)
        {
            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
                }
            }
        }
        
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}