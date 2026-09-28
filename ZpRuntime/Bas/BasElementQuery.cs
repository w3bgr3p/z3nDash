namespace z3nDash.Bas;

/// <summary>
/// Описание поиска элемента по правилам ZP. Элемент в мосте ленивый, как
/// PlaywrightElement: запрос хранится и исполняется заново при каждой операции.
/// Kind: "attr" — FindElementByAttribute/ById/ByName, "xpath" — FindElementByXPath.
/// </summary>
public sealed record BasElementQuery(string Kind, string Tags, string Attr, string Pattern,
                                     string Mode, int Index, BasElementQuery? Parent)
{
    public static BasElementQuery ByAttribute(string tags, string attr, string pattern, string mode, int index)
        => new("attr", tags ?? "", attr ?? "", pattern ?? "", mode ?? "text", index, null);

    public static BasElementQuery ById(string id)       => ByAttribute("", "id", id, "text", 0);
    public static BasElementQuery ByName(string name)   => ByAttribute("", "name", name, "text", 0);
    public static BasElementQuery ByXPath(string xpath, int index) => new("xpath", "", "", xpath ?? "", "xpath", index, null);

    public BasElementQuery Child(string tags, string attr, string pattern, string mode, int index)
        => ByAttribute(tags, attr, pattern, mode, index) with { Parent = this };

    public BasElementQuery WithIndex(int index) => this with { Index = index };

    public string ToJson()
    {
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = Kind, ["tags"] = Tags, ["attr"] = Attr, ["pattern"] = Pattern,
            ["mode"] = Mode, ["index"] = Index,
            ["parent"] = Parent is null ? null : System.Text.Json.Nodes.JsonNode.Parse(Parent.ToJson()),
        };
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Для текста ошибки: какой поиск выполнялся.</summary>
    public string Describe()
    {
        var self = Kind == "xpath" ? $"xpath[{Pattern}]#{Index}" : $"{Tags}[{Attr} {Mode} {Pattern}]#{Index}";
        return Parent is null ? self : Parent.Describe() + " > " + self;
    }
}
