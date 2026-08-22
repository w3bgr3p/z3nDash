// ══════════════════════════════════════════════════════════════════════════════
// XmlCodeRunner.cs — исполнение <Code> из веток OwnCode.
//
// Отличие от CsxExecutor только в источнике: там файл на диске и кеш по его
// хешу, здесь строка из XML. Набор ссылок и usings общий — он вынесен в
// CsxExecutor.ScriptOptionsFor, чтобы csx и шаблоны видели одно и то же
// окружение. Скрипт, написанный в ZennoPoster, не знает, чем его запустят.
//
// Компиляция кешируется по тексту блока: в цикле шаблон проходит одну и ту же
// ветку сотни раз, а Roslyn стоит десятки миллисекунд на компиляцию.
// ══════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace DevDeck.Xml;

/// <summary>
/// Globals для кода веток. Имена совпадают с ZennoPoster: там в OwnCode доступны
/// project и instance, и шаблоны написаны под них.
/// </summary>
public sealed class XmlCodeGlobals
{
    public required ZennoLab.InterfacesLibrary.ProjectModel.IZennoPosterProjectModel project { get; init; }
    public required ZennoLab.CommandCenter.Instance                                  instance { get; init; }
}

public sealed class XmlCodeRunner
{
    private static readonly ConcurrentDictionary<string, Script<object>> _cache = new();

    private readonly XmlCodeGlobals _globals;
    private readonly string         _templateDir;
    private readonly OwnCodeContext _context;
    private readonly ScriptOptions  _options;

    public XmlCodeRunner(XmlCodeGlobals globals, string templateDir, OwnCodeContext? context = null)
    {
        _globals     = globals;
        _templateDir = templateDir;
        _context     = context ?? new OwnCodeContext();
        _options     = BuildOptions();
    }

    /// <summary>
    /// Окружение csx плюс то, что объявил сам шаблон: usings из OwnCodeUsings.Text
    /// и сборки из References. Ссылки ищутся среди уже загруженных — z3n7 и
    /// Newtonsoft у нас в процессе, а вот z3n7.Nmlx или z3n7.Captcha нет, и
    /// молчать об этом нельзя: ветка упадёт непонятной ошибкой имени.
    /// </summary>
    private ScriptOptions BuildOptions()
    {
        var opt = CsxExecutor.ScriptOptionsFor(System.IO.Path.Combine(_templateDir, "branch.csx"));

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .GroupBy(a => a.GetName().Name!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // References сопоставляются по имени сборки, а у нас перенесённый код
        // лежит в ZpRuntime, а не в сборке «z3n7». Поэтому здесь честно
        // отмечается только то, чего в процессе действительно нет; на
        // компиляцию влияют не ссылки, а usings ниже.
        foreach (var name in _context.References)
            if (loaded.TryGetValue(name, out var asm)) opt = opt.AddReferences(asm);
            else Missing.Add(name);

        // Using на несуществующее пространство имён — ошибка компиляции, и она
        // убила бы ветку, которая этим пространством не пользуется. Шаблоны
        // тащат в Text весь набор из ZennoPoster, включая z3n7.Captcha и прочие
        // проекты, которых у нас нет. Поэтому пропускаем только те, что реально
        // существуют, а остальные показываем списком.
        var known = KnownNamespaces(loaded.Values);
        var usable = _context.Usings.Where(known.Contains).ToArray();
        UnknownUsings.AddRange(_context.Usings.Except(usable));

        if (usable.Length > 0) opt = opt.AddImports(usable);

        if (CompileCommonCode(loaded.Values) is { } common)
            opt = opt.AddReferences(common);

        return opt;
    }

    /// <summary>Сборки из &lt;References&gt;, которых нет в процессе.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Usings шаблона, для которых не нашлось пространства имён.</summary>
    public List<string> UnknownUsings { get; } = [];

    private static HashSet<string> KnownNamespaces(IEnumerable<System.Reflection.Assembly> loaded)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in loaded)
        {
            IEnumerable<Type> types;
            try { types = a.GetExportedTypes(); }
            catch { continue; }   // сборка может не грузиться целиком — не наша забота

            foreach (var t in types)
                if (t.Namespace is { Length: > 0 } ns) set.Add(ns);
        }
        return set;
    }

    /// <summary>
    /// CommonCode шаблона — обычный C#-файл с namespace и классами, а не скрипт.
    /// Roslyn в скриптовом режиме namespace запрещает (CS7021), поэтому дописать
    /// его к тексту ветки нельзя: компилируем отдельной сборкой в память и
    /// подключаем ссылкой. Так же поступает и ZennoPoster — там это часть
    /// сборки проекта, общая для всех веток.
    /// </summary>
    private MetadataReference? CompileCommonCode(IEnumerable<System.Reflection.Assembly> loaded)
    {
        if (string.IsNullOrWhiteSpace(_context.CommonCode)) return null;

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            _context.CommonCode,
            new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                kind: Microsoft.CodeAnalysis.SourceCodeKind.Regular));

        var refs = loaded
            .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location))
            .ToList();

        // Имя сборки завязано на хеш исходника. Раньше оно было одно на все
        // шаблоны — "TemplateCommonCode", — и второй шаблон в том же процессе
        // падал с «Assembly with same name is already loaded»: у DevDeck
        // планировщик долгоживущий, там за сессию проходит не один шаблон.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(_context.CommonCode)))[..16];
        var asmName = $"TemplateCommonCode_{hash}";

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devdeck-xml");
        System.IO.Directory.CreateDirectory(dir);
        var dll = System.IO.Path.Combine(dir, $"{asmName}.dll");

        // Тот же шаблон во второй раз: сборка уже в процессе, второй раз её
        // грузить нельзя и незачем — просто ссылаемся на готовый файл.
        var already = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == asmName);
        if (already is not null && System.IO.File.Exists(dll))
            return Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(dll);

        var comp = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            asmName,
            [tree],
            refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        // Пишем на диск, а не держим в памяти: скрипт исполняется в этом же
        // процессе, и CLR разрешает ссылку по имени сборки. Загрузка из byte[]
        // такую сборку по имени не находит — ветка падает на «Could not load
        // file or assembly».
        var ms   = new MemoryStream();
        var emit = comp.Emit(ms);

        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .Take(20)
                .ToList();
            throw new InvalidOperationException(
                "CommonCode шаблона не компилируется:\n" + string.Join("\n", errors));
        }

        // Файл мог остаться от прошлого запуска и быть занят уже загруженной
        // сборкой. Содержимое при совпадении хеша то же самое, переписывать
        // нечего — а падать на занятом файле незачем.
        try { System.IO.File.WriteAllBytes(dll, ms.ToArray()); }
        catch (System.IO.IOException) when (System.IO.File.Exists(dll)) { }

        System.Reflection.Assembly.LoadFrom(dll);

        return Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(dll);
    }

    /// <summary>
    /// Скомпилировать код ветки, не исполняя его. Нужен диагностике: так видно,
    /// чего шаблону не хватает, без побочных действий — а они у веток настоящие,
    /// вплоть до покупки номера и регистрации аккаунта.
    /// </summary>
    public void Compile(string source) => Prepare(source);

    private Script<object> Prepare(string source)
    {
        return _cache.GetOrAdd(source, src =>
        {
            var s = CSharpScript.Create<object>(src, _options, globalsType: typeof(XmlCodeGlobals));

            var errors = s.Compile()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "не компилируется:" + Environment.NewLine
                    + string.Join(Environment.NewLine, errors));

            return s;
        });
    }

    public object? Run(string source, CancellationToken ct)
    {
        var script = _cache.GetOrAdd(source, src =>
        {
            var s = CSharpScript.Create<object>(src, _options, globalsType: typeof(XmlCodeGlobals));

            var errors = s.Compile()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "не компилируется:\n" + string.Join("\n", errors));

            return s;
        });

        return script.RunAsync(_globals, ct).GetAwaiter().GetResult().ReturnValue;
    }
}
