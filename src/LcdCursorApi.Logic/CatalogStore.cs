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
            var dir = Path.GetDirectoryName(typeof(CatalogStore).Assembly.Location) ?? ".";
            var path = Path.Combine(dir, FileName);
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

    public static bool TryGet(Guid blockGuid, int surfaceIndex, out CatalogSurface surface)
    {
        surface = null;
        if (!ByGuid.TryGetValue(blockGuid, out var block)) return false;
        foreach (var s in block.Surfaces)
            if (s.SurfaceIndex == surfaceIndex) { surface = s; return true; }
        return false;
    }
}
