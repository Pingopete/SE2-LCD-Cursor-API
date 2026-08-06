namespace LcdCursorApi.Logic;

/// <summary>
/// Loads the baked screen-geometry catalog and answers lookups by block subtype.
/// </summary>
/// <remarks>
/// The catalog is data, loaded once. If it is missing or stale the API still runs — every
/// panel simply reports <see cref="PanelInfo.FromCatalog"/> false and falls through to
/// calibration — so a bad catalog degrades the experience rather than breaking the mod.
/// </remarks>
internal static class CatalogStore
{
    private const string FileName = "lcd-catalog.json";

    private static readonly Dictionary<Guid, CatalogBlock> ByGuid = new();
    private static bool _loaded;

    public static int BlockCount => ByGuid.Count;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var path = Paths.In(FileName);
            if (!File.Exists(path))
            {
                Log.Line($"No catalog at {path} — every panel will need calibration until one is baked.");
                return;
            }

            var catalog = LcdCatalog.FromJson(File.ReadAllText(path));
            if (catalog == null) { Log.Line("Catalog failed to parse — ignoring it."); return; }

            if (catalog.Version != LcdCatalog.CurrentVersion)
            {
                Log.Line($"Catalog version {catalog.Version} != expected {LcdCatalog.CurrentVersion} — ignoring it. Re-bake.");
                return;
            }

            int skipped = 0;
            foreach (var b in catalog.Blocks)
            {
                if (Guid.TryParse(b.BlockGuid, out var g)) ByGuid[g] = b;
                else skipped++;
            }

            Log.Line($"Catalog loaded: {ByGuid.Count} block(s), baked from game build '{catalog.GameBuild}'."
                   + (skipped > 0 ? $" {skipped} entr(ies) skipped: unparseable BlockGuid." : ""));
        }
        catch (Exception e) { Log.Error("catalog load", e); }
    }

    /// <summary>
    /// Record a measured quad and write the catalog back to disk.
    /// </summary>
    /// <remarks>
    /// Saved immediately rather than on shutdown. A calibration is several minutes of careful
    /// clicking, and losing it to a crash — in a game with this one's crash history — would be
    /// the kind of loss that stops people using the feature at all.
    /// </remarks>
    public static void Store(Guid blockGuid, string debugName, int surfaceIndex, ScreenQuad quad)
    {
        try
        {
            if (!ByGuid.TryGetValue(blockGuid, out var block))
            {
                block = new CatalogBlock { BlockGuid = blockGuid.ToString(), DebugName = debugName };
                ByGuid[blockGuid] = block;
            }

            block.Surfaces.RemoveAll(s => s.SurfaceIndex == surfaceIndex);
            block.Surfaces.Add(new CatalogSurface { SurfaceIndex = surfaceIndex, Quad = quad });
            block.Surfaces.Sort((a, b) => a.SurfaceIndex.CompareTo(b.SurfaceIndex));

            Save();
            Log.Line($"Catalog: stored surface {surfaceIndex} for '{debugName}' ({ByGuid.Count} block(s) now catalogued).");
        }
        catch (Exception e) { Log.Error("catalog store", e); }
    }

    private static void Save()
    {
        var path = Paths.In(FileName);
        var catalog = new LcdCatalog { GameBuild = "measured in-game", Blocks = ByGuid.Values.ToList() };

        // Written beside the target then moved into place: a half-written catalog would be
        // silently ignored at next load as a parse failure, quietly discarding every
        // calibration in it.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, catalog.ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static bool TryGet(Guid blockGuid, int surfaceIndex, out CatalogSurface surface)
    {
        surface = null;
        if (!ByGuid.TryGetValue(blockGuid, out var block)) return false;
        foreach (var s in block.Surfaces)
            if (s.SurfaceIndex == surfaceIndex) { surface = s; return true; }
        return false;
    }
}
