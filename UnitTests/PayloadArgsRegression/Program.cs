using System.Diagnostics;
using System.Text.Json;
using z3nDash;

// Регрессия PayloadArgs: payload задачи → флаги командной строки.
// Каждая собранная строка ещё и прогоняется через настоящий python:
// проверяется то, что получит скрипт, а не только текст строки.

var failures = 0;

string Schema(params (string key, string type)[] fields)
    => JsonSerializer.Serialize(fields.Select(f => new { key = f.key, type = f.type, label = "" }));

string Values(object values) => JsonSerializer.Serialize(values);

void Expect(string name, string actual, string expected)
{
    if (actual == expected) { Console.WriteLine($"ok   {name}"); return; }
    Console.WriteLine($"FAIL {name}\n     expected: {expected}\n     actual:   {actual}");
    failures++;
}

// Что получит скрипт: argv[1:] python-а, запущенного с этой строкой.
string Argv(string arguments)
{
    var probe = Path.Combine(Path.GetTempPath(), "payload_args_probe.py");
    File.WriteAllText(probe, "import sys, json; print(json.dumps(sys.argv[1:]))");
    var psi = new ProcessStartInfo("python", $"\"{probe}\" {arguments}")
    {
        RedirectStandardOutput = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    using var proc = Process.Start(psi)!;
    var output = proc.StandardOutput.ReadToEnd().Trim();
    proc.WaitForExit();
    // Пересериализуем: у python и System.Text.Json разные пробелы и экранирование.
    return JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(output));
}

string Json(params string[] argv) => JsonSerializer.Serialize(argv);

// ── имена ─────────────────────────────────────────────────────────────────────
var r = PayloadArgs.Build("python", "", Schema(("proxyUrl", "text")), Values(new { proxyUrl = "a" }));
Expect("camelCase -> kebab-case", r.Args, "--proxy-url a");

r = PayloadArgs.Build("python", "", Schema(("acc_id", "text")), Values(new { acc_id = "7" }));
Expect("underscore stays", r.Args, "--acc_id 7");

// ── boolean ───────────────────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "",
    Schema(("dry", "boolean"), ("skip", "boolean"), ("fast", "boolean"), ("none", "boolean")),
    "{\"dry\":\"true\",\"skip\":\"False\",\"fast\":\"True\",\"none\":\"\"}");
Expect("boolean: true -> flag, false/empty -> nothing", r.Args, "--dry --fast");

r = PayloadArgs.Build("python", "", Schema(("dry", "boolean")), "{\"dry\":true}");
Expect("boolean: JSON true", r.Args, "--dry");

// ── пустые значения ──────────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "", Schema(("url", "text")), Values(new { url = "" }));
Expect("empty text -> --key \"\"", r.Args, "--url \"\"");
Expect("empty text reaches script", Argv(r.Args), Json("--url", ""));

r = PayloadArgs.Build("python", "", Schema(("url", "text")), "{}");
Expect("missing value -> --key \"\"", r.Args, "--url \"\"");

r = PayloadArgs.Build("python", "", Schema(("tags", "multiselect")), Values(new { tags = "" }));
Expect("empty multiselect -> --key \"\"", r.Args, "--tags \"\"");

r = PayloadArgs.Build("python", "", Schema(("tags", "multiselect")), Values(new { tags = "a,b" }));
Expect("multiselect -> one value", r.Args, "--tags a,b");

// ── skipEmpty ────────────────────────────────────────────────────────────────
const string SkipEmptySchema = "[{\"key\":\"out\",\"type\":\"text\",\"skipEmpty\":true},{\"key\":\"n\",\"type\":\"text\"}]";
r = PayloadArgs.Build("python", "", SkipEmptySchema, Values(new { @out = "", n = "" }));
Expect("skipEmpty: empty value not passed", r.Args, "--n \"\"");

r = PayloadArgs.Build("python", "", SkipEmptySchema, "{}");
Expect("skipEmpty: missing value not passed", r.Args, "--n \"\"");

r = PayloadArgs.Build("python", "", SkipEmptySchema, Values(new { @out = "a.har", n = "1" }));
Expect("skipEmpty: filled value passed", r.Args, "--out a.har --n 1");

r = PayloadArgs.Build("python", "", "[{\"key\":\"out\",\"type\":\"text\",\"skipEmpty\":\"true\"}]", Values(new { @out = "" }));
Expect("skipEmpty: string \"true\" accepted", r.Args, "");

r = PayloadArgs.Build("python", "", "[{\"key\":\"out\",\"type\":\"text\",\"skipEmpty\":false}]", Values(new { @out = "" }));
Expect("skipEmpty false: empty still passed", r.Args, "--out \"\"");

// ── что во флаги не идёт ─────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "", Schema(("url", "text")), Values(new { url = "x", acc0Forced = "1" }));
Expect("keys outside schema dropped", r.Args, "--url x");

r = PayloadArgs.Build("python", "",
    Schema(("", "section"), ("h", "html"), ("t", "tab"), ("url", "text")), Values(new { url = "x", h = "1", t = "2" }));
Expect("section/html/tab skipped", r.Args, "--url x");

r = PayloadArgs.Build("python", "", Schema(("url", "text"), ("url", "text")), Values(new { url = "x" }));
Expect("duplicate schema key emitted once", r.Args, "--url x");

r = PayloadArgs.Build("python", "--keep 1", "", "");
Expect("no schema -> Args untouched", r.Args, "--keep 1");

// ── совпадение с Args ────────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "--url A", Schema(("url", "text"), ("proxy", "text")), Values(new { url = "B", proxy = "p" }));
Expect("Args wins over payload", r.Args, "--url A --proxy p");
Expect("skipped flag reported", string.Join(",", r.Skipped), "--url");

r = PayloadArgs.Build("python", "--url=A", Schema(("url", "text")), Values(new { url = "B" }));
Expect("Args --key=value form wins", r.Args, "--url=A");

// ── квотирование ─────────────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "", Schema(("name", "text")), Values(new { name = "a b \"c\"" }));
Expect("spaces and quotes reach script", Argv(r.Args), Json("--name", "a b \"c\""));

r = PayloadArgs.Build("python", "", Schema(("dir", "text")), Values(new { dir = @"S:\folder x\" }));
Expect("trailing backslash reaches script", Argv(r.Args), Json("--dir", @"S:\folder x\"));

r = PayloadArgs.Build("python", "\"S:\\in dir\"", Schema(("n", "text")), Values(new { n = "5" }));
Expect("Args first, then flags", Argv(r.Args), Json(@"S:\in dir", "--n", "5"));

// ── незакрытая кавычка в Args ────────────────────────────────────────────────
try
{
    PayloadArgs.Build("python", "\"S:\\in dir\\\"", Schema(("n", "text")), Values(new { n = "5" }));
    Expect("open quote in Args throws", "no exception", "ArgumentException");
}
catch (ArgumentException ex) { Expect("open quote in Args throws", ex.Message.StartsWith("Args end inside an open quote") ? "ok" : ex.Message, "ok"); }

r = PayloadArgs.Build("python", "\"S:\\in dir\\\"", "", "");
Expect("open quote without flags is left alone", r.Args, "\"S:\\in dir\\\"");

// ── npm ───────────────────────────────────────────────────────────────────────
r = PayloadArgs.Build("npm", "run start", Schema(("n", "text")), Values(new { n = "5" }));
Expect("npm gets -- before flags", r.Args, "run start -- --n 5");

r = PayloadArgs.Build("npm", "run start -- --x 1", Schema(("n", "text")), Values(new { n = "5" }));
Expect("npm keeps existing --", r.Args, "run start -- --x 1 --n 5");

// ── маска пароля ─────────────────────────────────────────────────────────────
r = PayloadArgs.Build("python", "", Schema(("user", "text"), ("pass", "password")), Values(new { user = "u", pass = "secret" }));
Expect("password in Args is real", r.Args, "--user u --pass secret");
Expect("password in Masked is ***", r.Masked, "--user u --pass ***");

// ── битый JSON ───────────────────────────────────────────────────────────────
try
{
    PayloadArgs.Build("python", "", "{not json", "{}");
    Expect("broken schema throws", "no exception", "JsonException");
}
catch (JsonException) { Expect("broken schema throws", "JsonException", "JsonException"); }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
