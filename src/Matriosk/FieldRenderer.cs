using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Matriosk;

/// <summary>
/// Software compositor: clears the WriteableBitmap back buffer, blends the coverage
/// masks of the lit cells into it, then hands it to WPF with one dirty rect. No
/// per-glyph text calls and no intermediate copy.
///
/// Brightness is a continuous ramp from the dim field colour to the hot colour. Word
/// cells blend further toward hot based on how far the pulse sits above the water
/// baseline, so rain lifts the text smoothly instead of switching tiers at a threshold.
/// </summary>
public sealed class FieldRenderer
{
    private const int Steps = 32;
    private const int HaloLevels = 8;
    private const float HotThreshold = 0.15f;

    private WriteableBitmap? bitmap;
    private int[] buffer = [];
    private GlyphMasks masks = new();
    private string charset = string.Empty;
    private byte[][] glyphLookup = [];
    private byte[][] haloLookup = [];
    private SparseGlyph[] sparseGlyphs = [];

    private int cols;
    private int rows;
    private int cellW = 7;
    private int cellH = 12;
    private int stride;

    public double ClearMs { get; private set; }
    public double BlendMs { get; private set; }
    public double UploadMs { get; private set; }

    private readonly Palette[] fieldPalette = new Palette[Steps];
    private readonly Palette[] haloPalette = new Palette[HaloLevels];

    private readonly List<int>[] fieldBins = Enumerable.Range(0, Steps).Select(_ => new List<int>()).ToArray();
    private readonly List<int>[] haloBins = Enumerable.Range(0, HaloLevels).Select(_ => new List<int>()).ToArray();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Visible { get; private set; }
    public int Hot { get; private set; }
    public WriteableBitmap? Bitmap => bitmap;

    public void Configure(int gridCols, int gridRows, int newCellW, int newCellH, GlyphMasks glyphMasks, Config cfg)
    {
        cols = gridCols;
        rows = gridRows;
        cellW = Math.Max(1, newCellW);
        cellH = Math.Max(1, newCellH);
        Width = cols * cellW;
        Height = rows * cellH;
        masks = glyphMasks;
        charset = Config.ResolveCharset(cfg.Charset);

        glyphLookup = new byte[charset.Length][];
        haloLookup = new byte[charset.Length][];
        sparseGlyphs = new SparseGlyph[charset.Length];
        for (var i = 0; i < charset.Length; i++)
        {
            glyphLookup[i] = masks.Glyph(charset[i]);
            haloLookup[i] = masks.Halo(charset[i]) ?? [];
            sparseGlyphs[i] = SparseGlyph.Build(glyphLookup[i], masks.CellW, masks.CellH);
        }

        if (buffer.Length != Width * Height) buffer = new int[Width * Height];
        if (bitmap is null || bitmap.PixelWidth != Width || bitmap.PixelHeight != Height)
        {
            bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        }

        BuildPalettes(cfg);
    }

    /// <summary>
    /// Rebuilds the brightness ramp: dim -> hot, with alpha and colour both moving
    /// linearly once past the bright threshold. The halo table is the word glow.
    /// </summary>
    public void BuildPalettes(Config cfg)
    {
        var dim = Config.ParseColor(cfg.Colors.Dim);
        var hot = Config.ParseColor(cfg.Colors.Hot);
        var brightAt = Math.Clamp(cfg.Energy.BrightThreshold, 0.01f, 0.99f);
        var ceiling = Math.Clamp(cfg.Energy.DimCeiling, 0.01f, 1f);

        for (var i = 0; i < Steps; i++)
        {
            var t = i / (float)(Steps - 1);
            var u = t <= brightAt ? 0 : (t - brightAt) / (1 - brightAt);

            var r = Lerp(dim.R, hot.R, u);
            var g = Lerp(dim.G, hot.G, u);
            var b = Lerp(dim.B, hot.B, u);

            var alpha = t <= brightAt
                ? t / brightAt * ceiling
                : ceiling + (1 - ceiling) * u;

            fieldPalette[i] = Palette.Create((byte)r, (byte)g, (byte)b, alpha);
        }

        for (var i = 0; i < HaloLevels; i++)
        {
            haloPalette[i] = Palette.Create(hot.R, hot.G, hot.B, (i + 1) / (float)HaloLevels * 0.5f);
        }
    }

    /// <summary>Blends a word cell from its field look toward hot, by pulse strength.</summary>
    public static float WordBlend(float t, float over) => t + (1 - t) * Math.Clamp(over, 0, 1);

    public void Draw(CharGrid grid, Config cfg, float fade)
    {
        if (bitmap is null) return;

        var background = Pack(cfg.Colors.Background);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Array.Fill(buffer, background);
        ClearMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        if (fade > 0.002f)
        {
            Classify(grid, cfg, fade);
            stride = Width;
            RenderBins(grid, fade);
        }

        BlendMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), buffer, Width * 4, 0);
        UploadMs = clock.Elapsed.TotalMilliseconds;
    }

    private void Classify(CharGrid grid, Config cfg, float fade)
    {
        for (var i = 0; i < Steps; i++) fieldBins[i].Clear();
        for (var i = 0; i < HaloLevels; i++) haloBins[i].Clear();

        var energy = grid.Energy;
        var baseNoise = grid.Base;
        var weight = grid.Weight;
        var minVisible = cfg.Energy.MinVisible;
        var noiseGain = cfg.TextMask.NoiseGain;
        var span = Math.Max(0.05f, 1 - minVisible);
        var wordThreshold = HotThreshold;

        var visible = 0;
        var hot = 0;

        for (var y = 0; y < rows; y++)
        {
            var rowOffset = y * cols;
            for (var x = 0; x < cols; x++)
            {
                var index = rowOffset + x;
                var water = baseNoise[index];
                var isWord = weight[index] > wordThreshold;
                if (isWord) water *= 1 + noiseGain;

                var pulse = energy[index];
                var value = pulse > water ? pulse : water;
                if (value < minVisible) continue;
                visible++;

                // Soft edge: alpha starts at zero right at the visibility threshold, so
                // cells drifting across it shimmer instead of popping in and out.
                var t = (value - minVisible) / span;

                if (isWord)
                {
                    var over = (pulse - water) / MathF.Max(0.05f, 1 - water);
                    t = WordBlend(t, over);

                    if (over > 0.02f)
                    {
                        var halo = Math.Min(HaloLevels - 1, (int)(over * HaloLevels));
                        haloBins[halo].Add(index);
                        hot++;
                    }
                }

                var step = Math.Clamp((int)(t * fade * (Steps - 1) + 0.5f), 0, Steps - 1);
                fieldBins[step].Add(index);
            }
        }

        Visible = visible;
        Hot = hot;
    }

    private unsafe void RenderBins(CharGrid grid, float fade)
    {
        var glyphs = masks;
        var charIndices = grid.Char;

        fixed (int* destination = buffer)
        {
            // Glow first, behind the glyphs, so the letters stay crisp. The halo gets
            // the startup fade too, so the word does not glow before it appears.
            var haloAlpha = Math.Clamp(fade, 0, 1);
            for (var level = 0; level < HaloLevels; level++)
            {
                var bin = haloBins[level];
                if (bin.Count == 0) continue;
                var palette = haloPalette[level];
                foreach (var index in bin)
                {
                    var y = index / cols;
                    var x = index - y * cols;
                    var halo = haloLookup[charIndices[index]];
                    if (halo.Length == 0) continue;
                    BakeBlit(destination, halo, glyphs.MaskW, glyphs.MaskH, x * cellW - glyphs.PadX, y * cellH - glyphs.PadY, palette, haloAlpha);
                }
            }

            for (var step = 0; step < Steps; step++)
            {
                var bin = fieldBins[step];
                if (bin.Count == 0) continue;
                var palette = fieldPalette[step];
                foreach (var index in bin)
                {
                    var y = index / cols;
                    var x = index - y * cols;
                    SparseBlit(destination, sparseGlyphs[charIndices[index]], glyphs.CellW, glyphs.CellH, x * cellW, y * cellH, palette);
                }
            }
        }
    }

    public void SavePng(string path)
    {
        if (bitmap is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private unsafe void BakeBlit(int* destination, byte[] mask, int maskW, int maskH, int originX, int originY, Palette palette, float alpha = 1f)
    {
        var x0 = Math.Max(0, -originX);
        var y0 = Math.Max(0, -originY);
        var x1 = Math.Min(maskW, Width - originX);
        var y1 = Math.Min(maskH, Height - originY);
        if (x0 >= x1 || y0 >= y1) return;

        var premul = palette.Premul;
        var inv = palette.Inv;
        var dim = alpha < 0.999f;

        for (var my = y0; my < y1; my++)
        {
            var row = destination + (originY + my) * stride;
            var maskRow = my * maskW;
            for (var mx = x0; mx < x1; mx++)
            {
                var coverage = mask[maskRow + mx];
                if (coverage == 0) continue;
                if (dim)
                {
                    coverage = (byte)(coverage * alpha);
                    if (coverage == 0) continue;
                }

                var pixel = row + originX + mx;
                var dst = *pixel;
                var back = inv[coverage];
                var pre = premul[coverage];
                var r = ((pre >> 16) & 0xFF) + (((dst >> 16) & 0xFF) * back >> 8);
                var g = ((pre >> 8) & 0xFF) + (((dst >> 8) & 0xFF) * back >> 8);
                var b = (pre & 0xFF) + ((dst & 0xFF) * back >> 8);
                *pixel = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }
    }

    private static int Pack(string hex)
    {
        var (r, g, b) = Config.ParseColor(hex);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private static byte Lerp(byte from, byte to, float t) => (byte)(from + (to - from) * t);

    /// <summary>
    /// Glyph as a list of covered pixels only, so the blend loop never visits the
    /// ~65% of a cell that the glyph does not touch.
    /// </summary>
    private sealed class SparseGlyph
    {
        public int[] Offsets = [];
        public byte[] Coverage = [];

        public static SparseGlyph Build(byte[] mask, int maskW, int maskH)
        {
            var offsets = new List<int>(mask.Length / 3);
            var coverage = new List<byte>(mask.Length / 3);
            for (var y = 0; y < maskH; y++)
            {
                var row = y * maskW;
                for (var x = 0; x < maskW; x++)
                {
                    var value = mask[row + x];
                    if (value == 0) continue;
                    offsets.Add((y << 16) | x);
                    coverage.Add(value);
                }
            }
            return new SparseGlyph { Offsets = offsets.ToArray(), Coverage = coverage.ToArray() };
        }
    }

    private unsafe void SparseBlit(int* destination, SparseGlyph glyph, int maskW, int maskH, int originX, int originY, Palette palette)
    {
        var premul = palette.Premul;
        var inv = palette.Inv;
        var count = glyph.Offsets.Length;
        if (count == 0) return;

        fixed (int* offsets = glyph.Offsets)
        fixed (byte* coverage = glyph.Coverage)
        {
            var inside = originX >= 0 && originY >= 0 && originX + maskW <= Width && originY + maskH <= Height;

            if (inside)
            {
                var basePointer = destination + originY * stride + originX;
                for (var k = 0; k < count; k++)
                {
                    var offset = offsets[k];
                    var pixel = basePointer + (offset >> 16) * stride + (offset & 0xFFFF);
                    var cov = coverage[k];
                    var dst = *pixel;
                    var back = inv[cov];
                    var pre = premul[cov];
                    var r = ((pre >> 16) & 0xFF) + (((dst >> 16) & 0xFF) * back >> 8);
                    var g = ((pre >> 8) & 0xFF) + (((dst >> 8) & 0xFF) * back >> 8);
                    var b = (pre & 0xFF) + ((dst & 0xFF) * back >> 8);
                    *pixel = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
                return;
            }

            for (var k = 0; k < count; k++)
            {
                var offset = offsets[k];
                var px = originX + (offset & 0xFFFF);
                var py = originY + (offset >> 16);
                if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;

                var pixel = destination + py * stride + px;
                var cov = coverage[k];
                var dst = *pixel;
                var back = inv[cov];
                var pre = premul[cov];
                var r = ((pre >> 16) & 0xFF) + (((dst >> 16) & 0xFF) * back >> 8);
                var g = ((pre >> 8) & 0xFF) + (((dst >> 8) & 0xFF) * back >> 8);
                var b = (pre & 0xFF) + ((dst & 0xFF) * back >> 8);
                *pixel = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }
    }

    private sealed class Palette
    {
        /// <summary>Premultiplied 0xRRGGBB for each coverage value.</summary>
        public readonly int[] Premul = new int[256];
        /// <summary>256 minus the effective alpha, fixed point 8.8.</summary>
        public readonly int[] Inv = new int[256];
        /// <summary>The un-premultiplied colour this palette was built from.</summary>
        public (byte R, byte G, byte B, float Alpha) Sample { get; private set; }

        public static Palette Create(byte r, byte g, byte b, float alpha)
        {
            var palette = new Palette { Sample = (r, g, b, alpha) };
            for (var coverage = 0; coverage < 256; coverage++)
            {
                var a = coverage / 255f * alpha;
                palette.Premul[coverage] = ((int)(r * a) << 16) | ((int)(g * a) << 8) | (int)(b * a);
                palette.Inv[coverage] = (int)((1 - a) * 256);
            }
            return palette;
        }
    }

    /// <summary>Effective colour for a cell value, used by the self-test to prove the ramp is monotonic.</summary>
    public (byte R, byte G, byte B, float Alpha) SampleFieldRamp(float value)
    {
        var step = Math.Clamp((int)(value * (Steps - 1) + 0.5f), 0, Steps - 1);
        return fieldPalette[step].Sample;
    }
}
