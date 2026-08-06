using System.Reflection;
using System.Runtime.CompilerServices;

namespace LcdCursorApi.Logic;

/// <summary>
/// Puts a panel into the engine's own custom-render state, which is the supported way to get
/// it a private material and its own render target.
/// </summary>
/// <remarks>
/// <para><b>Why this replaces the clone approach.</b> The engine already has the state machine
/// this was trying to fake:</para>
/// <code>
///   TransitionToCustomRender  -> SetNewScreenMaterialHandle     (PRIVATE material)
///   TransitionToDefaultScreen -> SetSharedScreenMaterialHandle  (SHARED material)
///   TransitionToPowerOff      -> SetSharedScreenMaterialHandle  (SHARED material)
/// </code>
/// <para>So a panel rendering its own content is <i>already</i> private, and only
/// <c>DefaultScreen</c>/<c>PowerOff</c> panels share. That is why duplicate cursors only
/// appeared on blank panels, and why switching a panel to text mode by hand fixed it — that
/// switch <i>is</i> this transition.</para>
///
/// <para>The previous attempt tried to force uniqueness by cloning the material definition.
/// It could not work: definitions are interned, so <c>DeepClone</c> returns the registered
/// object and the shared key — which holds the definition by reference — is unchanged. The
/// RTT project reached the same conclusion independently and has had it open for weeks. The
/// engine's transition sidesteps the whole question rather than fighting it.</para>
///
/// <para><b>What it costs.</b> Exactly what the engine spends on any panel showing content: a
/// private material and a pooled render target. Done once per panel, on the first frame the
/// cursor lands there, and never undone by us — letting the engine move the panel back when
/// it decides to is stabler than fighting it for ownership.</para>
///
/// <para><b>The visible side effect</b>, stated plainly: a panel sitting on the stock default
/// screen will start rendering its own (possibly empty) content instead. That is the same
/// thing the player sees when switching the panel to text mode. It is gated behind
/// <see cref="Config.ForceCustomRender"/> so it can be turned off live.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class CustomRenderMode
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The engine's <c>LcdPanelRenderState.CustomRender</c>.</summary>
    private const byte CustomRender = 2;

    /// <summary>Surfaces already transitioned. Weak keys: a destroyed panel must not be held alive.</summary>
    private static readonly ConditionalWeakTable<object, object> Done = new();

    private static MethodInfo _transition;
    private static FieldInfo _stateField;
    private static bool _resolved, _giveUp;
    private static int _count, _errors;

    public static int TransitionCount => _count;

    /// <summary>
    /// Ensure this surface is rendering its own content. Safe to call every frame; the work
    /// happens once.
    /// </summary>
    public static void Ensure(object renderComponent, object ctx, int surfaceIndex)
    {
        if (!Config.ForceCustomRender || _giveUp || renderComponent == null || ctx == null) return;

        try
        {
            if (!_resolved)
            {
                _resolved = true;
                _transition = renderComponent.GetType()
                    .GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "TransitionToCustomRender" && m.GetParameters().Length == 1);
                _stateField = ctx.GetType().GetField("CurrentMaterialState", Any);
                Log.Line($"CUSTOM RENDER: TransitionToCustomRender {(_transition != null ? "resolved" : "NOT FOUND")}, " +
                         $"CurrentMaterialState {(_stateField != null ? "resolved" : "NOT FOUND")}.");
                if (_transition == null || _stateField == null)
                {
                    _giveUp = true;
                    Log.Line("CUSTOM RENDER: disabled — panels will keep the shared material, so the cursor " +
                             "may appear on other panels of the same size.");
                    return;
                }
            }

            // Already rendering its own content: nothing to do, and re-transitioning would
            // churn a render target every frame.
            var state = _stateField.GetValue(ctx);
            if (state != null && Convert.ToByte(state) == CustomRender) { Done.AddOrUpdate(ctx, null); return; }

            if (Done.TryGetValue(ctx, out _)) return; // tried once; do not spin on a refusal

            Done.AddOrUpdate(ctx, null);
            _transition.Invoke(renderComponent, new object[] { surfaceIndex });
            _count++;

            Log.Line($"CUSTOM RENDER: moved surface {surfaceIndex} off the shared material " +
                     $"(transition #{_count}). It now renders its own content and has a private material.");
        }
        catch (Exception e)
        {
            if (_errors++ < 3) Log.Error("custom render transition", e);
            if (_errors >= 3) { _giveUp = true; Log.Line("CUSTOM RENDER: disabled after repeated failures."); }
        }
    }

    /// <summary>Forget what we transitioned. The engine keeps whatever state it is in.</summary>
    public static void Reset()
    {
        _count = 0;
        _errors = 0;
        _giveUp = false;
        _resolved = false;
    }
}
