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

        string? selector = FindSelector(input);
        if (selector == null) return null;

        return action switch
        {
            HeAction.Get   => "var msg = instance.HeGet(" + selector + ");",
            HeAction.Click => "instance.HeClick(" + selector + ");",
            HeAction.Set   => "instance.HeSet(" + selector + ", \"value\");",
            _              => null,
        };
    }


    // ProjectMaker порождает два вида строки поиска. Берём ту, что встретилась
    // в тексте раньше — как и прежде, первую подходящую.
    private static string? FindSelector(string input)
    {
        string literal = "\"(?:[^\"\\\\\r\n]|\\\\[^\r\n])*\"";
        string args = literal + @"\s*,\s*" + literal + @"\s*,\s*" + literal
            + @"\s*,\s*" + literal + @"\s*,\s*\d+";

        const string head = @"(?m)^\s*(?:HtmlElement|var)\s+\w+\s*=\s*instance\.ActiveTab\.";

        Match byAttribute = Regex.Match(input,
            head + @"FindElementByAttribute\(\s*(?<args>" + args + @")\s*\)\s*;");

        // FindElementById("x") -> ("x", "id");  FindElementByName("x") -> ("x", "name")
        Match byIdOrName = Regex.Match(input,
            head + @"FindElementBy(?<kind>Id|Name)\(\s*(?<arg>" + literal + @")\s*\)\s*;");

        bool a = byAttribute.Success;
        bool b = byIdOrName.Success;

        if (a && (!b || byAttribute.Index <= byIdOrName.Index))
            return "(" + byAttribute.Groups["args"].Value + ")";

        if (b)
            return "(" + byIdOrName.Groups["arg"].Value + ", \""
                 + byIdOrName.Groups["kind"].Value.ToLowerInvariant() + "\")";

        return null;
    }

    public readonly record struct SelfTestCase(string Name, bool Passed, string Expected, string Actual);

    private const string Sample =
        "HtmlElement he = instance.ActiveTab.FindElementByAttribute(\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0);\r\n"
        + "if (he.IsVoid) return -1;";

    private const string EscapedSample =
        "HtmlElement he = instance.ActiveTab.FindElementByAttribute(\"modern-notification\", \"innertext\", \"The\\\\ password\\\\ !@#\\\\$%\\\\^&\\\\*\\\\(\\\\)-_\\\\+=\", \"regexp\", 0);";


    private const string IdSample =
        "// Конструктор действий, тип RiseEvent\r\n"
        + "HtmlElement he = instance.ActiveTab.FindElementById(\"signup-launch-btn\");\r\n"
        + "if (he.IsVoid) return -1;\r\n"
        + "\r\n"
        + "// Задержка эмуляции\r\n"
        + "instance.WaitFieldEmulationDelay();\r\n"
        + "// Вызвать событие \"click\"\r\n"
        + "he.RiseEvent(\"click\", instance.EmulationLevel);";
    private const string NameSample =
        "// Конструктор действий, тип RiseEvent\r\n"
        + "HtmlElement he = instance.ActiveTab.FindElementByName(\"signup-launch-btn\");\r\n"
        + "if (he.IsVoid) return -1;\r\n"
        + "\r\n"
        + "// Задержка эмуляции\r\n"
        + "instance.WaitFieldEmulationDelay();\r\n"
        + "// Вызвать событие \"click\"\r\n"
        + "he.RiseEvent(\"click\", instance.EmulationLevel);";
    public static IReadOnlyList<SelfTestCase> SelfTest()
    {
        var cases = new List<SelfTestCase>();

        void Check(string name, string? expected, string? actual) =>
            cases.Add(new SelfTestCase(name, expected == actual, expected ?? "<null>", actual ?? "<null>"));

        Check("Get",   "var msg = instance.HeGet((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0));",          Convert(Sample, HeAction.Get));
        Check("Click", "instance.HeClick((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0));",          Convert(Sample, HeAction.Click));
        Check("Set",   "instance.HeSet((\"div\", \"data-ttid\", \"modal-msg\", \"regexp\", 0), \"value\");", Convert(Sample, HeAction.Set));

        Check("Escaped literal", "var msg = instance.HeGet((\"modern-notification\", \"innertext\", \"The\\\\ password\\\\ !@#\\\\$%\\\\^&\\\\*\\\\(\\\\)-_\\\\+=\", \"regexp\", 0));", Convert(EscapedSample, HeAction.Get));

        Check("id / Get",   "var msg = instance.HeGet((\"signup-launch-btn\", \"id\"));",          Convert(IdSample, HeAction.Get));
        Check("id / Click", "instance.HeClick((\"signup-launch-btn\", \"id\"));",          Convert(IdSample, HeAction.Click));
        Check("id / Set",   "instance.HeSet((\"signup-launch-btn\", \"id\"), \"value\");", Convert(IdSample, HeAction.Set));

        Check("name / Get",   "var msg = instance.HeGet((\"signup-launch-btn\", \"name\"));",          Convert(NameSample, HeAction.Get));
        Check("name / Click", "instance.HeClick((\"signup-launch-btn\", \"name\"));",          Convert(NameSample, HeAction.Click));
        Check("name / Set",   "instance.HeSet((\"signup-launch-btn\", \"name\"), \"value\");", Convert(NameSample, HeAction.Set));
        Check("ById without declaration", null, Convert("instance.ActiveTab.FindElementById(\"signup-launch-btn\");", HeAction.Get));

        Check("Unrelated text / Get",   null, Convert("ordinary clipboard text", HeAction.Get));
        Check("Unrelated text / Click", null, Convert("ordinary clipboard text", HeAction.Click));
        Check("Unrelated text / Set",   null, Convert("ordinary clipboard text", HeAction.Set));
        Check("Empty string",           null, Convert("", HeAction.Get));
        Check("Null input",             null, Convert(null, HeAction.Get));

        return cases;
    }
}
