using SEAerospace;
using SEAerospace.Entry;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE ORBITAL MOD'S PUBLIC API - for other mods (and, once SE2 has them, scripts) that want what orbital knows: the
/// solar system, the frames on orbit and their elements, which frame a grid is in, reentry, and the settings.
///
/// Mods compile separately, so a consumer reaches this class BY REFLECTION: find the type "OrbitalMod.OrbitalApi" in
/// the loaded assemblies, check <see cref="Version"/> (a const: FieldInfo.GetRawConstantValue), cache the MethodInfos.
/// Every signature uses only .NET and engine types (Entity, Vector3, Vector3D, List), never an orbital type.
///
/// Frames: the mod keeps every orbiting thing in a FRAME (a "proximity frame"): a group of grids and players sharing
/// one Keplerian orbit about a body, simulated around a berth in the game world. Frame ids are runtime numbers (stable
/// for the session; a save keeps the frames, the ids may change). States are relative to the frame's body, in the
/// solar system's inertial axes (m, m/s), at the universe time (<see cref="GetTime"/>, s).
///
/// Where it answers: where the frame registry lives - the machine that runs the server (single player, a host). The
/// player calls (GetPlayerFrame, TryGetEntryPrediction) answer where that player's client runs.
/// Threads: any. Frame answers are copied under the frame lock (one consistent tick).
/// Stability: members are only ADDED within a version; a change to an existing one bumps <see cref="Version"/>.
/// Documented in docs/API.md (the same text, with an example).
/// </summary>
public static class OrbitalApi
{
    /// <summary>The API contract. 1: the first published API.</summary>
    public const int Version = 1;

    // ── the solar system ──

    /// <summary>The solar system is built (bodies and frames exist). False early in a session, and with the mod idle.</summary>
    public static bool IsReady() => SystemHost.Built;

    /// <summary>The universe clock (s): what orbits are propagated by. Runs faster in time warp.</summary>
    public static double GetTime() => SystemHost.Now;

    /// <summary>The time warp factor now (1: none).</summary>
    public static double GetTimescale() => SystemHost.Timescale;

    /// <summary>The bodies by name: the star first, then planets and moons. Cleared first; returns the count.</summary>
    public static int GetBodies(List<string> names)
    {
        if (names == null) return 0;
        names.Clear();
        var reg = SystemHost.Registry;
        if (reg == null) return 0;
        foreach (var b in reg.Bodies) names.Add(b.Name);
        return names.Count;
    }

    /// <summary>A body: its parent ("" for the star), gravitational parameter (m3/s2), radius (m), sphere of influence
    /// (m; infinity for the star), whether it has an atmosphere and how high it reaches (m), its sidereal day (s; 0 none).</summary>
    public static bool TryGetBody(string name, out string parent, out double mu, out double radius, out double soiRadius,
        out bool hasAtmosphere, out double atmosphereHeight, out double rotationPeriod)
    {
        parent = ""; mu = radius = soiRadius = atmosphereHeight = rotationPeriod = 0; hasAtmosphere = false;
        var reg = SystemHost.Registry;
        var b = name != null ? reg?.Find(name) : null;
        if (b == null) return false;
        var def = reg.FindDefinition(name);
        parent = b.Parent?.Name ?? ""; mu = b.Mu; soiRadius = b.SoiRadius; rotationPeriod = b.RotationPeriodSeconds;
        if (def != null) { radius = def.RadiusMeters; hasAtmosphere = def.HasAtmosphere; atmosphereHeight = def.AtmosphereHeightMeters; }
        return true;
    }

    /// <summary>A body's state relative to its parent (m, m/s, inertial) at a universe time (NaN: now). False: no such
    /// body, or the star (it has no parent).</summary>
    public static bool TryGetBodyState(string name, double time, out Vector3D position, out Vector3D velocity)
    {
        position = velocity = default;
        var b = name != null ? SystemHost.Registry?.Find(name) : null;
        if (b == null || b.IsRoot) return false;
        var s = b.StateInParentAt(double.IsNaN(time) ? SystemHost.Now : time);
        position = s.Position; velocity = s.Velocity;
        return true;
    }

    // ── frames ──

    /// <summary>Every frame's id. Cleared first; returns the count.</summary>
    public static int GetFrames(List<long> ids)
    {
        if (ids == null) return 0;
        ids.Clear();
        lock (ServerFrames.FramesLock)
        {
            var reg = SystemHost.Frames;
            if (reg == null) return 0;
            foreach (var f in reg.Frames) ids.Add(f.Id);
        }
        return ids.Count;
    }

    /// <summary>A frame: the body it orbits, how many members (grids and players), whether it is an encounter site.</summary>
    public static bool TryGetFrame(long frameId, out string body, out int memberCount, out bool isEncounter)
    {
        body = null; memberCount = 0; isEncounter = false;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.Get(frameId);
            if (f == null) return false;
            body = f.ParentBodyName; memberCount = f.MemberCount; isEncounter = f.IsEncounter;
            return true;
        }
    }

    /// <summary>A frame's members: grid member ids (from 1 000 000 000; see <see cref="GetMemberId"/>) and player ids.
    /// Cleared first; returns the count.</summary>
    public static int GetFrameMembers(long frameId, List<long> memberIds)
    {
        if (memberIds == null) return 0;
        memberIds.Clear();
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.Get(frameId);
            if (f == null) return 0;
            memberIds.AddRange(f.Members);
        }
        return memberIds.Count;
    }

    /// <summary>A frame's orbit (Keplerian elements about its body): semi-major axis (m; negative: hyperbolic),
    /// eccentricity, inclination, longitude of the ascending node, argument of periapsis, true anomaly (rad) at the
    /// epoch (universe time, s), and the body's gravitational parameter (m3/s2).</summary>
    public static bool TryGetFrameOrbit(long frameId, out double semiMajorAxis, out double eccentricity, out double inclination,
        out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)
    {
        semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = epoch = mu = 0;
        KeplerianElements el;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.Get(frameId);
            if (f == null) return false;
            el = f.Elements;
        }
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly; epoch = el.Epoch; mu = el.Mu;
        return true;
    }

    /// <summary>A frame's orbit in plain numbers: periapsis and apoapsis altitude over its body's surface (m; apoapsis
    /// infinity when hyperbolic), period (s; infinity when hyperbolic), inclination (rad).</summary>
    public static bool TryGetFrameOrbitSummary(long frameId, out double periapsisAltitude, out double apoapsisAltitude, out double period, out double inclination)
    {
        periapsisAltitude = apoapsisAltitude = period = inclination = 0;
        if (!TryGetFrameOrbit(frameId, out double a, out double e, out double i, out _, out _, out _, out _, out double mu)) return false;
        TryGetFrame(frameId, out string body, out _, out _);
        double r = body != null ? SystemHost.Registry?.FindDefinition(body)?.RadiusMeters ?? 0 : 0;
        var el = new KeplerianElements { SemiMajorAxis = a, Eccentricity = e, Mu = mu };
        periapsisAltitude = el.PeriapsisRadius - r;
        apoapsisAltitude = e < 1 ? el.ApoapsisRadius - r : double.PositiveInfinity;
        period = e < 1 ? el.Period : double.PositiveInfinity;
        inclination = i;
        return true;
    }

    /// <summary>A frame's state relative to its body (m, m/s, inertial) at a universe time (NaN: now): where it is on
    /// its orbit, propagated on rails.</summary>
    public static bool TryGetFrameState(long frameId, double time, out Vector3D position, out Vector3D velocity)
    {
        position = velocity = default;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.Get(frameId);
            if (f == null) return false;
            var s = f.StateAt(double.IsNaN(time) ? SystemHost.Now : time);
            position = s.Position; velocity = s.Velocity;
            return true;
        }
    }

    // ── grids and the player ──

    /// <summary>A grid's member id (what frames list), or -1. A grid's server copy answers directly; a client copy in
    /// the same process (single player, a host) by the nearest server grid within 20 m.</summary>
    public static long GetMemberId(Entity grid)
    {
        if (grid == null) return -1;
        foreach (var c in grid.Components) if (c is OrbitalGridComponent g && g.Id >= GridMembers.IdBase) return g.Id;
        Vector3D at;
        try { at = grid.Data.GetWorldTransform().Position; } catch (Exception) { return -1; }
        long best = -1; double bestD = 20 * 20;
        lock (ServerFrames.GridPositions)
            foreach (var kv in ServerFrames.GridPositions)
            {
                double d = (kv.Value - at).LengthSquared();
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
        return best;
    }

    /// <summary>The frame a grid is in, or -1 (not on an orbit: inside a planet's cell, or not framed).</summary>
    public static long GetFrameOf(Entity grid)
    {
        long id = GetMemberId(grid);
        if (id < 0) return -1;
        lock (ServerFrames.FramesLock) return SystemHost.Frames?.FindByMember(id)?.Id ?? -1;
    }

    /// <summary>The local player's frame, or -1 (on a planet, or not framed).</summary>
    public static long GetPlayerFrame() => FrameHost.PlayerFrame?.Id ?? -1;

    /// <summary>The planet whose cell the local player (the camera) is in, or "" (in space, a conjunction).</summary>
    public static string GetPlayerBody() => FrameHost.ObserverPlanet ?? "";

    // ── reentry ──

    /// <summary>The world's speed cap (m/s): what reentry slows a frame to before it enters a planet's cell.</summary>
    public static double GetSpeedCap() => EntryHost.Cap;

    /// <summary>A body's braking band: radii (m) of its bottom (the planet cell's border, where the game's physics
    /// takes over) and its top (the entry interface). False: no band (no atmosphere, or not built).</summary>
    public static bool TryGetEntryBand(string body, out double bottomRadius, out double topRadius)
    {
        bottomRadius = topRadius = 0;
        if (body == null || !SystemHost.Built) return false;
        var b = EntryHost.BandOf(body);
        if (!b.IsValid) return false;
        bottomRadius = b.Bottom; topRadius = b.Top;
        return true;
    }

    /// <summary>A frame's reentry now: its heat (J/kg; the tolerance unshielded is 1.15e6), whether it is being braked
    /// and how hard (m/s2).</summary>
    public static bool TryGetReentry(long frameId, out double heat, out bool braking, out double deceleration)
    {
        heat = EntryHost.HeatOf(frameId);
        braking = EntryHost.Braking(frameId, out deceleration);
        return heat > 0 || braking;
    }

    /// <summary>A grid's wear this entry: its heat tolerance (J/kg; doubled by heavy armour in front), the heat it is
    /// worn up to (J/kg), and the fraction of its forward layer worn. False: not worn this entry.</summary>
    public static bool TryGetEntryWear(Entity grid, out double tolerance, out double wornUpTo, out double worn)
    {
        tolerance = wornUpTo = worn = 0;
        long id = GetMemberId(grid);
        return id >= 0 && EntryHost.TryGetWear(id, out tolerance, out wornUpTo, out worn);
    }

    /// <summary>The plasma orbital tells the Aerodynamics Mod about for a grid (its entry source): the air past the grid
    /// (world, m/s) and how hard it burns (0..1). False: not burning now.</summary>
    public static bool TryGetEntryGlow(Entity grid, out Vector3 airWorld, out float strength)
    {
        var v = EntryHost.GlowOf(grid);
        airWorld = new Vector3(v.X, v.Y, v.Z); strength = v.W;
        return strength > 0;
    }

    /// <summary>The local player's next pass through a braking band, predicted: the body, when it starts (universe
    /// time, s), the peak braking (m/s2) and heat (J/kg), when it reaches the band's bottom and at what air speed
    /// (m/s). False: no pass ahead.</summary>
    public static bool TryGetEntryPrediction(out string body, out double enterTime, out double peakDeceleration, out double peakHeat,
        out double bottomTime, out double speedAtBottom)
    {
        var p = EntryHost.Prediction; body = EntryHost.PredictedBody;
        enterTime = peakDeceleration = peakHeat = bottomTime = speedAtBottom = 0;
        if (p == null || body == null) { body = null; return false; }
        enterTime = p.EnterTime; peakDeceleration = p.PeakDecel; peakHeat = p.PeakHeat; bottomTime = p.BottomTime; speedAtBottom = p.SpeedAtBottom;
        return true;
    }

    // ── settings ──

    /// <summary>Every setting's name (world ones first). Cleared first; returns the count.</summary>
    public static int GetSettings(List<string> names) => OrbitalSettings.Names(names);

    /// <summary>A setting's description: type ("bool", "int", "float"), default, range (a bool is 0..1), scope ("world":
    /// the same for everyone, saved with the world, set by the host / server; "client": this player's, saved on this
    /// PC), whether it applies only after a world reload, and what it does.</summary>
    public static bool TryGetSettingInfo(string name, out string type, out double defaultValue, out double min, out double max, out string scope, out bool needsReload, out string description)
        => OrbitalSettings.Info(name, out type, out defaultValue, out min, out max, out scope, out needsReload, out description);

    /// <summary>A setting's value now (a bool as 0 / 1).</summary>
    public static bool TryGetSetting(string name, out double value) => OrbitalSettings.TryGet(name, out value);

    /// <summary>Change a setting: it takes effect at once (or after a world reload, when so marked) and is saved. False,
    /// with why: no such setting, out of range, a rule between settings, or a world setting where the server does not run.</summary>
    public static bool SetSetting(string name, double value, out string why) => OrbitalSettings.Set(name, value, out why);

    /// <summary>Bumped on every change of any setting (the API, a settings file, a world load).</summary>
    public static int GetSettingsVersion() => OrbitalSettings.Version;
}
