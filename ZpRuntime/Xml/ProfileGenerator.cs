// ══════════════════════════════════════════════════════════════════════════════
// ProfileGenerator.cs — личность профиля из настроек шаблона.
//
// В <StaticTechnologies><Profile> лежит не сам профиль, а правила его генерации:
//
//   Nationality="USA"  Regions="Alabama;Alaska;…"  Males="50"
//   MinAge="20"  MaxAge="45"  LoginGenerationRule="[Eng|4][RndNum|1970|1990]"
//
// ZennoPoster по ним собирает человека, и дальше шаблон заполняет им формы через
// {-Profile.Name-} и {-Profile.Surname-}. Без этого в форму уходит пустая строка,
// причём молча — регистрация просто не проходит.
//
// Здесь генератор ровно под то, чем пользуются шаблоны: имя, фамилия, пол, дата
// рождения, логин, пароль, почта, страна с регионом. Списки имён небольшие и
// лежат прямо в коде: тащить ради этого словари ZP смысла нет, а совпадать с
// ними побуквенно и не требуется — важно, что поле не пустое и правдоподобное.
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3nDash.Xml;

/// <summary>Правила генерации из &lt;Profile&gt;. Всё необязательно.</summary>
public sealed class ProfileRules
{
    public string   Nationality { get; init; } = "USA";
    public string[] Regions     { get; init; } = [];
    /// <summary>Доля мужчин в процентах, как у ZP.</summary>
    public int      Males       { get; init; } = 50;
    public int      MinAge      { get; init; } = 20;
    public int      MaxAge      { get; init; } = 45;
    public string   LoginRule   { get; init; } = "";

    /// <summary>
    /// Веса выбора браузера, ОС и платформы из XML, как написаны.
    /// Читаются только V2-атрибуты: у тех же шаблонов рядом лежат
    /// старые Browsers/OperationSystems/Resolutions, и они ему противоречат
    /// — в simroute.megapari старый говорит FireFox=100, а новый PureChrome=100.
    /// </summary>
    public string BrowsersV2         { get; init; } = "";
    public string OperationSystemsV2 { get; init; } = "";
    public string PlatformsV2        { get; init; } = "";

    /// <summary>
    /// Чего шаблон просит, а мы таким быть не можем. Браузер здесь —
    /// настоящий Chrome под Windows на рабочем столе, без подмены UserAgent
    /// и без принудительного вьюпорта: это условие запуска, а не упущение.
    /// Поэтому веса не исполняются — но и молчать о них нельзя: расхождение
    /// между «что задано в шаблоне» и «что поднялось» иначе не видно никак.
    /// </summary>
    public IReadOnlyList<string> UnrunnableRequests()
    {
        var said = new List<string>();

        Check(BrowsersV2,         ["Chrome", "PureChrome"], "браузер",  "Chrome");
        Check(OperationSystemsV2, ["Windows"],              "ОС",       "Windows");
        Check(PlatformsV2,        ["Desktop"],              "платформу", "Desktop");

        return said;

        void Check(string raw, string[] canBe, string what, string weAre)
        {
            var asked = Weights(raw)
                .Where(w => w.Weight > 0 &&
                            !canBe.Contains(w.Name, StringComparer.OrdinalIgnoreCase))
                .Select(w => $"{w.Name}={w.Weight}")
                .ToArray();

            if (asked.Length > 0)
                said.Add($"шаблон просит {what} {string.Join(", ", asked)}, " +
                         $"поднимается {weAre} — вес не исполняется");
        }
    }

    /// <summary>
    /// Значение вида { "Chrome": 0, "PureChrome": 100 } — в пары имя/вес.
    /// Разбор выборкой, а не JSON-парсером: атрибут пишет чужой
    /// редактор, и споткнуться на его формате ради предупреждения незачем.
    /// </summary>
    private static IEnumerable<(string Name, int Weight)> Weights(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(raw, "\u0022([^\u0022]+)\u0022\\s*:\\s*(\\d+)"))
            if (int.TryParse(m.Groups[2].Value, out var weight))
                yield return (m.Groups[1].Value, weight);
    }

    public static ProfileRules From(XElement? profile)
    {
        if (profile is null) return new ProfileRules();

        int Int(string name, int fallback)
            => int.TryParse(profile.Attribute(name)?.Value, out var v) ? v : fallback;

        return new ProfileRules
        {
            Nationality = profile.Attribute("Nationality")?.Value is { Length: > 0 } n ? n : "USA",
            Regions     = (profile.Attribute("Regions")?.Value ?? "")
                              .Split(';', StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim())
                              .Where(s => s.Length > 0)
                              .ToArray(),
            Males     = Int("Males",  50),
            MinAge    = Int("MinAge", 20),
            MaxAge    = Int("MaxAge", 45),
            LoginRule = profile.Attribute("LoginGenerationRule")?.Value ?? "",

            BrowsersV2         = profile.Attribute("BrowsersV2")?.Value ?? "",
            OperationSystemsV2 = profile.Attribute("OperationSystemsV2")?.Value ?? "",
            PlatformsV2        = profile.Attribute("PlatformsV2")?.Value ?? "",
        };
    }
}

public static class ProfileGenerator
{
    // Random.Shared: System.Random не потокобезопасен, а личности
    // генерируются параллельно — по одной на каждый поток шаблона.

    private static readonly string[] MaleNames =
    [
        "James", "Michael", "Robert", "John", "David", "William", "Richard", "Joseph",
        "Thomas", "Christopher", "Daniel", "Matthew", "Anthony", "Mark", "Steven",
    ];

    private static readonly string[] FemaleNames =
    [
        "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan",
        "Jessica", "Sarah", "Karen", "Lisa", "Nancy", "Betty", "Margaret", "Sandra",
    ];

    private static readonly string[] Surnames =
    [
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
    ];

    /// <summary>Заполнить профиль проекта по правилам шаблона.</summary>
    public static void Fill(IZennoPosterProjectModel project, ProfileRules rules)
    {
        var p = project.Profile;

        bool male = Random.Shared.Next(100) < rules.Males;
        p.Gender  = male ? "Male" : "Female";
        p.Name    = Pick(male ? MaleNames : FemaleNames);
        p.Surname = Pick(Surnames);

        int age  = Random.Shared.Next(Math.Min(rules.MinAge, rules.MaxAge),
                             Math.Max(rules.MinAge, rules.MaxAge) + 1);
        var born = DateTime.Today.AddYears(-age).AddDays(-Random.Shared.Next(365));
        p.BirthDate = born.ToString("yyyy-MM-dd");

        p.Country = rules.Nationality;
        p.Region  = rules.Regions.Length > 0 ? Pick(rules.Regions) : "";

        p.NickName = string.IsNullOrWhiteSpace(rules.LoginRule)
            ? z3n7.Rnd.RndNickname()
            : ApplyLoginRule(rules.LoginRule);
        p.Login = p.NickName;

        p.Password = z3n7.Rnd.RndPass();
        p.Email    = z3n7.Rnd.RndMail();
    }

    private static string Pick(string[] from) => from[Random.Shared.Next(from.Length)];

    /// <summary>
    /// LoginGenerationRule вида "[Eng|4][RndNum|1970|1990]": куски в скобках
    /// раскрываются, остальное берётся как есть. Реализованы те виды, что
    /// встречаются в шаблонах; незнакомый оставляется текстом — видеть его в
    /// логине лучше, чем молча получить пустоту.
    /// </summary>
    private static string ApplyLoginRule(string rule)
        => Regex.Replace(rule, @"\[([^\]]+)\]", m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            var kind  = parts[0].Trim();

            return kind.ToLowerInvariant() switch
            {
                "eng" when parts.Length > 1 && int.TryParse(parts[1], out var n)
                    => z3n7.Rnd.RndString(n).ToLowerInvariant(),
                "rndnum" when parts.Length > 2
                              && int.TryParse(parts[1], out var lo)
                              && int.TryParse(parts[2], out var hi)
                    => Random.Shared.Next(Math.Min(lo, hi), Math.Max(lo, hi) + 1).ToString(),
                _   => m.Value,
            };
        });
}
