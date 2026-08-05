using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Models;
using Keen.VRage.Core.Model;
using Keen.VRage.Library.Mathematics;

namespace LcdCursorApi.Logic;

/// <summary>
/// Derives a screen quad from the block's own <c>LcdPanel</c> model dummy, at runtime.
/// </summary>
/// <remarks>
/// <para><b>This is why there is no mandatory catalog.</b> Block model definitions carry named
/// dummies — transform-plus-scale markers baked into the block — and LCD blocks carry one
/// named <c>LcdPanel</c>. It is reachable live: <c>BlockModelComponent.Definition</c> derives
/// from <c>ModelComponentDefinition</c>, which exposes
/// <c>DummiesByType : ListDictionaryReader&lt;DummyTypeDefinition, ModelDummy&gt;</c>.
/// <c>ModelDummy.GetMatrix()</c> is <c>Matrix.CreateFromTransformScale(Orientation, Position,
/// Scale)</c> — a full TRS for a unit box — so the screen's placement, orientation and size
/// come straight out of it with no model parsing and no bake step.</para>
///
/// <para><b>Evidence it is the screen and not just some volume.</b> The scales track the block
/// and the declared aspect: flat 2.5m is 2.5x2.5 against a declared aspect of 1.0, flat 0.5m
/// is 0.5x0.5 at 1.0, and the corner LCD is 2.5x0.5 — exactly 5.0, matching its declared
/// <c>AspectRatio</c> of 5. That last one is hard to explain as coincidence.</para>
///
/// <para><b>Where it is not yet confirmed.</b> The wide LCD is 1.5x1.25, i.e. 1.2, against a
/// declared 1.33 — so either the visible screen is inset within the dummy box, or the declared
/// aspect is a rounded 4:3 that the geometry does not actually honour. The dummy is also used
/// to build an interaction collider (<c>DummyShapedEntityDetectorComponent</c> takes
/// <c>_halfExtents</c>), and a detection volume normally carries margin over the thing it
/// detects. Both possibilities mean the same thing in practice: the box may be slightly larger
/// than the glass.</para>
///
/// <para>So the dummy is the <i>default</i> source and the catalog is an <i>override</i>. If
/// measurement shows a consistent inset, that is a correction to store per block — which is a
/// far smaller problem than deriving the whole quad by hand, and it is measured once per block
/// type rather than per placement.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class DummyQuadSource
{
    /// <summary>The dummy name the LCD blocks use.</summary>
    private const string LcdDummyName = "LcdPanel";

    /// <summary>
    /// Build a quad from the block's <c>LcdPanel</c> dummy, or null when it has none.
    /// </summary>
    public static ScreenQuad TryBuild(CubeBlockComponent block, out string diagnostic)
    {
        diagnostic = null;
        try
        {
            if (!TryFindLcdDummy(block, out var dummy, out diagnostic)) return null;

            // The dummy is a unit box under GetMatrix(). Its local axes, scaled, are the box's
            // half-extents; the screen is the face at +Z.
            var m = dummy.GetMatrix();
            var right = new Vector3(m.M11, m.M12, m.M13);
            var up = new Vector3(m.M21, m.M22, m.M23);
            var fwd = new Vector3(m.M31, m.M32, m.M33);
            var centre = new Vector3(m.M41, m.M42, m.M43);

            // Unit box spans -0.5..+0.5 before scale, so the axes above are full extents and
            // the face sits half a depth out from the centre.
            var faceCentre = centre + fwd * 0.5f;
            var origin = faceCentre - right * 0.5f - up * 0.5f;

            var n = Normalize(fwd);

            // V runs down the screen: surface coordinates are top-left origin, the dummy's up
            // axis is not.
            var edgeV = -up;
            var quadOrigin = origin + up;

            return new ScreenQuad
            {
                Origin = new[] { quadOrigin.X, quadOrigin.Y, quadOrigin.Z },
                EdgeU = new[] { right.X, right.Y, right.Z },
                EdgeV = new[] { edgeV.X, edgeV.Y, edgeV.Z },
                Normal = new[] { n.X, n.Y, n.Z },
                PlanarResidual = 0f, // a dummy is a box by construction; nothing is being fitted
            };
        }
        catch (Exception e)
        {
            Log.Error("dummy quad", e);
            diagnostic = e.Message;
            return null;
        }
    }

    private static bool TryFindLcdDummy(CubeBlockComponent block, out ModelDummy dummy, out string diagnostic)
    {
        dummy = null;
        diagnostic = null;

        var modelComp = block?.Entity?.TryGet<BlockModelComponent>();
        var def = modelComp?.Definition;
        if (def == null) { diagnostic = "no BlockModelComponent/Definition"; return false; }

        // DummiesByType is declared on the ModelComponentDefinition base, and its
        // ListDictionaryReader is awkward to name from here, so it is walked reflectively.
        // Matching is by dummy NAME rather than by the type GUID: the name is stable and
        // legible, whereas the GUID would be one more magic constant to keep in step.
        var prop = def.GetType().GetProperty("DummiesByType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (prop?.GetValue(def) is not System.Collections.IEnumerable byType)
        {
            diagnostic = "definition exposes no DummiesByType";
            return false;
        }

        foreach (var group in byType)
        {
            if (group == null) continue;
            // Entries are key/value groupings; the values are the ModelDummy instances.
            foreach (var candidate in Flatten(group))
            {
                if (candidate is not ModelDummy md) continue;
                if (!string.Equals(md.Name, LcdDummyName, StringComparison.OrdinalIgnoreCase)) continue;
                dummy = md;
                return true;
            }
        }

        diagnostic = $"no '{LcdDummyName}' dummy on this block";
        return false;
    }

    /// <summary>Yield the ModelDummy values out of a key/value grouping, whatever its exact shape.</summary>
    private static IEnumerable<object> Flatten(object group)
    {
        if (group is ModelDummy) { yield return group; yield break; }

        if (group is System.Collections.IEnumerable seq && group is not string)
        {
            foreach (var item in seq) yield return item;
            yield break;
        }

        var valueProp = group.GetType().GetProperty("Value");
        if (valueProp?.GetValue(group) is System.Collections.IEnumerable values)
            foreach (var item in values) yield return item;
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-6f ? v : new Vector3(v.X / len, v.Y / len, v.Z / len);
    }
}
