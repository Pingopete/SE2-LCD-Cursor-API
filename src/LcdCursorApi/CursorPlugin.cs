using System.Reflection;
using System.Runtime.Loader;
using Keen.VRage.Core.Plugins;

namespace LcdCursorApi.Host;

/// <summary>
/// The static surface the reloadable logic talks to the bootstrap through. Harmony patches
/// are applied once, at game start, and must survive every logic reload — so the patch
/// bodies live here and only forward to whatever logic is currently loaded.
/// </summary>
public static class HostBridge
{
    /// <summary>Per-frame hook off the LCD render component's tick.</summary>
    public static volatile Action<object> LcdTickHook;

    /// <summary>
    /// Fired inside the LCD content render, with <c>(IDrawBatch batch, LcdPanelSurfaceContext ctx)</c>.
    /// The only place anything can be drawn onto a panel.
    /// </summary>
    public static volatile Action<object, object> LcdRenderHook;

    /// <summary>Fired as each <c>LcdPanelSurfaceContext</c> is constructed — the cheapest place
    /// to learn a panel's surface definition without walking every grid.</summary>
    public static volatile Action<object> LcdSurfaceDefHook;

    /// <summary>Non-zero suppresses the interaction glow. A count, not a flag: several consumers
    /// may hold a claim and the glow must return only when the last one releases.</summary>
    public static int HighlightSuppressions;

    /// <summary>
    /// Called with the camera's mouse delta before the camera consumes it. Returning true
    /// swallows the movement, so the view stays put and the cursor gets it instead.
    /// </summary>
    /// <remarks>
    /// Reading the delta and suppressing the camera are the same problem, so they are one
    /// hook. The engine's own relative pointer state can report movement, but nothing outside
    /// the camera's input path can stop the camera acting on it.
    /// </remarks>
    public static volatile Func<float, float, bool> MouseDeltaHook;

    // NOTE: adding fields here is a BREAKING change for hot reload. The bootstrap loads once
    // at game start and never reloads, so logic referencing a newly-added field throws
    // MissingFieldException against the bootstrap actually in memory, and every reload fails
    // until the game restarts. If you must add one, the logic side has to reach it
    // REFLECTIVELY so an older bootstrap degrades to a no-op instead of throwing — see
    // Logic/HostHooks.cs. Anything derivable should be derived instead; see Logic/Paths.cs.
}

public sealed class CursorPlugin : IPlugin
{
    private const string LogicName = "LcdCursorApi.Logic.dll";

    private readonly string _deployDir;
    private readonly string _logicPath;
    private readonly string _logPath;

    private AssemblyLoadContext _logicContext;
    private MethodInfo _tick;
    private DateTime _loadedStamp;
    private int _tickBusy;
    private long _tickStartedAt;

    public CursorPlugin() : this(null) { }

    public CursorPlugin(PluginHost host)
    {
        // The deploy directory is wherever this assembly was loaded from, so the plugin has
        // no machine-specific path baked into it.
        _deployDir = Path.GetDirectoryName(typeof(CursorPlugin).Assembly.Location) ?? ".";
        _logicPath = Path.Combine(_deployDir, LogicName);
        _logPath = Path.Combine(_deployDir, "lcdcursor.log");

        try { File.WriteAllText(_logPath, $"=== LcdCursorApi bootstrap {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}"); }
        catch { }
        Log($"Bootstrap constructed. Hot-reload watching {_logicPath}");

        ApplyPatches();

        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "LcdCursorBootstrap" };
        worker.Start();
    }

    // ---------------------------------------------------------------- patches

    private void ApplyPatches()
    {
        try
        {
            var harmony = new HarmonyLib.Harmony("lcdcursorapi.bootstrap");

            // The yellow interaction glow. Suppressing it at the point of creation is the
            // whole fix; the alternative — hunting down the HighlightEffectDefinitions by
            // reflection and zeroing their colour — reaches the same result far less
            // directly and depends on definition GUIDs that can move between builds.
            var meshEffects = Type.GetType("Keen.VRage.Render.Contracts.MeshEffectSystem, VRage.Render");
            var createHighlight = meshEffects?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                              .FirstOrDefault(m => m.Name == "CreateHighlight");
            if (createHighlight != null)
            {
                harmony.Patch(createHighlight, prefix: new HarmonyLib.HarmonyMethod(
                    typeof(CursorPlugin).GetMethod(nameof(HighlightPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log("Highlight suppression patch applied (MeshEffectSystem.CreateHighlight).");
            }
            else Log("WARNING: MeshEffectSystem.CreateHighlight not found — glow suppression unavailable.");

            // The content render itself. This is the only seam that hands over an IDrawBatch
            // bound to a panel's render target, so it is the only place a cursor can be drawn.
            var contentRenderer = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdContentRendererSessionComponent, Game2.Client");
            var render = contentRenderer?.GetMethod("Render", BindingFlags.Public | BindingFlags.Instance);
            if (render != null)
            {
                harmony.Patch(render, postfix: new HarmonyLib.HarmonyMethod(
                    typeof(CursorPlugin).GetMethod(nameof(LcdRenderPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log("LCD content render patch applied (LcdContentRendererSessionComponent.Render).");
            }
            else Log("WARNING: LcdContentRendererSessionComponent.Render not found — no cursor can be drawn.");

            // Per-frame tick on the LCD render component: a cursor update that rides the
            // renderer stays in step with what is actually on screen.
            var renderComp = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceRenderComponent, Game2.Client");
            var tick = renderComp?.GetMethod("TickFsrMask", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tick != null)
            {
                harmony.Patch(tick, postfix: new HarmonyLib.HarmonyMethod(
                    typeof(CursorPlugin).GetMethod(nameof(LcdTickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log("LCD tick patch applied (TickFsrMask).");
            }
            else Log("WARNING: TickFsrMask not found — falling back to the worker-thread tick only.");

            // The camera's own mouse-delta handler. Patching here is what makes decoupled
            // cursor mode possible at all: it is the only point that both carries the delta
            // and can decline to apply it to the view. It is a compiler-generated local
            // function, so it is matched by prefix rather than by exact name.
            var fpCam = Type.GetType("Keen.Game2.Client.GameSystems.CameraSystems.Modes.FirstPersonCameraWithInputComponent, Game2.Client");
            var handleMouse = fpCam?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                    .FirstOrDefault(m => m.Name.Contains("HandleMouseDelta"));
            if (handleMouse != null)
            {
                harmony.Patch(handleMouse, prefix: new HarmonyLib.HarmonyMethod(
                    typeof(CursorPlugin).GetMethod(nameof(MouseDeltaPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log($"Camera mouse-delta patch applied ({handleMouse.Name}).");
            }
            else Log("WARNING: FirstPersonCamera mouse-delta handler not found — decoupled cursor mode cannot suppress the view.");

            // Surface construction: hands us the LcdPanelSurface definition (mesh part name,
            // resolution, aspect) without walking grids to find panels.
            var surfaceCtx = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceContext, Game2.Client");
            var ctor = surfaceCtx?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2);
            if (ctor != null)
            {
                harmony.Patch(ctor, prefix: new HarmonyLib.HarmonyMethod(
                    typeof(CursorPlugin).GetMethod(nameof(SurfaceCtorPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log("LCD surface ctor patch applied.");
            }
            else Log("WARNING: LcdPanelSurfaceContext ctor not found — panel discovery will need a grid walk.");
        }
        catch (Exception e) { Log($"Patch application FAILED: {e.Message}"); }
    }

    /// <summary>Returning false skips the original — no highlight is created at all.</summary>
    private static bool HighlightPrefix() => Volatile.Read(ref HostBridge.HighlightSuppressions) <= 0;

    private static void LcdTickPostfix(object __instance)
    {
        try { HostBridge.LcdTickHook?.Invoke(__instance); } catch { }
    }

    private static void LcdRenderPostfix(object __0, object __1)
    {
        try { HostBridge.LcdRenderHook?.Invoke(__0, __1); } catch { }
    }

    /// <summary>
    /// Offer the camera's mouse delta to the logic. Returning false skips the original, so
    /// the view does not move.
    /// </summary>
    /// <remarks>
    /// The parameter is boxed as <see cref="object"/> rather than typed as <c>Vector2</c>: the
    /// bootstrap deliberately avoids a hard reference to the engine's math types here so that
    /// a signature change in the engine degrades to "no delta" instead of a patch that fails
    /// to apply at startup. X and Y are read reflectively, once.
    /// </remarks>
    private static void MouseDeltaPrefixCore(object value, out float x, out float y)
    {
        x = y = 0f;
        if (value == null) return;
        var t = value.GetType();
        _mdX ??= t.GetField("X");
        _mdY ??= t.GetField("Y");
        if (_mdX?.GetValue(value) is float fx) x = fx;
        if (_mdY?.GetValue(value) is float fy) y = fy;
    }

    private static FieldInfo _mdX, _mdY;

    private static bool MouseDeltaPrefix(object __0)
    {
        try
        {
            var hook = HostBridge.MouseDeltaHook;
            if (hook == null) return true;
            MouseDeltaPrefixCore(__0, out var x, out var y);
            return !hook(x, y); // hook returns true when it claims the movement
        }
        catch { return true; } // never let our fault freeze the player's view
    }

    private static void SurfaceCtorPrefix(object __1)
    {
        try { HostBridge.LcdSurfaceDefHook?.Invoke(__1); } catch { }
    }

    // ----------------------------------------------------------- hot reload

    private void WorkerLoop()
    {
        Thread.Sleep(8000); // let the session come up before touching anything
        while (true)
        {
            try
            {
                // Reload runs unconditionally. Ticks are dispatched to the pool behind a
                // busy flag, so a wedged tick can never block the reload that would fix it.
                ReloadLogicIfChanged();

                var tick = _tick;
                if (tick != null && Interlocked.CompareExchange(ref _tickBusy, 1, 0) == 0)
                {
                    _tickStartedAt = Environment.TickCount64;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { tick.Invoke(null, null); }
                        catch (Exception e) { Log($"ERROR tick: {e.InnerException?.Message ?? e.Message}\n{e.InnerException?.StackTrace ?? e.StackTrace}"); }
                        finally { Interlocked.Exchange(ref _tickBusy, 0); }
                    });
                }
                else if (tick != null && Environment.TickCount64 - _tickStartedAt > 30000)
                {
                    Log("WARNING: logic tick running >30s — likely wedged; reload stays alive, ticks skipped.");
                    _tickStartedAt = Environment.TickCount64;
                }
            }
            catch (Exception e) { Log($"ERROR worker: {e.Message}"); }
            Thread.Sleep(2000);
        }
    }

    private void ReloadLogicIfChanged()
    {
        if (!File.Exists(_logicPath))
        {
            if (_tick == null) Log("Waiting for logic dll to appear...");
            return;
        }

        var stamp = File.GetLastWriteTimeUtc(_logicPath);
        if (_tick != null && stamp == _loadedStamp) return;

        try
        {
            var old = _logicContext;
            var ctx = new AssemblyLoadContext("LcdCursorLogic_" + stamp.Ticks, isCollectible: true);

            Assembly asm;
            using (var ms = new MemoryStream(File.ReadAllBytes(_logicPath)))
            {
                var pdbPath = Path.ChangeExtension(_logicPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
                    asm = ctx.LoadFromStream(ms, pdb);
                }
                else asm = ctx.LoadFromStream(ms);
            }

            var entry = asm.GetType("LcdCursorApi.Logic.LogicEntry");
            var tick = entry?.GetMethod("Tick", BindingFlags.Public | BindingFlags.Static);
            if (tick == null)
            {
                Log("Logic dll loaded but LcdCursorApi.Logic.LogicEntry.Tick not found — keeping previous logic.");
                ctx.Unload();
                return;
            }

            // Tell the outgoing logic to withdraw its runtime from the facade before the new
            // one publishes, so consumers never see two runtimes or a torn handover.
            InvokeShutdown(_logicContext);

            _logicContext = ctx;
            _tick = tick;
            _loadedStamp = stamp;
            Log($"Logic loaded (build stamp {stamp:HH:mm:ss}). Hot-reload active.");
            old?.Unload();
        }
        catch (Exception e) { Log($"ERROR loading logic dll: {e.Message} — keeping previous logic."); }
    }

    private void InvokeShutdown(AssemblyLoadContext ctx)
    {
        if (ctx == null) return;
        try
        {
            foreach (var asm in ctx.Assemblies)
            {
                var entry = asm.GetType("LcdCursorApi.Logic.LogicEntry");
                entry?.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            }
        }
        catch (Exception e) { Log($"Shutdown of previous logic threw (continuing): {e.Message}"); }
    }

    private static readonly object LogGate = new();

    /// <summary>
    /// Append a line, but never let logging stall the caller.
    /// </summary>
    /// <remarks>
    /// This used to be an unconditional <c>lock</c> around the file write, and that wedged the
    /// whole plugin once: a write that blocks while holding the gate blocks the reload worker
    /// behind it, and the worker is the only thing that could have loaded the fix. The symptom
    /// was total silence — no reloads, no ticks, no errors — which reads exactly like a dead
    /// thread and took a while to tell apart from one.
    ///
    /// A dropped log line is a much smaller problem than a reload loop that cannot recover, so
    /// the gate is only ever taken with a timeout.
    /// </remarks>
    private void Log(string msg)
    {
        bool taken = false;
        try
        {
            Monitor.TryEnter(LogGate, 250, ref taken);
            if (!taken) return; // contended: drop the line rather than block the worker
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [boot] {msg}{Environment.NewLine}");
        }
        catch { }
        finally { if (taken) Monitor.Exit(LogGate); }
    }
}
