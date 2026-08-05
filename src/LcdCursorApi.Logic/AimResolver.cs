using System.Collections.Concurrent;

namespace LcdCursorApi.Logic;

/// <summary>
/// Produces the frame's <see cref="CursorHit"/> from the camera ray, the panel set and the
/// mode machine.
/// </summary>
/// <remarks>
/// <para><b>Status: engine-facing, not yet verified in game.</b></para>
///
/// <para>The intended shape, in order: acquire the camera
/// (<c>CameraSystemComponent.RenderCameraEntity</c>, then
/// <c>EntityTransformFunctions.GetWorldTransform</c> for position and forward — this path is
/// proven, it is what the GS2 prototype uses); for each known panel, transform the ray into
/// that block's model frame; project against the catalog quad with
/// <see cref="ScreenQuadSolver"/>; keep the nearest hit. Then hand the head-aim result to
/// <see cref="CursorModeMachine"/>, which either passes it through or overrides the position
/// with the decoupled cursor.</para>
///
/// <para>Two things must not be gotten wrong here. Panels are tested against a real quad, so
/// the nearest hit is the right one with no entry-face heuristic — the prototype's axis/sign
/// bookkeeping does not carry over and should not be reintroduced. And when the mode machine
/// is latched, the returned panel is its <see cref="CursorModeMachine.LockedPanel"/>, not
/// whatever the head ray currently finds, or the cursor will jump the moment the player
/// looks away.</para>
/// </remarks>
internal static class AimResolver
{
    public static CursorHit Resolve(CursorModeMachine modes, ConcurrentDictionary<PanelId, PanelInfo> panels)
    {
        // Not yet implemented: see the remarks. Returning an invalid hit keeps the event
        // stream honest — consumers see "no cursor" rather than a fabricated position.
        return default;
    }
}
