using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using z3nIO;
using z3nIO.Browser;

/// <summary>
/// Extension-методы для IBrowserInstance.
/// Заменяют InstanceExtensions.cs из z3nCore — те же сигнатуры,
/// работают с обоими бэкендами (ZennoPoster и Playwright).
/// </summary>
public static partial class InstanceExtensions
{
    private static readonly Time.Sleeper _clickSleep = new(1008, 1337);
    private static readonly Time.Sleeper _inputSleep = new(1337, 2077);
    private static readonly Random       _rnd        = new();

    // ── GetHe ─────────────────────────────────────────────────────────────────

    public static IHeElement GetHe(this IBrowserInstance instance, object obj, string method = "")
    {
        if (obj is IHeElement he) { if (he.IsVoid) throw new Exception("IHeElement is void"); return he; }

        var type   = obj.GetType();
        var fields = type.GetFields();

        if (fields.Length == 2)
        {
            string value = fields[0].GetValue(obj).ToString();
            string attr  = fields[1].GetValue(obj).ToString().ToLower();
            var found = attr switch
            {
                "id"   => instance.FindElementById(value),
                "name" => instance.FindElementByName(value),
                _      => throw new ArgumentException($"Unsupported 2-field attr: {attr}")
            };
            if (found.IsVoid) throw new Exception($"no element by {attr}=[{value}]");
            return found;
        }

        if (fields.Length == 5)
        {
            string tag     = fields[0].GetValue(obj).ToString();
            string attr    = fields[1].GetValue(obj).ToString();
            string pattern = fields[2].GetValue(obj).ToString();
            string mode    = fields[3].GetValue(obj).ToString();
            int    pos     = int.Parse(fields[4].GetValue(obj).ToString());

            if (method == "last")
            {
                var all = instance.FindElementsByAttribute(tag, attr, pattern, mode).ToList();
                if (all.Count == 0) throw new Exception($"no element (last): {tag}[{attr}='{pattern}']");
                return all[^1];
            }

            if (method == "random")
            {
                var all = instance.FindElementsByAttribute(tag, attr, pattern, mode).ToList();
                if (all.Count == 0) throw new Exception($"no element (random): {tag}[{attr}='{pattern}']");
                return all[_rnd.Next(all.Count)];
            }

            var el = instance.FindElementByAttribute(tag, attr, pattern, mode, pos);
            if (el.IsVoid) throw new Exception($"no element: {tag}[{attr}='{pattern}'] mode={mode} pos={pos}");
            return el;
        }

        throw new ArgumentException($"Unsupported selector type: {type.Name}");
    }

    // ── HeGet ─────────────────────────────────────────────────────────────────

    public static string HeGet(this IBrowserInstance instance, object obj, string method = "",
        int deadline = 10, string atr = "innertext", int delay = 1,
        bool thrw = true, bool thr0w = true, bool waitTillVoid = false)
    {
        if (!thr0w) thrw = false;
        var start = DateTime.Now;

        while (true)
        {
            if ((DateTime.Now - start).TotalSeconds > deadline)
            {
                if (waitTillVoid) return null;
                if (thrw) throw new TimeoutException($"HeGet {deadline}s: {obj} url={instance.ActiveTab.URL}");
                return null;
            }

            try
            {
                var he = instance.GetHe(obj, method);
                if (waitTillVoid) throw new Exception("element present when expected void");
                Thread.Sleep(delay * 1000);
                return he.GetAttribute(atr);
            }
            catch (Exception ex)
            {
                if (waitTillVoid && ex.Message.Contains("no element")) { /* ожидаем исчезновения */ }
                else if (!waitTillVoid) { /* ожидаем появления */ }
                else throw;
            }

            Thread.Sleep(500);
        }
    }

    // ── HeClick ───────────────────────────────────────────────────────────────

    public static void HeClick(this IBrowserInstance instance, object obj, string method = "",
        int deadline = 10, double delay = 1, string comment = "",
        bool thrw = true, bool thr0w = true, int emu = 0)
    {
        if (!thr0w) thrw = false;

        bool snap = instance.UseFullMouseEmulation;
        if (emu > 0) instance.UseFullMouseEmulation = true;
        if (emu < 0) instance.UseFullMouseEmulation = false;

        var start = DateTime.Now;

        while (true)
        {
            if ((DateTime.Now - start).TotalSeconds > deadline)
            {
                instance.UseFullMouseEmulation = snap;
                if (thrw) throw new TimeoutException($"{comment} HeClick {deadline}s");
                return;
            }

            try
            {
                var he = instance.GetHe(obj, method);
                _clickSleep.Sleep(delay);
                he.RiseEvent("click", instance.EmulationLevel);
                instance.UseFullMouseEmulation = snap;

                if (method == "clickOut")
                    while (true)
                    {
                        try   { var h = instance.GetHe(obj, method); _clickSleep.Sleep(delay); h.RiseEvent("click", instance.EmulationLevel); }
                        catch { break; }
                    }

                return;
            }
            catch { instance.UseFullMouseEmulation = snap; }

            Thread.Sleep(500);
        }
    }

    public static void HeMultiClick(this IBrowserInstance instance, List<object> selectors)
    {
        foreach (var s in selectors) instance.HeClick(s);
    }

    // ── HeSet ─────────────────────────────────────────────────────────────────

    public static void HeSet(this IBrowserInstance instance, object obj, string value,
        string method = "id", int deadline = 10, double delay = 1,
        string comment = "", bool thrw = true, bool thr0w = true)
    {
        if (!thr0w) thrw = false;
        var start = DateTime.Now;

        while (true)
        {
            if ((DateTime.Now - start).TotalSeconds > deadline)
            {
                if (thrw) throw new TimeoutException($"{comment} HeSet {deadline}s");
                return;
            }

            try
            {
                var he = instance.GetHe(obj, method);
                _inputSleep.Sleep(delay);
                instance.WaitFieldEmulationDelay();
                he.SetValue(value, "Full", false);
                return;
            }
            catch { }

            Thread.Sleep(500);
        }
    }

    // ── HeDrop ────────────────────────────────────────────────────────────────

    public static void HeDrop(this IBrowserInstance instance, object obj, string method = "",
        int deadline = 10, bool thrw = true)
    {
        var start = DateTime.Now;
        while (true)
        {
            if ((DateTime.Now - start).TotalSeconds > deadline)
            {
                if (thrw) throw new TimeoutException($"HeDrop {deadline}s");
                return;
            }
            try { var he = instance.GetHe(obj, method); he.ParentElement.RemoveChild(he); return; }
            catch { }
            Thread.Sleep(500);
        }
    }

    // ── Browser management ────────────────────────────────────────────────────

    public static void ClearShit(this IBrowserInstance instance, string domain)
    {
        instance.CloseAllTabs();
        instance.ClearCache(domain);
        instance.ClearCookie(domain);
        Thread.Sleep(500);
        instance.ActiveTab.Navigate("about:blank", "");
    }

    public static void CloseExtraTabs(this IBrowserInstance instance, bool blank = false, int tabToKeep = 1)
    {
        for (;;)
        {
            try
            {
                var tabs = instance.AllTabs;
                if (tabs.Count <= tabToKeep) break;
                ((dynamic)tabs[tabToKeep]).Close();
                Thread.Sleep(100);
            }
            catch { break; }
        }
        if (blank) instance.ActiveTab.Navigate("about:blank", "");
    }

    public static void Go(this IBrowserInstance instance, string url,
        bool strict = false, bool waitTdle = false, bool newTab = false)
    {
        if (newTab) instance.NewTab();
        string current = instance.ActiveTab.URL;
        bool go = strict ? current != url : !current.Contains(url);
        if (go) instance.ActiveTab.Navigate(url, "");
        if (instance.ActiveTab.IsBusy && waitTdle) instance.ActiveTab.WaitDownloading();
    }

    public static void F5(this IBrowserInstance instance, bool waitTillLoad = true)
    {
        instance.ActiveTab.MainDocument.EvaluateScript("location.reload(true)");
        if (instance.ActiveTab.IsBusy && waitTillLoad) instance.ActiveTab.WaitDownloading();
    }

    public static void ScrollDown(this IBrowserInstance instance, int y = 420)
        => instance.ActiveTab.FullEmulationMouseWheel(0, y);

    public static string SaveCookies(this IBrowserInstance instance)
    {
        string tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"cookies_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N8}.txt");
        try   { instance.SaveCookie(tmp); return System.IO.File.ReadAllText(tmp); }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
    }

    public static void SetTimeFromDb(this IBrowserInstance instance,
        ZennoLab.InterfacesLibrary.ProjectModel.IZennoPosterProjectModel project)
    {
        var timezone = project.DbGet("timezone", "_instance");
        if (string.IsNullOrEmpty(timezone)) { project.warn("no time zone data found in db"); return; }
        var tz = Newtonsoft.Json.Linq.JObject.Parse(timezone);
        instance.SetTimezone((int)tz["timezoneOffset"], 0);
        instance.SetIanaTimezone(tz["timezoneName"].ToString());
    }
}