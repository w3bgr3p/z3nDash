// ══════════════════════════════════════════════════════════════════════════════
// WaterMark.cs — ветка ImageProcessing/WaterMark.
//
// В шаблонах это не украшение, а рабочий инструмент разбора: на ветке ошибки
// снимается страница и на снимок наносится состояние — текст ошибки, адрес,
// переменные, идентификатор упавшей ветки. Потом по каталогу debug_screens
// видно, на чём именно всё встало.
//
// В ZennoPoster то же делает ZennoPoster.ImageProcessingWaterMarkTextFromScreenshot,
// адресуя инстанс по порту. Порта у нас нет и не будет, поэтому снимок берётся
// у вкладки (Tab.GetPagePreview), а текст наносится здесь.
//
// Чего эта реализация не повторяет: параметр Imposition (Horizontally|Vertically)
// в ZP размножает знак по всей картинке. Здесь знак ставится один раз в точке
// Location — для разбора нужен читаемый текст, а не заливка им кадра. Это
// сознательное отступление, а не недосмотр.
// ══════════════════════════════════════════════════════════════════════════════

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace z3nDash.Xml;

/// <summary>Разбор параметров ветки и собственно нанесение надписи.</summary>
internal static class WaterMark
{
    /// <summary>
    /// Наложить текст на картинку и сохранить. Возвращает путь сохранённого
    /// файла — его же ZP кладёт в OutputFile.
    /// </summary>
    public static string Draw(byte[] source, string text, string outputFile,
                              string fontSpec, string location,
                              int offsetLeft, int offsetTop,
                              int transparency, int quality)
    {
        using var ms  = new MemoryStream(source);
        using var img = Image.FromStream(ms);
        using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(img, 0, 0, img.Width, img.Height);

            if (!string.IsNullOrEmpty(text))
            {
                var (font, color) = ParseFont(fontSpec);
                using (font)
                {
                    // Прозрачность в ZP задаётся процентами: 0 — знак непрозрачен.
                    var alpha = (int)Math.Round(color.A * (1 - Math.Clamp(transparency, 0, 100) / 100.0));
                    using var brush = new SolidBrush(Color.FromArgb(alpha, color));

                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.SmoothingMode     = SmoothingMode.AntiAlias;

                    var size  = g.MeasureString(text, font);
                    var point = Place(location, bmp.Width, bmp.Height, size, offsetLeft, offsetTop);

                    // Подложка под текст: без неё запись поверх пёстрой страницы
                    // нечитаема, а весь смысл ветки — прочитать её потом глазами.
                    using var pad = new SolidBrush(Color.FromArgb(Math.Min(alpha, 160), Color.White));
                    g.FillRectangle(pad, point.X - 2, point.Y - 2, size.Width + 4, size.Height + 4);

                    g.DrawString(text, font, brush, point);
                }
            }
        }

        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Save(bmp, outputFile, quality);
        return outputFile;
    }

    /// <summary>
    /// Формат ZP: "Tahoma, 10pt, Regular, [255;0;0;0]" — имя, размер, начертание
    /// и цвет как [A;R;G;B]. Любая часть может отсутствовать.
    /// </summary>
    private static (Font Font, Color Color) ParseFont(string? spec)
    {
        var name  = "Tahoma";
        var size  = 10f;
        var style = FontStyle.Regular;
        var color = Color.Red;

        foreach (var raw in (spec ?? "").Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;

            if (raw.StartsWith('['))
            {
                var p = raw.Trim('[', ']').Split(';', StringSplitOptions.TrimEntries);
                var n = p.Where(x => int.TryParse(x, out _)).Select(int.Parse).ToArray();
                color = n.Length switch
                {
                    >= 4 => Color.FromArgb(n[0], n[1], n[2], n[3]),
                    3    => Color.FromArgb(255,  n[0], n[1], n[2]),
                    _    => color,
                };
            }
            else if (raw.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
                     && float.TryParse(raw[..^2], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var pt))
            {
                size = pt;
            }
            else if (Enum.TryParse<FontStyle>(raw, ignoreCase: true, out var st))
            {
                style |= st;
            }
            else name = raw;
        }

        return (new Font(name, size, style, GraphicsUnit.Point), color);
    }

    private static PointF Place(string? location, int w, int h, SizeF text, int dx, int dy)
    {
        var (x, y) = (location ?? "LeftTop").ToLowerInvariant() switch
        {
            "righttop"    => (w - text.Width, 0f),
            "leftbottom"  => (0f,             h - text.Height),
            "rightbottom" => (w - text.Width, h - text.Height),
            "center"      => ((w - text.Width) / 2, (h - text.Height) / 2),
            _             => (0f, 0f),               // LeftTop
        };

        // Не даём надписи уехать за кадр: снимок с обрезанным текстом бесполезен
        // ровно тогда, когда он нужен.
        return new PointF(Math.Clamp(x + dx, 0, Math.Max(0, w - text.Width)),
                          Math.Clamp(y + dy, 0, Math.Max(0, h - text.Height)));
    }

    private static void Save(Bitmap bmp, string path, int quality)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext is ".jpg" or ".jpeg")
        {
            var codec = ImageCodecInfo.GetImageEncoders()
                                      .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bmp.Save(path, codec, p);
            return;
        }

        bmp.Save(path, ext switch
        {
            ".bmp" => ImageFormat.Bmp,
            ".gif" => ImageFormat.Gif,
            _      => ImageFormat.Png,
        });
    }
}
