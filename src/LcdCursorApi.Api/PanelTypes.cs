namespace LcdCursorApi;

/// <summary>
/// Identifies one drawable LCD surface. A block may carry several
/// (<c>LcdMultiPanelComponent</c> is a *multi*-panel component), so the
/// surface index is part of the identity, never just the block.
/// </summary>
/// <remarks>
/// The block is keyed by EntityId and not by DebugName: in SE2 the debug name is
/// the shared composition name and is emphatically not per-block-unique.
/// </remarks>
public readonly struct PanelId : IEquatable<PanelId>
{
    public readonly long BlockEntityId;
    public readonly int SurfaceIndex;

    public PanelId(long blockEntityId, int surfaceIndex)
    {
        BlockEntityId = blockEntityId;
        SurfaceIndex = surfaceIndex;
    }

    public bool IsValid => BlockEntityId != 0;

    public bool Equals(PanelId other)
        => BlockEntityId == other.BlockEntityId && SurfaceIndex == other.SurfaceIndex;

    public override bool Equals(object obj) => obj is PanelId o && Equals(o);
    public override int GetHashCode() => unchecked((BlockEntityId.GetHashCode() * 397) ^ SurfaceIndex);
    public override string ToString() => $"{BlockEntityId}#{SurfaceIndex}";

    public static bool operator ==(PanelId a, PanelId b) => a.Equals(b);
    public static bool operator !=(PanelId a, PanelId b) => !a.Equals(b);
}

/// <summary>
/// Static description of a panel's drawable surface, as the engine defines it.
/// </summary>
/// <remarks>
/// <see cref="Width"/>/<see cref="Height"/> come straight from
/// <c>LcdPanelSurface.Resolution</c> and are the render-target size in pixels — this is
/// the coordinate space a consumer draws in. Note it is NOT the visual shape: a wide
/// LCD is a 512x512 target displayed through <see cref="AspectRatio"/> 1.33, and a
/// corner LCD is 512x128 at aspect 5.0. Square pixels are not a safe assumption.
/// </remarks>
public readonly struct PanelInfo
{
    public readonly PanelId Id;
    public readonly int Width;
    public readonly int Height;
    public readonly float AspectRatio;

    /// <summary>
    /// The block definition's GUID — the key this panel's catalog entry was baked under.
    /// </summary>
    /// <remarks>
    /// Definitions are GUID-identified (<c>Definition.Guid</c>, matching the <c>"Guid"</c> in the
    /// shipped <c>.def</c> files). That is the stable key: <c>DebugName</c> is the block's
    /// *composition* name and is shared by every block of a model, so it identifies a type at
    /// best and collides in every other use.
    /// </remarks>
    public readonly Guid BlockGuid;

    /// <summary>Human-readable definition name. For logs and diagnostics; never a key.</summary>
    public readonly string BlockName;

    /// <summary>False when no catalog entry matched and a user calibration is supplying the mapping.</summary>
    public readonly bool FromCatalog;

    public PanelInfo(PanelId id, int width, int height, float aspectRatio,
                     Guid blockGuid, string blockName, bool fromCatalog)
    {
        Id = id;
        Width = width;
        Height = height;
        AspectRatio = aspectRatio;
        BlockGuid = blockGuid;
        BlockName = blockName;
        FromCatalog = fromCatalog;
    }
}

/// <summary>Where the cursor position is currently coming from.</summary>
public enum CursorMode
{
    /// <summary>No panel is being aimed at; there is no cursor.</summary>
    None = 0,
    /// <summary>The player's view ray drives the cursor. The default.</summary>
    HeadAim = 1,
    /// <summary>Mouse movement drives the cursor and is withheld from the camera. Latched by Alt+RightClick.</summary>
    DecoupledLatched = 2,
    /// <summary>As <see cref="DecoupledLatched"/>, but only while Alt is held.</summary>
    DecoupledHeld = 3,
}

[Flags]
public enum CursorButtons
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
}

/// <summary>
/// A resolved cursor position on a panel. Both normalised and pixel coordinates are
/// given because consumers want pixels and layout code wants a resolution-free unit.
/// </summary>
public readonly struct CursorHit
{
    public readonly PanelId Panel;
    /// <summary>Surface coordinates in [0,1], origin top-left.</summary>
    public readonly float U, V;
    /// <summary>Surface coordinates in render-target pixels, origin top-left.</summary>
    public readonly float X, Y;
    /// <summary>Distance in metres from the viewer to the screen plane.</summary>
    public readonly double Distance;
    public readonly CursorMode Mode;
    public readonly CursorButtons Buttons;

    public CursorHit(PanelId panel, float u, float v, float x, float y, double distance,
                     CursorMode mode, CursorButtons buttons)
    {
        Panel = panel;
        U = u; V = v;
        X = x; Y = y;
        Distance = distance;
        Mode = mode;
        Buttons = buttons;
    }

    public bool IsValid => Panel.IsValid;
}

/// <summary>What happened to the cursor. Delivered to <see cref="LcdCursor"/> subscribers.</summary>
public enum CursorEventKind
{
    Enter,
    Move,
    Leave,
    Press,
    Release,
    /// <summary>A press and release on the same panel without leaving it.</summary>
    Click,
    Scroll,
    ModeChanged,
}

public readonly struct CursorEvent
{
    public readonly CursorEventKind Kind;
    public readonly CursorHit Hit;
    /// <summary>Which button the event concerns, for press/release/click. <see cref="CursorButtons.None"/> otherwise.</summary>
    public readonly CursorButtons Button;
    /// <summary>Scroll delta in notches, positive = away from the player. Zero unless <see cref="CursorEventKind.Scroll"/>.</summary>
    public readonly float ScrollDelta;

    public CursorEvent(CursorEventKind kind, CursorHit hit, CursorButtons button, float scrollDelta)
    {
        Kind = kind;
        Hit = hit;
        Button = button;
        ScrollDelta = scrollDelta;
    }
}
