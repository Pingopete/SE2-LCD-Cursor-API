using System.Collections.Concurrent;
using System.Reflection;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Contracts;
using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Draws the cursor onto whichever panel is being aimed at, and drives the repaint that lets
/// it move.
/// </summary>
/// <remarks>
/// <para><b>Two halves, and both are necessary.</b> Drawing happens in a postfix on
/// <c>LcdContentRendererSessionComponent.Render</c>, the only seam handing over an
/// <c>IDrawBatch</c> bound to a panel's target. But a panel only re-renders when the engine
/// thinks its content changed, so on its own the cursor would be painted once and then sit
/// frozen. The second half re-invokes the component's own private
/// <c>RebuildSurfaceContent</c> for the aimed panel — the engine's real repaint path. Setting
/// <c>ContentDirty</c> from inside the render postfix does not work: it is cleared immediately
/// after Render returns.</para>
///
/// <para><b>Why a vector crosshair rather than a texture.</b> The GS2 prototype tries a texture
/// first and keeps a vector fallback, having hit two problems: the texture streamer evicts by
/// distance and priority, and an evicted texture draws nothing at all while still recording
/// draws — a blank panel with healthy counters — which forces a re-pin every frame; and the
/// cursor icon's file extension turned out not to be supported by <c>DrawImage</c> anyway. A
/// crosshair built from filled rectangles has no asset to ship, nothing to pin and nothing to
/// evict, which is what a first-light build wants.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class CursorOverlay
{
    /// <summary>Master switch. A consumer drawing its own cursor turns this off via the config.</summary>
    public static bool Enabled => Config.ShowCursor;

    /// <summary>Arm length of the crosshair, in panel pixels.</summary>
    public static volatile float Size = 14f;

    /// <summary>Half-thickness of each arm, in panel pixels.</summary>
    public static volatile float Thickness = 2f;

    private static CursorRuntime _runtime;

    public static void Attach(CursorRuntime runtime)
    {
        _runtime = runtime;
        HostBridge.LcdRenderHook = OnRender;
    }

    public static void Detach()
    {
        HostBridge.LcdRenderHook = null;
        _runtime = null;
        Tracked.Clear();

        // Hand the panels back before this assembly goes away. A private material left
        // dangling across a reload is what produces the engine's "Can't remove material"
        // double-release.
        PrivateMaterial.ReleaseAll();
    }

    /// <summary>Contexts that carried a cursor recently, and when. Drives the repaint.</summary>
    private static readonly ConcurrentDictionary<object, long> Tracked = new();

    private static int _errors;
    private static bool _firstDrawLogged;

    private static int _repaintCalls, _drawCalls, _tickCalls;
    private static long _lastLiveLog;

    /// <summary>Counted from the render tick, for every LCD component, whether or not it repaints.</summary>
    public static void NoteTick() => Interlocked.Increment(ref _tickCalls);

    /// <summary>
    /// While the cursor is on a panel, report whether the machinery behind it is actually
    /// running.
    /// </summary>
    /// <remarks>
    /// This exists to separate three failures that look identical on screen — a cursor that
    /// is simply absent:
    /// <list type="bullet">
    /// <item><b>ticks 0</b> — the engine stopped ticking that panel's render component, so
    /// nothing of ours runs and the repaint can never restart itself.</item>
    /// <item><b>ticks &gt; 0, repaints 0</b> — we are running but declining to repaint, so the
    /// fault is in the active-surface match.</item>
    /// <item><b>repaints &gt; 0, draws 0</b> — we repaint but the engine never calls Render,
    /// so the content path is refusing.</item>
    /// </list>
    /// Guessing between these has already cost two rounds.
    /// </remarks>
    public static void ReportLiveness(PanelId panel)
    {
        long now = Environment.TickCount64;
        if (now - _lastLiveLog < 2000) return;
        _lastLiveLog = now;

        int ticks = Interlocked.Exchange(ref _tickCalls, 0);
        int repaints = Interlocked.Exchange(ref _repaintCalls, 0);
        int draws = Interlocked.Exchange(ref _drawCalls, 0);
        Log.Line($"Cursor live on {panel}: ticks {ticks}, repaints {repaints}, draws {draws} (last 2s).");
    }

    // ------------------------------------------------------------------ draw

    private static void OnRender(object batchObj, object ctxObj)
    {
        if (!Enabled) return;
        if (batchObj is not IDrawBatch batch || ctxObj is not LcdPanelSurfaceContext ctx) return;

        try
        {
            var rt = _runtime;
            if (rt == null) return;

            var hit = rt.Current;
            if (!PanelRegistry.TryGetByContext(ctx, out var panelId)) return;

            // Not the aimed panel: draw nothing. The repaint driver keeps this surface
            // rendering for a moment after the cursor leaves precisely so this no-draw pass
            // happens and erases the old crosshair — otherwise it stays burned on, which
            // looks exactly like a second live cursor.
            if (!hit.IsValid || panelId != hit.Panel) return;

            // Keep repainting this context for a moment after the cursor leaves, so the last
            // frame drawn is a clean one rather than a cursor stamped where it used to be.
            Tracked[ctx] = Environment.TickCount64;

            if (!_firstDrawLogged)
            {
                _firstDrawLogged = true;
                Log.Line($"Cursor drawing on panel {panelId} at ({hit.X:F0},{hit.Y:F0}).");
            }

            Interlocked.Increment(ref _drawCalls);
            DrawCrosshair(batch, hit.X, hit.Y);
        }
        catch (Exception e)
        {
            if (_errors++ < 3) Log.Error("cursor overlay", e);
        }
    }

    private static void DrawCrosshair(IDrawBatch batch, float x, float y)
    {
        float s = Size, t = Thickness;

        // Dark arms one pixel proud of the light ones. An LCD's content is arbitrary — a
        // single-colour cursor disappears against a panel of the same colour, and the panel's
        // contents are the consumer's business, not something to be worked around per mod.
        var shadow = new ColorSRGB((byte)0, (byte)0, (byte)0, (byte)200);
        FillRect(batch, x - s - 1, y - t - 1, x + s + 1, y + t + 1, shadow);
        FillRect(batch, x - t - 1, y - s - 1, x + t + 1, y + s + 1, shadow);

        var white = new ColorSRGB((byte)235, (byte)235, (byte)235, (byte)255);
        FillRect(batch, x - s, y - t, x + s, y + t, white);
        FillRect(batch, x - t, y - s, x + t, y + s, white);
    }

    private static void FillRect(IDrawBatch batch, float x0, float y0, float x1, float y1, ColorSRGB color)
    {
        Span<QuadraticBezier2> rect = stackalloc QuadraticBezier2[4];
        rect[0] = new QuadraticBezier2(new Vector2(x0, y0), new Vector2(x1, y0));
        rect[1] = new QuadraticBezier2(new Vector2(x1, y0), new Vector2(x1, y1));
        rect[2] = new QuadraticBezier2(new Vector2(x1, y1), new Vector2(x0, y1));
        rect[3] = new QuadraticBezier2(new Vector2(x0, y1), new Vector2(x0, y0));
        batch.DrawFill(rect, color, null, false);
    }

    // --------------------------------------------------------------- repaint

    private static MethodInfo _rebuild;
    private static bool _rebuildResolved;

    /// <summary>How long a context keeps repainting after the cursor last touched it, in ms.</summary>
    private const long TrackTimeoutMs = 500;

    /// <summary>
    /// From the render tick: force a repaint of this component's surfaces while any of them
    /// is carrying the cursor.
    /// </summary>
    /// <remarks>
    /// <para><b>Driven by where the cursor IS, not by where it last drew.</b> The obvious
    /// version keys off a set populated in <see cref="OnRender"/> — repaint the panels we
    /// recently drew on. That version works right up until the chain breaks once, and then
    /// never recovers: no repaint means no render, no render means no draw, no draw means
    /// nothing re-arms the repaint. The panel goes dead and stays dead until something
    /// outside this loop makes the engine render it — in testing, toggling the display mode
    /// off and back on.</para>
    ///
    /// <para>That is this codebase's recurring bug: state that can only change while the
    /// thing it controls is already working. The aim resolve does not depend on the panel
    /// re-rendering — <c>TickFsrMask</c> fires per frame regardless — so asking it where the
    /// cursor is breaks the circularity and the loop becomes self-starting.</para>
    /// </remarks>
    public static void DriveRepaint(object renderComponent, Array surfaces)
    {
        if (!Enabled || renderComponent == null || surfaces == null) return;

        try
        {
            if (!_rebuildResolved)
            {
                _rebuildResolved = true;
                _rebuild = renderComponent.GetType().GetMethod("RebuildSurfaceContent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Log.Line($"Repaint driver: RebuildSurfaceContent {(_rebuild != null ? "resolved" : "NOT FOUND — cursor will not move")}.");
            }
            if (_rebuild == null) return;

            var rt = _runtime;
            if (rt == null) return;

            long now = Environment.TickCount64;
            var hit = rt.Current;

            // Is the cursor on one of THIS component's surfaces right now?
            bool anyActive = false;
            if (hit.IsValid)
            {
                foreach (var s in surfaces)
                {
                    if (s == null) continue;
                    if (!PanelRegistry.TryGetByContext(s, out var id) || id != hit.Panel) continue;

                    // First visit only: take this panel off the engine's shared LCD material,
                    // or our cursor shows up on every panel sharing it.
                    PrivateMaterial.Ensure(renderComponent, s);

                    Tracked[s] = now;
                    anyActive = true;
                    break;
                }
            }

            // Keep going briefly after it leaves, so the frame that erases the crosshair is
            // actually rendered. Without this the cursor stays burned onto the panel.
            if (!anyActive)
            {
                foreach (var s in surfaces)
                {
                    if (s == null) continue;
                    if (!Tracked.TryGetValue(s, out var touched)) continue;
                    if (now - touched > TrackTimeoutMs) { Tracked.TryRemove(s, out _); continue; }
                    anyActive = true;
                }
            }
            if (!anyActive) return;
            Interlocked.Increment(ref _repaintCalls);

            // Rebuild every surface of the component, not only the tracked one: contexts get
            // re-created, and a stale reference would otherwise stop repainting silently.
            foreach (var s in surfaces)
                if (s != null) _rebuild.Invoke(renderComponent, new[] { s });
        }
        catch (Exception e)
        {
            if (_errors++ < 3) Log.Error("repaint driver", e);
        }
    }
}
