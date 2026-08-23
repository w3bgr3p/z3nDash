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

        _log($"[xml] RiseEvent: {ev} по {Target(branch)}");

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
        var raw   = branch.Param("Value");
        var value = _project.Expand(raw);

        // Пустая подстановка — почти всегда не «так задумано», а незаполненная
        // переменная. Молча вводить ничего и отчитаться «выполнено» — ровно то,
        // из-за чего ветка потом падает через две штуки и в другом месте.
        _log($"[xml] SetAttribute: {attr} = «{value}»" +
             (raw != value ? $" (из «{raw}»)" : "") + $" в {Target(branch)}");

        if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(raw) && raw.Contains("{-"))
            _log($"[xml] SetAttribute: «{raw}» развернулось в пустую строку — вводить нечего");

        // ZP пишет любой атрибут, но value вводится с эмуляцией, а не
        // присвоением: на нём висят обработчики, которые от SetAttribute не
        // сработают. Эталонный HeSet это и делает — заодно ждёт элемент.
        if (attr.Equals("value", StringComparison.OrdinalIgnoreCase))
        {
            _instance.HeSet(Selector(branch), value, deadline: Deadline(branch));

            // Проверка по факту. ZP её не делает, но ZP и не наша подложка: без
            // неё «ветка выполнена» значит только «не бросила исключение», а
            // поле при этом может остаться пустым — и разбираться придётся уже
            // на следующей ветке, где ничего не появилось.
            var actual = ReadBack(branch);
            if (actual is not null && actual != value)
            {
                _log($"[xml] SetAttribute: в поле осталось «{actual}», а вводили «{value}»");
                _log("[xml] SetAttribute: " + Describe(branch));
            }
        }
        else _instance.GetHe(Selector(branch)).SetAttribute(attr, value);

        return BranchResult.Empty;
    }

    /// <summary>
    /// Перечитать value у того же элемента. Ошибку глотаем: это диагностика, и
    /// падать из-за неё там, где ZP не падает, нельзя.
    /// </summary>
    private string? ReadBack(Branch branch)
    {
        try   { return _instance.GetHe(Selector(branch)).GetAttribute("value"); }
        catch { return null; }
    }

    /// <summary>
    /// Состояние элемента на момент неудачного ввода. Печатается только когда
    /// значение не закрепилось, поэтому обычный прогон от этого не шумит.
    ///
    /// Смысл каждого признака — назвать причину, по которой набор проходит без
    /// единого исключения и не даёт результата:
    /// совпадений больше одного — вводили, возможно, не в то поле;
    /// нулевой размер — элемент в разметке есть, но не отрисован;
    /// readonly — Playwright спокойно шлёт нажатия, значение не меняется;
    /// disabled — то же самое.
    /// </summary>
    private string Describe(Branch branch)
    {
        try
        {
            var (tag, attr, val, kind, number) = Selector(branch);
            var all = _instance.ActiveTab.FindElementsByAttribute(tag, attr, val, kind);
            var he  = _instance.GetHe(Selector(branch));

            var ro   = he.GetAttribute("readonly");
            var dis  = he.GetAttribute("disabled");
            var type = he.GetAttribute("type");

            return $"совпадений {all.Count} (берём {number}), размер {he.Width}x{he.Height}, " +
                   $"type={(string.IsNullOrEmpty(type) ? "—" : type)}, " +
                   $"readonly={(string.IsNullOrEmpty(ro) ? "нет" : ro)}, " +
                   $"disabled={(string.IsNullOrEmpty(dis) ? "нет" : dis)}";
        }
        catch (Exception ex) { return "состояние снять не удалось: " + FirstLine(ex.Message); }
    }

    private BranchResult GetAttribute(Branch branch)
    {
        var attr = branch.Param("Attribute");

        // Пустой Attribute у ZP означает innertext — так стоит в шаблоне для
        // чтения капчи.
        var atr = string.IsNullOrWhiteSpace(attr) ? "innertext" : attr;
        _log($"[xml] GetAttribute: читаю {atr} из {Target(branch)}");

        var value = _instance.HeGet(Selector(branch), deadline: Deadline(branch), atr: atr);
        _log($"[xml] GetAttribute: прочитано «{value}»");

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

    /// <summary>Первая строка сообщения — в лог не нужен весь стек Playwright.</summary>
    private static string FirstLine(string text)
    {
        var i = text.IndexOf('\n');
        return i < 0 ? text : text[..i].TrimEnd();
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
    /// <summary>
    /// Куда метится ветка — теми же словами, какими об этом говорит ошибка
    /// поиска. В логе была одна строка «10. HTMLElement/SetAttribute», и по ней
    /// нельзя было сказать ни что вводится, ни куда: приходилось лезть в
    /// шаблон и сверять вручную.
    /// </summary>
    private string Target(Branch branch)
    {
        try
        {
            var (tag, attr, val, kind, number) = Selector(branch);
            return $"tag=[{tag}] attribute=[{attr}] pattern=[{val}] mode=[{kind}] pos=[{number}]";
        }
        catch (Exception ex) { return "цель разобрать не удалось: " + FirstLine(ex.Message); }
    }

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

    /// <summary>
    /// Сколько секунд ждать элемент. Срок лежит в самой ветке —
    /// &lt;WaitElementTime&gt;, в секундах, ровно как в поле ZP «Время ожидания
    /// элемента».
    ///
    /// Здесь стояла десятка числом и комментарий «в XML срока нет». Это было
    /// неправдой: срок есть, и он у веток разный — в шаблонах встречается и 60.
    /// Поднятое до 30 ожидание молча оставалось десяткой, а ветка падала
    /// «not found in 10s» на сайте, который просто медленнее.
    ///
    /// Ноль пропускается как есть: в ZP это «не ждать», и подменять его
    /// десяткой значит менять смысл ветки.
    /// </summary>
    private int Deadline(Branch branch)
    {
        var raw = _project.Expand(branch.Param("WaitElementTime"));
        return int.TryParse(raw, out var seconds) && seconds >= 0 ? seconds : 10;
    }

    /// <summary>
    /// Уровень эмуляции ветки. В XML это два поля: &lt;Emulation&gt; говорит,
    /// откуда брать — Current значит «как у инстанса», — и только иначе в дело
    /// идёт &lt;EmulationLevel&gt;.
    ///
    /// Проверялся один EmulationLevel на равенство «Current», а там лежит
    /// уровень («Middle»), поэтому условие не срабатывало никогда: настройка
    /// инстанса молча игнорировалась, и клик шёл с уровнем ветки даже там, где
    /// шаблон просил обратное.
    /// </summary>
    private string EmulationLevel(Branch branch)
    {
        var source = branch.Param("Emulation");
        if (string.IsNullOrWhiteSpace(source) || source.Equals("Current", StringComparison.OrdinalIgnoreCase))
            return _instance.EmulationLevel;

        var level = branch.Param("EmulationLevel");
        return string.IsNullOrWhiteSpace(level) ? _instance.EmulationLevel : level;
    }
}
