using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Playwright;
using OpenCvSharp;

namespace z3n8.Browser
{
    /// <summary>
    /// Реализация canvas-методов IBrowserInstance для PlaywrightInstance:
    /// image matching (OpenCvSharp matchTemplate) + mouse/touch actions.
    /// </summary>
    public sealed partial class PlaywrightInstance
    {
        private static readonly Random _rng = new();

        // ── Profile ───────────────────────────────────────────────────────────

        public IBrowserProfile Profile => new PlaywrightProfile(_activePage);

        // ── CloseExtraTabs ────────────────────────────────────────────────────

        public void CloseExtraTabs()
        {
            var pages = _context.Pages.ToList();
            foreach (var p in pages.Skip(1))
                Sync(p.CloseAsync());
            if (pages.Count > 0)
                _activePage = pages[0];
        }

        // ── SetCookie ─────────────────────────────────────────────────────────

        /// <summary>
        /// Принимает строку в формате ZP (tab-separated Netscape cookie format).
        /// Каждая строка: domain \t includeSubdomains \t path \t secure \t expires \t name \t value
        /// </summary>
        public void SetCookie(string cookieString)
        {
            var cookies = new List<Cookie>();
            foreach (var line in cookieString.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 7) continue;
                cookies.Add(new Cookie
                {
                    Domain  = p[0],
                    Path    = p[2],
                    Secure  = p[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    Name    = p[5],
                    Value   = p[6],
                    SameSite = SameSiteAttribute.None
                });
            }
            if (cookies.Count > 0)
                Sync(_context.AddCookiesAsync(cookies));
        }

        // ── SaveRequestHeadersToVariable ──────────────────────────────────────

        public void SaveRequestHeadersToVariable(IBrowserInstance instance, string url, bool includeBody)
        {
            // no-op: в ZP это перехват трафика ZennoBrowser.
            // В Playwright-стеке авторизация идёт через прямые API-вызовы,
            // поэтому метод присутствует для совместимости сигнатуры.
        }

        // ── Viewport helpers ──────────────────────────────────────────────────

        public int[] CenterArea(int w, int h = 0)
        {
            if (h == 0) h = w;
            var size = _activePage.ViewportSize ?? new PageViewportSizeResult { Width = 1280, Height = 720 };
            int cx = size.Width  / 2;
            int cy = size.Height / 2;
            return new[] { cx - w / 2, cy - h / 2, w, h };
        }

        public int[] GetCenter()
        {
            var size = _activePage.ViewportSize ?? new PageViewportSizeResult { Width = 1280, Height = 720 };
            return new[] { size.Width / 2, size.Height / 2 };
        }

        // ── Screenshot helper ─────────────────────────────────────────────────

        /// <summary>Снимает скриншот области [x, y, w, h] и возвращает Mat.</summary>
        private Mat ScreenshotArea(int[] area)
        {
            // area: [x, y, w, h]
            var bytes = Sync(_activePage.ScreenshotAsync(new PageScreenshotOptions
            {
                Clip = new Clip { X = area[0], Y = area[1], Width = area[2], Height = area[3] }
            }));
            return Cv2.ImDecode(bytes, ImreadModes.Color);
        }

        /// <summary>Base64 PNG/любой формат → Mat.</summary>
        private static Mat Base64ToMat(string base64)
        {
            var bytes = Convert.FromBase64String(base64);
            return Cv2.ImDecode(bytes, ImreadModes.Color);
        }

        // ── FindImg ───────────────────────────────────────────────────────────

        public int[] FindImg(string base64Template, int[] area, float threshold = 0.97f)
        {
            using var scene    = ScreenshotArea(area);
            using var tmpl     = Base64ToMat(base64Template);
            using var result   = new Mat();

            Cv2.MatchTemplate(scene, tmpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

            if (maxVal < threshold) return null;

            // Возвращаем координаты центра совпадения в системе координат страницы
            int x = area[0] + maxLoc.X + tmpl.Width  / 2;
            int y = area[1] + maxLoc.Y + tmpl.Height / 2;
            return new[] { x, y };
        }

        // ── FindMultipleInScreenshot ──────────────────────────────────────────

        public Dictionary<string, int[]> FindMultipleInScreenshot(
            Dictionary<string, string> templates, int[] area, float threshold = 0.85f)
        {
            using var scene  = ScreenshotArea(area);
            var found = new Dictionary<string, int[]>();

            foreach (var kv in templates)
            {
                using var tmpl   = Base64ToMat(kv.Value);
                using var result = new Mat();

                Cv2.MatchTemplate(scene, tmpl, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                if (maxVal < threshold) continue;

                int x = area[0] + maxLoc.X + tmpl.Width  / 2;
                int y = area[1] + maxLoc.Y + tmpl.Height / 2;
                found[kv.Key] = new[] { x, y };
            }

            return found;
        }

        // ── FindMultipleInMultipleAreas ───────────────────────────────────────

        public Dictionary<string, int[]> FindMultipleInMultipleAreas(
            Dictionary<string, (string template, int[] area)> templatesWithAreas, float threshold = 0.85f)
        {
            var found = new Dictionary<string, int[]>();

            foreach (var kv in templatesWithAreas)
            {
                var coords = FindImg(kv.Value.template, kv.Value.area, threshold);
                if (coords != null)
                    found[kv.Key] = coords;
            }

            return found;
        }

        // ── JsClick ───────────────────────────────────────────────────────────

        public void JsClick(int x, int y)
            => Sync(_activePage.EvaluateAsync(
                $"document.elementFromPoint({x},{y})?.dispatchEvent(new MouseEvent('click',{{bubbles:true,cancelable:true,clientX:{x},clientY:{y}}}))"));

        // ── TapCenter / CenterMouse ───────────────────────────────────────────

        public void TapCenter()
        {
            var c = GetCenter();
            Sync(_activePage.Mouse.ClickAsync(c[0], c[1]));
        }

        public void CenterMouse()
        {
            var c = GetCenter();
            Sync(_activePage.Mouse.MoveAsync(c[0], c[1]));
        }

        // ── SwipeFromCenter ───────────────────────────────────────────────────

        public void SwipeFromCenter(int distance, object unused, int[] bounds)
        {
            var c = GetCenter();

            double angle  = _rng.NextDouble() * Math.PI * 2;
            int    tx     = (int)(c[0] + distance * Math.Cos(angle));
            int    ty     = (int)(c[1] + distance * Math.Sin(angle));

            // Зажимаем в bounds [x, y, w, h]
            tx = Math.Clamp(tx, bounds[0], bounds[0] + bounds[2]);
            ty = Math.Clamp(ty, bounds[1], bounds[1] + bounds[3]);

            SwipeBetweenPoints(c[0], c[1], tx, ty);
        }

        // ── TapImg / SwipeImgToCenter / ClickImg ──────────────────────────────

        public void TapImg(string base64Template, int[] area, float threshold = 0.97f)
        {
            var coords = FindImg(base64Template, area, threshold)
                ?? throw new Exception($"TapImg: template not found in area [{area[0]},{area[1]},{area[2]},{area[3]}]");
            Sync(_activePage.Touchscreen.TapAsync(coords[0], coords[1]));
        }

        public void SwipeImgToCenter(string base64Template, int[] area, float threshold = 0.97f)
        {
            var coords = FindImg(base64Template, area, threshold)
                ?? throw new Exception($"SwipeImgToCenter: template not found in area [{area[0]},{area[1]},{area[2]},{area[3]}]");
            var center = GetCenter();
            SwipeBetweenPoints(coords[0], coords[1], center[0], center[1]);
        }

        public void ClickImg(string base64Template, int[] area, float threshold = 0.97f)
        {
            var coords = FindImg(base64Template, area, threshold)
                ?? throw new Exception($"ClickImg: template not found in area [{area[0]},{area[1]},{area[2]},{area[3]}]");
            Sync(_activePage.Mouse.ClickAsync(coords[0], coords[1]));
        }

        // ── Swipe helper ──────────────────────────────────────────────────────

        private void SwipeBetweenPoints(int x1, int y1, int x2, int y2)
        {
            Sync(_activePage.Mouse.MoveAsync(x1, y1));
            Sync(_activePage.Mouse.DownAsync());
            // Интерполяция — имитация движения пальца
            int steps = Math.Max(10, (int)(Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)) / 10));
            for (int i = 1; i <= steps; i++)
            {
                double t  = (double)i / steps;
                int    mx = (int)(x1 + (x2 - x1) * t);
                int    my = (int)(y1 + (y2 - y1) * t);
                Sync(_activePage.Mouse.MoveAsync(mx, my));
                Thread.Sleep(5);
            }
            Sync(_activePage.Mouse.UpAsync());
        }
    }

    // ── PlaywrightProfile ─────────────────────────────────────────────────────

    public sealed class PlaywrightProfile : IBrowserProfile
    {
        private readonly IPage _page;
        public PlaywrightProfile(IPage page) => _page = page;

        public string UserAgent
            => _page.EvaluateAsync<string>("navigator.userAgent").GetAwaiter().GetResult() ?? "";
    }

    // ── PlaywrightTouch ───────────────────────────────────────────────────────

    public sealed class PlaywrightTouch : ITouch
    {
        private readonly IPage _page;
        public PlaywrightTouch(IPage page) => _page = page;

        private static void Sync(System.Threading.Tasks.Task t) => t.GetAwaiter().GetResult();

        public void Touch(int x, int y)
            => Sync(_page.Touchscreen.TapAsync(x, y));

        public void SwipeBetween(int x1, int y1, int x2, int y2)
        {
            Sync(_page.Mouse.MoveAsync(x1, y1));
            Sync(_page.Mouse.DownAsync());
            int steps = Math.Max(10, (int)(Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)) / 10));
            for (int i = 1; i <= steps; i++)
            {
                double t  = (double)i / steps;
                int    mx = (int)(x1 + (x2 - x1) * t);
                int    my = (int)(y1 + (y2 - y1) * t);
                Sync(_page.Mouse.MoveAsync(mx, my));
                System.Threading.Thread.Sleep(5);
            }
            Sync(_page.Mouse.UpAsync());
        }
    }
}