# SE2 LCD Cursor API

A small, shared plugin for Space Engineers 2 that turns "the player is looking at an LCD
panel" into "the cursor is at pixel (x, y) on that panel's render surface", and delivers the
clicks, drags and scrolls that go with it.

It exists so that Grid Schematics 2, the RTT Camera API, and anything else that draws an
interactive surface on an LCD can share one implementation instead of each re-deriving the
panel geometry by hand.

## What it does

- **Exact surface coordinates.** A view ray becomes a `(u, v)` in `[0,1]` and a pixel
  position in the panel's own render-target space, using screen geometry baked per block
  subtype from the block's actual mesh part — not a bounding-box approximation with a
  hand-tuned inset.
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
| `tools/CatalogBaker` | Dev-only in-game bake of the screen-geometry catalog. |
| `catalog/lcd-catalog.json` | The baked catalog, reviewed and committed as data. |
| `docs/` | Design notes and the investigation behind them. |

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
