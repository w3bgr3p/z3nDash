// ══════════════════════════════════════════════════════════════════════════════
// BasProtocol.cs — сообщения между хостом и Node-действием BAS.
//
// Node забирает команды long-poll'ом (/bas/next) и отвечает на них в следующем
// же опросе. Результат команды — строка из переменной BAS ZP_IO: обратно из
// BAS в Node синхронизируются только переменные из списка Node-действия
// (проверено probe v2), поэтому канал один и в нём JSON.
// ══════════════════════════════════════════════════════════════════════════════

namespace z3nDash.Bas;

/// <summary>Команда хоста. Op: "exec" | "idle" | "done".</summary>
public sealed record BasCommand(string Op, long Id = 0, string Code = "", int TimeoutMs = 0,
                                string Status = "", string Error = "")
{
    public static BasCommand Idle() => new("idle");
    public static BasCommand Exec(long id, string code, int timeoutMs) => new("exec", id, code, timeoutMs);
    public static BasCommand Done(bool ok, string error) => new("done", Status: ok ? "ok" : "fail", Error: error);
}

/// <summary>Ответ Node на exec: Value — содержимое ZP_IO, Error — дословный текст исключения BAS.</summary>
public sealed record BasReply(long Id, bool Ok, string Value, string Error);

/// <summary>BAS выполнил команду с ошибкой. Текст — шаг и дословный ответ BAS.</summary>
public sealed class BasCommandException : Exception
{
    public string Step       { get; }
    public string ServerText { get; }

    public BasCommandException(string step, string serverText)
        : base($"step={step} | server: {serverText}")
    {
        Step       = step;
        ServerText = serverText;
    }
}

/// <summary>
/// Сессия больше не может исполнять команды: BAS не забрал команду, не ответил
/// вовремя или прогон уже закончен. Вызов BAS_API снаружи не отменить, поэтому
/// после такого продолжать нельзя — следующая команда встала бы в очередь за
/// зависшей.
/// </summary>
public sealed class BasSessionBrokenException : Exception
{
    public BasSessionBrokenException(string message) : base(message) { }
}
