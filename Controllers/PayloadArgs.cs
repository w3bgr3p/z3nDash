using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace z3nDash;

/// <summary>
/// Payload задачи → именованные аргументы командной строки. Флагами становятся
/// только поля схемы; Args задачи идут первыми и выигрывают при совпадении.
/// Чистая логика без БД и процессов — её же зовёт превью в модалке.
/// </summary>
public static class PayloadArgs
{
    /// <param name="Args">Строка для запуска: Args задачи + флаги.</param>
    /// <param name="Masked">То же, но значения полей password заменены на ***.</param>
    /// <param name="Skipped">Флаги payload, не добавленные, потому что уже есть в Args.</param>
    public sealed record Result(string Args, string Masked, IReadOnlyList<string> Skipped);

    private static readonly HashSet<string> Markup = ["section", "html", "tab"];

    public static Result Build(string executor, string args, string schemaJson, string valuesJson)
    {
        args = (args ?? "").Trim();
        var values  = Values(valuesJson);
        var tokens  = Tokenize(args, out var openQuote);
        var present = new HashSet<string>(
            tokens.Where(t => t.StartsWith("--") && t.Length > 2).Select(t => t.Split('=')[0]),
            StringComparer.Ordinal);

        var flags   = new List<string>();
        var masked  = new List<string>();
        var skipped = new List<string>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, type, skipEmpty) in Fields(schemaJson))
        {
            var flag = "--" + Kebab(key);
            if (!emitted.Add(flag)) continue;
            if (present.Contains(flag)) { skipped.Add(flag); continue; }

            var value = values.GetValueOrDefault(key, "");
            if (type == "boolean")
            {
                if (!string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) continue;
                flags.Add(flag);
                masked.Add(flag);
                continue;
            }

            // Пустое значение передаётся явно: скрипт ждёт пустую строку, а не
            // отсутствие аргумента — на этом уже падали. Галка skipEmpty на поле —
            // для обратного случая: пусто значит «не задано», скрипт возьмёт своё
            // значение по умолчанию.
            if (value.Length == 0 && skipEmpty) continue;
            flags.Add(flag);
            flags.Add(Quote(value));
            masked.Add(flag);
            masked.Add(type == "password" && value.Length > 0 ? "***" : Quote(value));
        }

        if (flags.Count > 0 && openQuote)
            throw new ArgumentException($"Args end inside an open quote: {args}");

        // npm отдаёт скрипту только то, что стоит после «--».
        if (flags.Count > 0 && executor == "npm" && !tokens.Contains("--"))
        {
            flags.Insert(0, "--");
            masked.Insert(0, "--");
        }

        return new Result(Join(args, flags), Join(args, masked), skipped);
    }

    /// <summary>Значения payload строками: true/false — "true"/"false", null — "".</summary>
    public static Dictionary<string, string> Values(string valuesJson)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(valuesJson)) return result;

        using var doc = JsonDocument.Parse(valuesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException($"payload values is {doc.RootElement.ValueKind}, not an object");

        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                JsonValueKind.Null   => "",
                _                    => prop.Value.GetRawText(),
            };
        return result;
    }

    private static List<(string key, string type, bool skipEmpty)> Fields(string schemaJson)
    {
        var fields = new List<(string key, string type, bool skipEmpty)>();
        if (string.IsNullOrWhiteSpace(schemaJson)) return fields;

        using var doc = JsonDocument.Parse(schemaJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException($"payload schema is {doc.RootElement.ValueKind}, not an array");

        foreach (var field in doc.RootElement.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object) continue;
            var key  = field.TryGetProperty("key",  out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim() : "";
            var type = field.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "text";
            if (key.Length == 0 || Markup.Contains(type)) continue;
            var skipEmpty = field.TryGetProperty("skipEmpty", out var s)
                && (s.ValueKind == JsonValueKind.True
                    || s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), "true", StringComparison.OrdinalIgnoreCase));
            fields.Add((key, type, skipEmpty));
        }
        return fields;
    }

    private static string Kebab(string key)
        => Regex.Replace(key, "(?<!^)(?=[A-Z])", "-").ToLowerInvariant();

    private static string Join(string args, List<string> parts)
        => string.Join(" ", new[] { args }.Concat(parts).Where(p => p.Length > 0));

    /// <summary>Квотирование по правилам разбора командной строки Windows (CommandLineToArgvW).</summary>
    internal static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return value;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Разбор строки аргументов по тем же правилам; openQuote — строка кончилась внутри кавычек.</summary>
    internal static List<string> Tokenize(string text, out bool openQuote)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) { current.Append('"'); i++; }
                }
                else current.Append('\\', n);
                started = true;
                continue;
            }
            if (c == '"') { inQuotes = !inQuotes; started = true; i++; continue; }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (started) { tokens.Add(current.ToString()); current.Clear(); started = false; }
                i++;
                continue;
            }
            current.Append(c);
            started = true;
            i++;
        }
        if (started) tokens.Add(current.ToString());
        openQuote = inQuotes;
        return tokens;
    }
}
