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
using z3n7;                 // GetHe/HeClick/HeSet/HeGet — перенесённые из эталона
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
            ("Logic",       "Pause")        => Pause(branch, ct),
            ("ImageProcessing", "WaterMark")=> WaterMarkBranch(branch),
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
        var ev = _project.Expand(branch.Param("EventName")) is { Length: > 0 } e ? e : "click";

        // Эталонный HeClick умеет только click — остальные события шлём сами,
        // но через тот же GetHe, чтобы ожидание элемента было общим.
        if (ev.Equals("click", StringComparison.OrdinalIgnoreCase))
            _instance.HeClick(Selector(branch), deadline: Deadline(branch));
        else
            _instance.GetHe(Selector(branch)).RiseEvent(ev, EmulationLevel(branch));

        return BranchResult.Empty;
    }

    private BranchResult SetAttribute(Branch branch)
    {
        var attr  = branch.Param("Attribute") ?? "value";
        var value = _project.Expand(branch.Param("Value"));

        // ZP пишет любой атрибут, но value вводится с эмуляцией, а не
        // присвоением: на нём висят обработчики, которые от SetAttribute не
        // сработают. Эталонный HeSet это и делает — заодно ждёт элемент.
        if (attr.Equals("value", StringComparison.OrdinalIgnoreCase))
            _instance.HeSet(Selector(branch), value, deadline: Deadline(branch));
        else
            _instance.GetHe(Selector(branch)).SetAttribute(attr, value);

        return BranchResult.Empty;
    }

    private BranchResult GetAttribute(Branch branch)
    {
        var attr = branch.Param("Attribute");

        // Пустой Attribute у ZP означает innertext — так стоит в шаблоне для
        // чтения капчи.
        var atr = string.IsNullOrWhiteSpace(attr) ? "innertext" : attr;
        var value = _instance.HeGet(Selector(branch), deadline: Deadline(branch), atr: atr);

        return new BranchResult(value ?? "");
    }

    // ── Прочее ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Снимок страницы с надписью поверх. В шаблонах это ветка разбора: на ней
    /// сохраняется состояние, на котором маршрут встал.
    ///
    /// В ZP это ZennoPoster.ImageProcessingWaterMarkTextFromScreenshot с портом
    /// инстанса. Порта у нас нет, поэтому источник — Tab.GetPagePreview.
    /// </summary>
    private BranchResult WaterMarkBranch(Branch branch)
    {
        var output = _project.Expand(branch.Param("OutputFile"));
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("WaterMark без OutputFile: некуда сохранять");

        var source = branch.Param("SourceImage") ?? "Browser";
        byte[] bytes;

        if (source.Equals("Browser", StringComparison.OrdinalIgnoreCase))
        {
            bytes = Convert.FromBase64String(_instance.ActiveTab.GetPagePreview());
        }
        else
        {
            // Источником может быть файл: у ZP это ImageFile, а FilePath —
            // каталог, куда он смотрит, когда путь относительный.
            var file = _project.Expand(branch.Param("ImageFile"));
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                throw new InvalidOperationException(
                    $"WaterMark: источник {source}, но файла нет: {file}");
            bytes = File.ReadAllBytes(file);
        }

        var signType = branch.Param("SignType") ?? "Text";
        if (!signType.Equals("Text", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"WaterMark: знак вида {signType} не реализован, есть только Text");

        var saved = WaterMark.Draw(
            bytes,
            _project.Expand(branch.Param("Text")) ?? "",
            output,
            branch.Param("Font"),
            branch.Param("Location"),
            Int(branch.Param("OffsetLeft")),
            Int(branch.Param("OffsetTop")),
            Int(branch.Param("Transparency")),
            Int(branch.Param("Quality"), 100));

        _log($"[xml] снимок сохранён: {saved}");
        return new BranchResult(saved);
    }

    private static int Int(string? raw, int fallback = 0)
        => int.TryParse(raw, out var v) ? v : fallback;

    private BranchResult Navigate(Branch branch)
    {
        var url = _project.Expand(branch.Param("Value"));
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("CMD_NAVIGATE без адреса");

        // Через эталонный Go, а не ActiveTab.Navigate: там ожидание загрузки и
        // проверка, что адрес действительно сменился. Своя короткая дорога здесь
        // ровно та же ошибка, что была с однократным поиском элемента.
        _instance.Go(url);
        return BranchResult.Empty;
    }

    /// <summary>
    /// Пауза. У ZP два вида: Constant — ровно столько секунд, Random — случайно
    /// между Delay и DelayTo. Ноль тоже осмыслен: в шаблонах так ставят точку
    /// разрыва между ветками.
    /// </summary>
    private BranchResult Pause(Branch branch, CancellationToken ct)
    {
        _ = int.TryParse(_project.Expand(branch.Param("Delay")),   out var from);
        _ = int.TryParse(_project.Expand(branch.Param("DelayTo")), out var to);

        var seconds = branch.Param("PauseType") == "Random" && to > from
            ? Random.Shared.Next(from, to + 1)
            : from;

        if (seconds > 0) ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        ct.ThrowIfCancellationRequested();
        return BranchResult.Empty;
    }

    private BranchResult UpdateProfile(Branch branch)
    {
        // Личность генерируется плеером до старта по правилам из <Profile>, а
        // отпечаток задаётся при создании браузера. Отдельной операции
        // «пересобрать профиль» у нас нет, поэтому ветка только отмечается.
        _log($"Profile/Update пропущена: личность уже сгенерирована, отпечаток задан браузером");
        return BranchResult.Empty;
    }

    // ── Finder ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finder ветки в том виде, в каком его ждёт эталонный GetHe: кортеж из пяти
    /// полей. Благодаря этому поиск, ожидание и эмуляция берутся из
    /// перенесённого слоя, а не пишутся здесь заново — своя реализация искала
    /// элемент однократно и роняла ветку на странице, которая ещё грузится.
    /// </summary>
    private (string, string, string, string, int) Selector(Branch branch)
    {
        var f = branch.ParamNode("Finder")
                ?? throw new InvalidOperationException($"{branch} без Finder");

        var type = f.Element("Type")?.Value ?? "DomFinder";
        if (type != "DomFinder")
            throw new NotSupportedException($"Finder типа {type} не реализован");

        var cond = f.Element("SearchCondition")
                   ?? throw new InvalidOperationException($"{branch}: Finder без SearchCondition");

        _ = int.TryParse(cond.Attribute("Number")?.Value, out var number);

        return (_project.Expand(f.Element("Tag")?.Value),
                _project.Expand(cond.Attribute("AttrName")?.Value),
                _project.Expand(cond.Attribute("AttrValue")?.Value),
                cond.Attribute("SearchKind")?.Value ?? "text",
                number);
    }

    /// <summary>Сколько ждать элемент. В XML срока нет — берём ZP-шный по умолчанию.</summary>
    private static int Deadline(Branch branch) => 10;

    /// <summary>ZP-шный EmulationLevel ветки; по умолчанию как у инстанса.</summary>
    private string EmulationLevel(Branch branch)
    {
        var level = branch.Param("EmulationLevel");
        return string.IsNullOrWhiteSpace(level) || level == "Current"
            ? _instance.EmulationLevel
            : level;
    }
}
