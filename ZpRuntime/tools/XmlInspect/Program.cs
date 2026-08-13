using DevDeck.Xml;

if (args.Length == 0)
{
    Console.WriteLine("использование: XmlInspect <шаблон.xml> [--run]");
    Console.WriteLine("  без --run печатает граф и пробелы в поддержке, ничего не исполняя");
    Console.WriteLine("  --run проигрывает шаблон безбраузерным Instance: ветки OwnCode");
    Console.WriteLine("        отработают, обращение к вкладке даст внятный отказ");
    return 2;
}

var run  = args.Contains("--run");
var path = args[0];
if (!File.Exists(path))
{
    Console.WriteLine($"нет файла: {path}");
    return 2;
}

var tpl   = XmlTemplate.Load(path);
var entry = tpl.EntryStep();
var dead  = tpl.UnreachableSteps().Select(s => s.Id).ToHashSet();

Console.WriteLine($"шаблон : {tpl.Name}");
Console.WriteLine($"узлов  : {tpl.Steps.Count}, веток: {tpl.Steps.Sum(s => s.Branches.Count)}");
Console.WriteLine($"вход   : {(entry is null ? "не найден" : entry.Id[..8])}");
Console.WriteLine($"мёртвых узлов: {dead.Count}");
Console.WriteLine();

// Ветки, которые плеер не умеет. Считаем только по достижимым: заготовки на
// холсте всё равно не исполняются, и раздувать ими список нечестно.
var supported = new HashSet<(string, string)>
{
    ("OwnCode", "CSharp"), ("HTMLElement", "RiseEvent"), ("HTMLElement", "SetAttribute"),
    ("HTMLElement", "GetAttribute"), ("WebBrowser", "CMD_NAVIGATE"), ("Profile", "Update"),
};

var gaps = tpl.Steps
    .Where(s => !dead.Contains(s.Id))
    .SelectMany(s => s.Branches)
    .Where(b => !supported.Contains((b.Type, b.Action)))
    .GroupBy(b => (b.Type, b.Action))
    .ToList();

foreach (var s in tpl.Steps)
{
    var mark = dead.Contains(s.Id) ? "  (недостижим)" : s.Id == entry?.Id ? "  ← вход" : "";
    Console.WriteLine($"Step {s.Id[..8]}{mark}");

    foreach (var b in s.Branches)
    {
        var ok   = supported.Contains((b.Type, b.Action)) ? " " : "!";
        var outv = Macros.OutputVariableName(b.OutputVariable);
        var arrows = new List<string>();
        if (!b.OnSuccess.IsNone) arrows.Add($"ok→{b.OnSuccess}");
        if (!b.OnError.IsNone)   arrows.Add($"err→{b.OnError}");
        if (outv.Length > 0)     arrows.Add($"⇒{outv}");

        Console.WriteLine($"  {ok} {b.Type,-12} {b.Action,-13} {b.Title,-24} {string.Join("  ", arrows)}");
    }
}

if (gaps.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("не реализовано в плеере (достижимые ветки):");
    foreach (var g in gaps)
        Console.WriteLine($"  {g.Key.Type}/{g.Key.Action} — {g.Count()} шт.");
}

if (!run) return gaps.Count == 0 ? 0 : 1;

Console.WriteLine();
Console.WriteLine("── запуск ──────────────────────────────────────────────");

// OnLog не задаём: StubProject и без него пишет в консоль, а с ним строки
// SendInfoToLog из веток задваиваются.
var project = new ZennoLab.InterfacesLibrary.ProjectModel.StubProject { Name = tpl.Name };

var player = new XmlPlayer(project, new ZennoLab.CommandCenter.Instance(), Console.WriteLine);
var result = player.Play(tpl, Path.GetDirectoryName(Path.GetFullPath(path))!);

Console.WriteLine();
Console.WriteLine(result.Success
    ? $"готово, веток выполнено: {result.BranchesRun}"
    : $"прервано на ветке {result.FailedAt}: {result.Message}");

return result.Success ? 0 : 1;
