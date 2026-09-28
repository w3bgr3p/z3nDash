// ══════════════════════════════════════════════════════════════════════════════
// BasSession.cs — канал команд одного прогона.
//
// Exec зовёт поток плеера (OwnCode синхронный) и ждёт ответа. NextAsync зовёт
// HTTP-обработчик на каждом опросе Node: отдаёт ответ ожидающему Exec и держит
// соединение до следующей команды.
//
// Команда в полёте одна: плеер исполняет ветки последовательно.
// ══════════════════════════════════════════════════════════════════════════════

namespace z3nDash.Bas;

public sealed class BasSessionOptions
{
    /// <summary>
    /// Сколько /bas/next держит соединение без команды. Шаг zpstep = опрос +
    /// выполнение команды (≤ 40 с) и обязан уложиться в 60 с жизни Node-действия.
    /// </summary>
    public int LongPollMs      { get; init; } = 12_000;
    /// <summary>Запас сверх таймаута самой команды, прежде чем считать BAS пропавшим.</summary>
    public int ReplyMarginMs   { get; init; } = 10_000;
    /// <summary>Сколько команда может ждать, пока BAS её заберёт.</summary>
    public int PickupTimeoutMs { get; init; } = 30_000;
}

public sealed class BasSession
{
    private sealed class Inflight
    {
        public required BasCommand Command;
        public required string     Step;
        public readonly TaskCompletionSource<BasReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly DateTime QueuedAt = DateTime.UtcNow;
        public DateTime? DeliveredAt;
    }

    private readonly object            _lock   = new();
    private readonly SemaphoreSlim     _signal = new(0);
    private readonly Queue<BasCommand> _outbox = new();
    private readonly BasSessionOptions _opt;
    private long      _seq;
    private Inflight? _inflight;
    private string?   _broken;
    private bool      _finished;

    public string Sid { get; } = Guid.NewGuid().ToString("N");

    public BasSession(BasSessionOptions? options = null) => _opt = options ?? new BasSessionOptions();

    public bool IsFinished { get { lock (_lock) return _finished; } }

    /// <summary>Выполнить BAS-код в BAS и вернуть ZP_IO. Блокирует вызывающий поток.</summary>
    public string Exec(string step, string code, int timeoutMs)
    {
        Inflight f;
        lock (_lock)
        {
            if (_broken is { } why) throw new BasSessionBrokenException(why);
            if (_finished) throw new BasSessionBrokenException($"step={step} | прогон уже завершён");
            f = new Inflight { Command = BasCommand.Exec(++_seq, code, timeoutMs), Step = step };
            _inflight = f;
            _outbox.Enqueue(f.Command);
        }
        _signal.Release();

        while (!f.Reply.Task.Wait(50))
        {
            string? why;
            lock (_lock) why = _broken ?? Overdue(f, timeoutMs);
            if (why is null) continue;
            Break(why);
            throw new BasSessionBrokenException(why);
        }

        lock (_lock) _inflight = null;
        var r = f.Reply.Task.Result;
        if (!r.Ok) throw new BasCommandException(step, r.Error);
        return r.Value;
    }

    private string? Overdue(Inflight f, int timeoutMs)
    {
        var now = DateTime.UtcNow;
        if (f.DeliveredAt is null)
            return (now - f.QueuedAt).TotalMilliseconds > _opt.PickupTimeoutMs
                ? $"step={f.Step} | BAS не забрал команду за {_opt.PickupTimeoutMs} мс"
                : null;
        var limit = timeoutMs + _opt.ReplyMarginMs;
        return (now - f.DeliveredAt.Value).TotalMilliseconds > limit
            ? $"step={f.Step} | нет ответа BAS за {limit} мс"
            : null;
    }

    /// <summary>Опрос Node: принять ответ на прошлую команду и отдать следующую.</summary>
    public async Task<BasCommand> NextAsync(BasReply? reply, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_inflight is { DeliveredAt: not null } f && !f.Reply.Task.IsCompleted)
            {
                if (reply is not null && reply.Id == f.Command.Id) f.Reply.TrySetResult(reply);
                // Node пришёл без ответа, хотя команда ему выдана: ответ на
                // прошлый опрос до него не дошёл. Отдаём ту же команду снова.
                else if (reply is null) return f.Command;
            }
        }

        var until = DateTime.UtcNow.AddMilliseconds(_opt.LongPollMs);
        while (true)
        {
            lock (_lock)
            {
                if (_outbox.Count > 0)
                {
                    var c = _outbox.Dequeue();
                    if (_inflight is { } f && c.Op == "exec" && f.Command.Id == c.Id)
                        f.DeliveredAt = DateTime.UtcNow;
                    return c;
                }
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return BasCommand.Idle();
            await _signal.WaitAsync(left, ct);
        }
    }

    /// <summary>Прогон закончен: следующий опрос Node получит done.</summary>
    public void Finish(bool ok, string error)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            _outbox.Enqueue(BasCommand.Done(ok, error));
        }
        _signal.Release();
    }

    /// <summary>
    /// Сессия сломана: команды больше не принимаются, Node получит done: fail.
    /// Невыданные команды снимаются — их уже никто не ждёт, и выполнять их в
    /// браузере нельзя.
    /// </summary>
    public void Break(string why)
    {
        lock (_lock)
        {
            // После Finish в очереди лежит done — его нельзя стирать.
            if (_broken is not null || _finished) return;
            _broken   = why;
            _inflight = null;
            _outbox.Clear();
        }
        Finish(false, why);
    }
}
