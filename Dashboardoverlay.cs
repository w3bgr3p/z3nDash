#if WINDOWS
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

namespace z3nIO;

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
    private const uint   MOD_CONTROL  = 0x0002;
    private const uint   MOD_ALT      = 0x0001;
    private const uint   MOD_NOREPEAT = 0x4000;
    private const int    HOTKEY_ID    = 0xB00B;
    private const uint   VK_Z         = 0x5A;

    // ── SetWindowPos-флаги ─────────────────────────────────────────────────────

    private static readonly IntPtr HWND_TOPMOST   = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // ── Геометрические константы ───────────────────────────────────────────────

    private const int B   = 5;
    private const int DH  = 28;
    private const int TH  = 0;

    private static readonly PrivateFontCollection _fonts = new();

    // ── Вкладки ────────────────────────────────────────────────────────────────

    private static readonly (string Label, string Match, Func<string, string> Url)[] Tabs =
    [
        ("⏻ z3nIO",  "page=scheduler", b => b + "/?page=scheduler"),
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

    private readonly string _url;
    private WebView2?       _wv;
    private bool            _isTopmost;
    private double          _opacity = 0.98;
    private NotifyIcon?     _trayIcon;
    private HotkeyReceiver? _hotkeyReceiver;

    private Panel?   _tabBar;
    private Label[]? _tabLabels;
    private int      _activeTab = -1;
    private string   _base = "";

    private bool      _dragging;
    private Point     _dragStartScreen;
    private Point     _formStartLocation;

    private bool      _resizing;
    private int       _resizeDir;
    private Point     _resizeStartScreen;
    private Rectangle _resizeStartBounds;

    private CancellationTokenSource? _navTimeoutCts;

    // ── Статический фабричный метод ───────────────────────────────────────────

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

        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = x >= 0 ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (x >= 0) Location = new Point(x, y);
        Size            = new Size(width, height);
        BackColor       = Color.FromArgb(13, 15, 20);
        Opacity         = _opacity;
        ShowInTaskbar   = true;
        Text            = "z3nIO";
        DoubleBuffered  = true;
        MinimumSize     = new Size(400, 300);

        try
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(p)) Icon = new Icon(p);
        }
        catch { }

        InitTray();
        InitWebView();

        _hotkeyReceiver = new HotkeyReceiver(ToggleVisibility);
        bool ok = RegisterHotKey(_hotkeyReceiver.Handle, HOTKEY_ID,
                                 MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Z);

        if (!ok)
            MessageBox.Show(
                $"Hotkey Ctrl+Alt+Z already in use (err {Marshal.GetLastWin32Error()})",
                "z3nIO", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Application.AddMessageFilter(new GlobalMouseFilter(this));
    }

    // ── Hotkey ────────────────────────────────────────────────────────────────

    private void ToggleVisibility()
    {
        if (InvokeRequired) { BeginInvoke(ToggleVisibility); return; }

        if (Visible && WindowState != FormWindowState.Minimized)
            WindowState = FormWindowState.Minimized;
        else
            ShowFromTray();
    }

    // ── System Tray ──────────────────────────────────────────────────────────

    private void InitTray()
    {
        _trayIcon = new NotifyIcon { Text = "z3nIO", Visible = false };

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

    // ── WebView2 ──────────────────────────────────────────────────────────────

    private async void InitWebView()
    {
        _wv = new WebView2
        {
            Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(B, DH + TH),
            Size     = new Size(Width - B * 2, Height - DH - TH - B)
        };

        Controls.Add(_wv);

        _fonts.AddFontFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"wwwroot\static\fonts\Maxellight.ttf"));

        BuildUI();

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null,
                System.IO.Path.GetTempPath() + "\\z3n_webview2_cache");
            await _wv.EnsureCoreWebView2Async(env);

            await _wv.CoreWebView2.Profile.ClearBrowsingDataAsync();

            _wv.CoreWebView2.Settings.IsWebMessageEnabled           = true;
            _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _wv.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            _wv.CoreWebView2.Settings.AreDevToolsEnabled            = true;

            _wv.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                DashboardOverlay.Open(e.Uri, Width, Height);
            };

            var uri = new Uri(_url);
            _base = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

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
                "z3nIO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── BuildUI ───────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var dragBar = new Panel
        {
            Location  = new Point(0, 0),
            Size      = new Size(Width, DH),
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(20, 20, 30),
            Cursor    = Cursors.SizeAll
        };

        dragBar.MouseDown        += DragBar_MouseDown;
        dragBar.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        dragBar.MouseWheel += (s, e) =>
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.3, 1.0);
            Opacity  = _opacity;
        };

        int bx = Width - 8;
        bx -= AddBarBtn(dragBar, bx, "✕",  Color.FromArgb(255, 80,  80),  () => Close());
        bx -= AddBarBtn(dragBar, bx, "□",  Color.FromArgb(100, 160, 255), ToggleMaximize);
        bx -= AddBarBtn(dragBar, bx, "─",  Color.FromArgb(200, 200, 200), () => WindowState = FormWindowState.Minimized);
        bx -= AddBarBtn(dragBar, bx, "📌", Color.FromArgb(255, 200, 80),  ToggleTopmost);

        _fonts.AddFontFile(@"wwwroot\static\fonts\Maxellight.ttf");
        var family = _fonts.Families.First(f => f.Name.Contains("Maxel"));

        var title = new Label
        {
            Text      = "z3nIO",
            ForeColor = Color.FromArgb(255, 255, 255),
            BackColor = Color.Transparent,
            Font      = new Font(family, 10f, FontStyle.Regular),
            AutoSize  = true,
            Location  = new Point(8, 7)
        };
        title.MouseDown        += DragBar_MouseDown;
        title.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        dragBar.Controls.Add(title);

        Controls.Add(dragBar);

        //BuildTabBar();

        int top = DH + TH;
        int bot = Height - B;
        int h   = Height - top - B;

        AddResizePanel(new Point(0, top),         new Size(B, h),               AnchorStyles.Left   | AnchorStyles.Top | AnchorStyles.Bottom, Cursors.SizeWE,   1);
        AddResizePanel(new Point(Width - B, top),  new Size(B, h),               AnchorStyles.Right  | AnchorStyles.Top | AnchorStyles.Bottom, Cursors.SizeWE,   2);
        AddResizePanel(new Point(B, top),          new Size(Width - B * 2, B),   AnchorStyles.Top    | AnchorStyles.Left | AnchorStyles.Right,  Cursors.SizeNS,   3);
        AddResizePanel(new Point(B, bot),          new Size(Width - B * 2, B),   AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,  Cursors.SizeNS,   4);
        AddResizePanel(new Point(0, top),          new Size(B, B),               AnchorStyles.Left   | AnchorStyles.Top,                        Cursors.SizeNWSE, 5);
        AddResizePanel(new Point(Width - B, top),  new Size(B, B),               AnchorStyles.Right  | AnchorStyles.Top,                        Cursors.SizeNESW, 6);
        AddResizePanel(new Point(0, bot),          new Size(B, B),               AnchorStyles.Left   | AnchorStyles.Bottom,                     Cursors.SizeNESW, 7);
        AddResizePanel(new Point(Width - B, bot),  new Size(B, B),               AnchorStyles.Right  | AnchorStyles.Bottom,                     Cursors.SizeNWSE, 8);
    }

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

    private int AddBarBtn(Control parent, int rightX, string text, Color fg, Action click)
    {
        const int W = 28, H = 28;
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
        btn.MouseEnter += (s, e) => btn.BackColor = Color.FromArgb(40, 40, 60);
        btn.MouseLeave += (s, e) => btn.BackColor = Color.Transparent;
        parent.Controls.Add(btn);
        return W;
    }

    // ── Drag ──────────────────────────────────────────────────────────────────

    private void DragBar_MouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (WindowState == FormWindowState.Maximized)
        {
            GetCursorPos(out var cur);
            var sz  = RestoreBounds.Size;
            double rel = (double)e.X / Width;
            WindowState = FormWindowState.Normal;
            Location = new Point(cur.X - (int)(sz.Width * rel), cur.Y - DH / 2);
        }

        GetCursorPos(out var cp);
        _dragging          = true;
        _dragStartScreen   = new Point(cp.X, cp.Y);
        _formStartLocation = Location;
        ((Control)s!).Capture = true;
    }

    // ── Resize ────────────────────────────────────────────────────────────────

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

    private void DoResize(Point cur)
    {
        int dx = cur.X - _resizeStartScreen.X;
        int dy = cur.Y - _resizeStartScreen.Y;
        var r  = _resizeStartBounds;
        int nx = r.X, ny = r.Y, nw = r.Width, nh = r.Height;

        switch (_resizeDir)
        {
            case 1: nx = r.X + dx; nw = r.Width  - dx; break;
            case 2: nw = r.Width  + dx; break;
            case 3: ny = r.Y + dy; nh = r.Height - dy; break;
            case 4: nh = r.Height + dy; break;
            case 5: nx = r.X + dx; nw = r.Width  - dx; ny = r.Y + dy; nh = r.Height - dy; break;
            case 6: nw = r.Width  + dx;                 ny = r.Y + dy; nh = r.Height - dy; break;
            case 7: nx = r.X + dx; nw = r.Width  - dx; nh = r.Height + dy; break;
            case 8: nw = r.Width  + dx;                 nh = r.Height + dy; break;
        }

        if (nw < MinimumSize.Width)  { if (_resizeDir is 1 or 5 or 7) nx = r.Right  - MinimumSize.Width;  nw = MinimumSize.Width; }
        if (nh < MinimumSize.Height) { if (_resizeDir is 3 or 5 or 6) ny = r.Bottom - MinimumSize.Height; nh = MinimumSize.Height; }

        SetBounds(nx, ny, nw, nh);
    }

    internal void OnGlobalMouseMove(Point screenPt)
    {
        if (_dragging && WindowState == FormWindowState.Normal)
            Location = new Point(
                _formStartLocation.X + screenPt.X - _dragStartScreen.X,
                _formStartLocation.Y + screenPt.Y - _dragStartScreen.Y);

        if (_resizing)
            DoResize(screenPt);
    }

    internal void OnGlobalMouseUp()
    {
        _dragging = false;
        _resizing = false;
        foreach (Control c in Controls) c.Capture = false;
    }

    // ── TabBar ────────────────────────────────────────────────────────────────

    private void BuildTabBar()
    {
        _tabBar = new Panel
        {
            Location  = new Point(0, DH),
            Size      = new Size(Width, TH),
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(13, 17, 23),
        };

        _tabBar.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(48, 54, 61));
            pe.Graphics.DrawLine(pen, 0, TH - 1, _tabBar.Width, TH - 1);
        };

        _tabLabels = new Label[Tabs.Length];
        int tx = 4;

        for (int i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var lbl = new Label
            {
                Text      = Tabs[i].Label,
                AutoSize  = false,
                Size      = new Size(TextRenderer.MeasureText(Tabs[i].Label, new Font("Segoe UI", 8.5f)).Width + 20, TH - 1),
                Location  = new Point(tx, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor    = Cursors.Hand,
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(139, 148, 158),
                BackColor = Color.Transparent,
                Tag       = idx,
            };

            lbl.Click      += (_, _) => NavigateTo(idx);
            lbl.MouseEnter += (_, _) => { if ((int)lbl.Tag != _activeTab) lbl.ForeColor = Color.FromArgb(201, 209, 217); };
            lbl.MouseLeave += (_, _) => { if ((int)lbl.Tag != _activeTab) lbl.ForeColor = Color.FromArgb(139, 148, 158); };
            lbl.Paint      += TabLabel_Paint;
            _tabLabels[i]   = lbl;
            _tabBar.Controls.Add(lbl);
            tx += lbl.Width;
        }

        Controls.Add(_tabBar);
        UpdateActiveTab();
    }

    private void TabLabel_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Label lbl) return;
        if ((int)lbl.Tag! != _activeTab) return;
        using var pen = new Pen(Color.FromArgb(56, 139, 253), 2);
        e.Graphics.DrawLine(pen, 0, lbl.Height - 2, lbl.Width, lbl.Height - 2);
    }

    private void NavigateTo(int idx)
    {
        if (_wv?.CoreWebView2 is null) return;

        _wv.CoreWebView2.Stop();
        _wv.CoreWebView2.ScriptDialogOpening += SkipDialogOnce;

        var targetUrl = Tabs[idx].Url(_base);

        _navTimeoutCts?.Cancel();
        _navTimeoutCts = new CancellationTokenSource();
        var cts = _navTimeoutCts;

        _wv.CoreWebView2.Navigate(targetUrl);

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

    private void SkipDialogOnce(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        e.Accept();
        if (_wv?.CoreWebView2 is not null)
            _wv.CoreWebView2.ScriptDialogOpening -= SkipDialogOnce;
    }

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
            _tabLabels[i].Invalidate();
        }
    }

    // ── Always on Top ─────────────────────────────────────────────────────────

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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── HotkeyReceiver ────────────────────────────────────────────────────────

    private sealed class HotkeyReceiver : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action _onFired;

        public HotkeyReceiver(Action onFired)
        {
            _onFired = onFired;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onFired();
            else base.WndProc(ref m);
        }
    }

    // ── GlobalMouseFilter ─────────────────────────────────────────────────────

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
            return false;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
    }
}
#endif