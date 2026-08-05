using Keen.VRage.Library.Mathematics;

namespace LcdCursorApi.Logic;

/// <summary>
/// Turns a ray and a baked <see cref="ScreenQuad"/> into surface coordinates.
/// </summary>
/// <remarks>
/// The whole point of the catalog is that this is the only geometry left at runtime: one
/// plane intersection and two dot products, with no per-panel constants, no glass-depth
/// guess and no axis-flip table. If anything here starts needing a tuning knob, the fault
/// is in the bake, not here.
/// </remarks>
internal static class ScreenQuadSolver
{
    /// <summary>Rays are rejected beyond this range. Interaction at 25 m is not a real use case
    /// and the limit keeps a stray ray from claiming a panel across a hangar.</summary>
    public const double MaxAimDistance = 25.0;

    /// <summary>
    /// Intersect a ray, expressed in the same model space the quad was baked in, with the
    /// screen plane and return normalised surface coordinates.
    /// </summary>
    /// <param name="origin">Ray origin, model space.</param>
    /// <param name="direction">Ray direction, model space. Need not be normalised.</param>
    /// <param name="quad">The baked quad for this surface.</param>
    /// <param name="u">Surface coordinate across <see cref="ScreenQuad.EdgeU"/>, 0..1 when on-screen.</param>
    /// <param name="v">Surface coordinate across <see cref="ScreenQuad.EdgeV"/>, 0..1 when on-screen.</param>
    /// <param name="distance">Distance along the ray to the plane, in metres.</param>
    /// <param name="margin">
    /// How far outside the quad a hit is still accepted, in surface units. A small margin
    /// keeps the cursor from dropping out along the extreme edge pixels, where sub-millimetre
    /// disagreement between the baked quad and the rendered mesh is expected.
    /// </param>
    /// <returns>False when the ray is parallel to the screen, hits it from behind, hits beyond
    /// <see cref="MaxAimDistance"/>, or lands outside the quad plus <paramref name="margin"/>.</returns>
    public static bool TryProject(
        in Vector3D origin, in Vector3D direction, ScreenQuad quad,
        out float u, out float v, out double distance, float margin = 0.02f)
    {
        u = v = 0f;
        distance = 0.0;
        if (quad == null) return false;

        var o = new Vector3D(quad.Origin[0], quad.Origin[1], quad.Origin[2]);
        var eu = new Vector3D(quad.EdgeU[0], quad.EdgeU[1], quad.EdgeU[2]);
        var ev = new Vector3D(quad.EdgeV[0], quad.EdgeV[1], quad.EdgeV[2]);
        var n = new Vector3D(quad.Normal[0], quad.Normal[1], quad.Normal[2]);

        double denom = Dot(n, direction);
        // Facing away, or edge-on. Edge-on is not merely degenerate arithmetic — a ray in the
        // screen plane has no single intersection, so there is no answer to return.
        if (denom >= -1e-9) return false;

        double t = Dot(n, Sub(o, origin)) / denom;
        if (t <= 1e-4 || t > MaxAimDistance) return false;

        var hit = new Vector3D(
            origin.X + direction.X * t,
            origin.Y + direction.Y * t,
            origin.Z + direction.Z * t);
        var d = Sub(hit, o);

        double uuLen2 = Dot(eu, eu), vvLen2 = Dot(ev, ev);
        if (uuLen2 <= 1e-12 || vvLen2 <= 1e-12) return false; // degenerate bake

        double du = Dot(d, eu) / uuLen2;
        double dv = Dot(d, ev) / vvLen2;
        if (du < -margin || du > 1 + margin || dv < -margin || dv > 1 + margin) return false;

        u = (float)Math.Clamp(du, 0.0, 1.0);
        v = (float)Math.Clamp(dv, 0.0, 1.0);
        distance = t;
        return true;
    }

    private static double Dot(in Vector3D a, in Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vector3D Sub(in Vector3D a, in Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
