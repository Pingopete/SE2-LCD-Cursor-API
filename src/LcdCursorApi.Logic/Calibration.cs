namespace LcdCursorApi.Logic;

/// <summary>
/// Click-through calibration for panels with no catalog entry.
/// </summary>
/// <remarks>
/// <para><b>Status: not yet implemented.</b> Deliberately last: with a baked catalog this is
/// the exception path, for modded panels only, and building it first is how the prototype
/// ended up treating hand-calibration as the normal way to use a panel.</para>
///
/// <para><b>What it must solve.</b> A screen plane cannot be recovered from one standpoint —
/// a near screen and a far one produce the same clicks. The prototype's answer was three
/// targets clicked from two standpoints, then a search over candidate plane depths for the
/// one minimising the fit residual, and that reasoning stands. What changes here is the
/// output: fit the same <see cref="ScreenQuad"/> the catalog stores, in model space, so a
/// calibrated panel and a baked one are indistinguishable downstream and there is only ever
/// one projection path.</para>
///
/// <para>The result is keyed by block subtype, not by placement, so calibrating one panel of
/// a modded type covers every other one the player ever places.</para>
/// </remarks>
internal static class Calibration
{
    public static void Begin(PanelId panel, bool force)
    {
        Log.Line($"Calibration requested for {panel} (force={force}) — not yet implemented.");
    }
}
