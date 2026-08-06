using Keen.VRage.Library.Mathematics;

namespace LcdCursorApi.Logic;

/// <summary>
/// Recovers a screen quad by having the player click its corners from two standpoints.
/// </summary>
/// <remarks>
/// <para><b>What this is for.</b> Exactly two vanilla block types carry LCD surfaces without an
/// <c>LcdPanel</c> dummy: <c>Cockpit150</c> (4 surfaces) and <c>ControlSeat250</c> (8). Their
/// screens are per-surface mesh parts — <c>CustomDisplay01</c>…<c>08</c> — and nothing on disk
/// carries their placement: no dummy, and the only mention in the block model definition is
/// <c>BuildProgressModels.PartChanges</c> hiding them during construction. At runtime a mesh
/// part exposes only an index range, never bounds. So the geometry has to be measured.</para>
///
/// <para>Twelve surfaces across two block types, measured once and committed as data. Modded
/// panels use the same path.</para>
///
/// <para><b>Two standpoints, and why.</b> A single viewpoint cannot recover depth — a small
/// near screen and a large far one produce identical clicks. Clicking the same corner from two
/// places gives two rays that meet at it.</para>
///
/// <para><b>Triangulation, not a search.</b> The prototype swept 400 candidate plane depths and
/// kept the best-fitting one. That is unnecessary: two rays to the <i>same</i> corner determine
/// that corner directly as the closest point between two skew lines, in closed form. Three
/// corners give the quad outright — origin, both edges, and a normal — with no residual to
/// minimise and no tuning constant anywhere.</para>
///
/// <para><b>Captured in model space</b>, via <see cref="BlockFrame"/>, so the result is a
/// <see cref="ScreenQuad"/> identical in kind to a dummy-derived one. That makes it per block
/// <i>type</i> rather than per placement — calibrate one cockpit and every cockpit works — and
/// leaves exactly one projection path downstream.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class Calibration
{
    /// <summary>Which corner the player is being asked for, in order.</summary>
    private static readonly (string Name, float U, float V)[] Corners =
    {
        ("TOP-LEFT", 0f, 0f),
        ("TOP-RIGHT", 1f, 0f),
        ("BOTTOM-LEFT", 0f, 1f),
    };

    private const int SamplesNeeded = 6; // 3 corners x 2 standpoints

    /// <summary>
    /// Choosing which surface to measure, before any corner is clicked.
    /// </summary>
    /// <remarks>
    /// This phase exists because the target cannot be aimed at — having no quad is the whole
    /// reason it needs calibrating — so picking "the surface nearest the view ray" would be
    /// close to arbitrary among a cockpit's four screens, and the player would have no way to
    /// know which one had been chosen. Instead every surface of the block is made to render
    /// and the selected one draws a bright frame, so the choice is visible on the glass. The
    /// player cycles until the right screen lights up. No index needs to be known.
    /// </remarks>
    public static bool Selecting { get; private set; }

    /// <summary>The surface currently highlighted for selection. Only meaningful while <see cref="Selecting"/>.</summary>
    public static PanelId Candidate { get; private set; }

    public static bool Active { get; private set; }
    public static PanelId Target { get; private set; }
    public static int Step { get; private set; }

    /// <summary>True while either phase is running, so the caller can suspend normal cursor work.</summary>
    public static bool Busy => Selecting || Active;

    private static long _blockEntityId;

    /// <summary>
    /// Begin choosing a surface on the block nearest the view ray, or advance to the next one.
    /// </summary>
    public static void SelectOrCycle(long blockEntityId, List<int> surfaceIndices)
    {
        if (surfaceIndices.Count == 0) return;

        if (!Selecting || _blockEntityId != blockEntityId)
        {
            Selecting = true;
            Active = false;
            _blockEntityId = blockEntityId;
            Candidate = new PanelId(blockEntityId, surfaceIndices[0]);
            Log.Line($"Calibration: selecting a surface on block {blockEntityId}. The highlighted screen is the one " +
                     "that will be measured — Shift+Alt+LeftClick cycles, Shift+Alt+RightClick confirms, " +
                     "right-click cancels.");
            UpdateSelectPrompt(surfaceIndices);
            return;
        }

        int at = surfaceIndices.IndexOf(Candidate.SurfaceIndex);
        int next = surfaceIndices[(at + 1) % surfaceIndices.Count];
        Candidate = new PanelId(blockEntityId, next);
        UpdateSelectPrompt(surfaceIndices);
    }

    private static void UpdateSelectPrompt(List<int> surfaceIndices)
    {
        int at = surfaceIndices.IndexOf(Candidate.SurfaceIndex) + 1;
        Prompt = $"Calibrate: surface {Candidate.SurfaceIndex} highlighted ({at} of {surfaceIndices.Count}) — " +
                 "Shift+Alt+LClick to cycle, Shift+Alt+RClick to start";
        Log.Line(Prompt);
    }

    /// <summary>Lock in the highlighted surface and start clicking corners.</summary>
    public static void ConfirmSelection()
    {
        if (!Selecting) return;
        Selecting = false;
        Begin(Candidate, force: true);
    }

    /// <summary>The prompt to show the player right now, or null when not calibrating.</summary>
    public static string Prompt { get; private set; }

    private static Guid _blockGuid;
    private static string _blockName;
    private static readonly Vector3D[] RayOrigin = new Vector3D[SamplesNeeded];
    private static readonly Vector3D[] RayDir = new Vector3D[SamplesNeeded];

    public static void Begin(PanelId panel, bool force)
    {
        if (!PanelRegistry.TryGet(panel, out var entry))
        {
            Log.Line($"Calibration: panel {panel} is not registered.");
            return;
        }
        if (entry.Quad != null && !force)
        {
            Log.Line($"Calibration: panel {panel} already has a quad. Pass force to override it.");
            return;
        }

        Active = true;
        Target = panel;
        Step = 0;
        _blockGuid = entry.BlockGuid;
        _blockName = entry.BlockName;
        UpdatePrompt();
        Log.Line($"Calibration START for {panel} on '{_blockName}'. " +
                 "Click the 3 corners, MOVE SIDEWAYS a metre or two, then click the same 3 again.");
    }

    public static void Cancel(string why)
    {
        if (!Busy) return;
        Active = false;
        Selecting = false;
        Prompt = null;
        Log.Line($"Calibration cancelled: {why}");
    }

    /// <summary>
    /// Record one click. The ray must already be in the block's model space.
    /// </summary>
    public static void Sample(in Vector3D modelOrigin, in Vector3D modelDir)
    {
        if (!Active || Step >= SamplesNeeded) return;

        RayOrigin[Step] = modelOrigin;
        RayDir[Step] = Normalize(modelDir);
        Step++;

        if (Step == 3)
            Log.Line("Calibration: first pass done — MOVE SIDEWAYS a metre or two, then click the same 3 corners.");

        if (Step >= SamplesNeeded) Finish();
        else UpdatePrompt();
    }

    private static void UpdatePrompt()
    {
        var c = Corners[Step % 3];
        int pass = Step / 3 + 1;
        Prompt = $"Calibrate: click the {c.Name} corner  (pass {pass} of 2)";
    }

    private static void Finish()
    {
        Active = false;
        Prompt = null;

        try
        {
            // Corner i is seen from sample i (pass 1) and sample i+3 (pass 2).
            var corner = new Vector3D[3];
            double worstGap = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!ClosestPoint(RayOrigin[i], RayDir[i], RayOrigin[i + 3], RayDir[i + 3],
                                  out corner[i], out double gap))
                {
                    Log.Line($"Calibration FAILED: the two rays for the {Corners[i].Name} corner are parallel — " +
                             "the second standpoint was not far enough to the side.");
                    return;
                }
                worstGap = Math.Max(worstGap, gap);
            }

            // The gap between the two rays at their closest approach is the honest error bar:
            // it is how far apart the two sightings of one corner actually were. Reporting it
            // beats reporting a residual nobody can interpret.
            var origin = corner[0];
            var edgeU = Sub(corner[1], corner[0]);
            var edgeV = Sub(corner[2], corner[0]);

            double lenU = Length(edgeU), lenV = Length(edgeV);
            if (lenU < 1e-3 || lenV < 1e-3)
            {
                Log.Line($"Calibration FAILED: degenerate quad ({lenU:F3}m x {lenV:F3}m). Clicks were probably too close together.");
                return;
            }

            var normal = Normalize(Cross(edgeU, edgeV));

            // Face the viewer: the surface must be hit from the front or every ray is
            // discarded as a backface. Standpoint 1's ray runs INTO the screen, so the
            // outward normal opposes it.
            if (Dot(normal, RayDir[0]) > 0) normal = new Vector3D(-normal.X, -normal.Y, -normal.Z);

            var quad = new ScreenQuad
            {
                Origin = new[] { (float)origin.X, (float)origin.Y, (float)origin.Z },
                EdgeU = new[] { (float)edgeU.X, (float)edgeU.Y, (float)edgeU.Z },
                EdgeV = new[] { (float)edgeV.X, (float)edgeV.Y, (float)edgeV.Z },
                Normal = new[] { (float)normal.X, (float)normal.Y, (float)normal.Z },
                PlanarResidual = (float)worstGap,
            };

            double aspect = lenU / lenV;
            Log.Line($"Calibration DONE for '{_blockName}' surface {Target.SurfaceIndex}: " +
                     $"{lenU:F3}m x {lenV:F3}m (aspect {aspect:F2}), corner agreement {worstGap * 100:F1} cm.");
            if (worstGap > 0.05)
                Log.Line("Calibration WARNING: corner agreement is poor (>5 cm). Redo it with a bigger sideways " +
                         "step and more careful clicks — the two sightings of a corner should meet.");

            CatalogStore.Store(_blockGuid, _blockName, Target.SurfaceIndex, quad);
            PanelRegistry.InvalidateQuads();
        }
        catch (Exception e) { Log.Error("calibration finish", e); }
    }

    // ------------------------------------------------------------- geometry

    /// <summary>
    /// Closest point between two skew rays, and how far apart they pass.
    /// </summary>
    /// <remarks>
    /// Standard two-line least squares. Returns the midpoint of the shortest connecting
    /// segment, which is the maximum-likelihood corner given two noisy sightings.
    /// </remarks>
    private static bool ClosestPoint(in Vector3D o1, in Vector3D d1, in Vector3D o2, in Vector3D d2,
                                     out Vector3D point, out double gap)
    {
        point = default;
        gap = 0;

        var w = Sub(o1, o2);
        double a = Dot(d1, d1), b = Dot(d1, d2), c = Dot(d2, d2);
        double d = Dot(d1, w), e = Dot(d2, w);
        double denom = a * c - b * b;

        // Near-parallel rays: the intersection is unstable and the depth is unrecovered,
        // which is precisely the case the second standpoint exists to avoid.
        if (Math.Abs(denom) < 1e-6) return false;

        double t1 = (b * e - c * d) / denom;
        double t2 = (a * e - b * d) / denom;

        var p1 = new Vector3D(o1.X + d1.X * t1, o1.Y + d1.Y * t1, o1.Z + d1.Z * t1);
        var p2 = new Vector3D(o2.X + d2.X * t2, o2.Y + d2.Y * t2, o2.Z + d2.Z * t2);

        gap = Length(Sub(p1, p2));
        point = new Vector3D((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5, (p1.Z + p2.Z) * 0.5);
        return true;
    }

    private static double Dot(in Vector3D a, in Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vector3D Sub(in Vector3D a, in Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3D Cross(in Vector3D a, in Vector3D b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Length(in Vector3D v) => Math.Sqrt(Dot(v, v));
    private static Vector3D Normalize(in Vector3D v)
    {
        double l = Length(v);
        return l < 1e-12 ? v : new Vector3D(v.X / l, v.Y / l, v.Z / l);
    }
}
