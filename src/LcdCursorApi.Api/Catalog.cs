using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcdCursorApi;

/// <summary>
/// One screen quad, in the block's own model frame (metres), as baked from the block's
/// mesh part.
/// </summary>
/// <remarks>
/// <para>The quad is stored as an origin plus two edge vectors rather than as a plane
/// depth and a rectangle. That is deliberate: an axis-aligned rect can only describe a
/// screen that is parallel to a block face, which is false for the corner LCD and for
/// every cockpit panel. An origin and two edges describe any parallelogram in any
/// orientation, and the mapping to surface coordinates is then a projection with no
/// special cases:</para>
/// <code>
///   d = hitModelSpace - Origin
///   u = dot(d, EdgeU) / dot(EdgeU, EdgeU)
///   v = dot(d, EdgeV) / dot(EdgeV, EdgeV)
/// </code>
/// <para><b>Frame.</b> Model space, not grid space. A catalog entry is per block *subtype*,
/// so it cannot be expressed relative to the block's grid AABB — that would change with
/// the block's rotation and force a re-bake per placement. The runtime composes
/// model → block → grid → world itself.</para>
/// </remarks>
public sealed class ScreenQuad
{
    /// <summary>Corner of the screen corresponding to surface coordinate (0,0), in model metres.</summary>
    public float[] Origin { get; set; } = new float[3];

    /// <summary>Edge from Origin toward (1,0). Its length is the screen's width in metres.</summary>
    public float[] EdgeU { get; set; } = new float[3];

    /// <summary>Edge from Origin toward (0,1). Its length is the screen's height in metres.</summary>
    public float[] EdgeV { get; set; } = new float[3];

    /// <summary>Outward normal, model space, unit length. Redundant with EdgeU x EdgeV but stored so
    /// the runtime can reject back-face hits without a cross product per ray.</summary>
    public float[] Normal { get; set; } = new float[3];

    /// <summary>
    /// How well the mesh part's vertices fitted a single plane, in metres (max deviation).
    /// A curved or multi-plane screen shows up here rather than silently producing a
    /// plausible-but-wrong mapping.
    /// </summary>
    public float PlanarResidual { get; set; }
}

/// <summary>One surface of one block subtype.</summary>
public sealed class CatalogSurface
{
    public int SurfaceIndex { get; set; }

    /// <summary>The mesh part the engine material-swaps for this surface, from the block's
    /// <c>LcdMultiPanelDefinition</c>. Kept for traceability when a bake looks wrong.</summary>
    public string MeshPartName { get; set; }

    /// <summary>Render target size in pixels, from <c>LcdPanelSurface.Resolution</c>.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    public float AspectRatio { get; set; }

    public ScreenQuad Quad { get; set; }
}

/// <summary>All LCD surfaces on one block subtype.</summary>
public sealed class CatalogBlock
{
    /// <summary>Block definition subtype key, e.g. <c>LCDFlat250</c>.</summary>
    public string Subtype { get; set; }

    /// <summary>Model file the quads were measured from, for traceability across game updates.</summary>
    public string ModelPath { get; set; }

    public List<CatalogSurface> Surfaces { get; set; } = new();
}

/// <summary>
/// The baked screen-geometry catalog. Built once, offline-ish (see the in-game baker),
/// and shipped as data so the runtime never parses a model.
/// </summary>
public sealed class LcdCatalog
{
    /// <summary>Bumped when the schema changes in a way a stale file cannot satisfy.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Game build the bake was taken from. A mismatch is a warning, not an error —
    /// block models change rarely, but when they do the catalog is silently wrong, so it
    /// needs to be visible.</summary>
    public string GameBuild { get; set; }

    public List<CatalogBlock> Blocks { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static LcdCatalog FromJson(string json) => JsonSerializer.Deserialize<LcdCatalog>(json, Options);
}
