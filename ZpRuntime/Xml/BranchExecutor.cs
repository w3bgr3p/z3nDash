// ══════════════════════════════════════════════════════════════════════════════
// BranchExecutor.cs — исполнение одной ветки шаблона.
//
// Каждый Type/Action ложится на уже перенесённый из z3n7 слой: Finder ветки
// HTMLElement — это ровно аргументы FindElementByAttribute, RiseEvent —
// HeClick, SetAttribute — HeSet, GetAttribute — HeGet. Ради этого перенос и
// делался: плееру не нужно знать про Playwright, он говорит на языке ZP.
//
// Незнакомый Type/Action не пропускается молча — ветка падает с внятным текстом.
// Тихий пропуск дал бы шаблон, который «отработал» и ничего не сделал.
// ══════════════════════════════════════════════════════════════════════════════

using System.Xml.Linq;
using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace DevDeck.Xml;

/// <summary>Что ветка вернула: текст для OutputVariable, если он есть.</summary>
public readonly record struct BranchResult(string Output)
{
    public static readonly BranchResult Empty = new("");
}

public sealed class BranchExecutor
{
    private readonly IZennoPosterProjectModel _project;
    private readonly Instance                 _instance;
    private readonly XmlCodeRunner            _code;
    private readonly Action<string>           _log;

    public BranchExecutor(IZennoPosterProjectModel project, Instance instance,
                          XmlCodeRunner code, Action<string>? log = null)
    {
        _project  = project;
        _instance = instance;
        _code     = code;
        _log      = log ?? (_ => { });
    }

    public BranchResult Execute(Branch branch, CancellationToken ct)
        => (branch.Type, branch.Action) switch
        {
            ("OwnCode",     "CSharp")       => RunCode(branch, ct),
            ("HTMLElement", "RiseEvent")    => RiseEvent(branch),
            ("HTMLElement", "SetAttribute") => SetAttribute(branch),
            ("HTMLElement", "GetAttribute") => GetAttribute(branch),
            ("WebBrowser",  "CMD_NAVIGATE") => Navigate(branch),
            ("Profile",     "Update")       => UpdateProfile(branch),
            _ => throw new NotSupportedException(
                     $"ветка {branch.Type}/{branch.Action} в плеере не реализована"),
        };

    // ── C# ────────────────────────────────────────────────────────────────────

    private BranchResult RunCode(Branch branch, CancellationToken ct)
    {
        var src = branch.Param("Code") ?? "";
        if (string.IsNullOrWhiteSpace(src)) return BranchResult.Empty;

        var value = _code.Run(src, ct);
        return new BranchResult(value?.ToString() ?? "");
    }

    // ── HTMLElement ───────────────────────────────────────────────────────────

    private BranchResult RiseEvent(Branch branch)
    {
        var he = Find(branch);
        var ev = _project.Expand(branch.Param("EventName")) is { Length: > 0 } e ? e : "click";
        he.RiseEvent(ev, EmulationLevel(branch));
        return BranchResult.Empty;
    }

    private BranchResult SetAttribute(Branch branch)
    {
        var he    = Find(branch);
        var attr  = branch.Param("Attribute") ?? "value";
        var value = _project.Expand(branch.Param("Value"));

        // ZP пишет любой атрибут, но value вводится с эмуляцией, а не присвоением:
        // на нём висят обработчики, которые от простого SetAttribute не сработают.
        if (attr.Equals("value", StringComparison.OrdinalIgnoreCase))
            he.SetValue(value, EmulationLevel(branch), false, false);
        else
            he.SetAttribute(attr, value);

        return BranchResult.Empty;
    }

    private BranchResult GetAttribute(Branch branch)
    {
        var he   = Find(branch);
        var attr = branch.Param("Attribute");

        // Пустой Attribute у ZP означает innertext — так стоит в шаблоне для
        // чтения капчи.
        var value = string.IsNullOrWhiteSpace(attr) || attr.Equals("innertext", StringComparison.OrdinalIgnoreCase)
            ? he.InnerText
            : he.GetAttribute(attr);

        return new BranchResult(value ?? "");
    }

    // ── Прочее ────────────────────────────────────────────────────────────────

    private BranchResult Navigate(Branch branch)
    {
        var url = _project.Expand(branch.Param("Value"));
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("CMD_NAVIGATE без адреса");
        _instance.ActiveTab.Navigate(url, "");
        return BranchResult.Empty;
    }

    private BranchResult UpdateProfile(Branch branch)
    {
        // Профиль у нас заводит браузерный слой при создании контекста, отдельной
        // операции «пересобрать и сохранить» нет. Ветка встречается в шаблонах
        // как первое действие, поэтому пропускаем её с записью в лог, а не падаем:
        // иначе ни один реальный шаблон не стартует.
        _log($"Profile/Update пропущена: профиль задаётся при создании браузера");
        return BranchResult.Empty;
    }

    // ── Finder ────────────────────────────────────────────────────────────────

    private HtmlElement Find(Branch branch)
    {
        var f = branch.ParamNode("Finder")
                ?? throw new InvalidOperationException($"{branch} без Finder");

        var type = f.Element("Type")?.Value ?? "DomFinder";
        if (type != "DomFinder")
            throw new NotSupportedException($"Finder типа {type} не реализован");

        var cond = f.Element("SearchCondition")
                   ?? throw new InvalidOperationException($"{branch}: Finder без SearchCondition");

        var tag       = _project.Expand(f.Element("Tag")?.Value);
        var attrName  = _project.Expand(cond.Attribute("AttrName")?.Value);
        var attrValue = _project.Expand(cond.Attribute("AttrValue")?.Value);
        var kind      = cond.Attribute("SearchKind")?.Value ?? "text";
        _ = int.TryParse(cond.Attribute("Number")?.Value, out var number);

        var he = _instance.ActiveTab.FindElementByAttribute(tag, attrName, attrValue, kind, number);
        if (he.IsVoid)
            throw new InvalidOperationException(
                $"элемент не найден: tag=[{tag}] {attrName}=[{attrValue}] kind=[{kind}] №{number}");

        return he;
    }

    /// <summary>ZP-шный EmulationLevel ветки; по умолчанию как у инстанса.</summary>
    private string EmulationLevel(Branch branch)
    {
        var level = branch.Param("EmulationLevel");
        return string.IsNullOrWhiteSpace(level) || level == "Current"
            ? _instance.EmulationLevel
            : level;
    }
}
