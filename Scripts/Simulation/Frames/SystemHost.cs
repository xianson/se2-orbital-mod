using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Builds and owns the celestial layer for an SE2 world: the star system, the universe clock,
/// the planet cells and the frame registry. The SE-Aerospace design, adapted to SE2 in three places:
///
///  1. The system is built FROM THE WORLD: each SE2 planet (found through its server beacon) becomes
///     a body orbiting a synthetic star. Its gravitational parameter matches the physics the engine
///     actually applies after the inverse-square patch: mu = worldMultiplier * g0 * r0^2.
///  2. Planet cells are PINNED to where the planets already sit (SE2 planets are not spawned or moved
///     by this mod). The conjunction lattice is placed far from every planet.
///  3. Planet gravity is patched to inverse-square (falloff 2) with a reach covering the cell, as
///     SE-Aerospace's PlanetGravity did on SE1.
///
/// SINGLE PLAYER / LISTEN HOST: built from the server beacons (same process). See README.
/// </summary>
public static class SystemHost
{
    /// <summary>Synthetic star: mu = g * R^2 = 28 * (6000 km)^2 ~ 1.0e15 (SampleSystems.Sol).</summary>
    /// <summary>The sun's mass as in SE-Aerospace (28 m/s^2 at its k-scaled radius); kept when the orbits shrink.</summary>
    /// <summary>
    /// Delfos, a red dwarf, and a compact system about it: light enough that each planet's own space
    /// (its sphere of influence) is about an eighth of its orbit, so a planet's system and the star's
    /// read on one map (with the sun's mass the planets' systems were ~2% of their orbits: specks).
    /// </summary>
    public const double StarMu = 1.41e13;
    public const double StarSurfaceGravity = StarMu / (StarRadius * StarRadius);
    /// <summary>The system scale (SE-Aerospace's rule): Verdure plays Earth, k = 63 km / 6371 km.</summary>
    public const double K = 63.0 / 6371.0;
    /// <summary>Orbits (and the sun's radius) a further 10x smaller than k: a compact, KSP-like system.</summary>
    public const double OrbitScale = 0.1;
    /// <summary>Sectors about a planet: their charted distance x this (less shrunk than the planets' own
    /// orbits: at 0.1 Kemik's sectors crowded within 800 km). Kemik's land at ~1,300-3,200 km (apoapses under ~3,900), inside its 4,574 km SOI.</summary>
    /// <summary>A body's name as shown to the player: the root is the star, named as the game names it.</summary>
    public static string DisplayName(string body) => body == Registry?.Root?.Name ? CleanMap.StarName : body;

    /// <summary>Warp stops here (game time), NaN for none: 'Warp here' on the map.</summary>
    public static double WarpStopAt = double.NaN;

    public const double SectorOrbitScale = 0.4;
    /// <summary>The system's unit distance: Verdure's orbit (30,000 km; Kemik 1.524 of it, the belt 2.2-3.2, the ring 3.9).</summary>
    public const double AU = 3.0e7;
    /// <summary>Delfos's radius: a red dwarf, some twenty radii from Verdure.</summary>
    public const double StarRadius = 1.5e6;
    /// <summary>First planet's orbit around the star, and the spacing factor for the next ones.</summary>
    public const double FirstOrbit = AU;                  // Verdure = Earth, 1 AU
    public const double OrbitSpacing = 1.524;             // Kemik = Mars, 1.524 AU
    /// <summary>Atmosphere height as a fraction of r0 (SE2 planets expose no clean atmosphere top).</summary>
    public const double AtmosphereFraction = 0.10;
    /// <summary>Wait this long after the last beacon appears before building (all planets loaded).</summary>
    public const double SettleSeconds = 2.0;

    public static bool Built { get; private set; }
    public static SystemRegistry Registry => SystemRegistry.Active;
    public static FrameRegistry Frames => FrameRegistry.Active;

    /// <summary>Planet body name -> its server beacon (world centre, gravity law).</summary>
    public static readonly Dictionary<string, PlanetBeacon> BeaconOf = new Dictionary<string, PlanetBeacon>();

    private static long _firstSeen;
    private static int _lastCount;
    private static readonly object _lock = new object();

    // ─────────────────────────── universe clock ───────────────────────────
    /// <summary>Universe time, seconds. Advances by real dt × <see cref="Timescale"/>.</summary>
    public static double Now { get; private set; }
    /// <summary>Time warp. 1 = real time. Warp only advances the celestial layer, never Havok.</summary>
    public static double Timescale = 1.0;
    private static long _lastClockTicks;

    private static DateTime _lastGameTime;

    /// <summary>Legacy space: the nearest planet cell (the window this world region belongs to).</summary>
    public static bool TryNearestCell(Vector3D pos, out string body, out Vector3D cell)
    {
        body = null; cell = default;
        double best = double.MaxValue;
        foreach (var kv in SEAerospace.VoxelBerthRegistry.PinnedCells)
        {
            double d = (kv.Value - pos).LengthSquared();
            if (d < best) { best = d; body = kv.Key; cell = kv.Value; }
        }
        return body != null;
    }

    /// <summary>The world's sun period (seconds, 0 = its sun does not rotate); set by the client host.</summary>
    public static double WorldSunPeriod;
    public static readonly string[] PlanetOrder = { "Verdure", "Kemik" };

    /// <summary>Palatine plays the Moon (384,400 km x k); other moons keep their charted distance (Caligo is Phobos-like).</summary>
    /// <summary>Caligo's charted distance (~195 km) sat it almost on Kemik; three times out (~585 km) it reads as a moon.</summary>
    public const double CaligoOrbitFactor = 3.0;
    private static double MoonOrbit(string name, double charted) =>
        name == "Palatine" ? 3.844e8 * K * OrbitScale : name == "Caligo" ? charted * CaligoOrbitFactor : charted;
    private static double MoonEccentricity(string name) => name == "Palatine" ? 0.0549 : 0.015;
    private static double PlanetEccentricity(string name) => name == "Verdure" ? 0.0167 : name == "Kemik" ? 0.0934 : 0.02;
    public const double MoonMaxDistance = 1.0e6;   // m
    public const double MoonMassRatio = 10.0;
    public static int MoonCount;

    private static double PlanetDay()
    {
        double d = OrbitalConfig.PlanetDaySeconds;
        if (d >= 0) return d;
        return WorldSunPeriod > 0 ? WorldSunPeriod : 0;
    }

    /// <summary>DEV: jump the universe clock (rails, sectors, planets) forward or back.</summary>
    public static void DevAdvanceClock(double seconds) { Now += seconds; }

    /// <summary>Load: continue the rails clock from the saved universe time.</summary>
    public static void RestoreClock(double t) { if (!double.IsNaN(t) && !double.IsInfinity(t)) Now = t; }
    /// <summary>Which clock drove the last advance ("game" = IGameTime, "wall" = fallback).</summary>
    public static string ClockSource = "-";

    /// <summary>
    /// Advance the clock once per frame (idempotent: a second call in the same frame sees no game time
    /// pass). Driven by the game's own time (IGameTime: synced client/server, follows game speed,
    /// pauses with the game), so the rails and the physics agree on how much time passed; the
    /// physics steps gravity on game time, not wall time. Falls back to the wall clock.
    /// </summary>
    public static double AdvanceClock(Keen.VRage.Core.Game.Systems.Session session = null)
    {
        double dt;
        Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime gt = null;
        try { gt = session?.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>(); } catch { gt = null; }
        if (gt != null)
        {
            DateTime g = gt.CurrentGameTime;
            if (_lastGameTime == default) { _lastGameTime = g; return 0; }
            dt = (g - _lastGameTime).TotalSeconds;
            if (dt <= 0) return 0;
            _lastGameTime = g;
            ClockSource = "game";
        }
        else
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastClockTicks == 0) { _lastClockTicks = now; return 0; }
            dt = (now - _lastClockTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (dt < 0.002) return 0;
            _lastClockTicks = now;
            ClockSource = "wall";
        }
        if (dt > 0.25) dt = 0.25; // a stall must not throw rails forward
        double next = Now + dt * Timescale;
        // Warp policy: the clock never steps past an arrival. Stop exactly at the earliest inbound
        // shell crossing and drop to x1, so the arrival checks (client and server, whatever their
        // tick rate) see the crossing instead of jumping over it.
        if (Timescale > 1.0 && Built && Frames != null)
        {
            double tc = double.NaN;
            lock (ServerFrames.FramesLock) tc = FrameHost.EarliestArrival(Now, next);
            if (!double.IsNaN(tc))
            {
                next = Math.Max(Now, tc);
                Log.Default?.Info($"[ORBIT-FRAME] warp x{Timescale} -> x1 at the shell crossing (t={tc:F1})");
                Timescale = 1.0;
            }
        }
        // Warp also stops a lead time before the next maneuver node.
        if (Timescale > 1.0)
        {
            double tn = Maneuvers.NextBurnStart(Now);
            if (!double.IsNaN(tn) && tn - Maneuvers.WarpLead <= next)
            {
                next = Math.Max(Now, tn - Maneuvers.WarpLead);
                Log.Default?.Info($"[ORBIT-FRAME] warp x{Timescale} -> x1 ahead of a maneuver node (t={tn:F1})");
                Timescale = 1.0;
            }
        }
        // And at a time asked for ('Warp here' on the map).
        if (Timescale > 1.0 && !double.IsNaN(WarpStopAt) && WarpStopAt <= next)
        {
            next = Math.Max(Now, WarpStopAt);
            Log.Default?.Info($"[ORBIT-FRAME] warp x{Timescale} -> x1 at the point asked for (t={WarpStopAt:F1})");
            Timescale = 1.0; WarpStopAt = double.NaN;
            WarpControl.Say("Warp stopped: arrived");
        }
        if (Timescale <= 1.0) WarpStopAt = double.NaN;
        // And at a sphere-of-influence change on your path (as KSP): a flyby is seen, not skipped
        // (at x10000 one frame took the whole Palatine pass, periapsis and all).
        if (Timescale > 1.0 && FrameHost.PlayerFrame != null)
        {
            double ts = double.NaN;
            try { ts = Maneuvers.NextSoiChange(Now); }
            catch (Exception ex) { FrameHost.Fault("warp SOI stop", ex); }
            if (!double.IsNaN(ts) && ts <= next)
            {
                next = Math.Max(Now, ts + 1.0);
                Log.Default?.Info($"[ORBIT-FRAME] warp x{Timescale} -> x1 at a sphere-of-influence change (t={ts:F1})");
                Timescale = 1.0;
                WarpControl.Say("Warp stopped: sphere of influence change");
            }
        }
        Now = next;
        return dt;
    }

    /// <summary>Try to build once all planets have reported. Returns true when the system exists.</summary>
    public static bool EnsureBuilt(double gravityMultiplier)
    {
        if (Built) return true;
        lock (_lock)
        {
            if (Built) return true;
            var beacons = PlanetBeacons.All();
            if (beacons.Count == 0) return false;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (beacons.Count != _lastCount) { _lastCount = beacons.Count; _firstSeen = now; return false; }
            if ((now - _firstSeen) / (double)System.Diagnostics.Stopwatch.Frequency < SettleSeconds) return false;
            foreach (var b in beacons) if (b.OriginalGravity.R0 <= 0) return false; // law not read yet

            Build(beacons, gravityMultiplier > 0 ? gravityMultiplier : 1.0);
            return Built;
        }
    }

    private static void Build(List<PlanetBeacon> beacons, double mult)
    {
        // Stable order: by name, so every build (and every client) agrees.
        // Stable order: the known planets first (Verdure inside Kemik), then by name, so every build agrees.
        int Rank(PlanetBeacon p) { int i = Array.IndexOf(PlanetOrder, DevHarness.PlanetName(p)); return i < 0 ? PlanetOrder.Length : i; }
        beacons.Sort((a, b) => { int r = Rank(a).CompareTo(Rank(b)); return r != 0 ? r : string.CompareOrdinal(DevHarness.PlanetName(a), DevHarness.PlanetName(b)); });

        var def = new SystemDefinition { Name = "SE2World", EpochSeconds = 0.0 };
        def.Bodies.Add(new BodyDefinition
        {
            Name = "Star", Parent = "", HasOrbit = false,
            SurfaceGravityMps2 = StarSurfaceGravity, RadiusMeters = StarRadius,
        });

        double a = FirstOrbit;
        double anomaly = 0;
        BeaconOf.Clear();
        var nameOf = new Dictionary<PlanetBeacon, string>();
        foreach (var b in beacons)
        {
            string name = DevHarness.PlanetName(b);
            if (BeaconOf.ContainsKey(name)) name = name + "_" + BeaconOf.Count;
            BeaconOf[name] = b;
            nameOf[b] = name;
        }
        // Moons: a body within MoonMaxDistance of one at least MoonMassRatio heavier orbits the
        // nearest such body (SE2's Palatine sits 229 km from Verdure, Caligo 195 km from Kemik).
        double Mu(PlanetBeacon p) => p.OriginalGravity.G0 * mult * p.OriginalGravity.R0 * p.OriginalGravity.R0;
        var parentOf = new Dictionary<PlanetBeacon, PlanetBeacon>();
        foreach (var m in beacons)
        {
            PlanetBeacon best = null; double bestD = MoonMaxDistance;
            foreach (var p in beacons)
            {
                if (p == m || Mu(p) < MoonMassRatio * Mu(m)) continue;
                double d = (p.Center - m.Center).Length();
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best != null) parentOf[m] = best;
        }
        MoonCount = parentOf.Count;
        foreach (var b in beacons)
        {
            string name = nameOf[b];
            var law = b.OriginalGravity;
            if (parentOf.TryGetValue(b, out var host))
            {
                def.Bodies.Add(new BodyDefinition
                {
                    Name = name, Parent = nameOf[host], HasOrbit = true,
                    SemiMajorAxisMeters = MoonOrbit(name, (b.Center - host.Center).Length()), Eccentricity = MoonEccentricity(name), MeanAnomalyAtEpochDeg = 90,
                    SurfaceGravityMps2 = law.G0 * mult, RadiusMeters = law.R0,
                    HasAtmosphere = true, AtmosphereHeightMeters = law.R0 * AtmosphereFraction,
                    RotationPeriodSeconds = PlanetDay(),
                    ParkSubtype = "SE2:" + name,
                });
                continue;
            }
            def.Bodies.Add(new BodyDefinition
            {
                Name = name, Parent = "Star", HasOrbit = true,
                SemiMajorAxisMeters = a, Eccentricity = PlanetEccentricity(name), MeanAnomalyAtEpochDeg = anomaly,
                // Match the engine's physics after the inverse-square patch.
                SurfaceGravityMps2 = law.G0 * mult, RadiusMeters = law.R0,
                HasAtmosphere = true, AtmosphereHeightMeters = law.R0 * AtmosphereFraction,
                RotationPeriodSeconds = PlanetDay(), // the cell is the rotating chart (see Chart)
                ParkSubtype = "SE2:" + name,
            });
            a *= OrbitSpacing;
            anomaly += 137.5;
        }

        // Conjunction lattice far from every planet: beyond the farthest one, along +Y.
        double far = 0;
        foreach (var b in beacons) far = Math.Max(far, b.Center.Length());
        PlanetBerths.CurrentBerth = new Vector3D(0, far + 2.0e6, 0);

        VoxelBerthRegistry.Clear();
        SystemBuildResult res = SystemRegistry.Build(def);
        if (!res.Ok)
        {
            Log.Default?.Warning($"[ORBIT] system build failed: {res.Error}");
            return;
        }
        var reg = SystemRegistry.Active;
        foreach (var kv in BeaconOf) VoxelBerthRegistry.PinCell(kv.Key, kv.Value.Center);

        var alloc = VoxelBerthRegistry.SharedAllocator(reg);
        FrameRegistry.Publish(new FrameRegistry(alloc));

        // Inverse-square gravity in each planet cell, reaching across the cell.
        foreach (var kv in BeaconOf)
        {
            var node = reg.Find(kv.Key);
            var bdef = reg.FindDefinition(kv.Key);
            double reach = Math.Min(node.SoiRadius, alloc.SlotRadius);
            // Stop short of every neighbour's own sphere of influence: standing on a moon you feel
            // the moon (the planet's pull is the frame's free fall, not a constant tug).
            foreach (var other in BeaconOf)
            {
                if (other.Key == kv.Key) continue;
                var on = reg.Find(other.Key);
                // The neighbour's claim on the world between us: its SOI, but at least its own keep and
                // at most what my keep leaves. (A moon moved out in the model, e.g. Caligo x3, keeps its
                // charted spot in the world: its model SOI can exceed the world gap.)
                double sep = (other.Value.Center - kv.Value.Center).Length();
                double onKeep = PlanetBerths.KeepRadius(reg.FindDefinition(other.Key));
                double claim = Math.Min(on.SoiRadius, alloc.SlotRadius);
                claim = Math.Max(onKeep, Math.Min(claim, sep - PlanetBerths.KeepRadius(bdef)));
                double gap = sep - claim;
                if (gap > 0) reach = Math.Min(reach, gap);
            }
            reach = Math.Max(reach, PlanetBerths.KeepRadius(bdef));
            kv.Value.PendingGravity = new GravityRequest { Falloff = 2f, Reach = (float)reach };
            Log.Default?.Info($"[ORBIT] body {kv.Key}: mu={node.Mu:E3} soi={node.SoiRadius / 1000:F0} km " +
                              $"shell={PlanetBerths.ShellRadius(bdef) / 1000:F1} km keep={PlanetBerths.KeepRadius(bdef) / 1000:F1} km " +
                              $"cell={ServerPlanetBeacon.Fmt(kv.Value.Center)} gravity reach={reach / 1000:F0} km");
        }
        Built = true;
        Log.Default?.Info($"[ORBIT] system built: {BeaconOf.Count} planets around Star, world gravity multiplier {mult}, " +
                          $"conjunction lattice at {ServerPlanetBeacon.Fmt(PlanetBerths.CurrentBerth)} slot radius {alloc.SlotRadius / 1000:F0} km");
    }

    /// <summary>A planet body by its beacon (for renderers).</summary>
    public static string BodyNameOf(PlanetBeacon beacon)
    {
        foreach (var kv in BeaconOf) if (ReferenceEquals(kv.Value, beacon)) return kv.Key;
        return null;
    }
}
