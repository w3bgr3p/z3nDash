using z3nDash.Bas;

namespace BasBridgeRegression;

public static class QueryTests
{
    public static Task Run(ExpectFn expect)
    {
        expect("js: кавычка, слэш, перевод строки",
               BasJs.Str("a\"b\\c\nd") == "\"a\\\"b\\\\c\\u000ad\"", BasJs.Str("a\"b\\c\nd"));
        expect("js: кириллица и U+2028 — в \\u",
               BasJs.Str("Т\u2028") == "\"\\u0422\\u2028\"", BasJs.Str("Т\u2028"));

        var q = BasElementQuery.ByAttribute("input:text;a", "class", "^btn-(a|b)$", "regexp", 2);
        expect("query: json",
               q.ToJson() == "{\"kind\":\"attr\",\"tags\":\"input:text;a\",\"attr\":\"class\",\"pattern\":\"^btn-(a|b)$\",\"mode\":\"regexp\",\"index\":2,\"parent\":null}",
               q.ToJson());
        var child = q.Child("span", "innertext", "OK", "text", 0);
        expect("query: дочерний несёт родителя", child.ToJson().Contains("\"parent\":{\"kind\":\"attr\""), child.ToJson());
        expect("query: описание для шага",
               child.Describe() == "input:text;a[class regexp ^btn-(a|b)$]#2 > span[innertext text OK]#0", child.Describe());
        expect("query: id", BasElementQuery.ById("f").ToJson().Contains("\"attr\":\"id\",\"pattern\":\"f\",\"mode\":\"text\""));
        expect("query: xpath", BasElementQuery.ByXPath("//a", 1).ToJson().StartsWith("{\"kind\":\"xpath\""));
        expect("query: другой индекс", q.WithIndex(5).ToJson().Contains("\"index\":5"));
        return Task.CompletedTask;
    }
}
