using System;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// FAR BLOCK EFFECTS HIDDEN (2026-10-12, the user: smoke and fires hanging in the sky "in a grid", with nothing there).
/// ParticleCensus showed them: the game's block damage effects (smoke, fire, sparks) of wrecks this mod's frames hold
/// hundreds to thousands of km away. They are where they should be - re-parenting them and moving their render roots to
/// their grids changed nothing (2026-10-12, tools/harness/wreckcam.sh) - but a looping effect has no distance limit (the
/// game culls only one-shots, by EffectMaxDistance): the far grid's hull is long gone from view while its fires, past the
/// far plane, are drawn on the sky in the wreck's shape. Vanilla never has a burning wreck thousands of km off and in
/// view. Every Every seconds each pooled effect whose owner (its render parent's entity) is farther than CullKm from the
/// camera has its emitters hidden (ParticleEffectHandle.SetEmittersVisible(false)); shown again inside ShowKm (a band:
/// no flicker at the edge). Only the ones this hid are shown again. By reflection (VRage.Game.Client is not
/// referenceable). Client only.
/// (The file keeps its first name, EffectReparent.cs - the re-parent experiment it held is gone.)
/// </summary>
public static class EffectCull
{
    /// <summary>OFF by default (2026-10-13: the user's own thruster flames invisible, right next to them - an effect hidden
    /// here (SetEmittersVisible(false)) that ended went back to the game's pool hidden, and the render entity was handed to
    /// new effects - thruster flames among them - still invisible; this never saw them again to show them. Back on only
    /// with a hiding that cannot outlive the effect.)</summary>
    public static bool On = false;
    public static double Every = 1.0, CullKm = 30, ShowKm = 25;
    public static string Status = "-";
    static double _next;
    static readonly HashSet<object> _hidden = new HashSet<object>(ReferenceEqualityComparer.Instance);
    static PropertyInfo _ownerProp; static MethodInfo _setVisible;

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, Vector3D camera)
    {
        if (!On && _hidden.Count == 0) return;   // (off with nothing to show again: nothing to do - no pool walk each second)
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _next) return;
        _next = now + Every;
        Run(session, camera);
    }

    public static string Run(Keen.VRage.Core.Game.Systems.Session session, Vector3D camera)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object comp = null;
        foreach (var c in session.SessionComponents.Components)
            if (c != null && c.GetType().Name == "ParticleEffectsSessionComponent") { comp = c; break; }
        if (comp == null) return Status = "no particle session (client only)";
        var scene = (comp as Keen.VRage.DCS.Components.Component)?.Entity?.Scene;
        var mgr = comp.GetType().GetField("_effectPoolManager", F)?.GetValue(comp);
        var pools = mgr?.GetType().GetField("_pools", F)?.GetValue(mgr) as System.Collections.IDictionary;
        if (scene == null || pools == null) return Status = "no effect pools";
        var handles = new List<object>();
        foreach (var pool in pools.Values)
        {
            var map = pool?.GetType().GetField("_entityMap", F)?.GetValue(pool) as System.Collections.IDictionary;
            if (map == null) continue;
            foreach (var v in map.Values) { var h = v?.GetType().GetField("Item3")?.GetValue(v); if (h != null) handles.Add(h); }
        }
        var dist = new Dictionary<Keen.VRage.DCS.Accessors.DEntity, double>();
        var live = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int hid = 0, shown = 0, failed = 0;
        foreach (var h in handles)
        {
            try
            {
                var ht = h.GetType();
                if (ht.GetProperty("IsDisposed")?.GetValue(h) is bool gone && gone) continue;
                live.Add(h);
                _ownerProp ??= ht.GetProperty("RootEntity");
                if (!(_ownerProp?.GetValue(h) is Keen.VRage.DCS.Accessors.DEntity de)) continue;
                if (!dist.TryGetValue(de, out double d))
                {
                    // (an owner already deleted - the aero mod's flight effects outlive their grids in the pool - is skipped
                    //  BEFORE it is touched: its transform read threw a null reference per effect, per pass, and a thrown
                    //  exception costs this game ~200 ms: frames of 1-4 s within two minutes of flying, 2026-10-12)
                    var e = scene.IsEntityAlive(de) ? Keen.VRage.DCS.Components.Entity.TryGetFromDataEntity(new Keen.VRage.DCS.Accessors.DEntityContext(scene, de)) : null;
                    d = e?.Data.Scene == null ? -1 : (e.Data.GetWorldTransform().Position - camera).Length() / 1000.0;
                    dist[de] = d;
                }
                if (d < 0) continue;
                _setVisible ??= ht.GetMethod("SetEmittersVisible", new[] { typeof(bool) });
                if (_setVisible == null) break;
                if (On && d > CullKm && !_hidden.Contains(h)) { _setVisible.Invoke(h, new object[] { false }); _hidden.Add(h); hid++; }
                else if ((!On || d < ShowKm) && _hidden.Contains(h)) { _setVisible.Invoke(h, new object[] { true }); _hidden.Remove(h); shown++; }
            }
            catch { failed++; break; }   // (one throw a pass at most: each costs ~200 ms - the guards above should keep it at none)
        }
        if (failed == 0) _hidden.RemoveWhere(x => !live.Contains(x));   // (returned to the pool: forgotten; not on a pass cut short)
        return Status = $"{_hidden.Count} far effects hidden (+{hid} now, {shown} shown again) of {live.Count}; owners {dist.Count}{(failed > 0 ? " - A HANDLE THREW, pass cut short" : "")}";
    }
}
