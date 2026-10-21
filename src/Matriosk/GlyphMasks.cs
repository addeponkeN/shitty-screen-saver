using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Matriosk;

/// <summary>
/// Rasterizes every glyph of the charset once into 8-bit coverage masks at cell
/// size, plus a blurred variant used for the word's halo. No text drawing happens
/// per frame.
/// </summary>
public sealed class GlyphMasks
{
    private readonly Dictionary<char, byte[]> glyphs = new();
    private readonly Dictionary<char, byte[]> halos = new();

    public int CellW { get; private set; }
    public int CellH { get; private set; }
    public int PadX { get; private set; }
    public int PadY { get; private set; }
    public int MaskW => CellW + PadX * 2;
    public int MaskH => CellH + PadY * 2;
    public bool HasHalo { get; private set; }

    private Typeface typeface = new("Consolas");
    private float emSize = 12;

    public void Configure(GlyphMasks source)
    {
        CellW = source.CellW;
        CellH = source.CellH;
        PadX = source.PadX;
        PadY = source.PadY;
        HasHalo = source.HasHalo;
        typeface = source.typeface;
        emSize = source.emSize;
        glyphs.Clear();
        halos.Clear();
        foreach (var pair in source.glyphs) glyphs[pair.Key] = pair.Value;
        foreach (var pair in source.halos) halos[pair.Key] = pair.Value;
    }

    public static GlyphMasks Build(string charset, string font, float fontWeight, float fontScale, int cellW, int cellH, float glow)
    {
        var masks = new GlyphMasks
        {
            CellW = Math.Max(1, cellW),
            CellH = Math.Max(1, cellH),
        };

        masks.typeface = new Typeface(
            new FontFamily(TextMaskBuilder.FontFamilyName(font)),
            FontStyles.Normal,
            FontWeight.FromOpenTypeWeight((int)fontWeight),
            FontStretches.Normal);
        masks.emSize = Math.Max(2, masks.CellH * MathF.Max(0.2f, fontScale));

        var pad = glow > 0 ? (int)MathF.Ceiling(MathF.Max(2f, masks.CellH * glow * 0.6f)) : 0;
        masks.PadX = pad;
        masks.PadY = pad;
        masks.HasHalo = pad > 0;

        foreach (var c in charset.Distinct())
        {
            masks.glyphs[c] = masks.RenderGlyph(c, masks.CellW, masks.CellH, 0, 0);
            if (masks.HasHalo)
            {
                var wide = masks.RenderGlyph(c, masks.MaskW, masks.MaskH, masks.PadX, masks.PadY);
                var blurred = Blur(wide, masks.MaskW, masks.MaskH, Math.Max(1, pad / 2));
                masks.halos[c] = Normalize(blurred);
            }
        }

        return masks;
    }

    public byte[] Glyph(char c)
    {
        if (glyphs.Count == 0) return [];
        return glyphs.TryGetValue(c, out var mask) ? mask : glyphs[glyphs.Keys.First()];
    }

    public byte[]? Halo(char c)
    {
        if (halos.Count == 0) return null;
        return halos.TryGetValue(c, out var mask) ? mask : halos[halos.Keys.First()];
    }

    private byte[] RenderGlyph(char c, int width, int height, int offsetX, int offsetY)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var text = new FormattedText(
                c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, emSize, Brushes.White, 1.0);
            var x = (width - text.Width) / 2.0;
            var y = (height - text.Height) / 2.0;
            dc.DrawText(text, new Point(x, y));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var mask = new byte[width * height];
        for (var i = 0; i < mask.Length; i++) mask[i] = pixels[i * 4 + 3];
        _ = offsetX + offsetY;
        return mask;
    }

    /// <summary>
    /// A thin glyph smeared by a box blur peaks well below full coverage, which made
    /// the halo invisible. Scale it back up so the baked glow actually shows.
    /// </summary>
    private static byte[] Normalize(byte[] mask)
    {
        var max = 0;
        foreach (var value in mask)
        {
            if (value > max) max = value;
        }
        if (max == 0 || max == 255) return mask;

        var scale = 255f / max;
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = (byte)Math.Min(255, (int)(mask[i] * scale));
        }
        return mask;
    }

    /// <summary>Three-box blur on the coverage plane, for the baked glow.</summary>
    private static byte[] Blur(byte[] source, int width, int height, int radius)
    {
        var current = source;
        for (var pass = 0; pass < 3; pass++)
        {
            current = BoxBlur(current, width, height, radius, horizontal: true);
            current = BoxBlur(current, width, height, radius, horizontal: false);
        }
        return current;
    }

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius, bool horizontal)
    {
        var result = new byte[source.Length];
        var span = radius * 2 + 1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = horizontal ? x + k : x;
                    var sy = horizontal ? y : y + k;
                    if ((uint)sx < (uint)width && (uint)sy < (uint)height)
                    {
                        sum += source[sy * width + sx];
                    }
                }
                result[y * width + x] = (byte)(sum / span);
            }
        }

        return result;
    }
}
