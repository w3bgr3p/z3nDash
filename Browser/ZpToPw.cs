using Microsoft.Playwright;
using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public static partial class PlaywrightExtensions
{
    private static readonly Random _random = new Random();
    
    
    
    
    public static ILocator GetHe(this IPage page, object obj, string method = "")
    {
        if (obj is ILocator locator) return locator;

        Type type = obj.GetType();
        FieldInfo[] fields = type.GetFields();
        int objLength = fields.Length;

        if (objLength == 2)
        {
            string value = fields[0].GetValue(obj).ToString();
            string attrType = fields[1].GetValue(obj).ToString();

            return attrType.ToLower() switch
            {
                "id" => page.Locator($"id={value}"),
                "name" => page.Locator($"[name='{value}']"),
                _ => page.Locator($"[{attrType}='{value}']")
            };
        }
        else if (objLength == 5)
        {
            string tag = fields[0].GetValue(obj).ToString();
            string attribute = fields[1].GetValue(obj).ToString();
            string pattern = fields[2].GetValue(obj).ToString();
            string mode = fields[3].GetValue(obj).ToString();
            int pos = int.Parse(fields[4].GetValue(obj).ToString());

            ILocator baseLocator;

            if (attribute.ToLower() == "innertext" || attribute.ToLower() == "text")
            {
                // ИСПРАВЛЕНО: Filter принимает объект LocatorFilterOptions, 
                // где HasTextString — это строка, а HasText — это Regex (но в .NET SDK это делается через Regex объект)
                if (mode == "regexp")
                    baseLocator = page.Locator(tag).Filter(new LocatorFilterOptions { HasTextRegex = new Regex(pattern, RegexOptions.IgnoreCase) });
                else
                    baseLocator = page.Locator(tag).Filter(new LocatorFilterOptions { HasTextString = pattern });
            }
            else
            {
                if (mode == "regexp")
                    baseLocator = page.Locator($"xpath=//{tag}[re:test(@{attribute}, '{pattern}', 'i')]");
                else
                    baseLocator = page.Locator($"{tag}[{attribute}='{pattern}']");
            }

            if (method.ToLower() == "last") return baseLocator.Last;
            
            if (method.ToLower() == "random")
            {
                int count = baseLocator.CountAsync().GetAwaiter().GetResult();
                if (count == 0) throw new Exception($"No elements for random: {pattern}");
                return baseLocator.Nth(_random.Next(0, count));
            }

            return baseLocator.Nth(pos);
        }

        throw new ArgumentException($"Unsupported selector type: {type.Name}");
    }

    
    
    
    
    #region Element Actions
    public static async Task<string> HeGet(this IPage page, object obj, string method = "", int deadline = 10, string atr = "innertext", bool thrw = true)
    {
        try
        {
            var locator = page.GetHe(obj, method);
            float timeout = deadline * 1000;

            // ИСПРАВЛЕНО: Разные методы требуют разные типы Options
            if (atr.ToLower() == "innertext") 
                return await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeout });
            
            if (atr.ToLower() == "value") 
                return await locator.InputValueAsync(new LocatorInputValueOptions { Timeout = timeout });
            
            return await locator.GetAttributeAsync(atr, new LocatorGetAttributeOptions { Timeout = timeout });
        }
        catch (Exception)
        {
            if (thrw) throw;
            return null;
        }
    }

    public static async Task HeClick(this IPage page, object obj, string method = "", int deadline = 10, string comment = "", bool thrw = true)
    {
        try
        {
            var locator = page.GetHe(obj, method);
            var options = new LocatorClickOptions { Timeout = deadline * 1000 };
            
            await locator.ClickAsync(options);
            
            if (method == "clickOut")
            {
                while (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                {
                    try { await locator.ClickAsync(new LocatorClickOptions { Timeout = 1000 }); } catch { break; }
                    await Task.Delay(500);
                }
            }
        }
        catch (Exception ex)
        {
            if (thrw) throw new TimeoutException($"{comment} click failed: {ex.Message}");
        }
    }

    public static async Task HeSet(this IPage page, object obj, string value, string method = "id", int deadline = 10, string comment = "", bool thrw = true)
    {
        try
        {
            var locator = page.GetHe(obj, method);
            await locator.FillAsync(value, new LocatorFillOptions { Timeout = deadline * 1000 });
        }
        catch (Exception ex)
        {
            if (thrw) throw new TimeoutException($"{comment} set failed: {ex.Message}");
        }
    }
    #endregion
  
}