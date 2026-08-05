namespace LcdCursorApi;

/// <summary>
/// The public entry point. Reference this assembly from a consuming mod; it binds to
/// the LCD Cursor API plugin at runtime and degrades to inert no-ops when that plugin
/// is not installed, so a consumer never has to guard its calls.
/// </summary>
/// <remarks>
/// <para><b>Why the events live here and not on the runtime.</b> The plugin hot-reloads its
/// logic assembly into a collectible load context. If subscribers attached to the runtime
/// object, every reload would silently drop them and the consumer would go dead without an
/// error. The subscriber list is therefore owned by this facade — which is loaded once, into
/// the default context — and the runtime only ever *raises* through
/// <see cref="Bridge.Dispatch"/>. Subscriptions survive reloads.</para>
/// </remarks>
public static class LcdCursor
{
    private static volatile ILcdCursorRuntime _runtime;

    /// <summary>True when the API plugin is loaded and serving. Poll it; it can go false across a reload.</summary>
    public static bool IsAvailable => _runtime != null;

    /// <summary>The cursor as of the last update, or an invalid hit when nothing is aimed at.</summary>
    public static CursorHit Current => _runtime?.Current ?? default;

    public static CursorMode Mode => _runtime?.Mode ?? CursorMode.None;

    /// <summary>The cursor on one specific panel, or false when that panel is not the aimed one.</summary>
    public static bool TryGetCursor(PanelId panel, out CursorHit hit)
    {
        var rt = _runtime;
        if (rt != null) return rt.TryGetCursor(panel, out hit);
        hit = default;
        return false;
    }

    /// <summary>Static surface description — resolution, aspect, whether a catalog entry backed it.</summary>
    public static bool TryGetPanelInfo(PanelId panel, out PanelInfo info)
    {
        var rt = _runtime;
        if (rt != null) return rt.TryGetPanelInfo(panel, out info);
        info = default;
        return false;
    }

    /// <summary>
    /// Suppress the yellow interaction glow the game draws around a panel the player looks at.
    /// Reference-counted: every consumer that asks gets its own claim, and the glow returns only
    /// when the last claim is released. Dispose the returned handle to release.
    /// </summary>
    public static IDisposable SuppressInteractionHighlight()
        => _runtime?.SuppressInteractionHighlight() ?? NullClaim.Instance;

    /// <summary>
    /// Begin the click-through calibration for a panel whose block has no catalog entry.
    /// A no-op when the panel is already catalog-backed unless <paramref name="force"/> is set.
    /// </summary>
    public static void BeginCalibration(PanelId panel, bool force = false)
        => _runtime?.BeginCalibration(panel, force);

    /// <summary>Cursor events for every panel. Filter by <c>e.Hit.Panel</c>.</summary>
    public static event Action<CursorEvent> Event;

    /// <summary>
    /// Plumbing between the plugin and this facade. Consumers have no reason to touch it.
    /// </summary>
    public static class Bridge
    {
        /// <summary>Called by the plugin as it loads, and with null as it unloads.</summary>
        public static void Publish(ILcdCursorRuntime runtime) => _runtime = runtime;

        /// <summary>Called by the plugin to raise an event to all consumers.</summary>
        public static void Dispatch(in CursorEvent e)
        {
            var handlers = Event;
            if (handlers == null) return;
            // One bad subscriber must not stop the others, and must never propagate
            // back into the input pump that raised this.
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((Action<CursorEvent>)h)(e); }
                catch { }
            }
        }
    }

    private sealed class NullClaim : IDisposable
    {
        public static readonly NullClaim Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Implemented by the plugin. Consumers use <see cref="LcdCursor"/> instead — this exists
/// so the facade has something to forward to across the assembly boundary.
/// </summary>
public interface ILcdCursorRuntime
{
    CursorHit Current { get; }
    CursorMode Mode { get; }
    bool TryGetCursor(PanelId panel, out CursorHit hit);
    bool TryGetPanelInfo(PanelId panel, out PanelInfo info);
    IDisposable SuppressInteractionHighlight();
    void BeginCalibration(PanelId panel, bool force);
}
