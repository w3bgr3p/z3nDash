using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

var helperType = typeof(z3nDash.DashboardOverlay).Assembly
    .GetType("z3nDash.WindowMaximizeGeometry");
var method = helperType?.GetMethod(
    "GetMaximizedBounds",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

if (method is null)
    return Fail("DashboardOverlay has no working-area maximize geometry.");

var cases = new[]
{
    new Case(
        "bottom taskbar",
        new Rectangle(-1920, 0, 1920, 1080),
        new Rectangle(-1920, 0, 1920, 1040),
        new Rectangle(0, 0, 1920, 1040)),
    new Case(
        "left taskbar on secondary monitor",
        new Rectangle(1920, 0, 1920, 1080),
        new Rectangle(1960, 0, 1880, 1080),
        new Rectangle(40, 0, 1880, 1080)),
};

foreach (var testCase in cases)
{
    var actual = method.Invoke(null, [testCase.MonitorBounds, testCase.WorkArea]);
    if (actual is not Rectangle actualBounds || actualBounds != testCase.Expected)
        return Fail($"{testCase.Name}: expected {testCase.Expected}, got {actual}.");
}

var applyMethod = helperType?.GetMethod(
    "ApplyWorkingArea",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

if (applyMethod is null)
    return Fail("DashboardOverlay has no WM_GETMINMAXINFO working-area handler.");

Rectangle actualWindowBounds = Rectangle.Empty;
Rectangle expectedWorkingArea = Rectangle.Empty;
var uiThread = new Thread(() =>
{
    using var form = new BorderlessTestForm(applyMethod)
    {
        FormBorderStyle = FormBorderStyle.None,
        ShowInTaskbar = false,
        Opacity = 0,
        StartPosition = FormStartPosition.Manual,
        Bounds = new Rectangle(100, 100, 800, 600),
    };

    form.Show();
    form.WindowState = FormWindowState.Maximized;
    Application.DoEvents();

    expectedWorkingArea = Screen.FromHandle(form.Handle).WorkingArea;
    actualWindowBounds = form.Bounds;
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();

if (actualWindowBounds != expectedWorkingArea)
    return Fail($"live borderless maximize: expected {expectedWorkingArea}, got {actualWindowBounds}.");

Console.WriteLine($"PASS: {cases.Length} geometry cases and live borderless maximize.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine("FAIL: " + message);
    return 1;
}

internal sealed record Case(
    string Name,
    Rectangle MonitorBounds,
    Rectangle WorkArea,
    Rectangle Expected);

internal sealed class BorderlessTestForm(MethodInfo applyMethod) : Form
{
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0024 && message.LParam != IntPtr.Zero)
            applyMethod.Invoke(null, [message.HWnd, message.LParam]);

        base.WndProc(ref message);
    }
}
