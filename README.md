# SE2 LCD Cursor API

A small, shared plugin for Space Engineers 2 that turns "the player is looking at an LCD
panel" into "the cursor is at pixel (x, y) on that panel's render surface", and delivers the
clicks, drags and scrolls that go with it.

It exists so that Grid Schematics 2, the RTT Camera API, and anything else that draws an
interactive surface on an LCD can share one implementation instead of each re-deriving the
panel geometry by hand.

## What it does

- **Exact surface coordinates.** A view ray becomes a `(u, v)` in `[0,1]` and a pixel
  position in the panel's own render-target space, using the screen placement the block
  definition already carries — not a bounding-box approximation with a hand-tuned inset.
- **Two cursor modes.** Head aim by default. Alt+RightClick latches a decoupled mode where
  mouse movement drives the cursor instead of the camera; holding Alt alone gives the same
  thing momentarily.
- **Input events.** Enter, move, leave, press, release, click, scroll — per panel.
- **Interaction-glow suppression.** Reference-counted removal of the yellow highlight the
  game draws around a panel the player looks at.
- **Calibration, only when needed.** Stock panels come from the shipped catalog. Modded
  panels fall back to a short click-through calibration, stored per block.

## Layout

| Path | What it is |
|---|---|
| `src/LcdCursorApi.Api` | The contract. BCL-only. **This is the only assembly a consumer references.** |
| `src/LcdCursorApi` | Plugin bootstrap: `IPlugin`, Harmony patches, hot-reload host. |
| `src/LcdCursorApi.Logic` | The implementation, hot-reloaded into a collectible load context. |
| `catalog/lcd-catalog.json` | Optional per-block corrections. Not needed for stock blocks. *None yet.* |
| `docs/` | Design notes and the investigation behind them. |

## Status

Nothing here has been run in game yet. What is finished and building:

- The contract (`LcdCursorApi.Api`) and its catalog schema.
- The plugin bootstrap: Harmony patches, hot-reload host, glow suppression.
- `ScreenQuadSolver` — the runtime projection.
- `CursorModeMachine` — the head-aim / decoupled mode logic and its escapes.
- `BlockFrame` — world ray to block model space.
- `DummyQuadSource` — the screen quad, read live from the block's own `LcdPanel` dummy.
- `PanelRegistry` — panel discovery off the LCD render tick.
- `AimResolver` — camera ray, nearest-panel resolve, input.

Still a marked stub: `Calibration` (the fallback for modded panels).

**The known gap** is that decoupled cursor mode reads mouse movement but does not yet withhold
it from the camera, so the view will turn while the cursor moves. The mode machine and its
escapes are done; the input suppression patch is not.

## Using it from a mod

Reference `LcdCursorApi.Api` only. It binds to the plugin at runtime and no-ops when the
plugin is absent, so calls never need guarding.

```csharp
using LcdCursorApi;

LcdCursor.Event += e =>
{
    if (e.Hit.Panel != myPanel) return;
    switch (e.Kind)
    {
        case CursorEventKind.Move:  MoveCursorTo(e.Hit.X, e.Hit.Y); break;
        case CursorEventKind.Click: HitTest(e.Hit.X, e.Hit.Y);      break;
    }
};

// Hide the yellow interaction glow for as long as this handle is held.
_glow = LcdCursor.SuppressInteractionHighlight();
```

Subscriptions survive a hot reload of the plugin's logic — the subscriber list is owned by
the facade, not by the reloadable half. See the remarks on `LcdCursor` for why.

## Building

Paths come from `Directory.Build.props` and can be overridden without editing it:

```bash
dotnet build -c Release /p:SE2Dir="E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2" /p:DeployDir="D:\SE2LcdCursor"
```

**Do not build while the game is running.** A build is a hot reload; in this engine that has
repeatedly cost a device removal. Close the game first.

## Design notes

The one thing worth reading before changing anything here is
[docs/findings-screen-geometry.md](docs/findings-screen-geometry.md) — it explains why the
screen geometry is baked into a catalog rather than queried at runtime, and documents the
routes that do not work so they are not retried.
