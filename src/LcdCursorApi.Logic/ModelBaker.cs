using System.Reflection;

namespace LcdCursorApi.Logic;

/// <summary>
/// Computes a screen quad from the block model's actual mesh, so panels without an
/// <c>LcdPanel</c> dummy need no hand calibration.
/// </summary>
/// <remarks>
/// <para><b>Why this is possible after all.</b> A first attempt used <c>ModelImporter</c> and
/// concluded a <c>.vrm</c> could not be read — that is the LEGACY tag-stream reader, and it
/// throws on the VR3B archive these files actually are. The current path is
/// <c>VRageBinaryModelReader</c> over <c>BinaryArchiveReader</c>, which takes a plain
/// <see cref="Stream"/> and unwraps the container itself:</para>
/// <code>
///   ReadLOD(0).ReadVertexData() -> Vertices, TexCoords, TriangleIndices
///   ReadLOD(0).ReadMeshData()   -> Parts[] { PartId, IndicesCount }
/// </code>
/// <para>Walking the parts' index counts gives each part's slice of the index buffer, and that
/// slice's vertices are the screen. Exact geometry rather than a measurement, and it covers
/// modded blocks for free.</para>
///
/// <para><b>The quad comes from the UVs, not from the corners.</b> Picking extreme vertices
/// would need to know which extreme is which, reintroducing exactly the face and handedness
/// guessing that the config knobs exist to resolve for dummies. Instead every vertex carries
/// both a position and a texture coordinate, and the screen's own UV layout is what the engine
/// maps content through — so fitting <c>P = Origin + u·EdgeU + v·EdgeV</c> across all of the
/// part's vertices recovers the mapping the panel actually uses, whatever its orientation,
/// mirroring or rotation. Three unknown vectors, a linear least-squares per component.</para>
///
/// <para><b>In-game only.</b> Outside the game <c>BinaryArchiveReader</c> throws for the
/// <c>SerializationContextServices</c> indexer because the engine's <c>MetadataContext</c> is
/// not bootstrapped — building one over the engine assemblies and calling <c>Activate()</c> is
/// not enough. Inside the game it is already live, so the bake runs here and the result ships
/// as data.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class ModelBaker
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static Type _archiveType, _readerType;
    private static bool _resolved, _unavailable;

    /// <summary>Model files whose part list has been logged, so a miss reports its contents once.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PartsListLogged = new();

    // UPGRADE PATH, recorded for when modded blocks need this: the engine's ContentCache
    // (Keen.VRage.Library.Filesystem.ContentCache.ContentCache) holds
    // _resourceHandleToFileHandle — its own ResourceHandle -> FileHandle map. Resolving the
    // definition's Model handle through the live instance would replace the folder heuristic
    // below entirely and work for content mounted from anywhere, including workshop mods.
    // Needs runtime discovery of the instance; not built while every target block is vanilla.

    /// <summary>
    /// Build the quad for one surface from the block's model, or null if it cannot be read.
    /// </summary>
    public static ScreenQuad TryBake(string blockDebugName, string meshPartName, out string note)
    {
        note = null;
        if (_unavailable) { note = "model reader unavailable"; return null; }

        try
        {
            if (!Resolve(out note)) return null;

            var modelPath = FindModelFile(blockDebugName, out note);
            if (modelPath == null) return null;

            using var fs = File.OpenRead(modelPath);
            using var archive = (IDisposable)_archiveType
                .GetConstructor(new[] { typeof(Stream), typeof(bool) })
                .Invoke(new object[] { fs, false });
            using var reader = (IDisposable)_readerType
                .GetConstructor(new[] { _archiveType })
                .Invoke(new object[] { archive });

            using var lod = (IDisposable)_readerType.GetMethod("ReadLOD")?.Invoke(reader, new object[] { 0 });
            if (lod == null) { note = "no LOD 0"; return null; }

            var lt = lod.GetType();
            var vertexData = lt.GetMethod("ReadVertexData")?.Invoke(lod, null);
            var meshData = lt.GetMethod("ReadMeshData")?.Invoke(lod, null);
            if (vertexData == null || meshData == null) { note = "no vertex/mesh data"; return null; }

            var verts = vertexData.GetType().GetField("Vertices")?.GetValue(vertexData) as Array;
            var uvs = vertexData.GetType().GetField("TexCoords")?.GetValue(vertexData) as Array;
            var tris = vertexData.GetType().GetField("TriangleIndices")?.GetValue(vertexData) as int[];
            if (verts == null || uvs == null || tris == null) { note = "vertex arrays missing"; return null; }

            if (meshData.GetType().GetField("Parts")?.GetValue(meshData) is not Array parts)
            {
                note = "no mesh parts";
                return null;
            }

            // Parts index the triangle buffer consecutively, so a running offset gives each
            // part its own slice.
            int offset = 0;
            var partNames = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var pt = p.GetType();
                int count = (int)(pt.GetField("IndicesCount")?.GetValue(p) ?? 0);
                string id = pt.GetField("PartId")?.GetValue(p)?.ToString() ?? "";
                partNames.Add(id);

                if (Matches(id, meshPartName) && count > 0 && offset + count <= tris.Length)
                    return FitQuad(verts, uvs, tris, offset, count, out note);

                offset += count;
            }

            note = $"mesh part '{meshPartName}' not found in {Path.GetFileName(modelPath)}";

            // Say what IS there, once per model file. The last miss burned a whole round trip
            // on "not found" with no way to tell a wrong file from a renamed part.
            if (PartsListLogged.TryAdd(modelPath, 0))
                Log.Line($"MODEL BAKE: {Path.GetFileName(modelPath)} contains {partNames.Count} part(s): " +
                         string.Join(", ", partNames.Take(30)) + (partNames.Count > 30 ? ", …" : ""));
            return null;
        }
        catch (Exception e)
        {
            note = $"{e.InnerException?.GetType().Name ?? e.GetType().Name}: {e.InnerException?.Message ?? e.Message}";
            return null;
        }
    }

    /// <summary>
    /// Mesh part names in the panel definition sometimes carry a build-stage prefix
    /// (<c>Fracture_16_Hide-LCDScreen_Off</c>) that the model's own part id may not, so both
    /// exact and suffix matches are accepted.
    /// </summary>
    private static bool Matches(string partId, string wanted)
    {
        if (string.Equals(partId, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        int dash = wanted.LastIndexOf('-');
        if (dash >= 0 && string.Equals(partId, wanted[(dash + 1)..], StringComparison.OrdinalIgnoreCase)) return true;
        dash = partId.LastIndexOf('-');
        return dash >= 0 && string.Equals(partId[(dash + 1)..], wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Least-squares fit of <c>P = Origin + u·EdgeU + v·EdgeV</c> over the part's vertices.
    /// </summary>
    private static ScreenQuad FitQuad(Array verts, Array uvs, int[] tris, int offset, int count, out string note)
    {
        note = null;

        // Normal equations for the basis [1, u, v]; the same 3x3 serves all three components.
        double n = 0, su = 0, sv = 0, suu = 0, suv = 0, svv = 0;
        double[] sp = new double[3], spu = new double[3], spv = new double[3];
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;

        var seen = new HashSet<int>();
        for (int i = offset; i < offset + count; i++)
        {
            int vi = tris[i];
            if (!seen.Add(vi)) continue; // each vertex once, not once per triangle that uses it
            if (vi < 0 || vi >= verts.Length || vi >= uvs.Length) continue;

            var vv = verts.GetValue(vi);
            var vt = vv.GetType();
            double px = Convert.ToDouble(vt.GetField("X").GetValue(vv));
            double py = Convert.ToDouble(vt.GetField("Y").GetValue(vv));
            double pz = Convert.ToDouble(vt.GetField("Z").GetValue(vv));

            var uv = uvs.GetValue(vi);
            if (!TryReadUv(uv, out double u, out double v)) continue;

            n++; su += u; sv += v; suu += u * u; suv += u * v; svv += v * v;
            sp[0] += px; sp[1] += py; sp[2] += pz;
            spu[0] += px * u; spu[1] += py * u; spu[2] += pz * u;
            spv[0] += px * v; spv[1] += py * v; spv[2] += pz * v;

            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }

        if (n < 3) { note = $"only {n} usable vertices"; return null; }
        if (maxU - minU < 0.1 || maxV - minV < 0.1)
        {
            // A part whose UVs barely vary is not a screen; fitting it would produce enormous
            // edge vectors from a near-singular system.
            note = $"UV span too small (u {maxU - minU:F3}, v {maxV - minV:F3}) — not a mapped screen";
            return null;
        }

        var origin = new float[3];
        var edgeU = new float[3];
        var edgeV = new float[3];

        for (int c = 0; c < 3; c++)
        {
            if (!Solve3(n, su, sv, suu, suv, svv, sp[c], spu[c], spv[c], out double o, out double a, out double b))
            {
                note = "singular UV fit";
                return null;
            }
            origin[c] = (float)o; edgeU[c] = (float)a; edgeV[c] = (float)b;
        }

        var nx = edgeU[1] * edgeV[2] - edgeU[2] * edgeV[1];
        var ny = edgeU[2] * edgeV[0] - edgeU[0] * edgeV[2];
        var nz = edgeU[0] * edgeV[1] - edgeU[1] * edgeV[0];
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-6f) { note = "degenerate quad"; return null; }

        return new ScreenQuad
        {
            Origin = origin,
            EdgeU = edgeU,
            EdgeV = edgeV,
            Normal = new[] { nx / len, ny / len, nz / len },
            PlanarResidual = 0f,
        };
    }

    /// <summary>Solve the symmetric 3x3 normal system by Cramer's rule.</summary>
    private static bool Solve3(double n, double su, double sv, double suu, double suv, double svv,
                               double sp, double spu, double spv,
                               out double o, out double a, out double b)
    {
        o = a = b = 0;
        double det = n * (suu * svv - suv * suv) - su * (su * svv - suv * sv) + sv * (su * suv - suu * sv);
        if (Math.Abs(det) < 1e-12) return false;

        o = (sp * (suu * svv - suv * suv) - su * (spu * svv - suv * spv) + sv * (spu * suv - suu * spv)) / det;
        a = (n * (spu * svv - suv * spv) - sp * (su * svv - suv * sv) + sv * (su * spv - spu * sv)) / det;
        b = (n * (suu * spv - spu * suv) - su * (su * spv - spu * sv) + sp * (su * suv - suu * sv)) / det;
        return true;
    }

    private static bool TryReadUv(object uv, out double u, out double v)
    {
        u = v = 0;
        if (uv == null) return false;
        var t = uv.GetType();

        // HalfVector2 stores packed halves; prefer its own accessor when there is one.
        var toVec = t.GetMethod("ToVector2", BindingFlags.Public | BindingFlags.Instance);
        object src = toVec != null ? toVec.Invoke(uv, null) : uv;
        if (src == null) return false;

        var st = src.GetType();
        var fx = st.GetField("X"); var fy = st.GetField("Y");
        if (fx == null || fy == null) return false;
        u = Convert.ToDouble(fx.GetValue(src));
        v = Convert.ToDouble(fy.GetValue(src));
        return true;
    }

    // ------------------------------------------------------------- plumbing

    private static bool Resolve(out string note)
    {
        note = null;
        if (_resolved) return !_unavailable;
        _resolved = true;

        _archiveType = Type.GetType("Keen.VRage.Library.Serialization.Binary.BinaryArchiveReader, VRage.Library");
        _readerType = Type.GetType("Keen.VRage.Core.Model.Serialization.Binary.VRageBinaryModelReader, VRage.Core");

        if (_archiveType == null || _readerType == null)
        {
            _unavailable = true;
            note = "BinaryArchiveReader/VRageBinaryModelReader not found";
            Log.Line($"MODEL BAKE: unavailable — {note}.");
            return false;
        }

        Log.Line("MODEL BAKE: binary model reader resolved.");
        return true;
    }

    /// <summary>
    /// Find the block's model file from its definition debug name.
    /// </summary>
    /// <remarks>
    /// A block definition's <c>DebugName</c> is a content-relative path — for example
    /// <c>Vanilla\Blocks\Cockpit\Cockpit\150\Cockpit150_...def</c> — so the model sits under
    /// that same folder. Resolving through the resource system instead would mean turning a
    /// <c>ResourceHandle</c> (a bare 128-bit key) back into a file, which needs the asset
    /// index; the folder is right there and costs nothing.
    ///
    /// Undamaged variants are preferred: a fractured or deformed model has the same part names
    /// but moved geometry, and silently baking from one would give a screen quad that is
    /// subtly wrong everywhere.
    /// </remarks>
    private static string FindModelFile(string blockDebugName, out string note)
    {
        note = null;
        try
        {
            if (string.IsNullOrEmpty(blockDebugName)) { note = "no block debug name"; return null; }

            var rel = blockDebugName.Replace('\\', '/');
            int slash = rel.LastIndexOf('/');
            if (slash < 0) { note = $"debug name is not a path: '{blockDebugName}'"; return null; }
            var folderRel = rel[..slash];

            // "Vanilla/Blocks/..." on disk is "GameData/Vanilla/Content/Blocks/...".
            int firstSlash = folderRel.IndexOf('/');
            if (firstSlash < 0) { note = "unexpected debug name shape"; return null; }
            var contentSet = folderRel[..firstSlash];
            var withinSet = folderRel[(firstSlash + 1)..];

            var root = FindGameDataRoot();
            if (root == null) { note = "GameData root not found"; return null; }

            var folder = Path.Combine(root, contentSet, "Content", withinSet.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(folder)) { note = $"content folder not found: {folder}"; return null; }

            var models = Directory.GetFiles(folder, "*.vrm", SearchOption.AllDirectories);
            if (models.Length == 0) { note = $"no .vrm under {folder}"; return null; }

            // Best pristine-variant score wins; ties go to the shortest path (the main model,
            // not a subpart). This replaced a boolean "is damaged" test that misfired on the
            // cockpit: its good model lives under "Non_Fractured", and the substring
            // "_fractured" MATCHES "non_fractured" — so every candidate looked damaged, the
            // fallback grabbed the first file alphabetically, and that was Deformed. The bake
            // then honestly reported the display parts missing… from the wrong file.
            var pick = models.OrderByDescending(Pristineness).ThenBy(m => m.Length).First();
            return pick;
        }
        catch (Exception e) { note = e.Message; return null; }
    }

    /// <summary>Higher is better: 2 = explicitly non-fractured, 1 = unmarked, 0 = damaged variant.</summary>
    private static int Pristineness(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();

        // Consume the "non fractured" token FIRST, in its spelling variants, so the damage
        // test below cannot match inside it.
        bool pristine = p.Contains("non_fractured") || p.Contains("nonfractured") || p.Contains("non-fractured");
        p = p.Replace("non_fractured", "#").Replace("nonfractured", "#").Replace("non-fractured", "#");

        bool damaged = p.Contains("fractured") || p.Contains("deformed") || p.Contains("damaged");
        return pristine ? 2 : damaged ? 0 : 1;
    }

    private static string _gameDataRoot;

    /// <summary>Locate <c>GameData</c> by walking up from the running game's own assembly.</summary>
    private static string FindGameDataRoot()
    {
        if (_gameDataRoot != null) return _gameDataRoot.Length == 0 ? null : _gameDataRoot;

        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "GameData");
                if (Directory.Exists(candidate)) return _gameDataRoot = candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }

        _gameDataRoot = "";
        return null;
    }
}
