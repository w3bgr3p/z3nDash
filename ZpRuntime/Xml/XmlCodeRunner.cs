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

    public XmlCodeRunner(XmlCodeGlobals globals, string templateDir)
    {
        _globals     = globals;
        _templateDir = templateDir;
    }

    public object? Run(string source, CancellationToken ct)
    {
        var script = _cache.GetOrAdd(source, src =>
        {
            var s = CSharpScript.Create<object>(
                src,
                CsxExecutor.ScriptOptionsFor(System.IO.Path.Combine(_templateDir, "branch.csx")),
                globalsType: typeof(XmlCodeGlobals));

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
