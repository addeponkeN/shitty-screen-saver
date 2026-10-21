namespace Matriosk;

/// <summary>Settings for one frame's water field, lifted out of the config so a scene roll can feed them.</summary>
public readonly record struct NoiseSettings(float Amount, float Scale, float Speed, float Drift, float Roughness);

/// <summary>
/// Slow 3D Perlin noise sampled into the cell grid: the constant "water"
/// shimmer. Third axis is time, and the sample point drifts in x.
/// </summary>
public sealed class NoiseField
{
    private readonly byte[] perm = new byte[512];

    public NoiseField(int? seed = null)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)i;

        var state = (uint)(seed ?? Random.Shared.Next());
        if (state == 0) state = 1;

        uint Next()
        {
            state = (state + 0x6D2B79F5u);
            var t = state;
            t = (uint)((t ^ (t >> 15)) * (t | 1u));
            t ^= t + (uint)((t ^ (t >> 7)) * (t | 61u));
            return (t ^ (t >> 14)) >> 8;
        }

        for (var i = 255; i > 0; i--)
        {
            var j = (int)(Next() % (uint)(i + 1));
            (table[i], table[j]) = (table[j], table[i]);
        }
        for (var i = 0; i < 512; i++) perm[i] = table[i & 255];
    }

    public float Noise(float x, float y, float z)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);
        var X = xi & 255;
        var Y = yi & 255;
        var Z = zi & 255;

        x -= xi;
        y -= yi;
        z -= zi;

        var u = Fade(x);
        var v = Fade(y);
        var w = Fade(z);

        var A = perm[X] + Y;
        var AA = perm[A] + Z;
        var AB = perm[A + 1] + Z;
        var B = perm[X + 1] + Y;
        var BA = perm[B] + Z;
        var BB = perm[B + 1] + Z;

        return Lerp(
            Lerp(
                Lerp(Grad(perm[AA], x, y, z), Grad(perm[BA], x - 1, y, z), u),
                Lerp(Grad(perm[AB], x, y - 1, z), Grad(perm[BB], x - 1, y - 1, z), u),
                v),
            Lerp(
                Lerp(Grad(perm[AA + 1], x, y, z - 1), Grad(perm[BA + 1], x - 1, y, z - 1), u),
                Lerp(Grad(perm[AB + 1], x, y - 1, z - 1), Grad(perm[BB + 1], x - 1, y - 1, z - 1), u),
                v),
            w);
    }

    /// <summary>
    /// Writes one noise value per cell (0..amount). The field is smooth, so it is
    /// sampled every <c>Step</c> cells and bilinearly interpolated.
    /// </summary>
    public void Fill(float[] output, int cols, int rows, NoiseSettings settings, float time)
    {
        const int step = 2;

        var scale = MathF.Max(1e-4f, settings.Scale);
        var amount = MathF.Max(0, settings.Amount);
        var roughness = Math.Clamp(settings.Roughness, 0, 1);
        var z = time * settings.Speed;
        var offsetX = time * settings.Drift;

        var coarseCols = (cols + step - 1) / step + 1;
        var coarseRows = (rows + step - 1) / step + 1;
        if (coarse.Length < coarseCols * coarseRows) coarse = new float[coarseCols * coarseRows];

        for (var cy = 0; cy < coarseRows; cy++)
        {
            var ny = (cy * step + 0.5f) * scale;
            var rowOffset = cy * coarseCols;
            for (var cx = 0; cx < coarseCols; cx++)
            {
                var nx = (cx * step + 0.5f + offsetX) * scale;

                var n = Noise(nx, ny, z);
                if (roughness > 0)
                {
                    n += roughness * Noise(nx * 2.03f + 11.7f, ny * 2.03f - 7.3f, z * 1.6f + 5.1f) * 0.5f;
                }

                var value = n * 0.5f + 0.5f;
                value = value < 0 ? 0 : value > 1 ? 1 : value;
                coarse[rowOffset + cx] = amount * value * value;
            }
        }

        for (var y = 0; y < rows; y++)
        {
            var gy = y / (float)step;
            var cy0 = (int)gy;
            var cy1 = Math.Min(coarseRows - 1, cy0 + 1);
            var ty = gy - cy0;
            var row0 = cy0 * coarseCols;
            var row1 = cy1 * coarseCols;
            var outRow = y * cols;

            for (var x = 0; x < cols; x++)
            {
                var gx = x / (float)step;
                var cx0 = (int)gx;
                var cx1 = Math.Min(coarseCols - 1, cx0 + 1);
                var tx = gx - cx0;

                var top = coarse[row0 + cx0] + (coarse[row0 + cx1] - coarse[row0 + cx0]) * tx;
                var bottom = coarse[row1 + cx0] + (coarse[row1 + cx1] - coarse[row1 + cx0]) * tx;
                output[outRow + x] = top + (bottom - top) * ty;
            }
        }
    }

    private float[] coarse = new float[0];

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    private static float Grad(int hash, float x, float y, float z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
