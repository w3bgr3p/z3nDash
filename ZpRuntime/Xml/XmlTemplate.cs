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
// ══════════════════════════════════════════════════════════════════════════════

using System.Xml.Linq;

namespace DevDeck.Xml;

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

    public override string ToString() => IsNone ? "—" : $"{StepId[..8]}|{BranchId[..8]}";
}

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

    /// <summary>ZP-шный флаг «не обязательно»: ошибка не валит маршрут.</summary>
    public bool IsOptional { get; init; }

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

public sealed class XmlTemplate
{
    public required string      Name  { get; init; }
    public required List<Step>  Steps { get; init; }

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
    /// Точка входа: узел, на который никто не ссылается. Явного признака старта
    /// в XML нет — на холсте это просто узел без входящих стрелок. Если таких
    /// несколько (обычное дело: на холсте валяются заготовки), берём тот, что
    /// раньше в файле, — ZP рисует его первым.
    /// </summary>
    public Step? EntryStep()
    {
        var targets = Steps
            .SelectMany(s => s.Branches)
            .SelectMany(b => new[] { b.OnSuccess, b.OnError })
            .Where(t => !t.IsNone)
            .Select(t => t.StepId)
            .ToHashSet();

        return Steps.FirstOrDefault(s => !targets.Contains(s.Id)) ?? Steps.FirstOrDefault();
    }

    /// <summary>Узлы, до которых из точки входа не дойти. На холсте это мусор.</summary>
    public List<Step> UnreachableSteps()
    {
        var entry = EntryStep();
        if (entry is null) return [];

        var seen = new HashSet<string>();
        var queue = new Queue<Step>([entry]);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            if (!seen.Add(s.Id)) continue;
            foreach (var t in s.Branches.SelectMany(b => new[] { b.OnSuccess, b.OnError }))
                if (!t.IsNone && StepById(t.StepId) is { } next) queue.Enqueue(next);
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
                    Parameters = b.Element("Parameters"),

                    OutputVariable = results?.Element("OutputVariable")?.Value ?? "",
                    OnSuccess      = BranchRef.Parse(results?.Element("OnSuccess")?.Value),
                    OnError        = BranchRef.Parse(results?.Element("OnError")?.Value),
                });
            }

            steps.Add(new Step { Id = stepId, Branches = branches });
        }

        var tpl = new XmlTemplate
        {
            Name  = root.Attribute("Name")?.Value is { Length: > 0 } n ? n : name,
            Steps = steps,
        };

        foreach (var s in steps)
        {
            tpl._byStep[s.Id] = s;
            for (int i = 0; i < s.Branches.Count; i++)
                tpl._position[s.Branches[i].Ref] = i;
        }

        return tpl;
    }
}
