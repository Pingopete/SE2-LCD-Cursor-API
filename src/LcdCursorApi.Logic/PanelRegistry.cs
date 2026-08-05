using System.Collections.Concurrent;
using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Discovers LCD surfaces and keeps <see cref="PanelInfo"/> for each one.
/// </summary>
/// <remarks>
/// <para><b>Status: engine-facing, not yet verified in game.</b> The shape below is what the
/// assembly evidence supports, but none of it has been run. Treat the reflection paths as
/// hypotheses until the log says otherwise.</para>
///
/// <para><b>Discovery strategy.</b> The bootstrap patches the <c>LcdPanelSurfaceContext</c>
/// constructor, which the engine runs once per surface as a panel comes into range. That is
/// cheaper and more timely than walking every grid looking for LCD blocks, and it hands over
/// the <c>LcdPanelSurface</c> definition — mesh part name, resolution, aspect — directly.</para>
///
/// <para><b>The open question.</b> A catalog entry is in the block's <i>model</i> frame, so
/// resolving a hit needs the block's model-to-grid transform. <c>CubeBlockComponent</c>
/// exposes <c>AABB</c> (grid cells) and the grid exposes <c>GetWorldTransform</c>, both of
/// which the GS2 prototype uses — but an AABB is not an orientation, and the prototype
/// sidesteps this by calibrating per placement instead of per block type. The block's
/// orientation within the grid is the one piece still to be located before the catalog can
/// pay off. Until it is, only blocks in a known orientation will resolve correctly, which
/// is exactly the sort of thing that looks like it works until someone rotates a panel.</para>
/// </remarks>
internal static class PanelRegistry
{
    private static ConcurrentDictionary<PanelId, PanelInfo> _panels;

    public static void Attach(ConcurrentDictionary<PanelId, PanelInfo> panels)
    {
        _panels = panels;
        HostBridge.LcdSurfaceDefHook = OnSurfaceConstructed;
        Log.Line("PanelRegistry attached to the surface-construction hook.");
    }

    public static void Detach()
    {
        HostBridge.LcdSurfaceDefHook = null;
        _panels = null;
    }

    /// <summary>
    /// Called with the <c>LcdPanelSurface</c> definition as the engine builds a surface context.
    /// </summary>
    private static void OnSurfaceConstructed(object surfaceDefinition)
    {
        var panels = _panels;
        if (panels == null || surfaceDefinition == null) return;

        try
        {
            // LcdPanelSurface is a plain field-bearing type: MeshPartName, Resolution,
            // AspectRatio. Read by reflection because the logic assembly deliberately does
            // not hard-reference Game2.Simulation types it only needs to peek at.
            var t = surfaceDefinition.GetType();
            var resolution = t.GetField("Resolution")?.GetValue(surfaceDefinition);
            var aspect = t.GetField("AspectRatio")?.GetValue(surfaceDefinition);
            var meshPart = t.GetField("MeshPartName")?.GetValue(surfaceDefinition);

            if (resolution == null) return;
            var rt = resolution.GetType();
            int w = (int)(rt.GetField("X")?.GetValue(resolution) ?? 0);
            int h = (int)(rt.GetField("Y")?.GetValue(resolution) ?? 0);

            // The definition alone cannot say which block instance this surface belongs to;
            // the owning entity comes from the context, not the definition. Wiring that up
            // is the next step and is why nothing is inserted into `panels` yet.
            Log.Line($"Surface seen: mesh part '{meshPart}', {w}x{h}, aspect {aspect}.");
        }
        catch (Exception e) { Log.Error("surface hook", e); }
    }
}
