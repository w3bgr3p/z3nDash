namespace z3nIO;

/// <summary>
/// Редиректит Console.Out в log sink на время блока using.
/// Console.WriteLine → log?.Invoke + оригинальный stdout.
///
/// Использование:
///   using var _ = new ConsoleSink(log);
///   Console.WriteLine("hello"); // попадёт и в дашборд и в stdout
/// </summary>
public sealed class ConsoleSink : IDisposable
{
    private readonly TextWriter _original;

    public ConsoleSink(Action<string>? log)
    {
        _original = Console.Out;
        if (log != null)
            Console.SetOut(new SinkWriter(log, _original));
    }

    public void Dispose() => Console.SetOut(_original);

    private sealed class SinkWriter(Action<string> log, TextWriter original) : TextWriter
    {
        public override System.Text.Encoding Encoding => original.Encoding;

        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            log(value ?? "");
        }

        public override void Write(string? value)
        {
            original.Write(value);
        }
    }
}