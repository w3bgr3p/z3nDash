namespace BasBridgeRegression;

public delegate void ExpectFn(string name, bool ok, string detail = "");

public static class Out
{
    /// Печать, которая не падает на кириллице в консоли cp1251 (правило 8).
    public static void P(string msg)
    {
        try { Console.WriteLine(msg); }
        catch (Exception) { try { Console.WriteLine(new string(msg.Select(c => c < 128 ? c : '?').ToArray())); } catch { } }
    }
}
