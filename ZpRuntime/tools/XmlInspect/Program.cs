using DevDeck.Xml;

if (args.Length == 0)
{
    Console.WriteLine("использование: XmlInspect <шаблон.xml> [--run] [--browser|--attach <ws>] [--headless] [--profile <dir>]");
    Console.WriteLine("  без --run   печатает граф и пробелы в поддержке, ничего не исполняя");
    Console.WriteLine("  --run       проигрывает шаблон; без браузера ветки со страницей откажут");
    Console.WriteLine("  --browser   поднять браузер через Patchright; нужен --profile");
    Console.WriteLine("  --attach ws подключиться по CDP к уже поднятому браузеру, например к профилю ZennoBrowser");
    Console.WriteLine("  --profile   каталог профиля: куки, localStorage, отпечаток");
    Console.WriteLine("  --headless  только для разбора шаблона: headless видно по десятку признаков");
    Console.WriteLine("  --proxy     прокси браузера; задаётся при запуске, на живом инстансе не меняется");
    return 2;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var run        = args.Contains("--run");
var wantLocal  = args.Contains("--browser");
var headless   = args.Contains("--headless");
var attachTo   = Arg("--attach");
var profileDir = Arg("--profile");
var proxy      = Arg("--proxy");
var path       = args[0];
if (!File.Exists(path))
{
    Console.WriteLine($"нет файла: {path}");
    return 2;
}

var tpl  = XmlTemplate.Load(path);
var dead = tpl.UnreachableSteps().Select(s => s.Id).ToHashSet();

Console.WriteLine($"шаблон   : {tpl.Name}");
Console.WriteLine($"узлов    : {tpl.Steps.Count}, веток: {tpl.Steps.Sum(s => s.Branches.Count)}");
Console.WriteLine($"Start    : {tpl.Start}");
Console.WriteLine($"GoodEnd  : {tpl.GoodEnd}");
Console.WriteLine($"BadEnd   : {tpl.BadEnd}");
Console.WriteLine($"артефактов (недостижимых узлов): {dead.Count}");
Console.WriteLine($"переменных: {tpl.Variables.Count}, usings: {tpl.OwnCode.Usings.Length}, "
                  + $"CommonCode: {tpl.OwnCode.CommonCode.Length} симв., "
                  + $"References: {string.Join(", ", tpl.OwnCode.References)}");
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
    var mark = dead.Contains(s.Id)      ? "  (артефакт)"
             : s.Id == tpl.Start.StepId  ? "  ← Start"
             : s.Id == tpl.GoodEnd.StepId? "  ← GoodEnd"
             : s.Id == tpl.BadEnd.StepId ? "  ← BadEnd" : "";
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

DevDeck.Browser.BrowserSession? session = null;

if (attachTo is not null)
{
    Console.WriteLine($"[br] подключаюсь по CDP: {attachTo}");
    session = await DevDeck.Browser.BrowserSession.AttachAsync(attachTo);
}
else if (wantLocal)
{
    // Профиль обязателен: Patchright работает через persistent context.
    profileDir ??= Path.Combine(Path.GetTempPath(), "devdeck-xml", "profile-" + tpl.Name);
    Console.WriteLine($"[br] Patchright{(headless ? " (headless)" : "")}, профиль {profileDir}");
    if (headless)
        Console.WriteLine("[br] headless видно по десятку признаков — для живых аккаунтов запускайте без него");
    // Прокси браузера задаётся при запуске и на живом инстансе не меняется,
    // поэтому берём его до старта: из флага, а иначе из переменной шаблона —
    // ветки разбирают ту же строку и потом сверяют её через SetProxy.
    var launchProxy = DevDeck.Browser.BrowserSession.NormalizeProxy(
        proxy ?? tpl.Variables.GetValueOrDefault("proxy"));

    if (launchProxy.Length > 0) Console.WriteLine($"[br] прокси {launchProxy}");
    else Console.WriteLine("[br] без прокси — ветки с ProxySet откажут");

    session = await DevDeck.Browser.BrowserSession.LaunchAsync(
        profileDir, headless, launchProxy.Length > 0 ? launchProxy : null);
}

var instance = session?.Instance ?? new ZennoLab.CommandCenter.Instance();
if (session is null)
    Console.WriteLine("[br] браузера нет: ветки со страницей откажут (--browser или --attach)");

var player = new XmlPlayer(project, instance, Console.WriteLine);
var result = player.Play(tpl, Path.GetDirectoryName(Path.GetFullPath(path))!);

if (session is not null) await session.DisposeAsync();

Console.WriteLine();
Console.WriteLine(result.Success
    ? $"готово, веток выполнено: {result.BranchesRun}"
    : $"прервано на ветке {result.FailedAt}: {result.Message}");

return result.Success ? 0 : 1;
