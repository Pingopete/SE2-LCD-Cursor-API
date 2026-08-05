namespace LcdCursorApi.Logic;

/// <summary>
/// Decides which cursor mode is active, and where the cursor is while the mouse is driving it.
/// </summary>
/// <remarks>
/// <para>Deliberately free of engine types: it takes an <see cref="Input"/> snapshot and returns
/// a decision. That keeps the one piece of this API with real edge-case density testable
/// without a running game.</para>
///
/// <para><b>The gestures.</b> Holding Alt while aiming at a panel gives momentary decoupled
/// control that ends when Alt is released. Clicking the right button while Alt is held
/// latches it, so the player can let go. A latch is escaped by pressing Alt <i>or</i> the
/// right button — either alone is enough, which also covers repeating the Alt+RightClick
/// gesture, since that gesture contains a right-button press.</para>
///
/// <para><b>Why any single key escapes.</b> This mode takes the mouse away from the camera. A
/// player who triggers it by accident, or who panics, must not have to work out which
/// combination releases it. Every plausible mash — Alt, right click, or both — exits. The
/// cost of an accidental exit is one lost click; the cost of a player being unable to look
/// around is the whole session.</para>
/// </remarks>
internal sealed class CursorModeMachine
{
    /// <summary>A snapshot of the inputs this machine cares about, taken once per update.</summary>
    internal readonly struct Input
    {
        /// <summary>Either Alt key held.</summary>
        public readonly bool Alt;
        public readonly bool RightButton;
        /// <summary>True when the head-aim ray currently lands on a usable panel.</summary>
        public readonly bool AimingAtPanel;
        /// <summary>The panel under the head-aim ray. Only meaningful when <see cref="AimingAtPanel"/>.</summary>
        public readonly PanelId AimedPanel;
        /// <summary>Raw mouse delta this update, in device units.</summary>
        public readonly float MouseDx, MouseDy;

        public Input(bool alt, bool rightButton, bool aimingAtPanel, PanelId aimedPanel, float mouseDx, float mouseDy)
        {
            Alt = alt;
            RightButton = rightButton;
            AimingAtPanel = aimingAtPanel;
            AimedPanel = aimedPanel;
            MouseDx = mouseDx;
            MouseDy = mouseDy;
        }
    }

    /// <summary>Pixels of cursor travel per unit of mouse delta. Tuned on-glass; a knob, not a constant.</summary>
    public float Sensitivity = 1.0f;

    private bool _prevAlt, _prevRight;

    /// <summary>Cursor position in the locked panel's pixel space while the mouse is driving.</summary>
    private float _x, _y;
    private int _panelW, _panelH;

    public CursorMode Mode { get; private set; } = CursorMode.HeadAim;

    /// <summary>The panel the decoupled cursor is bound to. Invalid outside a decoupled mode.</summary>
    public PanelId LockedPanel { get; private set; }

    /// <summary>True while the camera must not receive mouse movement.</summary>
    public bool CapturesMouse => Mode is CursorMode.DecoupledLatched or CursorMode.DecoupledHeld;

    public float X => _x;
    public float Y => _y;

    /// <summary>
    /// Advance one update. <paramref name="headAimX"/>/<paramref name="headAimY"/> are the
    /// head-aim cursor position in panel pixels, used to seed the decoupled cursor so it
    /// starts where the player was already looking rather than jumping to the centre.
    /// </summary>
    public void Update(in Input input, int panelWidth, int panelHeight, float headAimX, float headAimY)
    {
        bool altRising = input.Alt && !_prevAlt;
        bool rightRising = input.RightButton && !_prevRight;
        _prevAlt = input.Alt;
        _prevRight = input.RightButton;

        switch (Mode)
        {
            case CursorMode.HeadAim:
            case CursorMode.None:
                if (input.Alt && input.AimingAtPanel)
                    EnterDecoupled(CursorMode.DecoupledHeld, input.AimedPanel, panelWidth, panelHeight, headAimX, headAimY);
                break;

            case CursorMode.DecoupledHeld:
                // Alt is already down, so a right press here is the Alt+RightClick gesture.
                if (rightRising) Mode = CursorMode.DecoupledLatched;
                else if (!input.Alt) Release();
                break;

            case CursorMode.DecoupledLatched:
                // Any single escape key. Checked before movement so the exit is never
                // swallowed by the same update that would have moved the cursor.
                if (altRising || rightRising) Release();
                break;
        }

        if (CapturesMouse)
        {
            // A latched cursor keeps its own panel size: the head ray may have wandered off
            // the panel entirely, and clamping to whatever is under the crosshair now would
            // teleport the cursor.
            _x = Math.Clamp(_x + input.MouseDx * Sensitivity, 0f, Math.Max(0, _panelW - 1));
            _y = Math.Clamp(_y + input.MouseDy * Sensitivity, 0f, Math.Max(0, _panelH - 1));
        }
    }

    private void EnterDecoupled(CursorMode mode, PanelId panel, int w, int h, float seedX, float seedY)
    {
        Mode = mode;
        LockedPanel = panel;
        _panelW = w;
        _panelH = h;
        _x = Math.Clamp(seedX, 0f, Math.Max(0, w - 1));
        _y = Math.Clamp(seedY, 0f, Math.Max(0, h - 1));
    }

    private void Release()
    {
        Mode = CursorMode.HeadAim;
        LockedPanel = default;
    }

    /// <summary>
    /// Drop out of any decoupled mode unconditionally. Called when the world unloads, the
    /// panel is destroyed, or the player is otherwise no longer in a state where holding
    /// their mouse hostage is defensible.
    /// </summary>
    public void ForceRelease() => Release();
}
