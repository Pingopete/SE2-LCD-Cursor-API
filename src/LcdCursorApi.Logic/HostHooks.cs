using System.Reflection;
using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Binds to bootstrap hooks that may not exist in the bootstrap currently in memory.
/// </summary>
/// <remarks>
/// The bootstrap loads once at game start and never hot-reloads. Referencing a newly-added
/// bootstrap member directly from the logic throws <c>MissingFieldException</c> against the
/// bootstrap actually running, and — because it throws on every tick — takes the whole plugin
/// down until the game restarts. That happened once already.
///
/// So anything added to <c>HostBridge</c> after the first release is reached reflectively:
/// an older bootstrap simply has no such field, the bind is skipped, and the feature is absent
/// rather than fatal. The log says which, so "the new feature does nothing" is never a mystery.
/// </remarks>
internal static class HostHooks
{
    private static bool _bound;

    /// <summary>True when the running bootstrap can suppress camera look.</summary>
    public static bool MouseDeltaAvailable { get; private set; }

    public static void Bind(Func<float, float, bool> onMouseDelta)
    {
        if (_bound) return;
        _bound = true;

        try
        {
            var f = typeof(HostBridge).GetField("MouseDeltaHook", BindingFlags.Public | BindingFlags.Static);
            if (f == null)
            {
                Log.Line("HOOKS: this bootstrap has no MouseDeltaHook — decoupled cursor mode is unavailable " +
                         "until the game is restarted on the current build.");
                return;
            }
            f.SetValue(null, onMouseDelta);
            MouseDeltaAvailable = true;
            Log.Line("HOOKS: camera mouse-delta hook bound — decoupled cursor mode is available.");
        }
        catch (Exception e) { Log.Error("bind host hooks", e); }
    }

    public static void Unbind()
    {
        try
        {
            typeof(HostBridge).GetField("MouseDeltaHook", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, null);
        }
        catch { }
        _bound = false;
        MouseDeltaAvailable = false;
    }
}
