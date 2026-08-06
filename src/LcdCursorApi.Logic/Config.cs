using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Live-reloaded knobs from <c>lcdcursor.cfg</c> in the deploy directory.
/// </summary>
/// <remarks>
/// <para>These exist because several facts about the screen geometry are not derivable from
/// the assemblies and have to be measured on glass — which face of the dummy box the screen
/// is, and how its UV axes are handed. Rebuilding the mod to try each combination would be
/// three restarts of a test session; a text file polled every couple of seconds settles it in
/// one.</para>
///
/// <para><b>Reloading must invalidate what it feeds.</b> Quads are built once, at panel
/// registration, so a changed knob that only affects future registrations would appear to do
/// nothing — the classic shape of bug in this codebase, where state can only change while the
/// thing it controls is already working. <see cref="Generation"/> is bumped on every observed
/// change and the panel registry rebuilds against it.</para>
///
/// <para>Polled from the housekeeping tick, not the render tick, so it keeps working even when
/// nothing is resolving — which is exactly the situation the knobs are there to fix.</para>
/// </remarks>
internal static class Config
{
    private const string FileName = "lcdcursor.cfg";

    /// <summary>Bumped whenever any value changes. Consumers of the config compare against it.</summary>
    public static int Generation { get; private set; } = 1;

    /// <summary>
    /// Which face of the dummy box carries the screen, along the dummy's local Z.
    /// -1 is the engine's forward convention (forward is -Z); +1 is the opposite face.
    /// </summary>
    public static float FaceSign = -1f;

    /// <summary>Mirror the horizontal surface axis.</summary>
    public static bool FlipU;

    /// <summary>Mirror the vertical surface axis.</summary>
    public static bool FlipV;

    /// <summary>Swap the two surface axes. For a panel whose screen is rotated 90 degrees.</summary>
    public static bool SwapUv;

    /// <summary>Push the screen plane along its normal, in metres. Positive is toward the viewer.</summary>
    public static float PlaneOffset;

    /// <summary>Draw the built-in crosshair.</summary>
    public static bool ShowCursor = true;

    /// <summary>
    /// Suppress the yellow interaction glow with no consumer asking. Standalone convenience:
    /// a consuming mod would take a refcounted claim instead.
    /// </summary>
    public static bool SuppressHighlight = true;

    /// <summary>Log why the aim resolve found nothing, every couple of seconds.</summary>
    public static bool DiagnoseAim = true;

    /// <summary>
    /// Move a panel onto the engine's custom-render path the first time the cursor lands on
    /// it. That is what gives it a private material and its own render target; panels left on
    /// the shared material can show another panel's cursor.
    /// </summary>
    public static bool ForceCustomRender = true;

    /// <summary>
    /// Solve a screen quad from the block model's own mesh when neither the catalog nor an
    /// <c>LcdPanel</c> dummy supplies one. This is what reaches cockpit and control-seat screens.
    /// </summary>
    public static bool BakeFromModel = true;

    private static long _lastPoll;
    private static DateTime _stamp;
    private static bool _announced;

    public static void Poll()
    {
        long now = Environment.TickCount64;
        if (now - _lastPoll < 2000) return;
        _lastPoll = now;

        try
        {
            var path = Paths.In(FileName);

            if (!File.Exists(path))
            {
                if (!_announced)
                {
                    _announced = true;
                    WriteDefault(path);
                }
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(path);
            if (_announced && stamp == _stamp) return;
            _stamp = stamp;
            _announced = true;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "facesign": FaceSign = ParseFloat(val, FaceSign) >= 0 ? 1f : -1f; break;
                    case "flipu": FlipU = ParseBool(val); break;
                    case "flipv": FlipV = ParseBool(val); break;
                    case "swapuv": SwapUv = ParseBool(val); break;
                    case "planeoffset": PlaneOffset = ParseFloat(val, PlaneOffset); break;
                    case "showcursor": ShowCursor = ParseBool(val); break;
                    case "suppresshighlight": SuppressHighlight = ParseBool(val); break;
                    case "diagnoseaim": DiagnoseAim = ParseBool(val); break;
                    case "forcecustomrender": ForceCustomRender = ParseBool(val); break;
                    case "bakefrommodel": BakeFromModel = ParseBool(val); break;
                }
            }

            Generation++;
            Log.Line($"Config reloaded (gen {Generation}): faceSign={FaceSign:+0;-0} flipU={FlipU} flipV={FlipV} " +
                     $"swapUv={SwapUv} planeOffset={PlaneOffset:F3} showCursor={ShowCursor} " +
                     $"suppressHighlight={SuppressHighlight} diagnoseAim={DiagnoseAim} " +
                     $"forceCustomRender={ForceCustomRender} bakeFromModel={BakeFromModel}");
        }
        catch (Exception e) { Log.Error("config poll", e); }
    }

    private static bool ParseBool(string v)
        => v is "1" or "true" or "True" or "yes" or "on";

    private static float ParseFloat(string v, float fallback)
        => float.TryParse(v, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;

    private static void WriteDefault(string path)
    {
        try
        {
            File.WriteAllText(path, """
                # LCD Cursor API - live knobs. Saved changes apply within ~2s; no restart, no rebuild.
                # Panels re-register when this file changes, so quad-shaping values take effect too.

                # Which face of the block's LcdPanel dummy box carries the screen, along its local Z.
                # -1 follows the engine's forward convention (forward is -Z). Try +1 if no cursor appears.
                faceSign = -1

                # Surface axis handedness. Flip these if the cursor moves the wrong way along an axis.
                flipU = 0
                flipV = 0
                swapUv = 0

                # Nudge the screen plane along its normal, in metres. Positive is toward the viewer.
                planeOffset = 0.0

                # Draw the built-in crosshair.
                showCursor = 1

                # Hide the stock yellow interaction glow. A consuming mod would claim this instead.
                suppressHighlight = 1

                # Log why the aim resolve found nothing, every couple of seconds.
                diagnoseAim = 1

                # Move a panel onto the engine's custom-render path when the cursor lands on it.
                # Panels showing the stock default screen SHARE one runtime material, so a cursor
                # drawn on one appears on all of them; custom render is the engine's own path to a
                # private material and a per-panel render target. Same thing that happens when you
                # switch a panel to text mode by hand. Side effect: such a panel starts showing its
                # own (possibly empty) content instead of the stock default screen.
                forceCustomRender = 1

                # Solve a screen quad from the block model's own mesh when neither the catalog nor
                # an LcdPanel dummy has one. This is what gives cockpit and control-seat screens
                # their geometry with no hand calibration. Results are written to the catalog, so
                # the work happens once per block type and is served from data thereafter.
                bakeFromModel = 1
                """);
            Log.Line($"Wrote default config to {path}.");
        }
        catch (Exception e) { Log.Error("config write", e); }
    }
}
