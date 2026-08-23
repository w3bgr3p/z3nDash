using System.Text.Json;

namespace DevDeck;

/// <summary>
/// Диапазон «точное число или случайное из интервала» — в планировщике
/// ZennoPoster так задаются попытки, пауза между повторами и число повторений.
/// Когда Min == Max, диапазон вырождается в точное число.
/// </summary>
public readonly record struct ZpRange(int Min, int Max)
{
    public int Pick(Random rnd) => Max <= Min ? Min : rnd.Next(Min, Max + 1);

    public static ZpRange From(JsonElement el, int fallback)
    {
        var min = ReadInt(el, "min", fallback);
        var max = ReadInt(el, "max", min);
        return new ZpRange(min, Math.Max(min, max));
    }

    private static int ReadInt(JsonElement el, string name, int fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : fallback,
            JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : fallback,
            _ => fallback,
        };
    }
}

/// <summary>Временное окно «когда повторять». To меньше From означает переход через полночь.</summary>
public readonly record struct ZpWindow(TimeSpan From, TimeSpan To)
{
    public bool Contains(TimeSpan t) => To > From ? t >= From && t < To : t >= From || t < To;

    /// <summary>Длина окна в минутах — нужна для режима «Распределить по интервалу».</summary>
    public int Minutes => (int)(To > From ? (To - From).TotalMinutes : (TimeSpan.FromDays(1) - From + To).TotalMinutes);
}

/// <summary>
/// Разобранные настройки ZP-расписания. Соответствуют шести блокам планировщика
/// ZennoPoster 7: как выполнять, начать, сколько делать, когда повторять,
/// как повторять, завершить.
/// </summary>
public sealed class ZpScheduleSpec
{
    public string How = "daily";                 // once | daily | weekly | monthly
    public HashSet<int> Weekdays = new();        // 0 = воскресенье .. 6 = суббота
    public HashSet<int> Monthdays = new();       // числа месяца из строки вида "1-5,10,20"

    public string StartMode = "now";             // now | date
    public DateTime? StartAt;

    public ZpRange Attempts = new(1, 1);
    public bool ResetSuccess;

    public List<ZpWindow> Windows = new();

    public string RepeatMode = "pause";          // back_to_back | pause | regular | spread
    public ZpRange RepeatMinutes = new(10, 10);

    public string EndMode = "never";             // date | count | never
    public DateTime? EndAt;
    public ZpRange EndCount = new(0, 0);

    /// <summary>Список ошибок настроек. Непустой — расписание включать нельзя.</summary>
    public List<string> Errors = new();
    public bool IsValid => Errors.Count == 0;
}

public static class ZpSchedule
{
    // ── Парсинг ───────────────────────────────────────────────────────────────

    public static ZpScheduleSpec Parse(string json)
    {
        var spec = new ZpScheduleSpec();
        if (string.IsNullOrWhiteSpace(json))
        {
            spec.Errors.Add("Расписание не заполнено");
            return spec;
        }

        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (Exception ex) { spec.Errors.Add($"Битый JSON расписания: {ex.Message}"); return spec; }

        spec.How = Str(root, "how", "daily");
        if (spec.How is not ("once" or "daily" or "weekly" or "monthly"))
            spec.Errors.Add($"Неизвестный режим выполнения: {spec.How}");

        if (root.TryGetProperty("weekdays", out var wd) && wd.ValueKind == JsonValueKind.Array)
            foreach (var d in wd.EnumerateArray())
                if (d.TryGetInt32(out var n) && n is >= 0 and <= 6) spec.Weekdays.Add(n);

        spec.Monthdays = ParseMonthdays(Str(root, "monthdays", ""), spec.Errors);

        if (spec.How == "weekly" && spec.Weekdays.Count == 0)
            spec.Errors.Add("Не выбран ни один день недели");
        if (spec.How == "monthly" && spec.Monthdays.Count == 0)
            spec.Errors.Add("Не указаны числа месяца");

        ParseStart(root, spec);
        ParseAttempts(root, spec);
        ParseWindows(root, spec);
        ParseRepeat(root, spec);
        ParseEnd(root, spec);

        return spec;
    }

    private static void ParseStart(JsonElement root, ZpScheduleSpec spec)
    {
        if (!root.TryGetProperty("start", out var st) || st.ValueKind != JsonValueKind.Object) return;

        spec.StartMode = Str(st, "mode", "now");
        if (spec.StartMode != "date") return;

        var raw = Str(st, "at", "");
        if (DateTime.TryParse(raw, out var at)) spec.StartAt = at;
        else spec.Errors.Add("Не разобрана дата начала");
    }

    private static void ParseAttempts(JsonElement root, ZpScheduleSpec spec)
    {
        if (!root.TryGetProperty("attempts", out var at) || at.ValueKind != JsonValueKind.Object) return;

        spec.Attempts = ZpRange.From(at, 1);
        if (spec.Attempts.Min < 1) spec.Errors.Add("Число попыток должно быть не меньше 1");
        if (at.TryGetProperty("resetSuccess", out var rs) && rs.ValueKind == JsonValueKind.True)
            spec.ResetSuccess = true;
    }

    private static void ParseWindows(JsonElement root, ZpScheduleSpec spec)
    {
        if (!root.TryGetProperty("windows", out var ws) || ws.ValueKind != JsonValueKind.Array) return;

        foreach (var w in ws.EnumerateArray())
        {
            var from = Str(w, "from", "");
            var to   = Str(w, "to", "");
            if (!TimeSpan.TryParse(from, out var f) || !TimeSpan.TryParse(to, out var t))
            {
                spec.Errors.Add($"Не разобран интервал {from}–{to}");
                continue;
            }
            if (f == t) { spec.Errors.Add($"Пустой интервал {from}–{to}"); continue; }
            spec.Windows.Add(new ZpWindow(f, t));
        }
    }

    private static void ParseRepeat(JsonElement root, ZpScheduleSpec spec)
    {
        if (!root.TryGetProperty("repeat", out var rp) || rp.ValueKind != JsonValueKind.Object) return;

        spec.RepeatMode = Str(rp, "mode", "pause");
        if (spec.RepeatMode is not ("back_to_back" or "pause" or "regular" or "spread"))
            spec.Errors.Add($"Неизвестный режим повтора: {spec.RepeatMode}");

        spec.RepeatMinutes = ZpRange.From(rp, 10);
        if (spec.RepeatMode is "pause" or "regular" && spec.RepeatMinutes.Min < 1)
            spec.Errors.Add("Пауза между повторами должна быть не меньше 1 минуты");
        if (spec.RepeatMode == "spread" && spec.Windows.Count == 0)
            spec.Errors.Add("Режим «Распределить по интервалу» требует хотя бы один интервал");
    }

    private static void ParseEnd(JsonElement root, ZpScheduleSpec spec)
    {
        if (!root.TryGetProperty("end", out var en) || en.ValueKind != JsonValueKind.Object) return;

        spec.EndMode = Str(en, "mode", "never");
        if (spec.EndMode is not ("date" or "count" or "never"))
            spec.Errors.Add($"Неизвестное условие завершения: {spec.EndMode}");

        if (spec.EndMode == "date")
        {
            var raw = Str(en, "at", "");
            if (DateTime.TryParse(raw, out var at)) spec.EndAt = at;
            else spec.Errors.Add("Не разобрана дата завершения");
        }

        if (spec.EndMode == "count")
        {
            spec.EndCount = ZpRange.From(en, 1);
            if (spec.EndCount.Min < 1) spec.Errors.Add("Число повторений должно быть не меньше 1");
        }
    }

    /// <summary>«1-5, 10, 20» → множество чисел месяца.</summary>
    private static HashSet<int> ParseMonthdays(string raw, List<string> errors)
    {
        var days = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(raw)) return days;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dash.Length == 1 && int.TryParse(dash[0], out var single) && single is >= 1 and <= 31)
            {
                days.Add(single);
                continue;
            }
            if (dash.Length == 2
                && int.TryParse(dash[0], out var lo) && int.TryParse(dash[1], out var hi)
                && lo is >= 1 and <= 31 && hi is >= 1 and <= 31 && lo <= hi)
            {
                for (var d = lo; d <= hi; d++) days.Add(d);
                continue;
            }
            errors.Add($"Не разобрано число месяца: {part}");
        }
        return days;
    }

    private static string Str(JsonElement el, string name, string fallback)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    // ── Решение о запуске ─────────────────────────────────────────────────────

    /// <summary>
    /// Состояние задачи, от которого зависит решение. LastRun — время старта
    /// последнего запуска, пока он идёт, и время завершения после него.
    /// Runs — сколько запусков сделало текущее расписание (без ручных).
    /// </summary>
    public readonly record struct State(DateTime? LastRun, int Runs, DateTime? StartedAt, bool IsRunning);

    /// <summary>Fire — пора запускать, Attempts — сколько запусков поставить.</summary>
    public readonly record struct Decision(bool Fire, int Attempts)
    {
        public static readonly Decision No = new(false, 0);
    }

    public static Decision ShouldFire(ZpScheduleSpec spec, State state, DateTime now)
    {
        if (!spec.IsValid) return Decision.No;

        var rnd = SeededRandom(state.StartedAt);

        if (!WithinLifetime(spec, state, now, rnd)) return Decision.No;
        if (!DayMatches(spec, now))                 return Decision.No;

        if (spec.How == "once")
            return state.Runs == 0 ? new Decision(true, spec.Attempts.Pick(rnd)) : Decision.No;

        if (spec.Windows.Count > 0 && !spec.Windows.Any(w => w.Contains(now.TimeOfDay)))
            return Decision.No;

        return spec.RepeatMode switch
        {
            "back_to_back" => state.IsRunning ? Decision.No : new Decision(true, spec.Attempts.Pick(rnd)),
            "pause"        => PauseDecision(spec, state, now, rnd),
            "regular"      => RegularDecision(spec, state, now, rnd),
            "spread"       => SpreadDecision(spec, state, now),
            _              => Decision.No,
        };
    }

    /// <summary>Блоки «Начать» и «Завершить»: расписание ещё не стартовало или уже исчерпано.</summary>
    private static bool WithinLifetime(ZpScheduleSpec spec, State state, DateTime now, Random rnd)
    {
        if (spec.StartMode == "date" && spec.StartAt.HasValue && now < spec.StartAt.Value) return false;
        if (spec.EndMode == "date" && spec.EndAt.HasValue && now >= spec.EndAt.Value)      return false;
        if (spec.EndMode == "count" && state.Runs >= spec.EndCount.Pick(rnd))              return false;
        return true;
    }

    /// <summary>Блок «Как выполнять» в части выбора дня.</summary>
    private static bool DayMatches(ZpScheduleSpec spec, DateTime now) => spec.How switch
    {
        "once"    => true,
        "daily"   => true,
        "weekly"  => spec.Weekdays.Contains((int)now.DayOfWeek),
        "monthly" => spec.Monthdays.Contains(now.Day),
        _         => false,
    };

    /// <summary>«Подряд с паузой»: пауза отсчитывается от завершения предыдущего запуска.</summary>
    private static Decision PauseDecision(ZpScheduleSpec spec, State state, DateTime now, Random rnd)
    {
        if (state.IsRunning) return Decision.No;
        if (state.LastRun.HasValue
            && (now - state.LastRun.Value).TotalMinutes < spec.RepeatMinutes.Pick(rnd))
            return Decision.No;
        return new Decision(true, spec.Attempts.Pick(rnd));
    }

    /// <summary>
    /// «Регулярно»: запуск через фиксированный период независимо от того,
    /// закончился ли предыдущий. Сетка отсчитывается от включения расписания.
    /// </summary>
    private static Decision RegularDecision(ZpScheduleSpec spec, State state, DateTime now, Random rnd)
    {
        var period = spec.RepeatMinutes.Pick(rnd);
        if (period < 1) return Decision.No;

        var anchor  = state.StartedAt ?? spec.StartAt ?? now.Date;
        var elapsed = (int)Math.Floor((now - anchor).TotalMinutes);
        if (elapsed < 0 || elapsed % period != 0) return Decision.No;

        // Тик раз в минуту, поэтому одну и ту же минуту сетки нельзя отработать дважды.
        if (state.LastRun.HasValue && SameMinute(state.LastRun.Value, now)) return Decision.No;

        return new Decision(true, spec.Attempts.Pick(rnd));
    }

    /// <summary>
    /// «Распределить по интервалу»: попытки раскидываются случайно по окнам дня.
    /// Раскладка детерминирована для суток — иначе на каждом тике получался бы
    /// новый набор моментов и задача запускалась бы вразнобой.
    /// </summary>
    private static Decision SpreadDecision(ZpScheduleSpec spec, State state, DateTime now)
    {
        var slots = SpreadSlots(spec, state.StartedAt, now.Date);
        var minute = (int)now.TimeOfDay.TotalMinutes;
        if (!slots.Contains(minute)) return Decision.No;
        if (state.LastRun.HasValue && SameMinute(state.LastRun.Value, now)) return Decision.No;
        return new Decision(true, 1);
    }

    /// <summary>Минуты суток, на которые расписание раскидало попытки.</summary>
    private static HashSet<int> SpreadSlots(ZpScheduleSpec spec, DateTime? startedAt, DateTime day)
    {
        var rnd   = SeededRandom(startedAt, day);
        var count = spec.Attempts.Pick(rnd);
        var slots = new HashSet<int>();
        if (spec.Windows.Count == 0 || count < 1) return slots;

        var minutes = spec.Windows
            .SelectMany(w => Enumerable.Range(0, w.Minutes)
                                       .Select(i => (int)((w.From.TotalMinutes + i) % 1440)))
            .Distinct()
            .OrderBy(m => m)
            .ToList();
        if (minutes.Count == 0) return slots;

        for (var i = 0; i < count && slots.Count < minutes.Count; i++)
            slots.Add(minutes[rnd.Next(minutes.Count)]);

        return slots;
    }

    private static bool SameMinute(DateTime a, DateTime b)
        => a.Year == b.Year && a.Month == b.Month && a.Day == b.Day
           && a.Hour == b.Hour && a.Minute == b.Minute;

    /// <summary>
    /// Диапазоны должны разворачиваться в одно и то же число на каждом тике,
    /// поэтому Random сеется временем включения расписания, а не часами.
    /// </summary>
    private static Random SeededRandom(DateTime? startedAt, DateTime? day = null)
    {
        var seed = (startedAt ?? DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        if (day.HasValue) seed ^= day.Value.Date.Ticks / TimeSpan.TicksPerDay;
        return new Random((int)(seed & 0x7FFFFFFF));
    }

    // ── Предпросмотр ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ближайшие расчётные запуски — упрощённый аналог отладчика расписания
    /// ZennoPoster. Прокручивает модель поминутно на сутки вперёд от каждого
    /// найденного запуска, считая, что предыдущий завершился мгновенно.
    /// </summary>
    public static List<DateTime> Preview(ZpScheduleSpec spec, DateTime from, int count = 20, int horizonDays = 90)
    {
        var result = new List<DateTime>();
        if (!spec.IsValid || count < 1) return result;

        var startedAt = spec.StartAt ?? from;
        var cursor    = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, from.Kind);
        var limit     = cursor.AddDays(horizonDays);
        DateTime? lastRun = null;
        var runs = 0;

        while (cursor < limit && result.Count < count)
        {
            var state    = new State(lastRun, runs, startedAt, IsRunning: false);
            var decision = ShouldFire(spec, state, cursor);
            if (decision.Fire)
            {
                result.Add(cursor);
                lastRun = cursor;
                runs   += Math.Max(1, decision.Attempts);
            }
            cursor = cursor.AddMinutes(1);
        }

        return result;
    }
}
