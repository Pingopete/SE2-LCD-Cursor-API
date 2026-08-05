using System.Collections.Concurrent;
using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// The live implementation behind <see cref="LcdCursor"/>.
/// </summary>
internal sealed class CursorRuntime : ILcdCursorRuntime
{
    private readonly CursorModeMachine _modes = new();
    private readonly ConcurrentDictionary<PanelId, PanelInfo> _panels = new();

    private CursorHit _current;
    private CursorButtons _prevButtons;
    private PanelId _prevPanel;

    public CursorHit Current => _current;
    public CursorMode Mode => _modes.Mode;

    public void Start()
    {
        CatalogStore.EnsureLoaded();
        PanelRegistry.Attach(_panels);
    }

    public void Stop()
    {
        PanelRegistry.Detach();
        _modes.ForceRelease();
    }

    public void Tick()
    {
        // Placeholder for the per-frame resolve; see PanelRegistry / AimResolver for the
        // engine-facing half. Kept separate so this class stays about state, not lookups.
        var hit = AimResolver.Resolve(_modes, _panels);
        Publish(hit);
    }

    /// <summary>
    /// Turn a resolved position into the event stream. Edge detection lives here, in one
    /// place, because enter/leave/click are all derived from the same two comparisons and
    /// splitting them across call sites is how a press on one panel ends up reported as a
    /// click on another.
    /// </summary>
    private void Publish(in CursorHit hit)
    {
        var prevPanel = _prevPanel;
        var prevButtons = _prevButtons;
        _current = hit;
        _prevPanel = hit.Panel;
        _prevButtons = hit.Buttons;

        if (hit.Panel != prevPanel)
        {
            if (prevPanel.IsValid)
                LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Leave,
                    new CursorHit(prevPanel, 0, 0, 0, 0, 0, Mode, CursorButtons.None), CursorButtons.None, 0f));
            if (hit.Panel.IsValid)
                LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Enter, hit, CursorButtons.None, 0f));
        }
        else if (hit.Panel.IsValid)
        {
            LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Move, hit, CursorButtons.None, 0f));
        }

        if (!hit.Panel.IsValid) return;

        foreach (var b in AllButtons)
        {
            bool was = (prevButtons & b) != 0, now = (hit.Buttons & b) != 0;
            if (now && !was)
                LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Press, hit, b, 0f));
            else if (!now && was)
            {
                LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Release, hit, b, 0f));
                // A click is only a click if the press and the release landed on the same
                // panel — dragging off a panel and letting go must not activate anything.
                if (hit.Panel == prevPanel)
                    LcdCursor.Bridge.Dispatch(new CursorEvent(CursorEventKind.Click, hit, b, 0f));
            }
        }
    }

    private static readonly CursorButtons[] AllButtons =
        { CursorButtons.Left, CursorButtons.Right, CursorButtons.Middle };

    // ------------------------------------------------------------ facade API

    public bool TryGetCursor(PanelId panel, out CursorHit hit)
    {
        var c = _current;
        if (c.IsValid && c.Panel == panel) { hit = c; return true; }
        hit = default;
        return false;
    }

    public bool TryGetPanelInfo(PanelId panel, out PanelInfo info) => _panels.TryGetValue(panel, out info);

    public IDisposable SuppressInteractionHighlight() => new HighlightClaim();

    public void BeginCalibration(PanelId panel, bool force) => Calibration.Begin(panel, force);

    /// <summary>
    /// A single consumer's claim on hiding the interaction glow. Counted rather than
    /// flagged so two consumers cannot un-hide each other's glow, and idempotent on dispose
    /// so a double-dispose cannot drive the count negative and strand the glow off.
    /// </summary>
    private sealed class HighlightClaim : IDisposable
    {
        private int _released;

        public HighlightClaim() => Interlocked.Increment(ref HostBridge.HighlightSuppressions);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Interlocked.Decrement(ref HostBridge.HighlightSuppressions);
        }
    }
}
