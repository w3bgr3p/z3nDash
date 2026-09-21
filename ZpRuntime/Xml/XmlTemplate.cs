// ══════════════════════════════════════════════════════════════════════════════
// XmlTemplate.cs — модель проекта ZennoPoster и её разбор из .xml.
//
// Формат снят с реальных шаблонов (xml_example/). Устроен так:
//
//   <Project>
//     <Step ID="..." x=".." y="..">      ← узел на холсте
//       <Branch ID=".." Type=".." Action="..">   ← действие внутри узла
//         <Parameters>  ...              ← зависят от Type/Action
//         <Results>
//           <OutputVariable>{-Variable.x-}</OutputVariable>
//           <OnSuccess>stepId|branchId</OnSuccess>   ← пусто = следующая ветка
//           <OnError>stepId|branchId</OnError>
//
// Ветки внутри Step идут подряд; переход между Step всегда записан в
// OnSuccess/OnError последней ветки. Пустой OnSuccess у последней ветки означает
// конец маршрута, а не переход к следующему Step по порядку в файле — на холсте
// порядок задаётся стрелками, а не позицией в XML.
//
// Всё, что не является узлом холста, лежит в <StaticTechnologies>:
//
//   <Start    nextAction="stepId|branchId">  ← точка входа, всегда есть
//   <GoodEnd  nextAction="...">              ← что выполнить при успехе маршрута
//   <BadEnd   nextAction="...">              ← и при провале; оба необязательны
//   <Variables><Variable Name= Value= .../>  ← объявленные переменные проекта
//   <References><Reference Include="[external]z3n7[external]"/>
//   <OwnCodeUsings Text="…" CommonCode="…">  ← usings и общий код всех веток
//
// CommonCode — это целый C#-файл с классами проекта (в simroute_test 33 КБ), и
// без него ветки OwnCode не компилируются: они зовут именно эти классы. То есть
// шаблон самодостаточен, внешняя сборка нужна только под <Reference>.
// ══════════════════════════════════════════════════════════════════════════════

using System.Xml.Linq;

namespace z3nDash.Xml;

/// <summary>Адрес ветки: узел плюс ветка внутри него.</summary>
public readonly record struct BranchRef(string StepId, string BranchId)
{
    public static readonly BranchRef None = new("", "");
    public bool IsNone => string.IsNullOrEmpty(StepId);

    /// <summary>В XML переход записан как "stepId|branchId".</summary>
    public static BranchRef Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return None;
        var parts = raw.Split('|');
        return parts.Length < 2 ? None : new BranchRef(parts[0].Trim(), parts[1].Trim());
    }

    /// <summary>
    /// Короткая запись для лога. Обрезка обязана переживать короткий
    /// идентификатор: ZP пишет GUID, но шаблон может быть собран руками, и
    /// падать в строке лога — худшее, что может делать диагностика.
    /// </summary>
    public override string ToString()
        => IsNone ? "—" : $"{Short(StepId)}|{Short(BranchId)}";

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
}

/// <summary>
/// Один вариант ветки Logic/Switch: значение и куда с ним уходить.
///
/// В XML лежит экранированной разметкой внутри &lt;Case0&gt;…&lt;CaseN&gt;:
/// <c>&lt;Pair&gt;&lt;Key&gt;Sent&lt;/Key&gt;&lt;Value&gt;stepId|branchId&lt;/Value&gt;&lt;/Pair&gt;</c>.
/// Пустой Value — вариант без стрелки на холсте, законный случай: во всех
/// 21 Switch рабочих шаблонов такие есть.
/// </summary>
public readonly record struct SwitchCase(string Key, BranchRef Target);

/// <summary>
/// Одно действие. Parameters оставлены как XElement: у каждого Type/Action свой
/// набор полей, и разбирать их заранее — значит писать модель под каждый тип ZP.
/// Исполнитель берёт то, что ему нужно, через хелперы ниже.
/// </summary>
public sealed class Branch
{
    public required string    Id         { get; init; }
    public required string    StepId     { get; init; }
    /// <summary>OwnCode, HTMLElement, WebBrowser, Profile — крупная категория.</summary>
    public required string    Type       { get; init; }
    /// <summary>CSharp, RiseEvent, SetAttribute, GetAttribute, CMD_NAVIGATE, Update.</summary>
    public required string    Action     { get; init; }
    /// <summary>Подпись на холсте. В логе полезнее любого идентификатора.</summary>
    public           string   Title      { get; init; } = "";
    public required XElement? Parameters { get; init; }

    public string    OutputVariable { get; init; } = "";
    public BranchRef OnSuccess      { get; init; } = BranchRef.None;
    public BranchRef OnError        { get; init; } = BranchRef.None;

    /// <summary>Варианты Logic/Switch в порядке из XML. У прочих веток пусто.</summary>
    public IReadOnlyList<SwitchCase> Cases { get; init; } = [];

    /// <summary>
    /// Переход из &lt;Default&gt;. null — узла Default в ветке нет вовсе; это не
    /// то же самое, что Default с пустым переходом.
    /// </summary>
    public BranchRef? CaseDefault { get; init; }

    /// <summary>ZP-шный флаг «не обязательно»: ошибка не валит маршрут.</summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Точка останова, расставленная в ProjectMaker. Плеер её не исполняет —
    /// это признак для отладчика. Атрибута может не быть вовсе: отсутствие
    /// значит «точки нет».
    /// </summary>
    public bool HasBreakPoint { get; init; }

    /// <summary>
    /// Действие выключено в редакторе — на холсте это серый кубик. ZP такую
    /// ветку не исполняет вовсе: она сразу «успешна» и маршрут идёт дальше.
    /// Выключают обычно именно то, что на текущем сайте не работает, поэтому
    /// попытка его выполнить — гарантированное падение, а не безобидный лишний
    /// шаг.
    /// </summary>
    public bool IsDisabled { get; init; }

    public BranchRef Ref => new(StepId, Id);

    public string? Param(string name)      => Parameters?.Element(name)?.Value;
    public XElement? ParamNode(string name) => Parameters?.Element(name);

    public override string ToString()
        => $"{Type}/{Action}" + (string.IsNullOrEmpty(Title) ? "" : $" «{Title}»");
}

/// <summary>Узел холста: последовательность веток.</summary>
public sealed class Step
{
    public required string        Id       { get; init; }
    public required List<Branch>  Branches { get; init; }
}

/// <summary>Общий код и окружение веток OwnCode — из &lt;OwnCodeUsings&gt;.</summary>
public sealed class OwnCodeContext
{
    /// <summary>Строки using, которые ZP подставляет каждой ветке.</summary>
    public string[] Usings     { get; init; } = [];
    /// <summary>Классы проекта: целый C#-файл, общий для всех веток.</summary>
    public string   CommonCode { get; init; } = "";
    /// <summary>Сборки из &lt;References&gt;, уже без обёртки [external].</summary>
    public string[] References { get; init; } = [];
}

public sealed class XmlTemplate
{
    public required string      Name  { get; init; }
    public required List<Step>  Steps { get; init; }

    /// <summary>Точка входа из &lt;Start nextAction&gt;. В шаблоне есть всегда.</summary>
    public BranchRef Start   { get; init; } = BranchRef.None;
    /// <summary>Ветка, которой ZP заканчивает удачный маршрут. Может отсутствовать.</summary>
    public BranchRef GoodEnd { get; init; } = BranchRef.None;
    /// <summary>То же для провала.</summary>
    public BranchRef BadEnd  { get; init; } = BranchRef.None;

    /// <summary>Объявленные переменные проекта с начальными значениями.</summary>
    public Dictionary<string, string> Variables { get; init; } = new();

    /// <summary>
    /// Значения из настроек проекта (&lt;InputSettings&gt;): имя переменной из
    /// OutputVariable → DefaultValue. В ZennoPoster это то, что видит человек
    /// перед запуском, и оно ложится в переменные раньше их собственных
    /// значений по умолчанию.
    /// </summary>
    public Dictionary<string, string> InputDefaults { get; init; } = new();

    public OwnCodeContext OwnCode { get; init; } = new();

    /// <summary>Правила генерации личности из &lt;Profile&gt;.</summary>
    public ProfileRules Profile { get; init; } = new();

    private readonly Dictionary<string, Step>   _byStep   = new();
    private readonly Dictionary<BranchRef, int> _position = new();

    public Step? StepById(string id) => _byStep.GetValueOrDefault(id);

    /// <summary>Ветка по адресу и её индекс внутри узла.</summary>
    public (Step Step, int Index)? Locate(BranchRef r)
    {
        if (r.IsNone || !_byStep.TryGetValue(r.StepId, out var step)) return null;
        return _position.TryGetValue(r, out var i) ? (step, i) : null;
    }

    /// <summary>
    /// Узлы, до которых не дойти ни из Start, ни из обработчиков конца. Это
    /// артефакты разработки — заготовки, оставленные на холсте.
    ///
    /// Считать их только от Start было бы неверно: GoodEnd и BadEnd ведут в свои
    /// цепочки, и в numlex.casino_ обработчик BadEnd — это как раз тот узел, на
    /// который прежняя эвристика «узел без входящих стрелок» показывала как на
    /// точку входа.
    /// </summary>
    public List<Step> UnreachableSteps()
    {
        var seen  = new HashSet<string>();
        var queue = new Queue<string>();

        foreach (var r in new[] { Start, GoodEnd, BadEnd })
            if (!r.IsNone) queue.Enqueue(r.StepId);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || StepById(id) is not { } s) continue;

            // Переходы Logic/Switch — такие же стрелки на холсте, только
            // записаны в вариантах, а не в OnSuccess. Без них узлы, куда ведёт
            // только Switch, объявлялись бы артефактами разработки.
            foreach (var t in s.Branches.SelectMany(b =>
                         new[] { b.OnSuccess, b.OnError, b.CaseDefault ?? BranchRef.None }
                             .Concat(b.Cases.Select(c => c.Target))))
                if (!t.IsNone) queue.Enqueue(t.StepId);
        }

        return Steps.Where(s => !seen.Contains(s.Id)).ToList();
    }

    public static XmlTemplate Load(string path)
        => Parse(XDocument.Load(path), System.IO.Path.GetFileName(path));

    public static XmlTemplate Parse(XDocument doc, string name)
    {
        var root = doc.Root ?? throw new InvalidOperationException("пустой XML");
        if (root.Name.LocalName != "Project")
            throw new InvalidOperationException(
                $"корневой узел {root.Name.LocalName}, ожидался Project — это не шаблон ZennoPoster");

        var steps = new List<Step>();

        foreach (var stepEl in root.Elements("Step"))
        {
            var stepId = stepEl.Attribute("ID")?.Value ?? "";
            var branches = new List<Branch>();

            foreach (var b in stepEl.Elements("Branch"))
            {
                var results = b.Element("Results");
                branches.Add(new Branch
                {
                    Id         = b.Attribute("ID")?.Value ?? "",
                    StepId     = stepId,
                    Type       = b.Attribute("Type")?.Value   ?? "",
                    Action     = b.Attribute("Action")?.Value ?? "",
                    Title      = b.Attribute("UserText")?.Value ?? "",
                    IsOptional = string.Equals(b.Attribute("IsNotNecessarily")?.Value,
                                               "True", StringComparison.OrdinalIgnoreCase),
                    HasBreakPoint = string.Equals(b.Attribute("HasBreakPoint")?.Value,
                                                  "True", StringComparison.OrdinalIgnoreCase),
                    // Атрибута может не быть вовсе: ZP пишет его не всегда, а
                    // отсутствие значит «включено».
                    IsDisabled = string.Equals(b.Attribute("IsDisable")?.Value,
                                               "True", StringComparison.OrdinalIgnoreCase),
                    Parameters = b.Element("Parameters"),

                    OutputVariable = results?.Element("OutputVariable")?.Value ?? "",
                    OnSuccess      = BranchRef.Parse(results?.Element("OnSuccess")?.Value),
                    OnError        = BranchRef.Parse(results?.Element("OnError")?.Value),

                    Cases       = ParseCases(results),
                    CaseDefault = ParsePair(results?.Element("Default")) is { } d ? d.Target : null,
                });
            }

            steps.Add(new Step { Id = stepId, Branches = branches });
        }

        var stat = root.Element("StaticTechnologies");

        var tpl = new XmlTemplate
        {
            Name  = root.Attribute("Name")?.Value is { Length: > 0 } n ? n : name,
            Steps = steps,

            Start   = NextAction(stat, "Start"),
            GoodEnd = NextAction(stat, "GoodEnd"),
            BadEnd  = NextAction(stat, "BadEnd"),

            InputDefaults = ParseInputDefaults(stat),

            Variables = stat?.Element("Variables")?.Elements("Variable")
                            .Where(v => v.Attribute("Name") is not null)
                            .ToDictionary(v => v.Attribute("Name")!.Value,
                                          v => v.Attribute("Value")?.Value ?? "")
                        ?? new Dictionary<string, string>(),

            OwnCode = ParseOwnCode(stat),
            Profile = ProfileRules.From(stat?.Element("Profile")),
        };

        foreach (var s in steps)
        {
            tpl._byStep[s.Id] = s;
            for (int i = 0; i < s.Branches.Count; i++)
                tpl._position[s.Branches[i].Ref] = i;
        }

        return tpl;
    }

    /// <summary>
    /// Настройки проекта. Ключ поля лежит в OutputVariable как
    /// {-Variable.имя-}; поля без него (Label, Comment, Tab) — это разметка
    /// формы, значения они не несут.
    /// </summary>
    private static Dictionary<string, string> ParseInputDefaults(XElement? stat)
    {
        var result = new Dictionary<string, string>();
        var settings = stat?.Element("InputSettings");
        if (settings is null) return result;

        foreach (var item in settings.Elements("InputSetting"))
        {
            var output = item.Attribute("OutputVariable")?.Value
                         ?? item.Element("OutputVariable")?.Value ?? "";
            var name = output.Replace("{-Variable.", "").Replace("-}", "").Trim();
            if (name.Length == 0) continue;

            var value = item.Attribute("DefaultValue")?.Value
                        ?? item.Element("DefaultValue")?.Value ?? "";
            result[name] = value;
        }
        return result;
    }

    /// <summary>
    /// Варианты Switch: Case0, Case1, … по порядку номера, а не по порядку в
    /// файле. Номер — это позиция варианта в редакторе, и от неё зависит, какой
    /// из двух одинаковых ключей сработает первым.
    /// </summary>
    private static IReadOnlyList<SwitchCase> ParseCases(XElement? results)
    {
        if (results is null) return [];

        var cases = new List<(int Number, SwitchCase Case)>();
        foreach (var el in results.Elements())
        {
            var name = el.Name.LocalName;
            if (!name.StartsWith("Case", StringComparison.Ordinal)) continue;
            if (!int.TryParse(name["Case".Length..], out var number)) continue;
            if (ParsePair(el) is { } pair) cases.Add((number, pair));
        }

        return cases.OrderBy(x => x.Number).Select(x => x.Case).ToList();
    }

    /// <summary>
    /// Тело варианта — экранированная разметка, а не узлы: в файле лежит
    /// «&amp;lt;Pair&amp;gt;…», поэтому содержимое сперва разбирается как
    /// отдельный документ.
    /// </summary>
    private static SwitchCase? ParsePair(XElement? el)
    {
        var raw = el?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        XElement pair;
        try { pair = XElement.Parse(raw); }
        catch (System.Xml.XmlException) { return null; }

        return new SwitchCase(
            pair.Element("Key")?.Value ?? "",
            BranchRef.Parse(pair.Element("Value")?.Value));
    }

    private static BranchRef NextAction(XElement? stat, string node)
        => BranchRef.Parse(stat?.Element(node)?.Attribute("nextAction")?.Value);

    private static OwnCodeContext ParseOwnCode(XElement? stat)
    {
        var oc = stat?.Element("OwnCodeUsings");

        // Usings и общий код лежат в атрибутах, а не в теле узла.
        var usings = (oc?.Attribute("Text")?.Value ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimEnd(';'))
            .Where(l => l.StartsWith("using ", StringComparison.Ordinal))
            .Select(l => l["using ".Length..].Trim())
            .Distinct()
            .ToArray();

        // ZP оборачивает имя сборки в [external]…[external]. Иногда там простое
        // имя ("z3n7"), иногда полное строгое ("System.Management, Version=4.0.0.0,
        // Culture=neutral, PublicKeyToken=…") — берём часть до первой запятой,
        // сопоставлять всё равно по простому имени.
        var refs = stat?.Element("References")?.Elements("Reference")
            .Select(r => r.Attribute("Include")?.Value ?? "")
            .Select(v => v.Replace("[external]", "").Split(',')[0].Trim())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToArray() ?? [];

        return new OwnCodeContext
        {
            Usings     = usings,
            CommonCode = oc?.Attribute("CommonCode")?.Value ?? "",
            References = refs,
        };
    }
}
