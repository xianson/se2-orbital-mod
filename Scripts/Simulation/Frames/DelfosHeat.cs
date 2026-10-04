using Keen.Game2.Simulation.WorldObjects.BrownDwarf;
using Keen.VRage.Core;
using Keen.VRage.DCS.Internal;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Delfos's heat: the game's brown-dwarf killing field, about OUR Delfos. The game's own field sits at the
/// game's star (a fixed world spot our system does not use); its damage, camera effect and warning all read
/// a KillingFieldSourceData (centre + definition) on the entity they hurt. So we write that data ourselves
/// on the player's character (server copy: damage; client copy: effect and warning), centred where our
/// Delfos is from where you are, with the game's definition cloned and its radii scaled to our star
/// (theirs, 700 km, would sit inside our 1,140 km Delfos).
/// </summary>
public static class DelfosHeat
{
    /// <summary>Above Delfos's surface: warning (the field's reach), damage starts, damage is full.</summary>
    public const double WarnAbove = 3.0e6, DamageAbove = 2.0e6, FullAbove = 0.5e6;   // (2,500 km above killed in seconds at 2.85/1.5)

    /// <summary>Damage per tick at the damage edge and at full (the game's receiver multiplies by 90 per second).</summary>
    public const double MinDamage = 0.011, MaxDamage = 1.0;

    public static string Status = "-";
    private static KillingFieldComponentDefinition _def;
    private static bool _tried;
    private static long _nextLook;

    /// <summary>The game's field definition, cloned with our radii (found on the game's own field entity).</summary>
    static KillingFieldComponentDefinition Def(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (_def != null || session == null) return _def;
        // (no killing field in this world: looked for again every 5 s, not every frame on two threads)
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextLook) return null;
        _nextLook = now + 5 * System.Diagnostics.Stopwatch.Frequency;
        try
        {
            foreach (var e in session.GetEntitiesOfType<KillingFieldComponent>())
            {
                var comp = e.TryGet<KillingFieldComponent>();
                var baseDef = typeof(KillingFieldComponent).GetField("_componentDefinition",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(comp) as KillingFieldComponentDefinition;
                if (baseDef == null) continue;
                var clone = (KillingFieldComponentDefinition)typeof(object).GetMethod("MemberwiseClone",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(baseDef, null);
                double R = SystemHost.StarRadius;
                SetF(clone, "Radius", R + WarnAbove);
                SetF(clone, "DamageRadius", R + DamageAbove);
                SetF(clone, "MaxDamageRadius", R + FullAbove);
                // Damage per tick x90 per second in the game's receiver: theirs (1..100) killed in seconds anywhere
                // inside. Ours ramps: ~1/s at the damage edge (a couple of minutes), ~90/s at full (a second).
                SetF(clone, "MinDamage", MinDamage);
                SetF(clone, "MaxDamage", MaxDamage);
                // (the client and server threads both get here: the first clone wins for both - a second replacing it made
                //  Remove's "only ours" check fail, and the heat stayed on the character)
                System.Threading.Interlocked.CompareExchange(ref _def, clone, null);
                Status = $"heat: field from the game's ({baseDef.Radius / 1000:F0} km) -> ours warn {clone.Radius / 1000:F0} km, damage {clone.DamageRadius / 1000:F0} km, full {clone.MaxDamageRadius / 1000:F0} km (from Delfos's centre)";
                return _def;
            }
            if (!_tried) { _tried = true; Status = "heat: no killing field entity in this session (yet)"; }
        }
        catch (System.Exception ex) { Status = "heat: definition failed: " + ex.Message; }
        return null;
    }

    static void SetF(KillingFieldComponentDefinition d, string name, double v) =>
        typeof(KillingFieldComponentDefinition).GetProperty(name)?.SetValue(d, (float)v);

    /// <summary>Your root (star-centred) position from where you are in the world: your frame's orbit plus your place in it.</summary>
    static bool Root(Vector3D world, double t, out Vector3D root)
    {
        root = default;
        var f = FrameHost.PlayerFrame;
        var body = f != null ? SystemHost.Registry?.Find(f.ParentBodyName) : null;
        if (body == null) return false;   // in a planet's own space: nowhere near Delfos
        root = body.OriginInRoot(t).Position + OrbitPropagation.StateAt(f.Elements, t).Position + (world - f.BerthCenter);
        return true;
    }

    /// <summary>Put (or take) Delfos's field on a character at a world position. Called for the server copy and the client copy.</summary>
    public static void Apply(Keen.VRage.Core.Game.Systems.Session session, Entity ch, Vector3D world)
    {
        if (ch == null) return;
        try
        {
            double t = SystemHost.Now;
            var def = Def(session);
            if (def == null || SystemHost.Registry?.Root == null || !Root(world, t, out Vector3D root)) { Remove(ch); return; }
            Vector3D delfos = SystemHost.Registry.Root.OriginInRoot(t).Position;
            double d = (root - delfos).Length();
            if (d > def.Radius) { Remove(ch); return; }
            Vector3D centre = world + (delfos - root);   // (a frame's world axes are the star's)
            if (ch.Data.TryGet<KillingFieldSourceData>(out var cur) && !cur.KillingFieldDefinition.IsEmpty)
            {
                cur.Center = centre;
                ch.Data.Set(cur);
            }
            else ch.Data.Set(new KillingFieldSourceData { KillingFieldDefinition = new ReferenceHandle<KillingFieldComponentDefinition>(def), Center = centre });
            Status = $"heat: IN Delfos's field, {(d - SystemHost.StarRadius) / 1000:F0} km above it" + (d <= def.DamageRadius ? " (burning)" : " (warning)");
        }
        catch (System.Exception ex) { Status = "heat: apply failed: " + ex.Message; }
    }

    static void Remove(Entity ch)
    {
        if (Status.StartsWith("heat: IN")) Status = "heat: outside Delfos's field";
        if (!ch.Data.TryGet<KillingFieldSourceData>(out var cur)) return;
        // Only ours: the game's own field (at its star) sets its own centre far away; leave that alone.
        if (!cur.KillingFieldDefinition.IsEmpty && !ReferenceEquals(cur.KillingFieldDefinition.Value, _def)) return;
        if (!cur.KillingFieldDefinition.IsEmpty) cur.KillingFieldDefinition.Dispose();
        ch.Data.TryRemove<KillingFieldSourceData>();
    }
}
