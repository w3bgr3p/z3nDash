using Microsoft.Playwright;
using z3n8;

namespace z3n8.Browser;

public static partial class PlaywrightExtensions
{
    public static async Task CloseExtraTabs(this IPage page, int keep = 1, bool blank = false)
    {
        var pages = page.Context.Pages;
    
        // Закрываем с конца, оставляем keep вкладок
        for (int i = pages.Count - 1; i >= keep; i--)
        {
            try
            {
                await pages[i].CloseAsync();
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                break;
            }
        }
    
        if (blank) await pages[0].GotoAsync("about:blank");
        Console.WriteLine("tabs closed");
    }
    public static async Task CloseExtraTabs(this IBrowserContext context, int keep = 1)
    {
        var pages = context.Pages;
    
        // Закрываем с конца, оставляем keep вкладок
        for (int i = pages.Count - 1; i >= keep; i--)
        {
            await pages[i].CloseAsync();
        }
    }
    
    public static async Task<IBrowserContext> NewInstance(this IPlaywright playwright, bool headless = false, string userDir = null, string extPath = null)
    {
        // Подготавливаем список аргументов
        var args = new List<string>();

        if (!string.IsNullOrEmpty(extPath))
        {
            string absoluteExtPath = Path.GetFullPath(extPath);
            args.Add($"--disable-extensions-except={absoluteExtPath}");
            args.Add($"--load-extension={absoluteExtPath}");
        }
        if (headless)
        {
            args.Add("--headless=new");
        }

        if (!string.IsNullOrEmpty(userDir))
        {
            return await playwright.Chromium.LaunchPersistentContextAsync(userDir, new()
            {
                Headless = false,
                Args = args,
                SlowMo = 50
            });
        }

        var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = false, // Для расширений всегда false
            Args = args,
            SlowMo = 50
        });

        return await browser.NewContextAsync();
    }
    public static async Task<IBrowserContext> ConnectToZb(this IPlaywright playwright, string profileId )
    {
        var zb = new ZB(Config.ApiConfig.ZB);
        string wsEndpoint = await zb.RunProfile(profileId);
        var browser = await playwright.Chromium.ConnectOverCDPAsync(wsEndpoint);
        return  browser.Contexts[0];
    }
    
    private static readonly Random _rnd = new Random();
    public static async Task Sleep(this IPage page, int max = 1500, int min = 50)
    {
        await Task.Delay(_rnd.Next(min, max));
    }
}