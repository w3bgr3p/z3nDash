using System.Diagnostics;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Windows.Forms;
#endif

namespace z3nDash;

/// <summary>
/// Преобразование селектора в буфере обмена по Alt+G / Alt+C / Alt+S.
/// Хоткеи регистрируются, только пока активно окно процесса из конфига
/// (по умолчанию ProjectMaker); пустое имя процесса снимает гейт.
/// Владеет собственным STA-потоком: RegisterHotKey доставляет WM_HOTKEY
/// в поток, который его вызвал, а главный поток приложения насоса сообщений
/// не имеет.
/// </summary>
public sealed class ClipboardConverterService : IDisposable
{
    private readonly Logger? _log;
    private readonly object  _lock = new();

    private ClipboardConfig _cfg;

    // ── счётчики и последнее наблюдение (для статуса) ─────────────────────
    private int    _converted;
    private int    _missed;
    private int    _errors;
    private string _lastResult = "";
    private string _lastError  = "";

    public ClipboardConverterService(Logger? log = null)
    {
        _log = log;
        _cfg = Config.ClipboardConfig ?? new ClipboardConfig();
        Reload();
    }

#if !WINDOWS
    // На non-windows таргете нет ни буфера, ни хоткеев. Заглушка нужна, чтобы
    // Program.cs и хендлер оставались безусловными, без #if по всему старту.
    public void Reload() { }
    public void Dispose() { }

    public object GetStatus() => new
    {
        supported   = false,
        enabled     = false,
        processName = _cfg.ProcessName ?? "",
        autoPaste   = _cfg.AutoPaste,
        foreground  = "",
        active      = false,
        hotkeys     = Array.Empty<object>(),
        converted   = 0,
        missed      = 0,
        errors      = 0,
        lastResult  = "",
        lastError   = "",
    };
#else

    // ── Win32 ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    // Union INPUT: MOUSEINPUT — самый большой член, он задаёт размер структуры.
    // Без него sizeof(INPUT) меньше ожидаемого и SendInput отклоняет вызов с кодом 87.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint       type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD  = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU    = 0x12;   // Alt
    private const ushort VK_V       = 0x56;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int  WM_HOTKEY    = 0x0312;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

    private const int WM_CLIPCONV_RELOAD = 0x8000 + 1;   // WM_APP + 1
    private const int WM_CLIPCONV_STOP   = 0x8000 + 2;   // WM_APP + 2

    private static readonly (string Combo, uint Vk)[] Keys =
    [
        ("Alt+G", 0x47),   // id 1 = HeAction.Get
        ("Alt+C", 0x43),   // id 2 = HeAction.Click
        ("Alt+S", 0x53),   // id 3 = HeAction.Set
    ];

    // ── состояние потока ──────────────────────────────────────────────────
    private Thread?       _thread;
    private HotkeyWindow? _window;
    private IntPtr        _handle;              // хэндл окна-приёмника, для PostMessage извне
    private IntPtr        _hook = IntPtr.Zero;
    private WinEventDelegate? _winEventProc;    // поле: иначе делегат соберёт GC и хук умрёт молча
    private bool          _registered;
    private readonly ManualResetEventSlim _ready = new(false);

    // статус по каждой клавише: результат ПОСЛЕДНЕЙ попытки регистрации
    private readonly bool[]   _keyOk  = new bool[Keys.Length];
    private readonly string[] _keyErr = new string[] { "", "", "" };

    private string Gate
    {
        get
        {
            var p = (_cfg.ProcessName ?? "").Trim();
            return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
        }
    }

    // ── жизненный цикл ────────────────────────────────────────────────────
    public void Reload()
    {
        bool enabled;
        lock (_lock)
        {
            _cfg    = Config.ClipboardConfig ?? new ClipboardConfig();
            enabled = _cfg.Enabled;
        }

        if (enabled && _thread == null)       Start();
        else if (!enabled && _thread != null) Stop();
        else if (_thread != null)             Post(WM_CLIPCONV_RELOAD);

        _log?.Info($"[ClipConv] reloaded enabled={enabled} process={Gate}");
    }

    private void Start()
    {
        _ready.Reset();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "clipconv" };
        _thread.SetApartmentState(ApartmentState.STA);   // Clipboard требует STA
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));            // дождаться хэндла окна
    }

    private void Stop()
    {
        var t = _thread;
        _thread = null;
        if (t == null) return;

        Post(WM_CLIPCONV_STOP);
        if (!t.Join(TimeSpan.FromSeconds(5)))
            _log?.Info("[ClipConv] поток не завершился за 5 с");
        _handle = IntPtr.Zero;
    }

    private void Post(int msg)
    {
        var h = _handle;
        if (h != IntPtr.Zero) PostMessage(h, (uint)msg, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadMain()
    {
        try
        {
            _window = new HotkeyWindow(this);
            _handle = _window.Handle;
            _ready.Set();

            ApplyOnThread();
            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            Record("thread", ex);
            _ready.Set();   // не оставлять Start() висеть на ожидании
        }
        finally
        {
            TeardownOnThread();
            try { _window?.DestroyHandle(); } catch { }
            _window = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    // ── гейт по активному окну ────────────────────────────────────────────
    // Всё в этом блоке исполняется на STA-потоке: RegisterHotKey привязывает
    // хоткей к вызвавшему потоку, а WM_HOTKEY приходит только туда же.
    private void ApplyOnThread()
    {
        TeardownHotkeysAndHook();

        if (Gate.Length == 0) { RegisterAll(); return; }   // гейт выключен

        _winEventProc = OnForegroundChanged;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        SyncToForeground();   // применить текущее состояние, не дожидаясь первого события
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd,
                                     int idObject, int idChild, uint thread, uint time)
    {
        // Колбэк исполняется внутри win32-машинерии: исключение отсюда наружу
        // с внятным контекстом не выйдет, поэтому гасим на месте.
        try { SyncToForeground(); }
        catch (Exception ex) { Record("foreground_hook", ex); }
    }

    private void SyncToForeground()
    {
        bool match = string.Equals(ForegroundProcessName(), Gate, StringComparison.OrdinalIgnoreCase);
        if (match  && !_registered) RegisterAll();
        if (!match &&  _registered) UnregisterAll();
    }

    private static string ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private void RegisterAll()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            bool ok  = RegisterHotKey(_handle, i + 1, MOD_ALT | MOD_NOREPEAT, Keys[i].Vk);
            int  err = ok ? 0 : Marshal.GetLastWin32Error();
            lock (_lock)
            {
                _keyOk[i] = ok;
                // Код как есть. Что он значит — вывод, его делает человек в карточке.
                _keyErr[i] = ok ? "" : $"step=register_hotkey | Win32 error {err}";
            }
        }
        _registered = true;
    }

    private void UnregisterAll()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            try { UnregisterHotKey(_handle, i + 1); } catch { }
            lock (_lock) { _keyOk[i] = false; _keyErr[i] = ""; }
        }
        _registered = false;
    }

    private void TeardownHotkeysAndHook()
    {
        if (_registered) UnregisterAll();
        if (_hook != IntPtr.Zero) { try { UnhookWinEvent(_hook); } catch { } _hook = IntPtr.Zero; }
        _winEventProc = null;
    }

    private void TeardownOnThread() => TeardownHotkeysAndHook();

    // ── нажатие ───────────────────────────────────────────────────────────
    private void OnHotkey(int id)
    {
        if (id < 1 || id > Keys.Length) return;

        // Страховка на случай пропущенного события хука: фокус мог уже уйти.
        if (Gate.Length > 0 &&
            !string.Equals(ForegroundProcessName(), Gate, StringComparison.OrdinalIgnoreCase))
            return;

        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex) { Record("read_clipboard", ex); return; }

        var action = (HeAction)id;
        var result = HeSelectorConverter.Convert(text, action);

        if (result == null)
        {
            lock (_lock) { _missed++; _lastResult = $"no match · {DateTime.Now:HH:mm:ss}"; }
            return;   // буфер не трогаем
        }

        try { Clipboard.SetText(result); }
        catch (Exception ex) { Record("write_clipboard", ex); return; }

        bool autoPaste;
        lock (_lock) { _converted++; _lastResult = $"{action} · {DateTime.Now:HH:mm:ss}"; autoPaste = _cfg.AutoPaste; }

        if (autoPaste) SendPaste();
    }

    // Эмуляция Ctrl+V. Хоткей сработал при зажатом Alt, а Ctrl+Alt+V — другое
    // сочетание, поэтому Alt на время вставки снимается и возвращается, если
    // пользователь всё ещё держит его физически (иначе следующий Alt+G не сработает).
    // Ctrl нажимается ДО отпускания Alt: Alt, отпущенный без другой клавиши между,
    // активирует меню окна.
    private void SendPaste()
    {
        try
        {
            Thread.Sleep(30);   // дать буферу обмена примениться до того, как приложение его прочтёт

            bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

            var seq = new List<INPUT> { Key(VK_CONTROL, false) };
            if (altHeld) seq.Add(Key(VK_MENU, true));
            seq.Add(Key(VK_V, false));
            seq.Add(Key(VK_V, true));
            seq.Add(Key(VK_CONTROL, true));
            if (altHeld) seq.Add(Key(VK_MENU, false));

            var arr  = seq.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
            if (sent != arr.Length)
                throw new InvalidOperationException(
                    $"SendInput accepted {sent} of {arr.Length} events, Win32 error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Record("send_paste", ex); }
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki   = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    // Шаг + дословный текст исключения. Без трактовки причины.
    private void Record(string step, Exception ex)
    {
        var msg = $"step={step} | {ex.GetType().Name}: {Short(ex.Message)}";
        lock (_lock) { _errors++; _lastError = msg; }
        _log?.Info($"[ClipConv] {msg}");
    }

    private static string Short(string? s, int limit = 200)
    {
        s = string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= limit ? s : s[..limit] + "...";
    }

    // ── статус ────────────────────────────────────────────────────────────
    public object GetStatus()
    {
        var foreground = ForegroundProcessName();   // вне lock: лезет в чужой процесс
        var hotkeys    = new List<object>();

        lock (_lock)
        {
            for (int i = 0; i < Keys.Length; i++)
                hotkeys.Add(new { combo = Keys[i].Combo, registered = _keyOk[i], error = _keyErr[i] });

            return new
            {
                supported   = true,
                enabled     = _cfg.Enabled,
                processName = _cfg.ProcessName ?? "",
                autoPaste   = _cfg.AutoPaste,
                foreground,
                active      = _registered,
                hotkeys,
                converted   = _converted,
                missed      = _missed,
                errors      = _errors,
                lastResult  = _lastResult,
                lastError   = _lastError,
            };
        }
    }

    // ── окно-приёмник ─────────────────────────────────────────────────────
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly ClipboardConverterService _svc;

        public HotkeyWindow(ClipboardConverterService svc)
        {
            _svc = svc;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case WM_HOTKEY:          _svc.OnHotkey(m.WParam.ToInt32()); return;
                    case WM_CLIPCONV_RELOAD: _svc.ApplyOnThread();              return;
                    case WM_CLIPCONV_STOP:
                        _svc.TeardownOnThread();
                        Application.ExitThread();
                        return;
                }
            }
            catch (Exception ex) { _svc.Record("wndproc", ex); return; }

            base.WndProc(ref m);
        }
    }
#endif
}
