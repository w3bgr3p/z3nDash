// Срез z3n7/Diagnostic/Diagnostic.cs — пока только SaveDebugScreenshot.
//
// Зовёт ветка «BAD end» шаблона simroute.bolt.lgn: когда маршрут уходит в
// BadEnd, она сохраняет снимок страницы с водяным знаком, по которому потом
// и разбираются. Без него ветка падала на CS1061.
//
// CatchErrorFromTraffic из того же файла не переносится: он читает трафик
// вкладки через GrabTrafficList, а это отдельный кусок, который пока никем
// не зовётся.
//
// Отступление от эталона одно и вынужденное. Эталон рисует водяной знак
// средствами ZennoPoster — ImageProcessingWaterMarkImageFromScreenshot по
// порту инстанса. У нас браузер поднимает Playwright, порта ZP нет и этой
// функции нет тоже. Поэтому снимок берётся у вкладки, а надпись рисуется
// System.Drawing прямо на нём — в левом верхнем углу, как в эталоне.
// Каталог, имя файла и состав надписи оставлены дословно.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;

namespace z3n7
{
    public static partial class ProjectExtensions
    {
        public static void SaveDebugScreenshot(this IZennoPosterProjectModel project, Instance instance, string watermark = null)
        {
            watermark = watermark ?? string.Join(
                Environment.NewLine,
                project.LastErrorComment,
                instance.ActiveTab.URL,
                project.LastExecutedActionId
            );

            var directory = Path.Combine(
                project.Path,
                "debug_screens",
                DateTime.Today.ToString("yyyy-MM-dd"),
                project.Name
            );

            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                project.LastExecutedActionId + " - " + Time.Now() + ".png"
            );

            byte[] shot;
            try
            {
                shot = Convert.FromBase64String(instance.ActiveTab.GetPagePreview() ?? "");
            }
            catch (Exception e)
            {
                // Снимок — вспомогательный след, и его отсутствие не повод
                // ронять ветку, которая и так уже уходит в BadEnd.
                project.SendWarningToLog($"[debug] снимок не сделан: {e.Message}");
                return;
            }

            const int padding = 10;

            try
            {
                using var input = new MemoryStream(shot);
                using var page = new Bitmap(input);
                using var graphics = Graphics.FromImage(page);
                using var font = new Font("Iosevka", 15f, FontStyle.Regular, GraphicsUnit.Point);
                using var background = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
                using var foreground = new SolidBrush(Color.White);

                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var textSize = graphics.MeasureString(watermark, font);

                graphics.FillRectangle(background, 0, 0,
                    textSize.Width + padding * 2, textSize.Height + padding * 2);
                graphics.DrawString(watermark, font, foreground, padding, padding);

                page.Save(path, ImageFormat.Png);
                project.SendInfoToLog($"[debug] снимок: {path}");
            }
            catch (Exception e)
            {
                project.SendWarningToLog($"[debug] снимок не сохранён: {e.Message}");
            }
        }
    }
}
