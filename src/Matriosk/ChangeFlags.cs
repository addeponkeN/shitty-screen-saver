namespace Matriosk;

/// <summary>What a config edit requires the renderer/simulation to rebuild.</summary>
[Flags]
internal enum ChangeFlags
{
    None = 0,
    /// <summary>Cell size or cell budget changed: relayout, masks and palette.</summary>
    Layout = 1,
    /// <summary>The word mask needs re-rasterizing.</summary>
    Mask = 2,
    /// <summary>Glyph coverage masks need re-rasterizing.</summary>
    Glyphs = 4,
    /// <summary>Only the colour tables changed.</summary>
    Palettes = 8,
}
