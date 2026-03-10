// DashboardOverlay.cs
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace z3n8;

public class DashboardOverlay : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int    WM_HOTKEY    = 0x0312;
    private const uint   MOD_CONTROL  = 0x0002;
    private const uint   MOD_SHIFT    = 0x0004;
    private const uint   MOD_NOREPEAT = 0x4000;
    // Ctrl+Shift+D — activate / restore from tray
    private const int    HOTKEY_ID    = 0xB00B;
    private const uint   VK_D         = 0x44;

    private static readonly IntPtr HWND_TOPMOST   = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly string _url;
    private WebView2?       _wv;
    private bool            _isTopmost = true;
    private double          _opacity   = 0.98;
    private NotifyIcon?     _trayIcon;

    private const int B   = 5;   // resize border px
    private const int DH  = 28;  // drag bar height px
    private const int TH  = 26;  // tab bar height px

    // Tabs: label, url fragment to match for active state, url builder from base
    private static readonly (string Label, string Match, Func<string, string> Url)[] Tabs =
    [
        ("⌂",     "/",   b => b + "/"),
        ("ZP",     "page=pm",   b => b + "/?page=pm"),
        ("z3n8",     "page=scheduler",   b => b + "/?page=scheduler"),
        ("Logs",   "page=logs", b => b + "/?page=logs"),
        ("HTTP",   "page=http", b => b + "/?page=http"),
        ("Report", "/report",   b => b + "/report"),
        ("JSON",   "/json",     b => b + "/json"),
        ("TXT",   "/text",     b => b + "/text"),
        ("⚙", "/page=config",   b => b + "/?page=config"),
    ];


    
    
    
    
    private Panel?   _tabBar;
    private Label[]? _tabLabels;

    private bool      _dragging;
    private Point     _dragStartScreen;
    private Point     _formStartLocation;

    private bool      _resizing;
    private int       _resizeDir;
    private Point     _resizeStartScreen;
    private Rectangle _resizeStartBounds;

    // ── Static open ───────────────────────────────────────────────────
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
        Text            = "z3n Dashboard";
        DoubleBuffered  = true;
        try { var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"); if (File.Exists(p)) Icon = new Icon(p); } catch { }
        MinimumSize     = new Size(400, 300);

        InitTray();

        // Порядок ВАЖЕН:
        // 1. WebView2 — добавляется первым → низший Z-order
        // 2. UI поверх — добавляются после → выше в Z-order
        InitWebView();

        Application.AddMessageFilter(new GlobalMouseFilter(this));
    }

    // ── Tray + global hotkey ──────────────────────────────────────────
    private void InitTray()
    {
        _trayIcon = new NotifyIcon
        {
            Text    = "z3n Dashboard",
            Visible = false,
        };

        // Reuse window icon if available, fallback to generic app icon
        try
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            _trayIcon.Icon = File.Exists(p) ? new Icon(p) : SystemIcons.Application;
        }
        catch { _trayIcon.Icon = SystemIcons.Application; }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show  (Ctrl+Shift+D)", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Environment.Exit(0));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // "─" minimize → tray instead of taskbar
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                ShowInTaskbar  = false;
                _trayIcon.Visible = true;
                Hide();
            }
        };
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar     = true;
        WindowState       = FormWindowState.Normal;
        _trayIcon!.Visible = false;
        Activate();
        // Re-apply topmost so it surfaces above other windows
        if (_isTopmost)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                // Already visible — minimize to tray
                WindowState = FormWindowState.Minimized;
            }
            else
            {
                ShowFromTray();
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ── WebView2 — добавляем первым ───────────────────────────────────
    private async void InitWebView()
    {
        _wv = new WebView2
        {
            // Оставляем место для drag bar + tab bar сверху и border по бокам/снизу
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(B, DH + TH),
            Size     = new Size(Width - B * 2, Height - DH - TH - B)
        };
        Controls.Add(_wv);   // <-- WebView2 в Controls первым = ниже всех

        // Теперь строим UI поверх
        BuildUI();

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null,
                System.IO.Path.GetTempPath() + "\\z3n_webview2_cache");
            await _wv.EnsureCoreWebView2Async(env);

            _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _wv.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            _wv.CoreWebView2.Settings.AreDevToolsEnabled            = true;
            
            
            _wv.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                DashboardOverlay.Open(e.Uri, Width, Height);  // новое окно с тем же размером
            };

            // Extract base (scheme+host+port) from initial URL
            var uri = new Uri(_url);
            _base = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

            _wv.CoreWebView2.NavigationCompleted += (_, _) =>
                BeginInvoke(UpdateActiveTab);

            _wv.CoreWebView2.Navigate(_url);
            UpdateActiveTab();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 init failed:\n{ex.Message}\n\nInstall WebView2 Runtime.",
                "z3n Dashboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── UI: drag bar + resize borders — добавляем после WebView2 ─────
    private void BuildUI()
    {
        // ── Drag bar ──
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
        dragBar.MouseWheel       += (s, e) =>
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.3, 1.0);
            Opacity  = _opacity;
        };

        // Кнопки в drag bar
        int bx = Width - 8;
        bx -= AddBarBtn(dragBar, bx, "✕",  Color.FromArgb(255, 80, 80),   () => Close());
        bx -= AddBarBtn(dragBar, bx, "□",  Color.FromArgb(100, 160, 255), ToggleMaximize);
        bx -= AddBarBtn(dragBar, bx, "─",  Color.FromArgb(200, 200, 200), () => WindowState = FormWindowState.Minimized);
        bx -= AddBarBtn(dragBar, bx, "📌", Color.FromArgb(255, 200, 80),  ToggleTopmost);

        var title = new Label
        {
            Text      = "⬡ z3n Dashboard",
            ForeColor = Color.FromArgb(80, 140, 255),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(8, 7)
        };
        title.MouseDown        += DragBar_MouseDown;
        title.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        dragBar.Controls.Add(title);

        Controls.Add(dragBar);
        BuildTabBar();

        // ── Resize borders ──
        // Left
        AddResizePanel(new Point(0, DH + TH), new Size(B, Height - DH - TH - B),
            AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
            Cursors.SizeWE, 1);
        // Right
        AddResizePanel(new Point(Width - B, DH + TH), new Size(B, Height - DH - TH - B),
            AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
            Cursors.SizeWE, 2);
        // Top (под tab bar)
        AddResizePanel(new Point(B, DH + TH), new Size(Width - B * 2, B),
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Cursors.SizeNS, 3);
        // Bottom
        AddResizePanel(new Point(B, Height - B), new Size(Width - B * 2, B),
            AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Cursors.SizeNS, 4);
        // Corners
        AddResizePanel(new Point(0, DH),          new Size(B * 2, B * 2),
            AnchorStyles.Top | AnchorStyles.Left,           Cursors.SizeNWSE, 5); // TL
        AddResizePanel(new Point(Width - B*2, DH), new Size(B * 2, B * 2),
            AnchorStyles.Top | AnchorStyles.Right,          Cursors.SizeNESW, 6); // TR
        AddResizePanel(new Point(0, Height - B*2), new Size(B * 2, B * 2),
            AnchorStyles.Bottom | AnchorStyles.Left,        Cursors.SizeNESW, 7); // BL
        AddResizePanel(new Point(Width - B*2, Height - B*2), new Size(B * 2, B * 2),
            AnchorStyles.Bottom | AnchorStyles.Right,       Cursors.SizeNWSE, 8); // BR
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
        btn.Location   = new Point(rightX - W, 0);
        btn.Anchor     = AnchorStyles.Top | AnchorStyles.Right;
        btn.Click     += (s, e) => click();
        btn.MouseEnter += (s, e) => btn.BackColor = Color.FromArgb(40, 40, 60);
        btn.MouseLeave += (s, e) => btn.BackColor = Color.Transparent;
        parent.Controls.Add(btn);
        return W;
    }

    // ── Drag ──────────────────────────────────────────────────────────
    private void DragBar_MouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (WindowState == FormWindowState.Maximized)
        {
            GetCursorPos(out var cur);
            var sz = RestoreBounds.Size;
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

    // ── Resize ────────────────────────────────────────────────────────
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

    // ── Tab bar ──────────────────────────────────────────────────────
    private void BuildTabBar()
    {
        _tabBar = new Panel
        {
            Location  = new Point(0, DH),
            Size      = new Size(Width, TH),
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(13, 17, 23),
        };
        // Bottom border line
        _tabBar.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(48, 54, 61));
            pe.Graphics.DrawLine(pen, 0, TH - 1, _tabBar.Width, TH - 1);
        };

        _tabLabels = new Label[Tabs.Length];
        int tx = 4;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var idx = i; // capture
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

    private int _activeTab = -1;

    private void TabLabel_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Label lbl) return;
        int idx = (int)lbl.Tag!;
        if (idx != _activeTab) return;
        // Active indicator: bottom border accent line
        using var pen = new Pen(Color.FromArgb(56, 139, 253), 2);
        e.Graphics.DrawLine(pen, 0, lbl.Height - 2, lbl.Width, lbl.Height - 2);
    }

    private string _base = "";   // e.g. "http://localhost:7000"

    private void NavigateTo(int idx)
    {
        if (_wv?.CoreWebView2 is null) return;
        _wv.CoreWebView2.Navigate(Tabs[idx].Url(_base));
    }

    private void UpdateActiveTab()
    {
        if (_tabLabels is null) return;
        var url = _wv?.Source?.ToString() ?? _url;
        int found = -1;
        for (int i = 0; i < Tabs.Length; i++)
            if (url.Contains(Tabs[i].Match, StringComparison.OrdinalIgnoreCase))
                { found = i; break; }

        if (found == _activeTab) return;
        _activeTab = found;

        for (int i = 0; i < _tabLabels.Length; i++)
        {
            bool active = i == _activeTab;
            _tabLabels[i].ForeColor  = active ? Color.FromArgb(201, 209, 217) : Color.FromArgb(139, 148, 158);
            _tabLabels[i].BackColor  = active ? Color.FromArgb(22, 27, 34)    : Color.Transparent;
            _tabLabels[i].Invalidate(); // repaint to update bottom line
        }
    }

    // ── Always on top ──────────────────────────────────────────────────
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

        RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_D);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Только реальный выход (из меню трея) закрывает — всё остальное в трей
        if (e.CloseReason != CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── GlobalMouseFilter ─────────────────────────────────────────────
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