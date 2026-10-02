using SEAerospace;
using SEAerospace.Entry;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Reentry in game (the rules: Core/Entry/Reentry). Server: every frame on rails about a body with an atmosphere
/// is braked through that body's band, its heat kept per frame. Client: your own frame's next pass predicted (the
/// orbit card, the map, and warp stopping ahead of it). Inside the planet's frame the world's speed cap applies;
/// the band makes sure you reach it at or under the cap.
/// </summary>
public static class EntryHost
{
    public static bool Enabled = true;
    /// <summary>The world's speed cap (m/s): what the band slows you to.</summary>
    public static double Cap => double.IsNaN(TestCap) ? FrameHost.SpeedCap : TestCap;   // (the arrival's cap: a hair under the world's)
    /// <summary>Harness: a lower cap for the reentry rules only (NaN: the world's).</summary>
    public static double TestCap = double.NaN;
    /// <summary>Warp stops this long before the braking starts.</summary>
    public const double WarpLead = 30.0;

    static readonly Dictionary<long, double> _last = new Dictionary<long, double>();
    static readonly Dictionary<long, double> _heat = new Dictionary<long, double>();
    static readonly Dictionary<long, (double decel, double t)> _braking = new Dictionary<long, (double, double)>();
    static readonly HashSet<long> _inPass = new HashSet<long>();
    // per grid, this entry: its tolerance (shielded or not), the heat it has been worn up to, and how much worn
    static readonly Dictionary<long, (double limit, double upTo, double worn)> _wear = new Dictionary<long, (double, double, double)>();
    public static string Status = "-";

    // ── the aero mod's entry plasma (AeroMod.AeroEntryFx.External): grids on rails sit still in their frame while
    //    the frame is braked through the air, so the aero mod cannot see their speed; it is told, per grid, the air
    //    past it (world axes are the inertial axes here) and how hard it burns ──
    static readonly Dictionary<Entity, (Vector3D air, double strength, double t)> _glow = new Dictionary<Entity, (Vector3D, double, double)>();
    static bool _glowHooked;
    /// <summary>Diagnostics: the hook is set; grid glows written; asked for / answered.</summary>
    public static bool GlowHooked => _glowHooked;
    public static int GlowWrites, GlowAsks, GlowAnswers;
    static int _glowTries;
    /// <summary>Braking (m/s^2) at which the plasma is full.</summary>
    public const double GlowFullDecel = 50.0;

    /// <summary>For the aero mod: a grid's air velocity (xyz, m/s) and glow (w, 0..1) while its frame is braked.</summary>
    public static Vector4 GlowOf(Entity grid)
    {
        lock (_glow)
        {
            GlowAsks++;
            if (grid == null || !_glow.TryGetValue(grid, out var g) || SystemHost.Now - g.t > 1.0) return default;
            GlowAnswers++;
            return new Vector4((float)g.air.X, (float)g.air.Y, (float)g.air.Z, (float)g.strength);
        }
    }

    /// <summary>A body's braking band (invalid: none).</summary>
    public static Band BandOf(string body)
    {
        var def = SystemHost.Registry?.FindDefinition(body);
        if (def == null || !def.HasAtmosphere) return default;
        return Reentry.For(def.RadiusMeters, def.AtmosphereHeightMeters, PlanetBerths.ShellRadius(def));
    }

    /// <summary>A body's spin (axis x rate, inertial): the air turns with it.</summary>
    public static Vector3D SpinOf(string body)
    {
        var b = SystemHost.Registry?.Find(body);
        return b != null && PlanetBerths.TryBodySpin(b, out Vector3D axis, out double omega) ? axis * omega : Vector3D.Zero;
    }

    /// <summary>A frame's heat (J/kg) now.</summary>
    public static double HeatOf(long frameId) { lock (_heat) return _heat.TryGetValue(frameId, out double h) ? h : 0; }
    /// <summary>A frame is being braked now (and how hard, m/s^2).</summary>
    public static bool Braking(long frameId, out double decel)
    {
        lock (_heat)
        {
            decel = 0;
            if (!_braking.TryGetValue(frameId, out var b) || SystemHost.Now - b.t > 1.0) return false;
            decel = b.decel; return true;
        }
    }
    /// <summary>A grid frame's heat goes with it when its grids arrive (the damage is applied from it).</summary>
    public static void Forget(long frameId) { lock (_heat) { _last.Remove(frameId); _heat.Remove(frameId); _braking.Remove(frameId); _inPass.Remove(frameId); } }

    /// <summary>Server tick (FramesLock held): brake every railed frame inside a band.</summary>
    public static void ServerTick(List<ProximityFrame> frames, double t)
    {
        if (!Enabled) return;
        if (!_glowHooked && _glowTries++ % 600 == 0)   // (until the aero mod is there: about every 10 s)
            _glowHooked = PlanetRenderBridge.SetForeignStatic("AeroMod.AeroEntryFx", "External", (Func<Entity, Vector4>)GlowOf);
        int braking = 0;
        foreach (var f in frames)
        {
            if (f.IsEncounter) continue;
            double t0;
            lock (_heat)
            {
                if (!_last.TryGetValue(f.Id, out t0)) { _last[f.Id] = t; continue; }
                _last[f.Id] = t;
            }
            if (!(t > t0)) continue;
            var b = BandOf(f.ParentBodyName);
            double heat = HeatOf(f.Id);
            if (!b.IsValid)
            {
                heat = Reentry.Cool(heat, t - t0);
                heat += AirlessBorder(f, t);   // an airless body: over the cap at its border, clamped (a jolt)
                lock (_heat) _heat[f.Id] = heat;
                Wear(f, t, heat);
                continue;
            }
            var log = new Pass();
            var el = Reentry.Advance(f.Elements, b, SpinOf(f.ParentBodyName), Cap, t0, Math.Min(t, t0 + 3600), ref heat, log);
            lock (_heat)
            {
                _heat[f.Id] = heat;
                if (log.Braked) _braking[f.Id] = (log.PeakDecel, t);
            }
            if (!log.Braked)
            {
                // The pass ends on leaving the band through its top (under the cap inside it is still the pass).
                bool outside = OrbitPropagation.StateAt(f.Elements, t).Position.Length() >= b.Top;
                if (outside) lock (_heat) if (_inPass.Remove(f.Id)) EndPass(f, heat);
                continue;
            }
            if (!IsFinite(el)) { FrameHost.Event($"ENTRY frame #{f.Id}: braking gave no orbit (kept the old)"); continue; }
            f.Elements = el;
            braking++;
            {
                var s = OrbitPropagation.StateAt(el, t);
                Vector3D air = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName));
                double strength = Math.Clamp(Math.Sqrt(log.PeakDecel / GlowFullDecel), 0.15, 1.0);
                lock (_glow)
                    foreach (long id in f.Members)
                        if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent gm && gm.Entity != null)
                        { _glow[gm.Entity] = (air, strength, t); GlowWrites++; }
            }
            Wear(f, t, heat);
            bool first; lock (_heat) first = _inPass.Add(f.Id);
            if (first) FrameHost.Event($"ENTRY frame #{f.Id} into {f.ParentBodyName}'s braking band at {log.ArrivalSpeed:F0} m/s (cap {Cap:F0})");
        }
        lock (_heat)
        {
            var done = new List<long>();
            foreach (var kv in _jolted) if (t - kv.Value > 60) done.Add(kv.Key);
            foreach (long id in done) { _jolted.Remove(id); var fr = SystemHost.Frames?.Get(id); if (fr != null) foreach (long m in fr.Members) _wear.Remove(m); }
        }
        Status = $"{braking} frame(s) braking, cap {Cap:F0} m/s";
    }

    /// <summary>
    /// An airless body has no band: a frame crossing its border over the cap is slowed to the cap there (the
    /// world would cap it anyway, silently); the speed lost is a jolt, a quarter of the same heat. Returns it.
    /// </summary>
    static double AirlessBorder(ProximityFrame f, double t)
    {
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        if (def == null || def.HasAtmosphere || string.IsNullOrEmpty(def.ParkSubtype)) return 0;
        var s = OrbitPropagation.StateAt(f.Elements, t);
        double r = s.Position.Length();
        if (!(r <= PlanetBerths.ShellRadius(def))) return 0;
        Vector3D w = SpinOf(f.ParentBodyName), spin = Vector3D.Cross(w, s.Position), air = s.Velocity - spin;
        double va = air.Length();
        if (!(va > Cap)) return 0;
        var el = Reentry.ToElements(new StateVector(s.Position, spin + air * (Cap / va)), f.Elements.Mu, t);
        if (!IsFinite(el)) return 0;
        f.Elements = el;
        double jolt = Reentry.Jolt(va, Cap);
        FrameHost.Event($"ENTRY frame #{f.Id}: {f.ParentBodyName} has no air: {va:F0} -> {Cap:F0} m/s at its border (a jolt, {Reentry.Share(jolt, false):P0} of tolerance)");
        lock (_heat) _jolted[f.Id] = t;
        return jolt;
    }
    static readonly Dictionary<long, double> _jolted = new Dictionary<long, double>();

    /// <summary>
    /// Heat past a grid's tolerance wears its forward layer (EntryDamage): by how far past it the heat has gone
    /// (beyond what it was already worn for), at most EntryDamage.MaxPerEntry an entry. A grid's tolerance is
    /// set at the start of the entry (doubled by heavy armour in front).
    /// </summary>
    static void Wear(ProximityFrame f, double t, double heat)
    {
        if (heat < Reentry.ToleranceJPerKg) return;   // under even an unshielded tolerance: nothing to do
        var s = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D motion = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName));
        if (motion.LengthSquared() < 1) return;
        motion = Vector3D.Normalize(motion);   // (the berth's world axes are the inertial axes)
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id) || !(GridMembers.Get(id) is OrbitalGridComponent g) || !g.IsServer || !GridMembers.IsDynamic(g)) continue;
            (double limit, double upTo, double worn) w;
            lock (_heat)
                if (!_wear.TryGetValue(id, out w))
                {
                    double lim = Reentry.ToleranceJPerKg * (EntryDamage.Shielded(g, motion) ? Reentry.ShieldFactor : 1);
                    w = (lim, lim, 0);
                }
            double room = EntryDamage.MaxPerEntry - w.worn;
            double frac = Math.Min((heat - w.upTo) / w.limit, room);
            // In chunks of 5% (or the last of this entry's allowance), not a sliver every tick.
            if (frac > 1e-4 && (frac >= 0.05 || frac >= room - 1e-9))
            {
                int n = EntryDamage.Apply(g, motion, frac);
                w.worn += frac;
                w.upTo = heat;
                FrameHost.Event($"ENTRY '{g.DisplayName}': too hot ({Reentry.Share(heat, w.limit > Reentry.ToleranceJPerKg):P0} of its tolerance): {n} forward block(s) worn {frac:P0} (this entry {w.worn:P0})");
            }
            if (w.worn >= EntryDamage.MaxPerEntry - 1e-9) w.upTo = double.MaxValue;   // this entry's allowance is spent
            lock (_heat) _wear[id] = w;
        }
    }

    static void EndPass(ProximityFrame f, double heat)
    {
        lock (_glow)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent gm && gm.Entity != null) _glow.Remove(gm.Entity);
        lock (_heat) foreach (long id in f.Members) _wear.Remove(id);
        Log(f, heat);
    }
    static void Log(ProximityFrame f, double heat)
        => FrameHost.Event($"ENTRY frame #{f.Id}: out of the band, heat {Reentry.Share(heat, false):P0} of tolerance, a={f.Elements.SemiMajorAxis / 1000:F0} km e={f.Elements.Eccentricity:F3}");

    static bool IsFinite(KeplerianElements el)
        => !double.IsNaN(el.SemiMajorAxis) && !double.IsInfinity(el.SemiMajorAxis) && !double.IsNaN(el.Eccentricity) && !double.IsNaN(el.MeanMotion);

    // ── your own frame's next pass (client) ──
    public static Pass Prediction;
    public static string PredictedBody;
    static double _predWall;

    public static void ClientTick(double t)
    {
        var f = FrameHost.PlayerFrame;
        if (!Enabled || f == null) { Prediction = null; return; }
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (wall - _predWall < 0.5) return;
        _predWall = wall;
        var b = BandOf(f.ParentBodyName);
        if (!b.IsValid) { Prediction = null; AirlessAhead(f, t); return; }
        Airless = null;
        try
        {
            KeplerianElements el; lock (ServerFrames.FramesLock) el = f.Elements;
            double horizon = el.Eccentricity < 1 ? Math.Min(el.Period, 6 * 3600) : 6 * 3600;
            var p = Reentry.Predict(el, b, SpinOf(f.ParentBodyName), Cap, t, horizon, HeatOf(f.Id));
            Prediction = p.Braked ? p : null;
            PredictedBody = f.ParentBodyName;
        }
        catch (Exception e) { FrameHost.Fault("entry prediction", e); Prediction = null; }
    }

    /// <summary>An airless body ahead that you would reach over the cap: when, and how fast (null: none).</summary>
    public static (double t, double speed, string body)? Airless;

    static void AirlessAhead(ProximityFrame f, double t)
    {
        Airless = null;
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        if (def == null || def.HasAtmosphere || string.IsNullOrEmpty(def.ParkSubtype)) return;
        KeplerianElements el; lock (ServerFrames.FramesLock) el = f.Elements;
        double shell = PlanetBerths.ShellRadius(def);
        if (!(el.PeriapsisRadius < shell) || !OrbitPropagation.TryTimeToRadius(el, shell, out _, out double tIn)) return;
        double tc = FrameHost.NextInboundCrossingPublic(el, tIn, t);
        if (double.IsNaN(tc) || tc < t) return;
        var s = OrbitPropagation.StateAt(el, tc);
        double va = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName)).Length();
        if (va > Cap) Airless = (tc, va, f.ParentBodyName);
    }

    /// <summary>When warp must stop: the lead time before the braking starts (NaN: none ahead).</summary>
    public static double NextWarpStop(double now)
    {
        var air = Airless;
        if (air.HasValue && air.Value.t - WarpLead > now) return air.Value.t - WarpLead;   // (an airless border: before it)
        var p = Prediction;
        // (only BEFORE a pass: once in it, warp is yours again; the braking is warp-proof)
        var f = FrameHost.PlayerFrame;
        if (p == null || double.IsNaN(p.EnterTime) || p.EnterTime < now + 1 || (f != null && Braking(f.Id, out _))) return double.NaN;
        if (now > p.EnterTime - WarpLead) return double.NaN;   // already stopped once for it
        return p.EnterTime - WarpLead;
    }

    /// <summary>The orbit card's line: the next pass (ahead), or the braking now.</summary>
    public static string CardLine(double now)
    {
        var f = FrameHost.PlayerFrame;
        if (f == null) return null;
        double heat = HeatOf(f.Id);
        if (Braking(f.Id, out double decel))
            return $"Entry · braking {decel / 9.81:F1} g · heat {Reentry.Share(heat, false):P0}";
        var air = Airless;
        if (air.HasValue)
        {
            string at = air.Value.t - now <= 10 ? "now" : "in " + Maneuvers.Clock(air.Value.t - now);
            double jolt = Reentry.Share(Reentry.Jolt(air.Value.speed, Cap), false);
            return $"No air {at} · {air.Value.speed:N0} > {Cap:N0} m/s · burn to slow (jolt {jolt:P0})";
        }
        var p = Prediction;
        if (p == null || double.IsNaN(p.EnterTime)) return null;
        string when = p.EnterTime - now <= 10 ? "now" : "in " + Maneuvers.Clock(p.EnterTime - now);
        string to = double.IsNaN(p.SpeedAtBottom) ? "aerobrake" : $"{p.ArrivalSpeed:N0} → {p.SpeedAtBottom:N0} m/s";
        double share = Reentry.Share(p.PeakHeat, false);
        return $"Entry {when} · {to} · heat {share:P0}{(share > 1 ? " — too hot" : "")}";
    }
}
