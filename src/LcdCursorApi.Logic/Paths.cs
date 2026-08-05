using LcdCursorApi.Host;

namespace LcdCursorApi.Logic;

/// <summary>
/// Where the log, the config and the catalog live.
/// </summary>
/// <remarks>
/// <para>Derived from the <b>bootstrap</b> assembly, not this one. The logic dll is loaded with
/// <c>LoadFromStream</c>, and <c>Assembly.Location</c> is the empty string for a stream-loaded
/// assembly — so deriving a directory from it silently yields a relative path and files land
/// in the process working directory (the game install root), where nobody looks. The bootstrap
/// is loaded from a real file path, so its Location is real.</para>
///
/// <para><b>Why not a field on <c>HostBridge</c>.</b> That was the first attempt, and it would
/// have broken every hot reload against an already-running game: the bootstrap loads once at
/// game start and never reloads, so freshly-built logic referencing a newly-added bootstrap
/// field throws <c>MissingFieldException</c> against the bootstrap actually in memory. Reading
/// a property that has always existed keeps new logic compatible with an old bootstrap, which
/// is the whole point of the hot-reload split.</para>
/// </remarks>
internal static class Paths
{
    private static string _deployDir;

    public static string DeployDir
    {
        get
        {
            if (_deployDir != null) return _deployDir;
            try
            {
                var loc = typeof(HostBridge).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    var dir = System.IO.Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) return _deployDir = dir;
                }
            }
            catch { }
            return _deployDir = ".";
        }
    }

    public static string In(string fileName) => System.IO.Path.Combine(DeployDir, fileName);
}
