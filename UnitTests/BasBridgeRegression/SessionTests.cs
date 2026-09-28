using z3nDash.Bas;

namespace BasBridgeRegression;

public static class SessionTests
{
    static BasSessionOptions Fast => new() { LongPollMs = 200, ReplyMarginMs = 300, PickupTimeoutMs = 400 };

    public static async Task Run(ExpectFn expect)
    {
        // Команда уходит в BAS, ответ возвращается вызывающему как есть.
        {
            var s = new BasSession(Fast);
            var call = Task.Run(() => s.Exec("tab.URL", "url()!", 1000));
            var cmd = await s.NextAsync(null);
            expect("exec: команда выдана", cmd.Op == "exec" && cmd.Code == "url()!" && cmd.TimeoutMs == 1000, cmd.ToString());
            await s.NextAsync(new BasReply(cmd.Id, true, "\"https://a/\"", ""));
            expect("exec: значение ZP_IO вернулось", await call == "\"https://a/\"");
        }

        // Ошибка BAS → исключение с шагом и дословным текстом.
        {
            var s = new BasSession(Fast);
            var call = Task.Run(() => s.Exec("element.InnerText css=h1", "x", 1000));
            var cmd = await s.NextAsync(null);
            await s.NextAsync(new BasReply(cmd.Id, false, "", "Таймаут во время выполнения text for [x]"));
            var ex = await Catch(call);
            expect("error: тип", ex is BasCommandException, ex?.GetType().Name ?? "нет исключения");
            expect("error: шаг и дословный текст", ex?.Message == "step=element.InnerText css=h1 | server: Таймаут во время выполнения text for [x]", ex?.Message ?? "");
        }

        // BAS не забрал команду → сессия сломана, Node получит done: fail.
        {
            var s = new BasSession(Fast);
            var ex = await Catch(Task.Run(() => s.Exec("tab.Navigate", "x", 1000)));
            expect("pickup: сломана", ex is BasSessionBrokenException && ex.Message.Contains("не забрал команду"), ex?.Message ?? "");
            // Невыданная команда снята: её уже никто не ждёт.
            var next = await s.NextAsync(null);
            expect("pickup: опрос получает done fail, а не брошенную команду", next.Op == "done" && next.Status == "fail", next.ToString());
        }

        // Команда выдана, ответа нет дольше timeout + margin → сломана.
        {
            var s = new BasSession(Fast);
            var call = Task.Run(() => s.Exec("element.Click", "x", 200));
            await s.NextAsync(null);
            var ex = await Catch(call);
            expect("reply timeout: сломана", ex is BasSessionBrokenException && ex.Message.Contains("нет ответа BAS за 500 мс"), ex?.Message ?? "");
            var next = await s.NextAsync(null);
            expect("reply timeout: следующий опрос получает done fail", next.Op == "done" && next.Status == "fail", next.ToString());
            var ex2 = await Catch(Task.Run(() => s.Exec("после", "x", 200)));
            expect("reply timeout: новые команды отказываются", ex2 is BasSessionBrokenException, ex2?.GetType().Name ?? "нет исключения");
        }

        // Ответ на опрос потерялся: Node пришёл без reply — получает ту же команду снова.
        {
            var s = new BasSession(Fast);
            var call = Task.Run(() => s.Exec("tab.URL", "url()!", 1000));
            var first = await s.NextAsync(null);
            var again = await s.NextAsync(null);
            expect("resend: та же команда", again.Op == "exec" && again.Id == first.Id, again.ToString());
            await s.NextAsync(new BasReply(first.Id, true, "\"u\"", ""));
            expect("resend: ответ принят", await call == "\"u\"");
        }

        // Чужой id в ответе игнорируется.
        {
            var s = new BasSession(Fast);
            var call = Task.Run(() => s.Exec("tab.URL", "url()!", 1000));
            var cmd = await s.NextAsync(null);
            await s.NextAsync(new BasReply(cmd.Id + 100, true, "\"wrong\"", ""));
            await s.NextAsync(new BasReply(cmd.Id, true, "\"right\"", ""));
            expect("stale: принят только свой id", await call == "\"right\"");
        }

        // Команд нет → idle по истечении long-poll.
        {
            var s = new BasSession(Fast);
            var t0 = DateTime.UtcNow;
            var cmd = await s.NextAsync(null);
            expect("idle", cmd.Op == "idle" && (DateTime.UtcNow - t0).TotalMilliseconds >= 150, cmd.ToString());
        }

        // Finish → done ok.
        {
            var s = new BasSession(Fast);
            s.Finish(true, "");
            var cmd = await s.NextAsync(null);
            expect("finish ok", cmd.Op == "done" && cmd.Status == "ok", cmd.ToString());
            expect("finish: сессия завершена", s.IsFinished);
        }
    }

    static async Task<Exception?> Catch<T>(Task<T> t)
    {
        try { await t; return null; } catch (Exception ex) { return ex; }
    }
}
