using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Library.Mathematics;

namespace LcdCursorApi.Logic;

/// <summary>
/// Transforms a world-space ray into a block's model frame — the frame the catalog quads
/// are baked in.
/// </summary>
/// <remarks>
/// <para>This is the piece that lets one catalog entry serve every placement of a block
/// subtype. Without it a quad could only be expressed relative to the block's grid AABB,
/// which changes with rotation, which is why the GS2 prototype had to calibrate per
/// placement rather than per block type.</para>
///
/// <para><b>The chain</b>, all of it engine-provided:</para>
/// <code>
///   world  --WorldTransform.TransformInv(gridTransform)-->  grid-local metres
///          --RelativeTransform.TransformInv(childTransform)-->  block model space
/// </code>
///
/// <para><b>Where the block transform comes from.</b> <c>CubeBlockComponent</c> holds a
/// <c>ChildTransformComponent</c>, whose <c>ChildTransform</c> is a <see cref="RelativeTransform"/>
/// — position and quaternion, block relative to grid. That is the authoritative source and
/// needs no basis matrix built by hand.</para>
///
/// <para><b>Why the integer cross-check exists.</b> <c>CubeBlockComponent.BlockOrientation</c>
/// is an <c>IntegerOrientation</c> — a Forward/Up pair from the 24 axis-aligned rotations,
/// exact by construction. The quaternion and the integer orientation must agree; if they
/// ever do not, the assumption that <c>ChildTransform</c> is block-relative-to-grid is wrong
/// (a hierarchy with an intermediate node would do it), and a silently wrong frame produces
/// a cursor that is plausibly placed and consistently off. Cheap to check, expensive to miss.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class BlockFrame
{
    /// <summary>
    /// Transform a world ray into <paramref name="block"/>'s model space.
    /// </summary>
    public static bool TryToModelSpace(
        CubeBlockComponent block, in Vector3D worldOrigin, in Vector3 worldDirection,
        out Vector3D modelOrigin, out Vector3D modelDirection)
    {
        modelOrigin = default;
        modelDirection = default;

        var grid = block?.Grid;
        if (grid == null) return false;
        if (!TryGetChildTransform(block, out var child)) return false;

        // World -> grid local. Vector3I.Zero is the grid's own origin cell; the returned
        // transform is the grid entity's world placement.
        var gridTransform = grid.GetWorldTransform(Vector3I.Zero);
        var gridLocalPos = WorldTransform.TransformInv(worldOrigin, gridTransform);
        var gridLocalDir = WorldTransform.TransformDirectionInv(worldDirection, gridTransform);

        // Grid local -> block model. Position is affine, direction is rotation-only:
        // applying the positional inverse to a direction is the classic way to get a
        // cursor that drifts with distance from the grid origin.
        var modelPos = RelativeTransform.TransformInv((Vector3)gridLocalPos, child);
        var modelDir = RelativeTransform.TransformDirectionInv(gridLocalDir, child);

        modelOrigin = new Vector3D(modelPos.X, modelPos.Y, modelPos.Z);
        modelDirection = new Vector3D(modelDir.X, modelDir.Y, modelDir.Z);
        return true;
    }

    private static bool TryGetChildTransform(CubeBlockComponent block, out RelativeTransform transform)
    {
        transform = RelativeTransform.Identity;
        try
        {
            var comp = block.Entity?.TryGet<ChildTransformComponent>();
            if (comp == null) return false;
            transform = comp.ChildTransform;
            return true;
        }
        catch (Exception e)
        {
            Log.Error("block child transform", e);
            return false;
        }
    }

    /// <summary>
    /// Compare the quaternion frame against the block's integer orientation. Returns the
    /// worst axis disagreement, in degrees; anything above a degree or so means the
    /// quaternion is not the block-to-grid transform this code assumes it is.
    /// </summary>
    /// <remarks>Diagnostic only — call it once per block subtype when a catalog entry is
    /// first used, not per frame.</remarks>
    public static float OrientationDisagreementDegrees(CubeBlockComponent block)
    {
        if (!TryGetChildTransform(block, out var child)) return float.NaN;

        var o = block.BlockOrientation;
        var expectF = Base6Directions.GetVector(o.Forward);
        var expectU = Base6Directions.GetVector(o.Up);

        // The engine's own convention: forward is -Z, up is +Y.
        var actualF = RelativeTransform.TransformDirection(new Vector3(0f, 0f, -1f), child);
        var actualU = RelativeTransform.TransformDirection(new Vector3(0f, 1f, 0f), child);

        return Math.Max(AngleDegrees(expectF, actualF), AngleDegrees(expectU, actualU));
    }

    private static float AngleDegrees(Vector3 a, Vector3 b)
    {
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        float la = MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        float lb = MathF.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (la < 1e-6f || lb < 1e-6f) return float.NaN;
        return MathF.Acos(Math.Clamp(dot / (la * lb), -1f, 1f)) * (180f / MathF.PI);
    }
}
