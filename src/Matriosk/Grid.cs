namespace Matriosk;

/// <summary>
/// The character field: character indices, pulse energy, Perlin base and the word
/// weight map, all in flat arrays. Row-major: index = y * cols + x.
/// </summary>
public sealed class CharGrid
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int Count => Cols * Rows;

    public byte[] Char { get; private set; } = [];
    public float[] Energy { get; private set; } = [];
    public float[] Base { get; private set; } = [];
    public float[] Weight { get; private set; } = [];

    /// <summary>Shuffled visit order for churn, consumed a slice at a time.</summary>
    private uint[] order = [];
    private int orderCursor;

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        Cols = cols;
        Rows = rows;
        var count = Count;
        Char = new byte[count];
        Energy = new float[count];
        Base = new float[count];
        Weight = new float[count];
        order = [];
        orderCursor = 0;
    }

    /// <summary>
    /// Re-randomizes roughly <c>count / buckets</c> characters, picked in shuffled
    /// order, so every cell refreshes once per cycle without repeats.
    /// </summary>
    public void Churn(int buckets, int charCount)
    {
        var count = Count;
        if (charCount <= 0 || count == 0) return;

        if (order.Length != count)
        {
            order = new uint[count];
            for (var i = 0; i < count; i++) order[i] = (uint)i;
            orderCursor = 0;
        }

        var perFrame = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, buckets)));
        for (var n = 0; n < perFrame; n++)
        {
            if (orderCursor >= count)
            {
                Shuffle(order);
                orderCursor = 0;
            }
            Char[order[orderCursor++]] = (byte)Random.Shared.Next(charCount);
        }
    }

    /// <summary>Exponential fade, with a floor for word cells (pulse flash).</summary>
    public void Decay(float factor, float textFactor, float floor, float epsilon, float flash)
    {
        var energy = Energy;
        var weight = Weight;
        var wordMin = MathF.Max(floor, flash);

        for (var i = 0; i < energy.Length; i++)
        {
            var t = weight[i];
            var e = energy[i];
            if (t == 0)
            {
                if (e == 0) continue;
                var only = e * factor;
                energy[i] = only < epsilon ? 0 : only;
                continue;
            }

            var blend = MathF.Min(1, t * 3);
            var next = e * (factor + (textFactor - factor) * blend);
            var min = wordMin * blend;
            if (next < min) next = min;
            energy[i] = next < epsilon ? 0 : next;
        }
    }

    public void ClampChars(int charCount)
    {
        if (charCount <= 0) return;
        for (var i = 0; i < Char.Length; i++)
        {
            if (Char[i] >= charCount) Char[i] = (byte)(Char[i] % charCount);
        }
    }

    public void SetWeight(float[] source)
    {
        var length = Math.Min(source.Length, Weight.Length);
        Array.Copy(source, Weight, length);
        if (length < Weight.Length) Array.Clear(Weight, length, Weight.Length - length);
    }

    private static void Shuffle(uint[] values)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
