using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;

namespace LcdCursorApi.Logic;

/// <summary>
/// Finds the engine-wide singletons this API needs — the live sessions, and the input manager.
/// </summary>
/// <remarks>
/// <para>Both are located by walking the engine's component list and matching on type rather
/// than by naming a known component. That is deliberate: which component owns the session or
/// the input manager is an implementation detail that has no contract behind it, whereas the
/// types themselves are public. Walking costs a one-off reflection pass and then nothing.</para>
///
/// <para>Results are cached but re-checked while null, so a world load that happens after the
/// plugin starts is picked up.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class EngineLocator
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Session, byte> Sessions = new();

    private static object _inputManager;
    private static long _lastSearch;

    /// <summary>The engine's input manager, as <see cref="object"/> — see <see cref="MouseDelta"/>.</summary>
    public static object InputManager => _inputManager;

    /// <summary>
    /// Re-scan if anything is still missing. Cheap to call every tick: once everything is
    /// found it does nothing, and while something is missing it is rate-limited.
    /// </summary>
    public static void Poll()
    {
        if (_inputManager != null && !Sessions.IsEmpty) return;

        long now = Environment.TickCount64;
        if (now - _lastSearch < 2000) return;
        _lastSearch = now;

        try
        {
            var engine = VRageCore.Instance?.Engine;
            if (engine == null) return;

            engine.ForEach<Keen.VRage.DCS.Components.Component>(c =>
            {
                var t = c.GetType();
                foreach (var p in t.GetProperties(Any))
                {
                    try
                    {
                        if (typeof(Session).IsAssignableFrom(p.PropertyType))
                        {
                            if (p.GetValue(c) is Session s && s.Scene != null && Sessions.TryAdd(s, 0))
                                Log.Line($"Session found via {t.Name}.{p.Name}.");
                        }
                        else if (_inputManager == null && p.PropertyType.Name == "IInputManager")
                        {
                            var v = p.GetValue(c);
                            if (v != null)
                            {
                                _inputManager = v;
                                Log.Line($"IInputManager found via {t.Name}.{p.Name}.");
                            }
                        }
                    }
                    catch { }
                }
            }, reverse: false);
        }
        catch (Exception e) { Log.Error("engine walk", e); }
    }

    // Resolved once from the input manager, then invoked directly.
    private static MethodInfo _getPointerState;
    private static object _mouseDevice, _positionInputId, _relativeKind;
    private static bool _mouseResolved, _mouseWarned;

    /// <summary>
    /// The mouse movement delta for this frame, in device units.
    /// </summary>
    /// <remarks>
    /// Read from the engine rather than from the OS. While the game has the pointer captured
    /// the OS cursor does not move, so <c>GetCursorPos</c> deltas are flat zero exactly when
    /// the decoupled cursor needs them — the engine's own relative pointer state is the only
    /// source that still reports movement.
    /// </remarks>
    public static bool MouseDelta(out float dx, out float dy)
    {
        dx = dy = 0f;
        var mgr = _inputManager;
        if (mgr == null) return false;

        try
        {
            if (!_mouseResolved)
            {
                _mouseResolved = true;
                _mouseDevice = mgr.GetType().GetProperty("Mouse", Any)?.GetValue(mgr);
                if (_mouseDevice == null) { Log.Line("Input manager has no Mouse device."); return false; }

                _getPointerState = _mouseDevice.GetType()
                    .GetMethod("GetPointerState", BindingFlags.Public | BindingFlags.Instance);

                // MouseInputs.Position is a PointerInput with an implicit conversion to InputId;
                // GetPointerState compares against exactly that, so anything else returns Zero.
                var mouseInputs = _mouseDevice.GetType().Assembly.GetType("Keen.VRage.Core.Input.MouseInputs")
                                  ?? Type.GetType("Keen.VRage.Core.Input.MouseInputs, VRage.Core");
                var positionField = mouseInputs?.GetField("Position", BindingFlags.Public | BindingFlags.Static);
                var pointerInput = positionField?.GetValue(null);
                var toInputId = pointerInput?.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType.Name == "InputId");
                _positionInputId = toInputId?.Invoke(null, new[] { pointerInput });

                var kindType = _getPointerState?.GetParameters().ElementAtOrDefault(1)?.ParameterType;
                if (kindType != null && kindType.IsEnum) _relativeKind = Enum.Parse(kindType, "Relative");

                Log.Line($"Mouse delta wiring: device={_mouseDevice.GetType().Name}, " +
                         $"method={(_getPointerState != null ? "ok" : "MISSING")}, " +
                         $"inputId={(_positionInputId != null ? "ok" : "MISSING")}, " +
                         $"kind={(_relativeKind != null ? "ok" : "MISSING")}.");
            }

            if (_getPointerState == null || _positionInputId == null || _relativeKind == null) return false;

            var v = _getPointerState.Invoke(_mouseDevice, new[] { _positionInputId, _relativeKind });
            if (v == null) return false;

            var vt = v.GetType();
            dx = (float)(vt.GetField("X")?.GetValue(v) ?? 0f);
            dy = (float)(vt.GetField("Y")?.GetValue(v) ?? 0f);
            return true;
        }
        catch (Exception e)
        {
            if (!_mouseWarned) { _mouseWarned = true; Log.Error("mouse delta", e); }
            return false;
        }
    }
}
