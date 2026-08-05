using System.Collections.Concurrent;
using System.Reflection;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Lcd;
using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Discovers LCD surfaces and keeps a live record of each one, with its catalog quad.
/// </summary>
/// <remarks>
/// <para><b>Discovery rides the renderer.</b> The bootstrap patches
/// <c>LcdPanelSurfaceRenderComponent.TickFsrMask</c>, which the engine runs per frame for every
/// panel it is drawing. That component holds both halves of what a panel is —
/// <c>_lcdBlock</c> (the <see cref="LcdMultiPanelComponent"/>) and <c>_surfaces</c> (the per-surface
/// contexts carrying index, resolution and mesh part) — so one hook yields the whole picture
/// with no grid walk, and a panel appears exactly when it becomes drawable.</para>
///
/// <para>Registration is idempotent and cheap on the repeat path: the per-frame case is one
/// dictionary probe. Everything expensive — reflection, catalog lookup, orientation
/// cross-check — happens once per block.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class PanelRegistry
{
    private static ConcurrentDictionary<PanelId, PanelInfo> _panels;

    /// <summary>Per-panel resolve state, kept beside the public <see cref="PanelInfo"/>.</summary>
    internal sealed class PanelEntry
    {
        public PanelId Id;
        public CubeBlockComponent Block;
        public ScreenQuad Quad;
        public int Width, Height;
    }

    private static readonly ConcurrentDictionary<PanelId, PanelEntry> Entries = new();

    /// <summary>Blocks already examined, so the expensive path runs once each.</summary>
    private static readonly ConcurrentDictionary<long, byte> SeenBlocks = new();

    public static ICollection<PanelEntry> Live => Entries.Values;

    public static bool TryGet(PanelId id, out PanelEntry entry) => Entries.TryGetValue(id, out entry);

    public static void Attach(ConcurrentDictionary<PanelId, PanelInfo> panels) => _panels = panels;

    public static void Detach()
    {
        _panels = null;
        Entries.Clear();
        SeenBlocks.Clear();
    }

    // Private fields on the render component, resolved once. Reflection is unavoidable here
    // (they are private), but it need not be repeated per frame.
    private static FieldInfo _fLcdBlock, _fSurfaces;
    private static bool _fieldsResolved;
    private static int _fieldWarnings;

    /// <summary>
    /// Called from the render tick with an <c>LcdPanelSurfaceRenderComponent</c>. Registers the
    /// block's surfaces the first time it is seen and does nothing thereafter.
    /// </summary>
    public static void Observe(object renderComponent)
    {
        var panels = _panels;
        if (panels == null || renderComponent == null) return;

        try
        {
            if (!_fieldsResolved)
            {
                var t = renderComponent.GetType();
                const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _fLcdBlock = t.GetField("_lcdBlock", any);
                _fSurfaces = t.GetField("_surfaces", any);
                _fieldsResolved = true;
                Log.Line($"Render component fields: _lcdBlock={(_fLcdBlock != null ? "ok" : "MISSING")}, " +
                         $"_surfaces={(_fSurfaces != null ? "ok" : "MISSING")}.");
            }
            if (_fLcdBlock == null || _fSurfaces == null) return;

            if (_fLcdBlock.GetValue(renderComponent) is not LcdMultiPanelComponent lcd) return;
            if (_fSurfaces.GetValue(renderComponent) is not Array surfaces || surfaces.Length == 0) return;

            var block = lcd.Entity?.TryGet<CubeBlockComponent>();
            if (block == null) return;

            long entityId = EntityIdOf(lcd.Entity);
            if (!SeenBlocks.TryAdd(entityId, 0)) return; // already registered; nothing to do

            Register(panels, block, lcd, surfaces, entityId);
        }
        catch (Exception e)
        {
            if (_fieldWarnings++ < 5) Log.Error("render tick hook", e);
        }
    }

    private static void Register(ConcurrentDictionary<PanelId, PanelInfo> panels,
                                 CubeBlockComponent block, LcdMultiPanelComponent lcd,
                                 Array surfaces, long entityId)
    {
        var def = block.Definition;
        Guid blockGuid = def?.Guid ?? Guid.Empty;
        string debugName = def?.DebugName ?? "?";

        // One-off sanity check that the frame we resolve rays in is the frame the catalog was
        // baked in. Logged per block subtype rather than per block: it is a property of the
        // engine's hierarchy, not of any one placement.
        if (WarnedGuids.TryAdd(blockGuid, 0))
        {
            float disagree = BlockFrame.OrientationDisagreementDegrees(block);
            if (float.IsNaN(disagree))
                Log.Line($"'{debugName}': no child transform — this block cannot be resolved.");
            else if (disagree > 1.0f)
                Log.Line($"'{debugName}': WARNING child transform disagrees with BlockOrientation by {disagree:F1}deg. " +
                         "The quaternion is not block-to-grid; cursor positions on this block will be wrong.");
        }

        int registered = 0, uncatalogued = 0;
        foreach (var s in surfaces)
        {
            if (s is not LcdPanelSurfaceContext ctx) continue;

            // LcdPanelSurface is a struct, so this is a copy — taken once rather than
            // re-fetched per field.
            var surfaceDef = ctx.Definition;

            int index = ctx.SurfaceIndex;
            var id = new PanelId(entityId, index);
            int w = surfaceDef.Resolution.X, h = surfaceDef.Resolution.Y;

            bool fromCatalog = CatalogStore.TryGet(blockGuid, index, out var catalogSurface);
            if (!fromCatalog) uncatalogued++;

            panels[id] = new PanelInfo(id, w, h, surfaceDef.AspectRatio, blockGuid, debugName, fromCatalog);
            Entries[id] = new PanelEntry
            {
                Id = id,
                Block = block,
                Quad = catalogSurface?.Quad,
                Width = w,
                Height = h,
            };
            registered++;
        }

        if (registered > 0)
            Log.Line($"Registered '{debugName}' ({entityId}): {registered} surface(s)"
                   + (uncatalogued > 0 ? $", {uncatalogued} with no catalog entry (needs calibration)." : "."));
    }

    private static readonly ConcurrentDictionary<Guid, byte> WarnedGuids = new();

    /// <summary>
    /// The block's entity id, or a stable per-object substitute.
    /// </summary>
    /// <remarks>
    /// Property first, then field: the engine's types use both, and a wrong guess reads as
    /// "no such member" rather than as an error. The fallback is the object's identity hash
    /// and emphatically not <c>DebugName</c> — that is the composition name, shared by every
    /// block of a model, so falling back to it would make two panels of the same model
    /// collide on one key.
    /// </remarks>
    private static long EntityIdOf(object entity)
    {
        if (entity == null) return 0;
        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = entity.GetType();
            object v = t.GetProperty("EntityId", any)?.GetValue(entity)
                    ?? t.GetField("EntityId", any)?.GetValue(entity);
            if (v != null)
            {
                if (v is long l) return l;
                if (v is ulong ul) return unchecked((long)ul);
                if (long.TryParse(v.ToString(), out var parsed)) return parsed;
                return v.GetHashCode();
            }
        }
        catch { }
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entity);
    }
}
