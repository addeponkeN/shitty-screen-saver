using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Matriosk;

/// <summary>
/// Rasterizes the configured word into a per-cell weight map: one pixel per
/// character cell, so the letters are aliased on purpose.
/// </summary>
public static class TextMaskBuilder
{
    public static float[] Build(Config cfg, int cols, int rows)
    {
        var weights = new float[cols * rows];
        var text = (cfg.Text ?? string.Empty).Trim();
        if (text.Length == 0) return weights;

        var glyphs = text.ToCharArray();
        var typeface = new Typeface(
            new FontFamily(FontFamilyName(cfg.Font)),
            FontStyles.Normal,
            FontWeight.FromOpenTypeWeight((int)cfg.TextMask.FontWeight),
            FontStretches.Normal);

        var fontPx = Math.Max(3f, cfg.TextMask.HeightCells);
        var spacing = Math.Max(0, cfg.TextMask.Spacing);
        var maxWidth = cols * 0.94f;
        var widths = new double[glyphs.Length];

        double Measure(char c, double size) => new FormattedText(
            c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

        double total = 0;
        void Fit(double size)
        {
            total = 0;
            for (var i = 0; i < glyphs.Length; i++)
            {
                widths[i] = Measure(glyphs[i], size);
                total += widths[i];
            }
            total += spacing * (glyphs.Length - 1);
        }

        Fit(fontPx);
        if (total > maxWidth)
        {
            var scale = maxWidth / total;
            fontPx = (float)Math.Max(3, fontPx * scale);
            spacing *= (float)scale;
            Fit(fontPx);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var x = (cols - total) / 2.0;
            for (var i = 0; i < glyphs.Length; i++)
            {
                var ft = new FormattedText(
                    glyphs[i].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontPx, Brushes.White, 1.0);
                var y = (rows - ft.Height) / 2.0;
                dc.DrawText(ft, new System.Windows.Point(x, y));
                x += widths[i] + spacing;
            }
        }

        var bitmap = new RenderTargetBitmap(cols, rows, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = cols * 4;
        var pixels = new byte[stride * rows];
        bitmap.CopyPixels(pixels, stride, 0);

        var alpha = new float[cols * rows];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3] / 255f;

        var raw = cfg.TextMask.Dilate > 0 ? Dilate(alpha, cols, rows, cfg.TextMask.Dilate) : alpha;

        var span = Math.Max(0.05f, 1 - cfg.TextMask.Threshold);
        for (var i = 0; i < weights.Length; i++)
        {
            var t = (raw[i] - cfg.TextMask.Threshold) / span;
            if (t <= 0) continue;
            if (t > 1) t = 1;
            weights[i] = t * t * (3 - 2 * t);
        }

        return weights;
    }

    /// <summary>First family from a CSS-style font list.</summary>
    public static string FontFamilyName(string font)
    {
        if (string.IsNullOrWhiteSpace(font)) return "Consolas";
        var first = font.Split(',')[0].Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(first) ? "Consolas" : first;
    }

    private static float[] Dilate(float[] source, int cols, int rows, int radius)
    {
        var horizontal = new float[source.Length];
        for (var y = 0; y < rows; y++)
        {
            var rowOffset = y * cols;
            for (var x = 0; x < cols; x++)
            {
                var max = 0f;
                var from = Math.Max(0, x - radius);
                var to = Math.Min(cols - 1, x + radius);
                for (var sx = from; sx <= to; sx++)
                {
                    var value = source[rowOffset + sx];
                    if (value > max) max = value;
                }
                horizontal[rowOffset + x] = max;
            }
        }

        var result = new float[source.Length];
        for (var y = 0; y < rows; y++)
        {
            var from = Math.Max(0, y - radius);
            var to = Math.Min(rows - 1, y + radius);
            for (var x = 0; x < cols; x++)
            {
                var max = 0f;
                for (var sy = from; sy <= to; sy++)
                {
                    var value = horizontal[sy * cols + x];
                    if (value > max) max = value;
                }
                result[y * cols + x] = max;
            }
        }

        return result;
    }
}
