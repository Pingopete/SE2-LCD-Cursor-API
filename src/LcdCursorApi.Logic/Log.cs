namespace LcdCursorApi.Logic;

/// <summary>
/// Log sink for the reloadable half.
/// </summary>
/// <remarks>
/// Writes are queued and drained on a background thread rather than written inline. That is
/// not premature caution: a synchronous <c>WriteLine</c> from a render-adjacent thread has
/// been measured in this engine blocking for 110-178 ms, because the log file shares a disk
/// with the game's DirectStorage streaming. A cursor API that ticks every frame is exactly
/// the shape of caller that would reproduce it.
/// </remarks>
internal static class Log
{
    private static readonly System.Collections.Concurrent.BlockingCollection<string> Queue = new(1024);
    private static readonly string Path;
    private static readonly Thread Writer;

    static Log()
    {
        Path = Paths.In("lcdcursor.log");
        Writer = new Thread(Drain) { IsBackground = true, Name = "LcdCursorLog" };
        Writer.Start();
    }

    public static void Line(string msg)
    {
        // Dropping a line under flood is strictly better than blocking the caller.
        Queue.TryAdd($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    public static void Error(string what, Exception e)
        => Line($"ERROR {what}: {e.Message}{Environment.NewLine}{e.StackTrace}");

    private static void Drain()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path, line + Environment.NewLine); }
            catch { }
        }
    }
}
