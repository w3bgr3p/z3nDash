// DashboardOverlay.cs
// Borderless WebView2-окно с drag-баром, tab-баром и resize-рамкой.
//
// ЗОНЫ ОКНА (сверху вниз):
//   [0 .. DH)          — DragBar: строка с заголовком и кнопками (✕ □ ─ 📌)
//   [DH .. DH+TH)      — TabBar: строка с вкладками
//   [DH+TH .. H-B)     — WebView2: основной контент
//   по периметру ±B    — resize-панели (невидимые, только курсор и захват мыши)
//
// КЛЮЧЕВЫЕ КОНСТАНТЫ (менять только здесь):
//   B  = толщина resize-бордера в пикселях
//   DH = высота DragBar в пикселях  (0 = убрать DragBar)
//   TH = высота TabBar  в пикселях  (0 = убрать TabBar)
//
// УДАЛИТЬ ВЕРХНЮЮ ПОЛОСУ (DragBar):
//   1. Установить DH = 0
//   2. Удалить Controls.Add(dragBar) в BuildUI()
//   3. BuildTabBar() автоматически встанет на Y=0 (использует DH)
//
// УДАЛИТЬ ВКЛАДКИ (TabBar):
//   1. Установить TH = 0
//   2. Удалить вызов BuildTabBar() в BuildUI()
//
// УДАЛИТЬ ОБЕ ПАНЕЛИ:
//   DH = 0, TH = 0 + удалить Controls.Add(dragBar) и BuildTabBar()

using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace z3n8;

public class DashboardOverlay : Form
{
    // ── Win32 API ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // ── Hotkey-константы ───────────────────────────────────────────────────────

    private const int    WM_HOTKEY    = 0x0312;
    private const uint   MOD_CONTROL  = 0x0002;  // Ctrl
    private const uint   MOD_ALT      = 0x0001;  // Alt
    private const uint   MOD_NOREPEAT = 0x4000;  // не повторять при удержании
    // Итоговая комбинация: Ctrl+Alt+Z (регистрируется в конструкторе)
    private const int    HOTKEY_ID    = 0xB00B;  // произвольный ID хоткея
    private const uint   VK_Z         = 0x5A;   // виртуальный код клавиши Z

    // ── SetWindowPos-флаги ─────────────────────────────────────────────────────

    private static readonly IntPtr HWND_TOPMOST   = new(-1); // поверх всех окон
    private static readonly IntPtr HWND_NOTOPMOST = new(-2); // обычный z-order
    
    private const uint SWP_NOMOVE     = 0x0002;  // не менять позицию
    private const uint SWP_NOSIZE     = 0x0001;  // не менять размер
    private const uint SWP_NOACTIVATE = 0x0010;  // не фокусировать окно

    // ── Геометрические константы ───────────────────────────────────────────────
    //
    //   B  — толщина невидимой resize-рамки по периметру окна
    //   DH — высота DragBar (верхняя строка с заголовком и кнопками управления)
    //   TH — высота TabBar  (строка с вкладками навигации)
    //
    //   ЧТОБЫ УБРАТЬ DragBar: DH = 0 + удалить Controls.Add(dragBar) в BuildUI()
    //   ЧТОБЫ УБРАТЬ TabBar:  TH = 0 + удалить BuildTabBar() в BuildUI()

    private const int B   = 5;   // px, ширина resize-захвата по периметру
    private const int DH  = 28;  // px, высота DragBar
    private const int TH  = 0;  // px, высота TabBar

    
    
    private static readonly PrivateFontCollection _fonts = new();

    // ── Вкладки ────────────────────────────────────────────────────────────────
    //
    // Каждая запись: (Текст на кнопке, подстрока для определения активной, функция URL)
    // Match используется в UpdateActiveTab() — сравнивает с текущим URL WebView2.
    // Url(base) строит полный URL для навигации по клику.

    private static readonly (string Label, string Match, Func<string, string> Url)[] Tabs =
    [
        ("⏻ z3n8",   "page=scheduler", b => b + "/?page=scheduler"),
        ("߷ ZP7",    "page=zp7",       b => b + "/?page=zp7"),
        ("🌍 ZB",    "page=zb",        b => b + "/?page=zb"),
        ("☰ Logs",   "page=logs",      b => b + "/?page=logs"),
        ("⭾ HTTP",   "page=http",      b => b + "/?page=http"),
        ("{} JSON",  "/json",           b => b + "/json"),
        ("¶ TXT",    "/text",           b => b + "/text"),
        ("☻ Clips",  "/clips",          b => b + "/?page=clips"),
        ("⚙ Config", "/page=config",    b => b + "/?page=config"),
        ("❓ Docs",  "/docs",           b => b + "/docs"),
        ("❓ Ai",    "/ai-report",      b => b + "/?page=ai-report"),
    ];

    // ── Поля ──────────────────────────────────────────────────────────────────

    private readonly string _url;       // начальный URL, передаётся при создании
    private WebView2?       _wv;        // WebView2-контрол
    private bool            _isTopmost; // текущее состояние always-on-top
    private double          _opacity = 0.98; // текущая прозрачность (0.3–1.0)
    private NotifyIcon?     _trayIcon;  // иконка в system tray
    private HotkeyReceiver? _hotkeyReceiver; // отдельный NativeWindow для WM_HOTKEY

    private Panel?   _tabBar;       // панель вкладок
    private Label[]? _tabLabels;    // ярлыки вкладок (индексы совпадают с Tabs[])
    private int      _activeTab = -1; // индекс активной вкладки (-1 = нет совпадения)
    private string   _base = "";    // схема+хост+порт, например "http://localhost:5000"

    // drag-состояние
    private bool      _dragging;
    private Point     _dragStartScreen;
    private Point     _formStartLocation;

    // resize-состояние
    private bool      _resizing;
    private int       _resizeDir;          // 1-8, направление (см. AddResizePanel)
    private Point     _resizeStartScreen;
    private Rectangle _resizeStartBounds;

    private CancellationTokenSource? _navTimeoutCts; // таймаут навигации (3 сек)

    // ── Статический фабричный метод ───────────────────────────────────────────
    //
    // Создаёт окно в отдельном STA-потоке (требование WinForms).
    // x/y = -1 → CenterScreen.

    public static void Open(string url, int width = 1400, int height = 900,
                            int x = -1, int y = -1)
    {
        var t = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DashboardOverlay(url, width, height, x, y));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    // ── Конструктор ───────────────────────────────────────────────────────────

    public DashboardOverlay(string url, int width, int height, int x, int y)
    {
        _url = url;

        FormBorderStyle = FormBorderStyle.None;     // без системной рамки
        StartPosition   = x >= 0 ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (x >= 0) Location = new Point(x, y);
        Size            = new Size(width, height);
        BackColor       = Color.FromArgb(13, 15, 20); // фон окна до загрузки WebView2
        Opacity         = _opacity;
        ShowInTaskbar   = true;
        Text            = "z3n8";
        DoubleBuffered  = true;
        MinimumSize     = new Size(400, 300);

        // иконка окна (необязательно, падение игнорируется)
        try
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(p)) Icon = new Icon(p);
        }
        catch { }

        InitTray();    // создаёт system tray иконку (пока скрытую)
        InitWebView(); // асинхронно инициализирует WebView2 и строит UI

        // HotkeyReceiver — отдельный NativeWindow, получает WM_HOTKEY
        // даже когда основное окно скрыто (Hide()).
        _hotkeyReceiver = new HotkeyReceiver(ToggleVisibility);
        bool ok = RegisterHotKey(_hotkeyReceiver.Handle, HOTKEY_ID,
                                 MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Z);

        if (!ok)
            MessageBox.Show(
                $"Hotkey Ctrl+Alt+Z already in use (err {Marshal.GetLastWin32Error()})",
                "z3n8", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // GlobalMouseFilter перехватывает WM_MOUSEMOVE/WM_LBUTTONUP во время
        // drag и resize — нужно, потому что мышь может уйти за пределы любого
        // дочернего контрола (в том числе за WebView2).
        Application.AddMessageFilter(new GlobalMouseFilter(this));
    }

    // ── Hotkey: показать/скрыть окно ─────────────────────────────────────────
    //
    // Ctrl+Alt+Z: если окно видно и не свёрнуто → свернуть в трей,
    //             иначе → показать из трея.

    private void ToggleVisibility()
    {
        if (InvokeRequired) { BeginInvoke(ToggleVisibility); return; }

        if (Visible && WindowState != FormWindowState.Minimized)
            WindowState = FormWindowState.Minimized;
        else
            ShowFromTray();
    }

    // ── System Tray ──────────────────────────────────────────────────────────
    //
    // Трей-иконка появляется только при сворачивании.
    // При сворачивании: ShowInTaskbar=false, иконка трея показывается, окно скрывается.
    // При разворачивании: ShowInTaskbar=true, иконка трея скрывается.

    private void InitTray()
    {
        _trayIcon = new NotifyIcon { Text = "z3n8", Visible = false };

        try
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            _trayIcon.Icon = File.Exists(p) ? new Icon(p) : SystemIcons.Application;
        }
        catch { _trayIcon.Icon = SystemIcons.Application; }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show  (Ctrl+Alt+Z)", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Environment.Exit(0));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // при каждом изменении размера проверяем: если свернули — уйти в трей
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                ShowInTaskbar     = false;
                _trayIcon.Visible = true;
                Hide();
            }
        };
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar      = true;
        WindowState        = FormWindowState.Normal;
        _trayIcon!.Visible = false;
        Activate();
        if (_isTopmost)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m) => base.WndProc(ref m);

    // ── WebView2: инициализация ───────────────────────────────────────────────
    //
    // WebView2 располагается под DragBar и TabBar.
    // Location.Y = DH + TH (начинается сразу под обеими панелями)
    // Size.Height = Height - DH - TH - B (оставляем B снизу для resize)
    //
    // ЕСЛИ DH = 0 (нет DragBar): Location.Y = TH
    // ЕСЛИ TH = 0 (нет TabBar):  Location.Y = DH
    // ЕСЛИ оба = 0:               Location.Y = B (только resize-захват сверху)

    private async void InitWebView()
    {
        _wv = new WebView2
        {
            Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(B, DH + TH),                          // ← Y-отступ = DH + TH
            Size     = new Size(Width - B * 2, Height - DH - TH - B)  // ← высота за вычетом панелей
            
        };
        

        Controls.Add(_wv);
        
        _fonts.AddFontFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, 
            @"wwwroot\static\fonts\Maxellight.ttf"));

        // BuildUI создаёт DragBar, TabBar и resize-панели.
        // Вызывается до await, чтобы UI появился немедленно, не ожидая WebView2.
        BuildUI();

        try
        {
            // Кэш WebView2 в %TEMP%\z3n_webview2_cache (изолирован от других экземпляров)
            var env = await CoreWebView2Environment.CreateAsync(null,
                System.IO.Path.GetTempPath() + "\\z3n_webview2_cache");
            await _wv.EnsureCoreWebView2Async(env);

            // Очистка кэша браузера при каждом запуске
            await _wv.CoreWebView2.Profile.ClearBrowsingDataAsync();

            _wv.CoreWebView2.Settings.IsWebMessageEnabled           = true;
            _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; // без правой кнопки
            _wv.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            _wv.CoreWebView2.Settings.AreDevToolsEnabled            = true;  // F12 работает

            // Новые окна (target="_blank" и т.п.) открываются в новом DashboardOverlay
            _wv.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                DashboardOverlay.Open(e.Uri, Width, Height);
            };

            // _base = "http://localhost:PORT" — используется для построения URL вкладок
            var uri = new Uri(_url);
            _base = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

            // После каждой завершённой навигации обновляем подсветку активной вкладки
            _wv.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _navTimeoutCts?.Cancel();
                BeginInvoke(UpdateActiveTab);
            };

            _wv.CoreWebView2.Navigate(_url);
            UpdateActiveTab();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 init failed:\n{ex.Message}\n\nInstall WebView2 Runtime.",
                "z3n8", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Построение UI (DragBar + TabBar + resize-панели) ─────────────────────
    //
    // СТРУКТУРА BuildUI():
    //   1. dragBar  — Panel (0, 0, Width, DH): заголовок + кнопки
    //   2. BuildTabBar() — Panel (0, DH, Width, TH): вкладки
    //   3. 8 resize-панелей по периметру окна
    //
    // УДАЛИТЬ DragBar: закомментировать блок dragBar и Controls.Add(dragBar)
    // УДАЛИТЬ TabBar:  закомментировать вызов BuildTabBar()

    private void BuildUI()
    {
        // ── DragBar ────────────────────────────────────────────────────────────
        //
        // Строка заголовка. Занимает всю ширину, высота DH пикселей.
        // Позволяет: перетаскивание окна (MouseDown), разворот по двойному клику,
        // изменение прозрачности колесом мыши.
        //
        // ЧТОБЫ УБРАТЬ: установить DH = 0 и удалить Controls.Add(dragBar) ниже.

        var dragBar = new Panel
        {
            Location  = new Point(0, 0),            // всегда в левом верхнем углу
            Size      = new Size(Width, DH),        // ширина = окно, высота = DH
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(20, 20, 30), // цвет фона DragBar — менять здесь
            Cursor    = Cursors.SizeAll             // курсор «перемещение» на всей полосе
        };

        dragBar.MouseDown        += DragBar_MouseDown;
        // двойной клик на DragBar = развернуть/восстановить окно
        dragBar.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        // колесо мыши на DragBar = изменить прозрачность (±5%, диапазон 30%–100%)
        dragBar.MouseWheel += (s, e) =>
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.3, 1.0);
            Opacity  = _opacity;
        };

        // Кнопки управления добавляются справа налево.
        // AddBarBtn возвращает ширину кнопки (28px), bx сдвигается влево.
        int bx = Width - 8;
        bx -= AddBarBtn(dragBar, bx, "✕",  Color.FromArgb(255, 80,  80),  () => Close());
        bx -= AddBarBtn(dragBar, bx, "□",  Color.FromArgb(100, 160, 255), ToggleMaximize);
        bx -= AddBarBtn(dragBar, bx, "─",  Color.FromArgb(200, 200, 200), () => WindowState = FormWindowState.Minimized);
        bx -= AddBarBtn(dragBar, bx, "📌", Color.FromArgb(255, 200, 80),  ToggleTopmost);

        // Заголовок слева на DragBar
        
        _fonts.AddFontFile(@"wwwroot\static\fonts\Maxellight.ttf");
        var family = _fonts.Families.First(f => f.Name.Contains("Maxel"));

        var title = new Label
        {
            Text      = "z3n8",
            ForeColor = Color.FromArgb(255, 255, 255),  // цвет текста заголовка
            BackColor = Color.Transparent,
            Font      = new Font(family, 10f, FontStyle.Regular),
            AutoSize  = true,
            Location  = new Point(8, 7)
        };
        title.MouseDown        += DragBar_MouseDown;   // заголовок тоже участвует в drag
        title.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        dragBar.Controls.Add(title);

        Controls.Add(dragBar); // ← УДАЛИТЬ ЭТУ СТРОКУ чтобы убрать DragBar

        // ── TabBar ─────────────────────────────────────────────────────────────
        // ЧТОБЫ УБРАТЬ: закомментировать BuildTabBar() и установить TH = 0
        //BuildTabBar();

        // ── Resize-панели ──────────────────────────────────────────────────────
        //
        // 8 невидимых панелей по периметру окна.
        // Каждая устанавливает нужный курсор и запускает resize при MouseDown.
        // Направления (dir):
        //   1 = левый борт      2 = правый борт
        //   3 = верхний борт    4 = нижний борт
        //   5 = левый верх      6 = правый верх
        //   7 = левый низ       8 = правый низ
        //
        // Расположение: начинаются с Y = DH + TH (под обеими панелями),
        // чтобы не перекрывать DragBar и TabBar.

        AddResizePanel(new Point(0, DH + TH),                new Size(B, Height - DH - TH - B),
            AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,   Cursors.SizeWE,   1); // левый
        AddResizePanel(new Point(Width - B, DH + TH),        new Size(B, Height - DH - TH - B),
            AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,  Cursors.SizeWE,   2); // правый
        AddResizePanel(new Point(B, DH + TH),                new Size(Width - B * 2, B),
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,    Cursors.SizeNS,   3); // верхний
        AddResizePanel(new Point(B, Height - B),             new Size(Width - B * 2, B),
            AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Cursors.SizeNS,   4); // нижний
        AddResizePanel(new Point(0, DH),                     new Size(B * 2, B * 2),
            AnchorStyles.Top | AnchorStyles.Left,                         Cursors.SizeNWSE, 5); // лев-верх
        AddResizePanel(new Point(Width - B * 2, DH),         new Size(B * 2, B * 2),
            AnchorStyles.Top | AnchorStyles.Right,                        Cursors.SizeNESW, 6); // прав-верх
        AddResizePanel(new Point(0, Height - B * 2),         new Size(B * 2, B * 2),
            AnchorStyles.Bottom | AnchorStyles.Left,                      Cursors.SizeNESW, 7); // лев-низ
        AddResizePanel(new Point(Width - B * 2, Height - B * 2), new Size(B * 2, B * 2),
            AnchorStyles.Bottom | AnchorStyles.Right,                     Cursors.SizeNWSE, 8); // прав-низ
    }

    // ── Вспомогательные методы построения UI ─────────────────────────────────

    // Добавляет одну невидимую resize-панель.
    // dir: 1-8 — направление (сохраняется в Tag, используется в DoResize)
    private void AddResizePanel(Point loc, Size sz, AnchorStyles anchor, Cursor cur, int dir)
    {
        var p = new Panel
        {
            Location  = loc,
            Size      = sz,
            Anchor    = anchor,
            BackColor = Color.Transparent,
            Cursor    = cur,
            Tag       = dir
        };
        p.MouseDown += ResizePanel_MouseDown;
        Controls.Add(p);
    }

    // Добавляет кнопку в DragBar.
    // rightX: правая граница, от которой отсчитывается позиция.
    // Возвращает ширину кнопки (для сдвига следующей кнопки).
    private int AddBarBtn(Control parent, int rightX, string text, Color fg, Action click)
    {
        const int W = 28, H = 28; // размер кнопки в DragBar — менять здесь
        var btn = new Label
        {
            Text      = text,
            ForeColor = fg,
            BackColor = Color.Transparent,
            Size      = new Size(W, H),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI", 9f)
        };
        btn.Location    = new Point(rightX - W, 0);
        btn.Anchor      = AnchorStyles.Top | AnchorStyles.Right;
        btn.Click      += (s, e) => click();
        btn.MouseEnter += (s, e) => btn.BackColor = Color.FromArgb(40, 40, 60); // hover
        btn.MouseLeave += (s, e) => btn.BackColor = Color.Transparent;
        parent.Controls.Add(btn);
        return W;
    }

    // ── Drag (перетаскивание окна) ────────────────────────────────────────────
    //
    // При MouseDown на DragBar (или заголовке):
    //   - если окно развёрнуто: сначала восстанавливается, затем начинается drag
    //   - сохраняем начальную позицию курсора и окна
    // Движение обрабатывается в OnGlobalMouseMove через GlobalMouseFilter.

    private void DragBar_MouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (WindowState == FormWindowState.Maximized)
        {
            GetCursorPos(out var cur);
            var sz  = RestoreBounds.Size;
            double rel = (double)e.X / Width; // относительная позиция курсора по X
            WindowState = FormWindowState.Normal;
            // восстанавливаем окно так, чтобы курсор был над той же относительной точкой
            Location = new Point(cur.X - (int)(sz.Width * rel), cur.Y - DH / 2);
        }

        GetCursorPos(out var cp);
        _dragging          = true;
        _dragStartScreen   = new Point(cp.X, cp.Y);
        _formStartLocation = Location;
        ((Control)s!).Capture = true; // захватываем мышь, чтобы получать события вне контрола
    }

    // ── Resize ────────────────────────────────────────────────────────────────
    //
    // MouseDown на resize-панели запускает resize.
    // Сохраняем начальное положение курсора и границы окна.
    // Движение обрабатывается в OnGlobalMouseMove → DoResize.

    private void ResizePanel_MouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState != FormWindowState.Normal) return;

        GetCursorPos(out var cp);
        _resizing          = true;
        _resizeDir         = (int)((Panel)s!).Tag!;
        _resizeStartScreen = new Point(cp.X, cp.Y);
        _resizeStartBounds = new Rectangle(Location, Size);
        ((Control)s!).Capture = true;
    }

    // Вычисляет новые границы окна по направлению dir и текущей позиции курсора.
    private void DoResize(Point cur)
    {
        int dx = cur.X - _resizeStartScreen.X;
        int dy = cur.Y - _resizeStartScreen.Y;
        var r  = _resizeStartBounds;
        int nx = r.X, ny = r.Y, nw = r.Width, nh = r.Height;

        switch (_resizeDir)
        {
            case 1: nx = r.X + dx; nw = r.Width  - dx; break; // левый
            case 2: nw = r.Width  + dx; break;                 // правый
            case 3: ny = r.Y + dy; nh = r.Height - dy; break; // верхний
            case 4: nh = r.Height + dy; break;                 // нижний
            case 5: nx = r.X + dx; nw = r.Width  - dx; ny = r.Y + dy; nh = r.Height - dy; break;
            case 6: nw = r.Width  + dx;                 ny = r.Y + dy; nh = r.Height - dy; break;
            case 7: nx = r.X + dx; nw = r.Width  - dx; nh = r.Height + dy; break;
            case 8: nw = r.Width  + dx;                 nh = r.Height + dy; break;
        }

        // ограничение минимального размера
        if (nw < MinimumSize.Width)  { if (_resizeDir is 1 or 5 or 7) nx = r.Right  - MinimumSize.Width;  nw = MinimumSize.Width; }
        if (nh < MinimumSize.Height) { if (_resizeDir is 3 or 5 or 6) ny = r.Bottom - MinimumSize.Height; nh = MinimumSize.Height; }

        SetBounds(nx, ny, nw, nh);
    }

    // Вызывается из GlobalMouseFilter при WM_MOUSEMOVE.
    internal void OnGlobalMouseMove(Point screenPt)
    {
        if (_dragging && WindowState == FormWindowState.Normal)
            Location = new Point(
                _formStartLocation.X + screenPt.X - _dragStartScreen.X,
                _formStartLocation.Y + screenPt.Y - _dragStartScreen.Y);

        if (_resizing)
            DoResize(screenPt);
    }

    // Вызывается из GlobalMouseFilter при WM_LBUTTONUP.
    internal void OnGlobalMouseUp()
    {
        _dragging = false;
        _resizing = false;
        foreach (Control c in Controls) c.Capture = false;
    }

    // ── TabBar ────────────────────────────────────────────────────────────────
    //
    // Строка вкладок под DragBar. Расположена на Y = DH.
    // Каждая вкладка — Label с Click → NavigateTo(idx).
    // Активная вкладка определяется в UpdateActiveTab() по текущему URL.
    // Активность подчёркивается синей линией (TabLabel_Paint).
    //
    // ЧТОБЫ УБРАТЬ TabBar: установить TH = 0, удалить вызов BuildTabBar() из BuildUI()

    private void BuildTabBar()
    {
        _tabBar = new Panel
        {
            Location  = new Point(0, DH),           // Y = DH (сразу под DragBar)
            Size      = new Size(Width, TH),
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(13, 17, 23), // фон TabBar — менять здесь
        };

        // нижняя граница TabBar (разделитель)
        _tabBar.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(48, 54, 61)); // цвет разделителя
            pe.Graphics.DrawLine(pen, 0, TH - 1, _tabBar.Width, TH - 1);
        };

        _tabLabels = new Label[Tabs.Length];
        int tx = 4; // начальный X-отступ первой вкладки

        for (int i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var lbl = new Label
            {
                Text      = Tabs[i].Label,
                AutoSize  = false,
                // ширина = ширина текста + 20px padding; высота = TH-1
                Size      = new Size(TextRenderer.MeasureText(Tabs[i].Label, new Font("Segoe UI", 8.5f)).Width + 20, TH - 1),
                Location  = new Point(tx, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor    = Cursors.Hand,
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(139, 148, 158), // неактивная вкладка
                BackColor = Color.Transparent,
                Tag       = idx,
            };

            lbl.Click      += (_, _) => NavigateTo(idx);
            lbl.MouseEnter += (_, _) => { if ((int)lbl.Tag != _activeTab) lbl.ForeColor = Color.FromArgb(201, 209, 217); };
            lbl.MouseLeave += (_, _) => { if ((int)lbl.Tag != _activeTab) lbl.ForeColor = Color.FromArgb(139, 148, 158); };
            lbl.Paint      += TabLabel_Paint; // рисует синюю черту под активной вкладкой
            _tabLabels[i]   = lbl;
            _tabBar.Controls.Add(lbl);
            tx += lbl.Width; // следующая вкладка вплотную к предыдущей
        }

        Controls.Add(_tabBar);
        UpdateActiveTab();
    }

    // Рисует синюю подчёркивающую линию под активной вкладкой.
    private void TabLabel_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Label lbl) return;
        if ((int)lbl.Tag! != _activeTab) return;
        using var pen = new Pen(Color.FromArgb(56, 139, 253), 2); // цвет активного подчёркивания
        e.Graphics.DrawLine(pen, 0, lbl.Height - 2, lbl.Width, lbl.Height - 2);
    }

    // Навигация по клику на вкладку.
    // Останавливает текущую навигацию, затем переходит на целевой URL.
    // Таймаут 3 сек: если URL не содержит Match — повторить навигацию.
    private void NavigateTo(int idx)
    {
        if (_wv?.CoreWebView2 is null) return;

        _wv.CoreWebView2.Stop();
        _wv.CoreWebView2.ScriptDialogOpening += SkipDialogOnce; // подавить confirm/alert при навигации

        var targetUrl = Tabs[idx].Url(_base);

        _navTimeoutCts?.Cancel();
        _navTimeoutCts = new CancellationTokenSource();
        var cts = _navTimeoutCts;

        _wv.CoreWebView2.Navigate(targetUrl);

        // fallback: если через 3 сек URL не совпал с ожидаемым — навигировать снова
        Task.Delay(3000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            BeginInvoke(() =>
            {
                if (_wv?.CoreWebView2 is null) return;
                var current = _wv.Source?.ToString() ?? "";
                if (!current.Contains(Tabs[idx].Match, StringComparison.OrdinalIgnoreCase))
                {
                    _wv.CoreWebView2.Stop();
                    _wv.CoreWebView2.Navigate(targetUrl);
                }
            });
        }, TaskScheduler.Default);
    }

    // Подавляет одиночный диалог (alert/confirm) при навигации.
    private void SkipDialogOnce(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        e.Accept();
        if (_wv?.CoreWebView2 is not null)
            _wv.CoreWebView2.ScriptDialogOpening -= SkipDialogOnce;
    }

    // Определяет активную вкладку по текущему URL WebView2.
    // Сравнивает URL с Tabs[i].Match (подстрока, без учёта регистра).
    // Обновляет цвета и фон всех вкладок.
    private void UpdateActiveTab()
    {
        if (_tabLabels is null) return;
        var url   = _wv?.Source?.ToString() ?? _url;
        int found = -1;

        for (int i = 0; i < Tabs.Length; i++)
            if (url.Contains(Tabs[i].Match, StringComparison.OrdinalIgnoreCase))
                { found = i; break; }

        if (found == _activeTab) return;
        _activeTab = found;

        for (int i = 0; i < _tabLabels.Length; i++)
        {
            bool active = i == _activeTab;
            _tabLabels[i].ForeColor = active ? Color.FromArgb(201, 209, 217) : Color.FromArgb(139, 148, 158);
            _tabLabels[i].BackColor = active ? Color.FromArgb(22, 27, 34)    : Color.Transparent;
            _tabLabels[i].Invalidate(); // перерисовать (нужно для TabLabel_Paint)
        }
    }

    // ── Always on Top ─────────────────────────────────────────────────────────
    //
    // Кнопка 📌 в DragBar.
    // Переключает z-order: HWND_TOPMOST (поверх всех) / HWND_NOTOPMOST (обычный).

    private void ToggleTopmost()
    {
        _isTopmost = !_isTopmost;
        SetWindowPos(Handle, _isTopmost ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_isTopmost)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ── Закрытие ──────────────────────────────────────────────────────────────
    //
    // Обычное Close() → свернуть в трей (не выходить из приложения).
    // Реальный выход: только через Environment.Exit(0) из трей-меню "Exit".

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.ApplicationExitCall)
        {
            e.Cancel    = true;
            WindowState = FormWindowState.Minimized;
            return;
        }
        UnregisterHotKey(_hotkeyReceiver?.Handle ?? IntPtr.Zero, HOTKEY_ID);
        _hotkeyReceiver?.ReleaseHandle();
        base.OnFormClosing(e);
    }

    // Escape закрывает окно (→ сворачивает в трей, см. OnFormClosing)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── HotkeyReceiver ────────────────────────────────────────────────────────
    //
    // Отдельный NativeWindow (не Form) для приёма WM_HOTKEY.
    // Работает независимо от видимости основного окна:
    // даже после Hide() хоткей продолжает работать.

    private sealed class HotkeyReceiver : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action _onFired;

        public HotkeyReceiver(Action onFired)
        {
            _onFired = onFired;
            CreateHandle(new CreateParams()); // создаём невидимое окно-приёмник
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onFired();
            else base.WndProc(ref m);
        }
    }

    // ── GlobalMouseFilter ─────────────────────────────────────────────────────
    //
    // IMessageFilter перехватывает сообщения мыши на уровне приложения.
    // Нужен потому, что WebView2 "поглощает" события мыши — без фильтра
    // drag и resize прерывались бы при входе курсора в область WebView2.

    private sealed class GlobalMouseFilter : IMessageFilter
    {
        private readonly DashboardOverlay _form;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;

        public GlobalMouseFilter(DashboardOverlay form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_MOUSEMOVE && (_form._dragging || _form._resizing))
            {
                GetCursorPos(out var pt);
                _form.OnGlobalMouseMove(new Point(pt.X, pt.Y));
            }
            else if (m.Msg == WM_LBUTTONUP && (_form._dragging || _form._resizing))
            {
                _form.OnGlobalMouseUp();
            }
            return false; // false = сообщение продолжает обработку дальше
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
    }
}