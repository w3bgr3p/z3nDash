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

namespace z3nDash.Xml;

/// <summary>
/// Что ветка вернула: текст для OutputVariable, если он есть, и — для веток,
/// которые сами решают, куда идти дальше, — адрес перехода.
/// </summary>
/// <param name="Output">Значение для OutputVariable.</param>
/// <param name="Goto">
/// Переход вместо OnSuccess. null — ветка выбор не делала, маршрут идёт обычным
/// правилом. Это не то же самое, что <see cref="BranchRef.None"/>: None значит
/// «ветка выбрала вариант, у которого перехода нет» — на холсте это вариант без
/// стрелки, и дальше действует то же правило, что при пустом OnSuccess.
/// </param>
public readonly record struct BranchResult(string Output, BranchRef? Goto = null)
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
            ("Logic",       "Switch")       => Switch(branch),
            ("Logic",       "Alert")        => Alert(branch),
            ("Emulation",   "KeyBoard")     => KeyBoard(branch, ct),
            ("Emulation",   "MouseClick")   => EmulatedClick(branch),
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

        SettleAfterAction();
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
            // Два чтения подряд: через наш же элемент и напрямую со страницы.
            // Первое говорит, что наш поиск считает результатом; второе — что
            // на самом деле лежит в документе. Пока они не сведены, спорить о
            // причинах бессмысленно.
            _log($"[xml] SetAttribute: после ввода наш элемент «{ReadBack(branch) ?? "прочитать не вышло"}», " +
                 $"страница: {PageSide(branch)}");
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
    /// Чтение того же места напрямую из документа, мимо нашего поиска: сколько
    /// узлов, какое у них value, виден ли узел и какого он размера.
    /// </summary>
    private string PageSide(Branch branch)
    {
        try
        {
            var (tag, attr, val, kind, number) = Selector(branch);
            if (!kind.Equals("text", StringComparison.OrdinalIgnoreCase))
                return $"режим {kind} — прямое чтение не делаю";

            var css = CssFor(tag, attr, val);
            var js  =
                "var n = document.querySelectorAll(" + Quote(css) + ");" +
                "var out = 'узлов ' + n.length;" +
                "for (var i = 0; i < n.length && i < 4; i++) {" +
                "  var e = n[i], r = e.getBoundingClientRect(), st = getComputedStyle(e);" +
                "  out += ' | #' + i + ' value=[' + e.value + '] ' + Math.round(r.width) + 'x' + Math.round(r.height) +" +
                "         ' display=' + st.display + ' visibility=' + st.visibility + ' opacity=' + st.opacity +" +
                "         (e.readOnly ? ' readonly' : '') + (e.disabled ? ' disabled' : '');" +
                "}" +
                "return out;";

            return _instance.ActiveTab.MainDocument.EvaluateScript(js);
        }
        catch (Exception ex) { return "прочитать не вышло: " + FirstLine(ex.Message); }
    }

    /// <summary>Тот же CSS, что строит подложка: ZP-шный "input:text" — не селектор.</summary>
    private static string CssFor(string tag, string attr, string val)
    {
        var t = (tag ?? "*").Trim();
        var i = t.IndexOf(':');
        var cssTag = i <= 0
            ? t
            : t[(i + 1)..].Equals("text", StringComparison.OrdinalIgnoreCase)
                ? $"{t[..i]}:is([type='text' i], :not([type]))"
                : $"{t[..i]}[type='{t[(i + 1)..]}' i]";
        return $"{cssTag}[{attr}='{val}']";
    }

    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

    /// <summary>
    /// Дождаться страницы, если действие её сменило.
    ///
    /// Клик по ссылке может открыть окно на месте, а может увести на другой
    /// адрес — со стороны ветки это неразличимо. Без ожидания следующая ветка
    /// работает с уходящей страницей: ввод ложится в поле, которое через
    /// мгновение заменится новым, пустым. Проверка после ввода при этом
    /// показывает введённое — она успевает раньше перехода, — и наружу всё
    /// выглядит успешным.
    ///
    /// Именно так вёл себя airbnb под прокси: клик «Se connecter ou s'inscrire»
    /// вместо окна на месте перезагружал страницу целиком.
    ///
    /// Так же поступает и эталонный Go: «if (IsBusy) WaitDownloading()».
    /// </summary>
    private void SettleAfterAction()
    {
        if (_instance.ActiveTab.IsBusy) _instance.ActiveTab.WaitDownloading();
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

    /// <summary>
    /// Ветвление по значению. В XML: &lt;Parameters&gt;&lt;Variable&gt; — то, что
    /// сравнивается, а варианты и переход по каждому лежат в &lt;Results&gt;.
    ///
    /// Сравнение точное, посимвольное. Приводит ли ZP регистр и обрезает ли
    /// пробелы — я не проверял, поэтому не приводим и не обрезаем: лишнее
    /// совпадение увело бы маршрут не туда молча. Чтобы промах было видно, в
    /// лог идёт и само значение, и выбранный вариант.
    /// </summary>
    private BranchResult Switch(Branch branch)
    {
        var value = _project.Expand(branch.Param("Variable")) ?? "";

        foreach (var c in branch.Cases)
        {
            if (!string.Equals(c.Key, value, StringComparison.Ordinal)) continue;

            _log($"[xml] Switch: «{value}» → вариант «{c.Key}»" +
                 (c.Target.IsNone ? ", перехода у варианта нет" : $", ухожу в {c.Target}"));
            return new BranchResult("", c.Target);
        }

        if (branch.CaseDefault is { } fallback)
        {
            _log($"[xml] Switch: «{value}» не совпало ни с одним из " +
                 $"{branch.Cases.Count} вариантов → Default" +
                 (fallback.IsNone ? ", перехода у Default нет" : $", ухожу в {fallback}"));
            return new BranchResult("", fallback);
        }

        // Ни варианта, ни Default. Молчать нельзя: маршрут пойдёт по обычному
        // правилу, и это будет выглядеть как «ветка отработала».
        _log($"[xml] Switch: «{value}» не совпало ни с одним из " +
             $"{branch.Cases.Count} вариантов, а Default в ветке нет — перехода не будет");
        return BranchResult.Empty;
    }

    /// <summary>
    /// Сообщение в лог. В ProjectMaker это ещё и окно с кнопкой, но окна тут нет
    /// и быть не может: раннер работает без человека у экрана. Поэтому пишем в
    /// лог в любом случае, а AlertAutoClose и AlertCloseTimeout не соблюдаем —
    /// ждать закрытия несуществующего окна значило бы просто спать.
    ///
    /// Уровень в XML записан как «Lavel» — это опечатка самого ZennoPoster, в
    /// файлах поле называется именно так.
    /// </summary>
    /// <summary>
    /// Клик по координатам окна, а не по элементу. В XML задан прямоугольник
    /// Xmin/Xmax/Ymin/Ymax — в шаблонах он обычно вырожден в точку.
    ///
    /// Что означает ClickDistribution внутри прямоугольника, я не проверял:
    /// точка берётся равномерно. Это влияет на то, куда именно внутри
    /// прямоугольника придётся клик, и никак — на вырожденный случай.
    /// Незнакомое значение не отменяет клик: отказаться было бы хуже, чем
    /// кликнуть не по той кривой.
    ///
    /// Курсор сначала подводится эмуляцией, а потом жмётся кнопка.
    /// Замер 2026-09-21 на капче megapari: нажатие без подвода, сразу после
    /// телепорта курсора, страница иногда не отрабатывает, а ветка названа Emulation.
    /// </summary>
    private BranchResult EmulatedClick(Branch branch)
    {
        int Coord(string name)
        {
            _ = int.TryParse(_project.Expand(branch.Param(name)), out var v);
            return v;
        }

        int Between(int a, int b) => a == b ? a : Random.Shared.Next(Math.Min(a, b), Math.Max(a, b) + 1);

        var x = Between(Coord("Xmin"), Coord("Xmax"));
        var y = Between(Coord("Ymin"), Coord("Ymax"));

        var button = (branch.Param("MouseButtonClick") ?? "").Trim().ToLowerInvariant() switch
        {
            "right"  => "right",
            "middle" => "middle",
            _        => "left",
        };

        _log($"[xml] MouseClick: {button} в {x},{y}");

        _instance.ActiveTab.FullEmulationMouseMove(x, y);
        _instance.ActiveTab.MouseClick(x, y, button, "click", false);

        SettleAfterAction();
        return BranchResult.Empty;
    }

    /// <summary>
    /// Ввод с клавиатуры. В XML: &lt;Text&gt; — что набрать, &lt;Latency&gt; —
    /// миллисекунды между нажатиями. Специальные клавиши записаны вставками
    /// вида <c>{BACKSPACE}</c> вперемежку с обычным текстом.
    ///
    /// Порядок важен: сначала разбиваем исходную строку на клавиши и
    /// куски текста, и только потом раскрываем макросы в кусках. Иначе
    /// значение переменной с фигурной скобкой внутри будет прочтено как клавиша.
    /// Сам макрос при этом из разбора исключён: он тоже в фигурных
    /// скобках, и без этого {-Variable.numPhone-} читался как клавиша.
    ///
    /// Список имён клавиш ниже — наш, а не выписанный из ZP: полного набора
    /// его вставок я не проверял. Незнакомая вставка поэтому не набирается
    /// буквально, а роняет ветку с её именем в тексте: набранный в поле
    /// «{F13}» нашёлся бы через три ветки и в другом месте.
    /// </summary>
    private BranchResult KeyBoard(Branch branch, CancellationToken ct)
    {
        var raw = branch.Param("Text") ?? "";
        if (raw.Length == 0) return BranchResult.Empty;

        _ = int.TryParse(branch.Param("Latency"), out var latency);
        if (latency < 0) latency = 0;

        foreach (var (isKey, value) in SplitKeys(raw))
        {
            ct.ThrowIfCancellationRequested();

            if (isKey)
            {
                _instance.ActiveTab.KeyEvent(KeyName(value), "press");
                if (latency > 0) ct.WaitHandle.WaitOne(latency);
                continue;
            }

            var text = _project.Expand(value);
            if (text.Length > 0) _instance.ActiveTab.TypeText(text, latency);
        }

        SettleAfterAction();
        return BranchResult.Empty;
    }

    /// <summary>Разбор «abc{ENTER}def» на чередующиеся куски.</summary>
    private static IEnumerable<(bool IsKey, string Value)> SplitKeys(string raw)
    {
        int at = 0;
        while (at < raw.Length)
        {
            var open = raw.IndexOf('{', at);
            var close = open < 0 ? -1 : raw.IndexOf('}', open + 1);

            // Одинокая «{» без пары — обычный символ, а не ошибка.
            if (open < 0 || close < 0)
            {
                yield return (false, raw.Substring(at));
                yield break;
            }

            if (open > at) yield return (false, raw.Substring(at, open - at));

            var inner = raw.Substring(open + 1, close - open - 1);

            // Макрос ZP записан в тех же фигурных скобках, что и клавиша:
            // {-Variable.numPhone-} рядом с {BACKSPACE}. Различает их дефис внутри
            // скобки — макрос уходит в текст целиком и раскрывается дальше.
            yield return inner.StartsWith("-", StringComparison.Ordinal)
                ? (false, raw.Substring(open, close - open + 1))
                : (true, inner);

            at = close + 1;
        }
    }

    /// <summary>Имя вставки ZP → имя клавиши Playwright.</summary>
    private static string KeyName(string token) => token.Trim().ToUpperInvariant() switch
    {
        "ENTER" or "RETURN" => "Enter",
        "TAB"               => "Tab",
        "BACKSPACE" or "BS" => "Backspace",
        "DELETE" or "DEL"   => "Delete",
        "ESC" or "ESCAPE"   => "Escape",
        "SPACE"             => "Space",
        "UP"                => "ArrowUp",
        "DOWN"              => "ArrowDown",
        "LEFT"              => "ArrowLeft",
        "RIGHT"             => "ArrowRight",
        "HOME"              => "Home",
        "END"               => "End",
        "PGUP" or "PAGEUP"     => "PageUp",
        "PGDN" or "PAGEDOWN"   => "PageDown",
        "INSERT" or "INS"      => "Insert",
        _ => throw new NotSupportedException(
                 $"Emulation/KeyBoard: вставка {{{token}}} не разобрана — " +
                 "такой клавиши в таблице плеера нет"),
    };

    private BranchResult Alert(Branch branch)
    {
        var text = _project.Expand(branch.Param("AlertText")) ?? "";
        var show = string.Equals(branch.Param("AlertShowInPoster"), "True",
                                 StringComparison.OrdinalIgnoreCase);

        switch ((branch.Param("Lavel") ?? "").Trim().ToLowerInvariant())
        {
            case "error":   _project.SendErrorToLog(text, show);   break;
            case "warning": _project.SendWarningToLog(text, show); break;
            default:        _project.SendInfoToLog(text, show);    break;
        }

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
