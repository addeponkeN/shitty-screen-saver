# Matriosk

A minimal screensaver: a full-bleed field of characters drifting like water, rain falling
through it, and a configured word that only appears where the rain or a ripple touches it.

The screensaver is a C# / .NET 10 / WPF app in `src/Matriosk`.

## WPF app

```sh
dotnet build Matriosk.slnx
dotnet run --project src/Matriosk
```

Controls: `Esc` quit, `D` stats overlay, `F` fullscreen, `R` reload config,
`` ` `` tuning panel.

The scene fades in from black at startup for `fadeIn` seconds (default 5, `0` = instant).
With `"screensaver": true` the window goes borderless and full screen on the primary
monitor, hides the cursor, stays on top, and **any** key, click or mouse move quits —
all the usual screensaver behaviour short of the `.scr` packaging.

Headless modes (no window, useful for measuring and screenshots):

```sh
dotnet run --project src/Matriosk -- --bench 200 --size=1920x1080
dotnet run --project src/Matriosk -- --shot out.png 120 --size=1600x900
dotnet run --project src/Matriosk -- --selftest
dotnet run --project src/Matriosk -- --panel
```

`--bench` writes `bench.json` next to the executable with frame time, per-stage timings
and CPU usage; `--shot` writes a PNG; `--selftest` round-trips every panel control and
applies every preset against the renderer; `--panel` opens the tuning window on start.

### Tuning panel

`` ` `` opens a second window with every config key as a slider/box, generated from
`ControlSchema.cs` (one row per config leaf, resolved by reflection):

- **Presets** — Ice, Mono, Phosphor, Amber, Dense rain, Drizzle. Applied on top of the
  defaults, so switching never inherits the previous preset's leftovers.
- **Sections** — Text, Field, Startup (fade-in, screensaver), Rain, Ripples, Energy,
  Water, Word mask, Colors. Colour rows have a `pick` button that opens the system colour
  dialog, plus a hex box and a live swatch.
- **Bounds** — every slider shows its min and max at the ends of the track
  (`fall speed 1 [====] 300 250`). The bounds come from `Tuning.Ranges`, the same table
  `Config.Normalize()` clamps with, so the panel can reach any value a config file may
  hold. If a value ever falls outside, the control widens to include it instead of
  snapping.
- Edits mutate the live config and are coalesced into a single rebuild per frame —
  cell size relayouts, text re-rasterizes the word mask, font/charset rebuild the glyph
  masks, colors rebuild only the palette tables.
- **Save** rewrites `config.json` next to the executable; **Copy JSON** puts it on the
  clipboard. While the panel has unsaved edits, file changes on disk are ignored so they
  can't stomp your work.

### Renderer

No text is drawn per frame. At startup every charset glyph is rasterized once into an
8-bit coverage mask (plus a blurred copy for the word halo). Each frame clears one BGRA
buffer, blends only the covered pixels of the lit cells into it, and blits the whole
buffer to a `WriteableBitmap` — one copy, no per-glyph draw calls.

That is what keeps the CPU low: the simulation is ~0.7 ms/frame, the compositor ~2–3 ms,
and nothing scales with the number of glyph draw calls.

Brightness is a continuous ramp rather than discrete tiers, and word cells blend further
toward the hot colour based on how far a pulse sits above the water baseline. That matters
for the text: a drop crossing the word raises those cells' opacity smoothly (no threshold
jump, so no flicker) and they fade at `textMask.decay` (default 0.99) instead of snapping
back. The visibility cutoff is soft for the same reason — a cell drifting across
`energy.minVisible` fades to zero rather than blinking on and off.

### Configuration

`config.json` is copied next to the executable at build time. Edit that copy (or change
`src/Matriosk/config.json` and rebuild) — the app watches the output file with a
`FileSystemWatcher` and hot-reloads on save.

Defaults are in `src/Matriosk/config.json` (copied next to the exe on build) and every key is
editable from the tuning panel. The build adds `fps` (default 30), `fadeIn` (seconds,
default 5), `screensaver` (default `false`) and `rain.glowTextOnly` (default `true`): when
on, a drop's radial glow only lights word cells, while its head and tail still show in the
field. Defaults are tuned for 1080p: `cell.maxCells` 30000 with 7×12 cells (274×90 = 24 660),
`energy.minVisible` 0.15.

### Randomised tunings

Rain and ripple values accept either a number (fixed) or a two-element array, in which
case a random value is picked from that range:

```jsonc
"rain": {
  "speed": 26,            // fixed
  "size": [1.5, 3.0],     // random per drop
  "perSecond": [4, 10]    // random per roll
},
"ripple": {
  "width": [2, 5]         // random per ripple
},
"randomize": { "seconds": 8 }
```

| Values | Sampled |
| --- | --- |
| rain `speed`, `trail`, `trailDecay`, `size`, `glow`, `strength`; every ripple value | per drop / per ripple, at spawn |
| rain `perSecond` | every `randomize.seconds` (default 8; `0` = once per load) |
| everything else — energy, water, word mask, cell size, `fps`, `maxCells`, mask geometry, colours | fixed |

In the panel, a ranged row is a two-thumb slider: drag the handles together for a fixed
value, apart for a range, or type the low/high boxes. Scalar rows keep a single slider.

## Project layout

```
Matriosk.slnx
src/Matriosk/
  Config.cs        JSON config, defaults, clamping, hot reload
  Grid.cs          cell arrays, shuffled churn, decay
  Noise.cs         3D Perlin water field
  Rain.cs          falling drops: size, glow fade, tail, word hits
  Ripples.cs       noisy wavefronts raised on text hits
  TextMask.cs      word -> per-cell weights (FormattedText)
  GlyphMasks.cs    charset -> coverage masks
  FieldRenderer.cs software compositor + WriteableBitmap
  Pipeline.cs      wiring, layout, step/draw
  MainWindow.xaml  window, timer, input
```

## Measured

Release build, headless bench, 30 fps target, `maxCells` 30000 (cell 7×12):

| Viewport | Grid | Frame | Sim | Draw | CPU at 30 fps |
| --- | --- | --- | --- | --- | --- |
| 1600×900 | 228×75 | 1.77 ms | 0.5 ms | 1.3 ms | ~5 % |
| 1920×1080 | 274×90 | 2.30 ms | 0.6 ms | 1.7 ms | ~7 % |
| 2560×1440 | 302×99 | 3.46 ms | 0.7 ms | 2.8 ms | ~10 % |

CPU is `frame time / 33 ms` of a single core; on a normal multi-core machine that is
well under 1 % of total CPU. Debug builds are ~35 % slower, so measure with `-c Release`.

The Canvas2D prototype this was ported from spent 15–20 ms per frame in ~10 000
`fillText` calls at 60 fps; the WPF compositor runs 24 660 cells in ~2.3 ms at 30 fps.

## Next

- `.scr` packaging: `/s` `/c` `/p`, and one window per monitor.
- Optional: GPU path if higher frame rates at 4K become interesting.
