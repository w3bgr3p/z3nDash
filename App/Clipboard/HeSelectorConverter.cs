using System.Text.RegularExpressions;

namespace z3nDash;

public enum HeAction { Get = 1, Click = 2, Set = 3 }

/// <summary>
/// Преобразует скопированную из ProjectMaker строку поиска элемента
/// в вызов хелпера проекта (ZpRuntime/Z3n7/InstanceExtencions.cs).
/// Чистая функция: ни буфера обмена, ни конфига, ни WinForms.
/// Регексп перенесён дословно из clipboard-heget.ps1.
/// </summary>
public static class HeSelectorConverter
{
    public static string? Convert(string? input, HeAction action)
    {
        if (string.IsNullOrEmpty(input)) return null;

        string literal = "\"(?:[^\"\\\\\r\n]|\\\\[^\r\n])*\"";
        string args = literal + @"\s*,\s*" + literal + @"\s*,\s*" + literal
            + @"\s*,\s*" + literal + @"\s*,\s*\d+";

        Match match = Regex.Match(input,
            @"(?m)^\s*(?:HtmlElement|var)\s+\w+\s*=\s*instance\.ActiveTab\.FindElementByAttribute\(\s*(?<args>"
            + args + @")\s*\)\s*;");

        if (!match.Success) return null;

        string selector = "(" + match.Groups["args"].Value + ")";
        return action switch
        {
            HeAction.Get   => "var msg = HeGet(" + selector + ");",
            HeAction.Click => "instance.HeClick(" + selector + ");",
            HeAction.Set   => "instance.HeSet(" + selector + ", \"value\");",
            _              => null,
        };
    }

    public readonly record struct SelfTestCase(string Name, bool Passed, string Expected, string Actual);

    private const string Sample =
        "HtmlElement he = instance.ActiveTab.FindElementByAttribute(\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0);\r\n"
        + "if (he.IsVoid) return -1;";

    public static IReadOnlyList<SelfTestCase> SelfTest()
    {
        var cases = new List<SelfTestCase>();

        void Check(string name, string? expected, string? actual) =>
            cases.Add(new SelfTestCase(name, expected == actual, expected ?? "<null>", actual ?? "<null>"));

        Check("Get",   "var msg = HeGet((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0));",          Convert(Sample, HeAction.Get));
        Check("Click", "instance.HeClick((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0));",          Convert(Sample, HeAction.Click));
        Check("Set",   "instance.HeSet((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0), \"value\");", Convert(Sample, HeAction.Set));

        Check("Unrelated text / Get",   null, Convert("ordinary clipboard text", HeAction.Get));
        Check("Unrelated text / Click", null, Convert("ordinary clipboard text", HeAction.Click));
        Check("Unrelated text / Set",   null, Convert("ordinary clipboard text", HeAction.Set));
        Check("Empty string",           null, Convert("", HeAction.Get));
        Check("Null input",             null, Convert(null, HeAction.Get));

        return cases;
    }
}
