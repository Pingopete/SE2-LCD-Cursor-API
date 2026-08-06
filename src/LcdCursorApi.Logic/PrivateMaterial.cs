using System.Reflection;
using System.Runtime.CompilerServices;

namespace LcdCursorApi.Logic;

/// <summary>
/// Moves a panel off the engine's shared LCD material so drawing on it cannot appear on
/// another panel.
/// </summary>
/// <remarks>
/// <para><b>The bug this fixes.</b> The engine borrows runtime LCD materials from a shared
/// store keyed on <c>SharedRuntimeMaterialKey { MaterialDefinition, AspectRatio, Orientation }</c>.
/// Content is not in that key, so two panels of the same size and material get the *same*
/// runtime material — and a cursor drawn into it appears on both. Observed in game as a
/// second crosshair mirroring the first, and cleared by power-cycling the panel (which makes
/// it re-acquire a handle).</para>
///
/// <para><b>What it costs.</b> Read from the IL rather than assumed:
/// <c>SetNewScreenMaterialHandle</c> is <c>ReleaseScreenMaterialHandle()</c> +
/// <c>CreateRuntimeLcdMaterial()</c>, and the latter builds an object builder, a runtime
/// definition and a <c>RuntimeMaterialHandle</c>. <b>No render target is allocated</b> — the
/// texture handle is a parameter, and render targets come from
/// <c>LcdRenderTargetPoolSessionComponent</c>, which this does not touch. So the cost is one
/// CPU definition plus one material handle per panel.</para>
///
/// <para><b>And it is once per panel, not per cursor movement.</b> The binding is cached and
/// never undone while the panel lives, so walking a row of ten panels costs ten binds total
/// and then nothing. Unbinding on cursor-leave is what would turn this into per-transition
/// churn, so it is deliberately not done.</para>
///
/// <para><b>The hazard is a leak, not the size</b> — this project's history is a transient
/// constant-buffer leak that reached 31417 alive. Per the RTT hazard note: "never claim a
/// private material without a <c>ReferenceEquals</c> check — the first attempt logged
/// 'PRIVATE clone' while printing the identical hash that disproved it." A guard that
/// silently fails turns a bounded one-off into a per-frame leak that reports success. Hence
/// the reference check, the honest failure log, and the running bind count.</para>
///
/// <para>Not yet verified in game.</para>
/// </remarks>
internal static class PrivateMaterial
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Contexts already bound. Weak keys: a destroyed panel must not be kept alive.</summary>
    private static readonly ConditionalWeakTable<object, object> Bound = new();

    /// <summary>Contexts we must hand back to the stock material on teardown.</summary>
    private static readonly List<(WeakReference Renderer, WeakReference Ctx)> Claimed = new();

    private static int _bindCount, _failCount;
    private static bool _giveUp;

    public static int BindCount => _bindCount;

    /// <summary>
    /// Give this surface its own material, once. Safe to call every frame.
    /// </summary>
    public static void Ensure(object renderComponent, object ctx)
    {
        if (!Config.PrivateMaterial || _giveUp || renderComponent == null || ctx == null) return;
        if (Bound.TryGetValue(ctx, out _)) return; // already ours

        try
        {
            if (IsForeignBound(ctx)) return;

            var renderer = Field(renderComponent, "_renderer");
            if (renderer == null) return;

            var def = Prop(ctx, "Definition");
            var baseMaterial = Prop(def, "DefaultScreenMaterial");
            var aspect = Prop(def, "AspectRatio");
            var orientation = Prop(Prop(ctx, "State"), "Orientation");
            if (baseMaterial == null || aspect == null) return;

            // Keep sampling exactly the texture the panel already samples. Passing null here
            // would rebind the material to nothing and blank the panel, which is a far worse
            // bug than the one being fixed.
            var target = Prop(ctx, "RenderTarget");
            var texture = target == null ? null : Prop(target, "TextureHandle");
            if (texture == null) return; // no target yet — try again next frame

            var clone = Clone(baseMaterial);
            if (clone == null || ReferenceEquals(clone, baseMaterial))
            {
                // Honest failure. The alternative — carrying on and logging success — is the
                // documented way this goes wrong.
                if (_failCount++ == 0)
                    Log.Line($"PRIVATE MATERIAL: {baseMaterial.GetType().Name} did not clone " +
                             $"(got {(clone == null ? "null" : "the same instance")}). Falling back to the SHARED " +
                             "material — cursors may appear on other panels of the same size. Disabled.");
                _giveUp = true;
                return;
            }

            var mi = ctx.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "SetNewScreenMaterialHandle");
            if (mi == null) { _giveUp = true; Log.Line("PRIVATE MATERIAL: SetNewScreenMaterialHandle not found — disabled."); return; }

            mi.Invoke(ctx, new[] { renderer, clone, aspect, orientation, texture });

            Bound.Add(ctx, clone);
            lock (Claimed) Claimed.Add((new WeakReference(renderer), new WeakReference(ctx)));
            _bindCount++;

            // The count is the leak instrument: it should settle at roughly the number of
            // panels visited. A number that keeps climbing means the cache is not taking.
            Log.Line($"PRIVATE MATERIAL: bound panel #{_bindCount} with a clone of " +
                     $"{baseMaterial.GetType().Name}#{baseMaterial.GetHashCode():x8} -> #{clone.GetHashCode():x8}.");
        }
        catch (Exception e)
        {
            if (_failCount++ < 3) Log.Error("private material bind", e);
        }
    }

    /// <summary>
    /// Hand every claimed panel back to the stock material.
    /// </summary>
    /// <remarks>
    /// Released through the engine's own path rather than dropped. A private material left
    /// dangling across a logic reload is exactly the shape that produces the engine's
    /// "Can't remove material" double-release.
    /// </remarks>
    public static void ReleaseAll()
    {
        List<(WeakReference Renderer, WeakReference Ctx)> list;
        lock (Claimed) { list = new(Claimed); Claimed.Clear(); }

        int restored = 0, gone = 0;
        foreach (var (rRef, cRef) in list)
        {
            var renderer = rRef.Target;
            var ctx = cRef.Target;
            if (renderer == null || ctx == null) { gone++; continue; }
            try
            {
                var def = Prop(ctx, "Definition");
                var baseMaterial = Prop(def, "DefaultScreenMaterial");
                var aspect = Prop(def, "AspectRatio");
                var orientation = Prop(Prop(ctx, "State"), "Orientation");
                var target = Prop(ctx, "RenderTarget");
                var texture = target == null ? null : Prop(target, "TextureHandle");
                if (baseMaterial == null) continue;

                var mi = ctx.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "SetNewScreenMaterialHandle");
                mi?.Invoke(ctx, new[] { renderer, baseMaterial, aspect, orientation, texture });
                Bound.Remove(ctx);
                restored++;
            }
            catch (Exception e) { Log.Error("private material release", e); }
        }

        if (restored > 0 || gone > 0)
            Log.Line($"PRIVATE MATERIAL: {restored} panel(s) handed back to the stock material" +
                     (gone > 0 ? $" ({gone} already destroyed)" : "") + ".");
        _bindCount = 0;
    }

    private static readonly HashSet<string> ForeignLogged = new();

    /// <summary>
    /// True when another mod already owns this panel's screen material.
    /// </summary>
    /// <remarks>
    /// <para>The RTT Camera plugin rebinds a tagged panel's material through the very same
    /// <c>SetNewScreenMaterialHandle</c> call so the panel samples its feed's render target.
    /// If both mods bind the same panel, the last writer wins and the other silently loses —
    /// which for RTT means a feed panel going blank or showing the wrong texture, a far worse
    /// outcome than a cursor that can bleed onto a neighbour.</para>
    ///
    /// <para>Ownership is read off the surface TEXT, which is how RTT claims panels: an
    /// <c>[RTT]</c> tag, optionally numbered (<c>[RTT2]</c>), and <c>[RTS]</c> for its mirror.
    /// Matching the bracket prefix covers all of them. Drawing the cursor on such a panel is
    /// still fine and often wanted — it is only the material bind that must not be contested.</para>
    /// </remarks>
    private static bool IsForeignBound(object ctx)
    {
        try
        {
            if (Prop(Prop(ctx, "State"), "Text") is not string text || text.Length == 0) return false;
            if (text.IndexOf("[RTT", StringComparison.OrdinalIgnoreCase) < 0
             && text.IndexOf("[RTS", StringComparison.OrdinalIgnoreCase) < 0) return false;

            lock (ForeignLogged)
            {
                if (ForeignLogged.Add(text.Length > 32 ? text[..32] : text))
                    Log.Line($"PRIVATE MATERIAL: skipping a panel claimed by another mod (surface text starts " +
                             $"'{(text.Length > 24 ? text[..24] : text)}'). Its material is left alone; the cursor " +
                             "still draws on it.");
            }
            return true;
        }
        catch { return false; }
    }

    private static MethodInfo _cloneMi;

    private static object Clone(object materialDefinition)
    {
        _cloneMi ??= materialDefinition.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "DeepClone" && m.GetParameters().Length == 0);
        return _cloneMi?.Invoke(materialDefinition, null);
    }

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            var p = t.GetProperty(name, Any);
            if (p != null) return p.GetValue(o);
            return t.GetField(name, Any)?.GetValue(o);
        }
        catch { return null; }
    }

    private static object Field(object o, string name)
    {
        if (o == null) return null;
        try { return o.GetType().GetField(name, Any)?.GetValue(o); }
        catch { return null; }
    }
}
