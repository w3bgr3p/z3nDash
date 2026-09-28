namespace z3nDash.Bas;

/// <summary>
/// Строковые литералы для BAS-кода. Движок BAS-скрипта — старый JS: сырой
/// U+2028 в литерале для него синтаксическая ошибка, поэтому всё не-ASCII и
/// все управляющие символы уходят как \uXXXX.
/// </summary>
public static class BasJs
{
    public static string Str(string? s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s ?? "")
        {
            if (c == '"')                   sb.Append("\\\"");
            else if (c == '\\')             sb.Append("\\\\");
            else if (c >= 0x20 && c < 0x7f) sb.Append(c);
            else                            sb.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sb.Append('"').ToString();
    }
}
