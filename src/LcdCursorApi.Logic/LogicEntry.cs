using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Entry point for the reloadable half. The bootstrap calls <see cref="Tick"/> on a worker
/// and <see cref="Shutdown"/> just before swapping this assembly out.
/// </summary>
public static class LogicEntry
{
    private static CursorRuntime _runtime;
    private static int _errors;

    public static void Tick()
    {
        try
        {
            var rt = _runtime;
            if (rt == null)
            {
                rt = new CursorRuntime();
                rt.Start();
                _runtime = rt;
                LcdCursor.Bridge.Publish(rt);
                Log.Line("Runtime published to the facade.");
            }
            rt.Tick();
        }
        catch (Exception e)
        {
            // Log defensively: if the logger itself is the thing that is broken — which is
            // exactly the case when the logic fails to bind against an older bootstrap —
            // then throwing out of the catch block turns one fault into a silent one.
            try { if (_errors++ < 5) Log.Error("logic tick", e); } catch { }
        }
    }

    /// <summary>
    /// Withdraw before the load context goes away. Publishing null rather than leaving a
    /// stale runtime matters: consumers poll <see cref="LcdCursor.IsAvailable"/>, and a
    /// runtime whose assembly has been unloaded would throw on every call instead of
    /// reporting itself absent.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            LcdCursor.Bridge.Publish(null);
            _runtime?.Stop();
            _runtime = null;
            // Any glow claims this logic held die with it; the count must not leak across
            // a reload or the highlight would stay suppressed forever.
            Volatile.Write(ref HostBridge.HighlightSuppressions, 0);
            HostBridge.LcdTickHook = null;
            HostBridge.LcdSurfaceDefHook = null;
            Log.Line("Runtime withdrawn.");
        }
        catch { }
    }
}
