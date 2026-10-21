namespace Matriosk;

/// <summary>
/// Digital rain: drops fall down the grid, each with a solid core (<c>Size</c>),
/// a glow that fades to nothing over <c>Glow</c> cells, and a vertical tail whose
/// brightness falls off with <c>TrailDecay</c>.
/// </summary>
public struct Drop
{
    public float X;
    public float Y;
    public float Speed;
    public float Trail;
    public float TrailDecay;
    public float Size;
    public float Glow;
    public float Strength;
    public bool Rippled;
}

public readonly struct RainUpdate
{
    public readonly float WordHit;
    public readonly bool HasContact;
    public readonly float ContactX;
    public readonly float ContactY;

    public RainUpdate(float wordHit, bool hasContact, float contactX, float contactY)
    {
        WordHit = wordHit;
        HasContact = hasContact;
        ContactX = contactX;
        ContactY = contactY;
    }
}

public sealed class RainField
{
    private Drop[] drops = new Drop[64];
    private float spawnAccumulator;

    public int Count { get; private set; }

    public void Clear()
    {
        Count = 0;
        spawnAccumulator = 0;
    }

    /// <summary>Deterministic drop, for the self-test.</summary>
    internal void AddDrop(float x, float y, float size, float glow, float strength, float trail)
    {
        if (Count == drops.Length) Array.Resize(ref drops, drops.Length * 2);
        drops[Count++] = new Drop
        {
            X = x,
            Y = y,
            Speed = 0,
            Trail = trail,
            TrailDecay = 1.2f,
            Size = size,
            Glow = glow,
            Strength = strength,
        };
    }

    public RainUpdate Update(float dt, CharGrid grid, RainConfig tuning, float perSecond)
    {
        spawnAccumulator += dt * perSecond;
        while (spawnAccumulator >= 1)
        {
            spawnAccumulator -= 1;
            if (Count < tuning.Max) Spawn(grid, tuning);
        }

        var wordHit = 0f;
        var hasContact = false;
        var contactX = 0f;
        var contactY = 0f;
        var contactStrength = 0f;

        for (var i = Count - 1; i >= 0; i--)
        {
            ref var drop = ref drops[i];
            drop.Y += drop.Speed * dt;
            if (drop.Y - drop.Trail > grid.Rows + 2)
            {
                drops[i] = drops[Count - 1];
                Count--;
                continue;
            }

            Deposit(ref drop, grid, tuning.GlowTextOnly, ref wordHit, ref hasContact, ref contactX, ref contactY, ref contactStrength);
        }

        return new RainUpdate(wordHit, hasContact, contactX, contactY);
    }

    private void Spawn(CharGrid grid, RainConfig tuning)
    {
        if (Count == drops.Length) Array.Resize(ref drops, drops.Length * 2);

        var trail = tuning.Trail.Sample() * (0.7f + Random.Shared.NextSingle() * 0.6f);
        drops[Count++] = new Drop
        {
            X = Random.Shared.NextSingle() * grid.Cols,
            Y = -Random.Shared.NextSingle() * (trail + 4) - 2,
            Speed = tuning.Speed.Sample() * (0.8f + Random.Shared.NextSingle() * 0.5f),
            Trail = trail,
            TrailDecay = tuning.TrailDecay.Sample(),
            Size = tuning.Size.Sample() * (0.85f + Random.Shared.NextSingle() * 0.3f),
            Glow = tuning.Glow.Sample() * (0.8f + Random.Shared.NextSingle() * 0.4f),
            Strength = tuning.Strength.Sample(),
        };
    }

    private static void Deposit(
        ref Drop drop,
        CharGrid grid,
        bool glowTextOnly,
        ref float wordHit,
        ref bool hasContact,
        ref float contactX,
        ref float contactY,
        ref float contactStrength)
    {
        var cols = grid.Cols;
        var size = MathF.Max(0.4f, drop.Size);
        var glow = MathF.Max(0.4f, drop.Glow);
        // Size is the drop's width in cells (1 = one cell), so its core radius is half.
        var coreRadius = MathF.Max(0.5f, size * 0.5f);
        var outer = coreRadius + glow;
        var reach = (int)MathF.Ceiling(outer);
        var headY = drop.Y;
        var tailY = headY - drop.Trail;
        var trailHalfWidth = MathF.Max(0.8f, size * 0.55f);
        var trailPower = MathF.Max(0.05f, drop.TrailDecay);

        var xStart = Math.Max(0, (int)MathF.Floor(drop.X - reach));
        var xEnd = Math.Min(cols - 1, (int)MathF.Floor(drop.X + reach));
        var yStart = Math.Max(0, (int)MathF.Floor(tailY - reach));
        var yEnd = Math.Min(grid.Rows - 1, (int)MathF.Floor(headY + reach));
        if (xStart > xEnd || yStart > yEnd) return;

        var energy = grid.Energy;
        var weight = grid.Weight;

        for (var y = yStart; y <= yEnd; y++)
        {
            var cy = y + 0.5f;
            var belowHead = cy - headY;
            var aboveHead = -belowHead;

            var trailT = 0f;
            if (aboveHead >= 0)
            {
                var along = 1 - aboveHead / drop.Trail;
                if (along > 0) trailT = MathF.Pow(along, trailPower);
            }

            var rowOffset = y * cols;
            for (var x = xStart; x <= xEnd; x++)
            {
                var dx = x + 0.5f - drop.X;
                var headDist = MathF.Sqrt(dx * dx + belowHead * belowHead);

                // One smooth profile: 1 at the centre, 0 at the outer radius, so the
                // glow never has a flat opaque core.
                var falloff = 1 - headDist / outer;
                var headGlow = falloff <= 0 ? 0 : falloff * falloff * (3 - 2 * falloff);

                var trailGlow = 0f;
                if (trailT > 0)
                {
                    var width = 1 - MathF.Abs(dx) / trailHalfWidth;
                    if (width > 0) trailGlow = trailT * width;
                }

                var brightest = MathF.Max(headGlow, trailGlow);
                if (brightest <= 0) continue;

                var index = rowOffset + x;
                var isWord = weight[index] > 0.05f;

                // The radial glow can be restricted to the word; the trail (the drop
                // itself) always stays visible.
                if (glowTextOnly && !isWord && headGlow >= trailGlow) continue;

                var target = drop.Strength * brightest;
                if (target > energy[index]) energy[index] = target;

                if (isWord)
                {
                    if (target > wordHit) wordHit = target;
                    if (!drop.Rippled && target > contactStrength)
                    {
                        contactStrength = target;
                        hasContact = true;
                        contactX = x + 0.5f;
                        contactY = cy;
                    }
                }
            }
        }

        if (hasContact && !drop.Rippled) drop.Rippled = true;
    }
}
