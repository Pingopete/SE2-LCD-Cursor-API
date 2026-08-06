using System.Runtime.InteropServices;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;

namespace LcdCursorApi.Logic;

/// <summary>
/// Produces the frame's <see cref="CursorHit"/> from the camera ray, the registered panels
/// and the mode machine.
/// </summary>
/// <remarks>
/// <para>Panels are tested against their real baked quad, so the nearest hit is simply the
/// right one. There is no entry-face detection and no axis/sign bookkeeping — that machinery
/// existed in the prototype only because it was slab-testing a bounding box and had to guess
/// which face the screen was on. Reintroducing it here would be a regression.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class AimResolver
{
    private static CameraSystemComponent _camera;
    private static Keen.VRage.DCS.Scenes.Scene _cameraScene;
    private static long _lastCameraSearch;

    public static CursorHit Resolve(CursorModeMachine modes)
    {
        EngineLocator.Poll();
        if (!TryGetViewRay(out var eye, out var forward)) { modes.ForceRelease(); return default; }

        // Nearest panel under the head ray. Also the seed for a decoupled cursor, and the
        // gate on entering one.
        PanelRegistry.PanelEntry bestEntry = null;
        float bestU = 0f, bestV = 0f;
        double bestT = double.MaxValue;

        // Calibration runs BEFORE the quad search and independently of it: the whole point is
        // that the target panel has no quad yet, so nothing would be found to aim at.
        if (Calibration.Active) { ServiceCalibration(eye, forward); return default; }
        if (Calibration.Selecting) { CheckCalibrationTrigger(eye, forward); return default; }

        int tested = 0, noQuad = 0, noFrame = 0;
        Span<int> rejects = stackalloc int[8];

        foreach (var entry in PanelRegistry.Live)
        {
            tested++;
            if (entry.Quad == null) { noQuad++; continue; } // nothing to project onto
            if (!BlockFrame.TryToModelSpace(entry.Block, eye, forward, out var mo, out var md)) { noFrame++; continue; }
            if (!ScreenQuadSolver.TryProject(mo, md, entry.Quad, out var u, out var v, out var t, out var why))
            {
                rejects[(int)why]++;
                continue;
            }
            if (t >= bestT) continue;
            bestT = t; bestU = u; bestV = v; bestEntry = entry;
        }

        bool aiming = bestEntry != null;
        if (!aiming) ReportNothingFound(tested, noQuad, noFrame, rejects);

        CheckCalibrationTrigger(eye, forward);
        var aimedPanel = aiming ? bestEntry.Id : default;

        // Delta comes from the camera's own input path (see MouseDelta below), not from the
        // OS and not from the engine's pointer state: while the game holds the pointer the OS
        // cursor does not move, and reading the pointer state cannot stop the camera acting
        // on the same movement.
        TakeMouseDelta(out float mdx, out float mdy);
        var buttons = ReadButtons(out bool modifier);

        // Seed the decoupled cursor where the player was already looking, so entering the
        // mode does not teleport the cursor to the panel centre.
        int seedW = aiming ? bestEntry.Width : 0, seedH = aiming ? bestEntry.Height : 0;
        float seedX = aiming ? bestU * seedW : 0f, seedY = aiming ? bestV * seedH : 0f;

        modes.Update(
            new CursorModeMachine.Input(modifier, (buttons & CursorButtons.Right) != 0, aiming, aimedPanel, mdx, mdy),
            seedW, seedH, seedX, seedY);

        // Publish whether the camera should be starved this frame. Read by the bootstrap's
        // mouse-delta prefix, which runs on the input thread.
        Volatile.Write(ref _captureMouse, modes.CapturesMouse ? 1 : 0);

        if (modes.Mode != _lastMode)
        {
            Log.Line($"Cursor mode {_lastMode} -> {modes.Mode} (ctrl={modifier}, aiming={aiming}, " +
                     $"panel={(aiming ? aimedPanel.ToString() : "none")}, cameraSuppressed={modes.CapturesMouse}, " +
                     $"hookAvailable={HostHooks.MouseDeltaAvailable}).");
            _lastMode = modes.Mode;
        }

        if (modes.CapturesMouse)
        {
            // The latched panel, not whatever the head ray finds now: the whole point of the
            // mode is that the head is free to look elsewhere.
            var locked = modes.LockedPanel;
            if (!PanelRegistry.TryGet(locked, out var lockedEntry)) { modes.ForceRelease(); return default; }

            float w = Math.Max(1, lockedEntry.Width), h = Math.Max(1, lockedEntry.Height);
            return new CursorHit(locked, modes.X / w, modes.Y / h, modes.X, modes.Y,
                                 bestT == double.MaxValue ? 0 : bestT, modes.Mode, buttons);
        }

        if (!aiming) return default;
        CursorOverlay.ReportLiveness(bestEntry.Id);
        return new CursorHit(bestEntry.Id, bestU, bestV, bestU * bestEntry.Width, bestV * bestEntry.Height,
                             bestT, CursorMode.HeadAim, buttons);
    }

    private static long _lastDiag;
    private static CursorMode _lastMode = CursorMode.HeadAim;
    private static bool _calibClickWasDown;

    private const int VK_SHIFT = 0x10, VK_MENU = 0x12; // Shift, Alt
    private static bool _triggerWasDown;

    /// <summary>
    /// Shift+Alt+LeftClick starts calibrating the nearest surface that has no quad.
    /// </summary>
    /// <remarks>
    /// <para>Selection cannot work the way it does for a calibrated panel. A cockpit screen has
    /// no quad — that is the entire reason it needs calibrating — so there is nothing to aim
    /// at and no hit to report. Instead the surface is picked by how close the block sits to
    /// the view ray, which is coarse but only has to choose a surface, not locate one.</para>
    ///
    /// <para>Surfaces are taken in index order, so repeating the gesture walks a cockpit's four
    /// screens one at a time, and the prompt names each. Shift+Alt avoids Ctrl, which now
    /// belongs to decoupled cursor mode.</para>
    /// </remarks>
    private static void CheckCalibrationTrigger(in Vector3D eye, in Vector3 forward)
    {
        bool mods = Down(VK_SHIFT) && Down(VK_MENU);

        // Confirm before cycle: both gestures share the modifiers, and testing the confirm
        // first means a right-click can never be read as another cycle.
        bool confirm = mods && Down(VK_RBUTTON);
        bool confirmRising = confirm && !_confirmWasDown;
        _confirmWasDown = confirm;
        if (confirmRising && Calibration.Selecting) { Calibration.ConfirmSelection(); return; }

        bool combo = mods && Down(VK_LBUTTON);
        bool rising = combo && !_triggerWasDown;
        _triggerWasDown = combo;
        if (!rising || Calibration.Active) return;

        // Which BLOCK is being looked at. Surfaces are then offered from that block, because a
        // cockpit's four screens are all at much the same distance and picking between them
        // geometrically would be a coin toss.
        long bestBlock = 0;
        double bestScore = double.MaxValue;

        foreach (var entry in PanelRegistry.Live)
        {
            if (entry.Quad != null) continue; // already has geometry
            if (!BlockFrame.TryToModelSpace(entry.Block, eye, forward, out var mo, out var md)) continue;

            // Perpendicular distance from the block's model origin to the view ray, plus a
            // little for distance along it. Coarse, but it only has to choose a block.
            double t = -(mo.X * md.X + mo.Y * md.Y + mo.Z * md.Z) / Math.Max(1e-9, md.X * md.X + md.Y * md.Y + md.Z * md.Z);
            if (t <= 0 || t > 12.0) continue; // behind, or too far to be the intended one
            double px = mo.X + md.X * t, py = mo.Y + md.Y * t, pz = mo.Z + md.Z * t;
            double score = Math.Sqrt(px * px + py * py + pz * pz) + t * 0.05;
            if (score < bestScore) { bestScore = score; bestBlock = entry.Id.BlockEntityId; }
        }

        if (bestBlock == 0)
        {
            Log.Line("Calibration: no uncalibrated LCD surface near your view. Look at the block and try again.");
            return;
        }

        var indices = PanelRegistry.Live
            .Where(e => e.Id.BlockEntityId == bestBlock && e.Quad == null)
            .Select(e => e.Id.SurfaceIndex)
            .OrderBy(i => i)
            .ToList();

        Calibration.SelectOrCycle(bestBlock, indices);
    }

    private static bool _confirmWasDown;

    /// <summary>
    /// Feed clicks to <see cref="Calibration"/> while it is running.
    /// </summary>
    /// <remarks>
    /// Rays are handed over in the target block's MODEL space, so the solved quad lands in the
    /// same frame a dummy-derived one would and needs no conversion afterwards. Right-click
    /// cancels — a calibration the player cannot escape is worse than one they have to redo.
    /// </remarks>
    private static void ServiceCalibration(in Vector3D eye, in Vector3 forward)
    {
        if (!PanelRegistry.TryGet(Calibration.Target, out var entry))
        {
            Calibration.Cancel("the panel is no longer registered");
            return;
        }

        var buttons = ReadButtons(out _);
        if ((buttons & CursorButtons.Right) != 0) { Calibration.Cancel("right-click"); _calibClickWasDown = true; return; }

        bool down = (buttons & CursorButtons.Left) != 0;
        bool rising = down && !_calibClickWasDown;
        _calibClickWasDown = down;
        if (!rising) return;

        if (!BlockFrame.TryToModelSpace(entry.Block, eye, forward, out var mo, out var md))
        {
            Log.Line("Calibration: could not transform the click into the block's frame — sample ignored.");
            return;
        }
        Calibration.Sample(mo, md);
    }

    /// <summary>
    /// Say why nothing was hit. Rate-limited, and only while the answer is still "nothing" —
    /// a resolve that finds no panel is otherwise completely silent, which is indistinguishable
    /// from the resolve not running at all.
    /// </summary>
    /// <remarks>
    /// The breakdown is the diagnosis. All-<c>BackFace</c> means the quad's normal points the
    /// wrong way — invert <c>faceSign</c> in the config. All-<c>OffQuad</c> means the plane is
    /// right but the rectangle is misplaced or mis-sized. All-<c>Range</c> means the plane is
    /// behind the viewer, which is the same normal problem seen from the other side.
    /// </remarks>
    private static void ReportNothingFound(int tested, int noQuad, int noFrame, ReadOnlySpan<int> rejects)
    {
        if (!Config.DiagnoseAim) return;
        long now = Environment.TickCount64;
        if (now - _lastDiag < 2000) return;
        _lastDiag = now;

        Log.Line($"No panel under the aim ray: {tested} tested, {noQuad} without a quad, {noFrame} without a block frame, " +
                 $"backface {rejects[(int)ScreenQuadSolver.Reject.BackFace]}, " +
                 $"range {rejects[(int)ScreenQuadSolver.Reject.Range]}, " +
                 $"off-quad {rejects[(int)ScreenQuadSolver.Reject.OffQuad]}, " +
                 $"degenerate {rejects[(int)ScreenQuadSolver.Reject.Degenerate]}.");
    }

    /// <summary>The player's view ray in world space.</summary>
    private static bool TryGetViewRay(out Vector3D eye, out Vector3 forward)
    {
        eye = default;
        forward = default;
        try
        {
            if (_camera == null)
            {
                long now = Environment.TickCount64;
                if (now - _lastCameraSearch < 2000) return false;
                _lastCameraSearch = now;
                if (!TryFindCamera()) return false;
            }

            var camEnt = _camera.RenderCameraEntity;
            if (camEnt == null) return false;

            var wt = EntityTransformFunctions.GetWorldTransform(new DEntityContext(_cameraScene, camEnt.DEntity));
            eye = wt.Position;
            // Engine convention: forward is -Z.
            forward = WorldTransform.TransformDirection(new Vector3(0f, 0f, -1f), wt);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("view ray", e);
            _camera = null;
            return false;
        }
    }

    private static bool TryFindCamera()
    {
        foreach (var session in EngineLocator.Sessions.Keys)
        {
            var scene = session.Scene;
            if (scene == null) continue;
            try
            {
                foreach (var d in scene.EnumerateEntities())
                {
                    var e = Entity.TryGetFromDataEntity(new DEntityContext(scene, d));
                    var cs = e?.TryGet<CameraSystemComponent>();
                    if (cs == null) continue;
                    _camera = cs;
                    _cameraScene = scene;
                    Log.Line($"Camera found (scene '{scene.DebugName}').");
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;
    private const int VK_CONTROL = 0x11; // either Ctrl

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static CursorButtons ReadButtons(out bool modifier)
    {
        modifier = Down(VK_CONTROL);
        var b = CursorButtons.None;
        if (Down(VK_LBUTTON)) b |= CursorButtons.Left;
        if (Down(VK_RBUTTON)) b |= CursorButtons.Right;
        if (Down(VK_MBUTTON)) b |= CursorButtons.Middle;
        return b;
    }

    // ------------------------------------------------------------ mouse delta

    private static int _captureMouse;
    private static float _pendingDx, _pendingDy;
    private static readonly object DeltaGate = new();

    /// <summary>
    /// Called by the bootstrap from the camera's mouse-delta handler, on the input thread.
    /// Returns true to swallow the movement so the view does not turn.
    /// </summary>
    /// <remarks>
    /// Deltas accumulate rather than overwrite: the input thread can deliver several between
    /// two cursor resolves, and taking only the last would drop most of a fast flick.
    /// </remarks>
    public static bool OnCameraMouseDelta(float dx, float dy)
    {
        bool capture = Volatile.Read(ref _captureMouse) != 0;
        if (!capture) return false;
        lock (DeltaGate) { _pendingDx += dx; _pendingDy += dy; }
        return true;
    }

    private static void TakeMouseDelta(out float dx, out float dy)
    {
        lock (DeltaGate)
        {
            dx = _pendingDx; dy = _pendingDy;
            _pendingDx = _pendingDy = 0f;
        }
    }

    /// <summary>Forget any accumulated movement and stop starving the camera.</summary>
    public static void ResetMouseCapture()
    {
        Volatile.Write(ref _captureMouse, 0);
        lock (DeltaGate) { _pendingDx = _pendingDy = 0f; }
    }
}
