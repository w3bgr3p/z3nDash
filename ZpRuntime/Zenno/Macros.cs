// ══════════════════════════════════════════════════════════════════════════════
// Macros.cs — раскрытие ZP-шных подстановок {-Область.Имя-} в параметрах веток.
//
// В шаблоне значения почти всегда написаны через них: Value ветки SetAttribute
// это "{-Profile.Name-} {-Profile.Surname-}", OutputVariable — "{-Variable.otp-}".
// Разбирать их приходится и на входе (подставить), и на выходе (понять, в какую
// переменную писать результат).
//
// Набор областей взят по факту из шаблонов, а не из документации ZP: неизвестная
// подстановка возвращается как есть. Молча подставить пустую строку хуже —
// получится тихо неверный запрос вместо видимого {-Foo.Bar-} в логе.
//
// Лежит рядом со стабом, а не в плеере, потому что макросы — контракт ZP, а не
// особенность XML: их раскрывает и IZennoPosterProjectModel.ExecuteMacro, и код
// веток. Раньше раскрывателя было два, и стабовский знал ровно одну подстановку
// из всех — остальные возвращал текстом.
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace DevDeck;

public static class Macros
{
    private static readonly Regex Pattern =
        new(@"\{-(?<scope>[A-Za-z]+)\.(?<name>[^-}]+)-\}", RegexOptions.Compiled);

    /// <summary>Подставить все {-…-} в строке.</summary>
    public static string Expand(this IZennoPosterProjectModel project, string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{-")) return text ?? "";

        return Pattern.Replace(text, m =>
        {
            var scope = m.Groups["scope"].Value;
            var name  = m.Groups["name"].Value;
            return Resolve(project, scope, name) ?? m.Value;
        });
    }

    private static string? Resolve(IZennoPosterProjectModel project, string scope, string name)
        => scope switch
        {
            "Variable" => project.Variables[name].Value,
            "Profile"  => ProfileField(project, name),
            "Project"  => name switch
            {
                "Directory" => project.Directory,
                "Path"      => project.Path,
                "Name"      => project.Name,
                _           => null,
            },
            "Environment" => name == "CurrentUser" ? Environment.UserName : null,
            _             => null,
        };

    private static string? ProfileField(IZennoPosterProjectModel project, string name)
    {
        var p = project.Profile;
        return name switch
        {
            "UserAgent"     => p.UserAgent,
            "Name"          => p.Name,
            "Surname"       => p.Surname,
            "MiddleName"    => p.MiddleName,
            "NickName"      => p.NickName,
            "Gender"        => p.Gender,
            "BirthDate"     => p.BirthDate,
            "Login"         => p.Login,
            "Password"      => p.Password,
            "Email"         => p.Email,
            "EmailPassword" => p.EmailPassword,
            "Country"       => p.Country,
            "Region"        => p.Region,
            "City"          => p.City,
            "Zip"           => p.Zip,
            "Address"       => p.Address,
            "Phone"         => p.Phone,
            // Поле, которого у нас нет, ищем среди переменных с тем же именем —
            // шаблон можно прокормить руками, не правя его.
            _ => project.Variables[$"Profile.{name}"].Value is { Length: > 0 } v ? v : null,
        };
    }

    /// <summary>
    /// Имя переменной из OutputVariable. В XML это "{-Variable.otp-}", а нужно
    /// "otp". Пустая строка означает «результат никуда не писать».
    /// </summary>
    public static string OutputVariableName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var m = Pattern.Match(raw);
        return m.Success && m.Groups["scope"].Value == "Variable"
            ? m.Groups["name"].Value.Trim()
            : "";
    }
}
