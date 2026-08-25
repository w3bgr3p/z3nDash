using Microsoft.Playwright;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DevDeck.Browser
{
    /// <summary>
    /// Курсор страницы: где он сейчас и как доходит до цели.
    ///
    /// В ZP уровень "superEmulation" — это не «кликни аккуратнее», а физическое
    /// наведение: курсор едет к элементу, по дороге страница получает поток
    /// mousemove, на входе в элемент — mouseover/mouseenter, и только потом
    /// mousedown/focus/mouseup/click. Пачка событий и её тайминг — то, по чему
    /// антибот и отличает человека от скрипта; одиночный клик в центр эту пачку
    /// не даёт, даже если он настоящий.
    ///
    /// Позицию храним сами, а не спрашиваем у Playwright: у Mouse она есть, но
    /// приватная, а траекторию надо строить от текущей точки. Таблица слабая —
    /// закрытая страница не держится в памяти из-за записи о её курсоре.
    /// </summary>
    internal static class MouseEmulation
    {
        private sealed class Cursor { public double X, Y; }

        private static readonly ConditionalWeakTable<IPage, Cursor> _cursors = new();

        private static Cursor Of(IPage page) => _cursors.GetValue(page, _ => new Cursor());

        internal static System.Drawing.Point Position(IPage page)
        {
            var c = Of(page);
            return new System.Drawing.Point((int)c.X, (int)c.Y);
        }

        internal static void Remember(IPage page, double x, double y)
        {
            var c = Of(page); c.X = x; c.Y = y;
        }

        /// <summary>
        /// Довести курсор до точки живой траекторией.
        ///
        /// Один MoveAsync со Steps даёт идеальную прямую с равным шагом — такого
        /// движения у руки не бывает. Поэтому путь ломается на несколько отрезков
        /// со смещением вбок и разной длиной шага, а между отрезками стоит
        /// микропауза: получается дуга с неровной скоростью.
        /// </summary>
        internal static void MoveTo(IPage page, double toX, double toY)
        {
            var c  = Of(page);
            double fromX = c.X, fromY = c.Y;
            double dx = toX - fromX, dy = toY - fromY;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            // Первое движение на странице: точки отсчёта нет, курсор «появляется»
            // рядом с целью, а не едет через весь экран из угла (0,0).
            if (fromX == 0 && fromY == 0 && distance > 400)
            {
                fromX = toX - Random.Shared.Next(-160, 160);
                fromY = toY - Random.Shared.Next(-160, 160);
                Sync(page.Mouse.MoveAsync((float)fromX, (float)fromY));
                dx = toX - fromX; dy = toY - fromY;
                distance = Math.Sqrt(dx * dx + dy * dy);
            }

            if (distance < 1)
            {
                // Совсем рядом — но событие движения всё равно нужно: без него
                // элемент не получит mouseover, если курсор уже стоял на месте.
                Sync(page.Mouse.MoveAsync((float)toX, (float)toY));
                Remember(page, toX, toY);
                return;
            }

            int legs = distance < 60 ? 2 : distance < 300 ? 3 : 4;
            // Отклонение дуги от прямой — тем заметнее, чем длиннее путь, но не
            // до промаха мимо экрана.
            double bow = Math.Min(distance * 0.12, 45) * (Random.Shared.Next(2) == 0 ? -1 : 1);

            for (int i = 1; i <= legs; i++)
            {
                double t = (double)i / legs;
                // Синус даёт отклонение нулевым на концах и максимальным в
                // середине: старт и финиш остаются точными.
                double off = Math.Sin(t * Math.PI) * bow;
                double px = fromX + dx * t - dy / distance * off;
                double py = fromY + dy * t + dx / distance * off;

                if (i == legs) { px = toX; py = toY; }

                int steps = Math.Clamp((int)(distance / legs / 8), 3, 18);
                Sync(page.Mouse.MoveAsync((float)px, (float)py, new MouseMoveOptions { Steps = steps }));
                if (i != legs) Thread.Sleep(Random.Shared.Next(8, 26));
            }

            Remember(page, toX, toY);
        }

        /// <summary>
        /// Точка внутри элемента, куда целиться. Не центр: человек попадает
        /// куда придётся, и ровно центр раз за разом — сам по себе признак
        /// скрипта. Края отрезаем, чтобы не промахнуться по границе или не
        /// попасть в соседний элемент, лежащий сверху на кромке.
        /// </summary>
        internal static (float x, float y) PointIn(float width, float height)
        {
            static float Pick(float size)
            {
                if (size <= 4) return size / 2;
                double lo = size * 0.30, hi = size * 0.70;
                return (float)(lo + Random.Shared.NextDouble() * (hi - lo));
            }
            return (Pick(width), Pick(height));
        }

        /// <summary>
        /// Полный «человеческий» клик по элементу.
        ///
        /// Ехать курсором мы обязаны сами, а вот жать — через ClickAsync: он
        /// перед нажатием проверяет, что элемент видим, устойчив и действительно
        /// принимает события. Ручные Down/Up эту проверку теряют и на живой
        /// вёрстке попадают в оверлей или в ещё едущий элемент. Мышь к моменту
        /// вызова уже стоит в целевой точке, так что телепорта ClickAsync не
        /// делает — только нажатие.
        /// </summary>
        internal static void Click(ILocator loc, MouseButton button = MouseButton.Left)
        {
            Sync(loc.ScrollIntoViewIfNeededAsync());

            var box = Sync(loc.BoundingBoxAsync());
            if (box == null)
            {
                // Геометрии нет — эмулировать нечего, но клик должен состояться.
                Sync(loc.ClickAsync(new LocatorClickOptions
                {
                    Button = button,
                    Delay  = Random.Shared.Next(40, 140),
                }));
                return;
            }

            var page = loc.Page;
            var (rx, ry) = PointIn((float)box.Width, (float)box.Height);

            MoveTo(page, box.X + rx, box.Y + ry);
            // Наведение и нажатие у человека не совпадают: он доводит курсор,
            // страница успевает отработать hover, и только потом идёт кнопка.
            Thread.Sleep(Random.Shared.Next(45, 160));

            Sync(loc.ClickAsync(new LocatorClickOptions
            {
                Button   = button,
                Position = new Position { X = rx, Y = ry },
                Delay    = Random.Shared.Next(40, 140),
            }));

            Remember(page, box.X + rx, box.Y + ry);
        }

        /// <summary>Навести курсор без нажатия: mousemove по пути, mouseover/mouseenter на входе.</summary>
        internal static void Hover(ILocator loc)
        {
            Sync(loc.ScrollIntoViewIfNeededAsync());

            var box = Sync(loc.BoundingBoxAsync());
            if (box == null) { Sync(loc.HoverAsync()); return; }

            var (rx, ry) = PointIn((float)box.Width, (float)box.Height);
            MoveTo(loc.Page, box.X + rx, box.Y + ry);
        }

        private static T    Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();
        private static void Sync(Task t)        => t.GetAwaiter().GetResult();
    }
}
