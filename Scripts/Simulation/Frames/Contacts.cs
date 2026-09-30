using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Frames;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// CONTACTS BY SIGHT (SE1's tracking, simplified: no error bands, no sensor blocks). What is not yours
/// is unknown until you have seen it: an NPC station or encounter, another's ship (or an unowned wreck),
/// a ring rock or belt asteroid. Seen by your eyes (MinRange), or by the sensor blocks on your grids
/// (SensorBlocks): a telescope (sunlight off it, or its warmth) or a radar (its echo); always with no
/// planet or moon between. Once seen it is known for good (saved with the world): on the map, targetable, its
/// rendezvous predicted. Knowledge never feeds the physics: what is unknown is simulated all the same.
/// A new contact is announced (rocks by the batch: "Oblivara: 14 rocks spotted").
/// </summary>
public static class Contacts
{
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>Your own eyes (and the cockpit's): everything within this, in line of sight (m).</summary>
    public const double MinRange = 20000;
    // Telescope, optical (reflected sunlight: range ~ r sqrt(albedo x phase)): a sunlit 1.5 km rock ~1500 km,
    // a 150 m station ~275 km. Infrared (its own warmth, day or night: ~ r sqrt(emissivity) (T/300)^2).
    // Radar (its own echo: ~ (power x cross-section pi r^2)^1/4): a station ~300 km, a rock ~950 km at full power.
    public const double OpticalK = 2600, InfraredK = 700, Emissivity = 0.9, RadarK = 18400;
    public const double SunExclusion = 15 * Math.PI / 180;
    const double GridSize = 150, GridAlbedo = 0.5, GridTempK = 290, RockAlbedo = 0.15, RockTempK = 250, TickSeconds = 0.5;
    public enum Sensor { Eyes, Telescope, Radar }
    /// <summary>A radar of yours is transmitting: it gives you away (to whoever listens: not modelled yet).</summary>
    public static bool Loud;
    /// <summary>Harness: a stand-in sensor at the camera (contacts sensor telescope|radar|off).</summary>
    public static Sensor? DevSensor;

    /// <summary>Lit fraction seen from the observer (1: full, the sun behind you; near 0: the sun behind it).</summary>
    static double Phase(Vector3D observer, Vector3D target, Vector3D sun)
    {
        double a = Angle(sun - target, observer - target);
        return Math.Max(0.05, 0.5 * (1 + Math.Cos(a)));   // (a floor: a thin crescent still glints)
    }
    static double Angle(Vector3D a, Vector3D b)
    {
        double la = a.Length(), lb = b.Length();
        return la > 0 && lb > 0 ? Math.Acos(Math.Clamp(Vector3D.Dot(a, b) / (la * lb), -1, 1)) : 0;
    }

    static readonly HashSet<string> _rocks = new HashSet<string>();
    static readonly HashSet<long> _grids = new HashSet<long>();
    /// <summary>Changes whenever something becomes known (caches that filter by knowledge key on it).</summary>
    public static int Version;
    static double _next;

    public static bool KnownRock(string label) { if (!Enabled || label == null) return true; lock (_rocks) return _rocks.Contains(label); }
    public static bool KnownGrid(long id) { if (!Enabled) return true; lock (_grids) if (_grids.Contains(id)) return true; return !IsContact(GridMembers.Get(id)); }
    /// <summary>A frame is shown when any of its grids is known (seeing one part of a station shows it).</summary>
    public static bool KnownFrame(ProximityFrame f)
    {
        if (!Enabled || f == null) return true;
        var site = EncounterFrames.SiteOf(f.Id);
        foreach (long id in f.Members) if (GridMembers.IsGridId(id) && KnownGrid(id) && IsContact(GridMembers.Get(id))) return true;
        // a ring rock's own site (no grids yet): its rock
        return site != null && site.Label != null && KnownRock(site.Label);
    }

    /// <summary>Not yours: an NPC's or an encounter's, or owned by someone else (or no one).</summary>
    public static bool IsContact(OrbitalGridComponent g)
    {
        if (g == null || g.Entity == null) return false;
        if (EncounterFrames.IsEncounterGrid(g)) return true;
        try
        {
            var own = g.Session?.SessionComponents.TryGet<OwnershipSessionComponent>();
            var players = _session?.Get<ClientPlayersSessionComponent>();
            if (own == null || players == null) return false;
            return !own.TryGetOwnerOf(g.Entity).Equals(players.LocalPlayerIdentity);
        }
        catch { return false; }
    }

    static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>Client tick (twice a second): what is in sight now becomes known.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        _session = session;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (!Enabled || wall < _next || !SystemHost.Built) return;
        _next = wall + TickSeconds;
        var reg = SystemHost.Registry;
        if (reg == null) return;
        Vector3D eye;
        lock (ServerFrames.FramesLock) if (!FrameMarkers.ModelOf(camera.Position, t, out eye, out _, out _)) return;

        // What can stand in the way: every body (the star too); where the light comes from.
        var block = new List<(Vector3D c, double r)>();
        foreach (var b in reg.Bodies)
        {
            double r = b.IsRoot ? SystemHost.StarRadius : reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
            if (r > 0) block.Add((b.OriginInRoot(t).Position, r));
        }
        Vector3D sun = reg.Root?.OriginInRoot(t).Position ?? Vector3D.Zero;
        bool Clear(Vector3D a, Vector3D bpt, bool skipSun = false)
        {
            Vector3D d = bpt - a; double dist2 = d.LengthSquared();
            if (!(dist2 > 0)) return true;
            foreach (var (c, r) in block)
            {
                if (skipSun && (c - sun).LengthSquared() < 1) continue;
                double k = Math.Clamp(Vector3D.Dot(c - a, d) / dist2, 0, 1);
                if ((a + d * k - c).Length() < r * 0.98) return false;   // (0.98: not blocked by the ground it sits on)
            }
            return true;
        }

        // Who is looking: your eyes (where the camera is), and every working sensor on your grids.
        var yours = new Dictionary<Entity, bool>();
        foreach (var g in GridMembers.All()) if (g.IsServer && g.Entity != null) yours[g.Entity] = !IsContact(g);
        var obs = new List<(Vector3D at, Sensor kind, double power)>();
        obs.Add((eye, Sensor.Eyes, 0));
        void AddSensor(Component c, Sensor kind, bool working, double power)
        {
            try
            {
                var top = c.Entity?.GetTopLevelParent();
                if (top == null || !yours.TryGetValue(top, out bool mine) || !mine || !working) return;
                Vector3D m; bool ok;
                lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(c.Entity.Data.GetWorldTransform().Position, t, out m, out _, out _);
                if (ok) obs.Add((m, kind, power));
            }
            catch { }
        }
        lock (SensorBlocks.Telescopes) foreach (var c in SensorBlocks.Telescopes) AddSensor(c, Sensor.Telescope, c.Working, 0);
        lock (SensorBlocks.Radars) foreach (var c in SensorBlocks.Radars) AddSensor(c, Sensor.Radar, c.Working, c.Power);
        // Harness: a stand-in sensor where you are (tests the physics without building the block).
        if (DevSensor.HasValue) obs.Add((eye, DevSensor.Value, 1.0));
        Loud = obs.Exists(o => o.kind == Sensor.Radar);

        // Seen by whom (null: by none): r its radius (m), its albedo, its temperature (a powered hull, a rock).
        string Seen(Vector3D at, double r, double albedo, double tempK)
        {
            bool lit = Clear(at, sun, skipSun: true);   // not in a planet's shadow
            double ir = InfraredK * r * Math.Sqrt(Emissivity) * (tempK / 300.0) * (tempK / 300.0);
            foreach (var (o, kind, power) in obs)
            {
                double d = (at - o).Length();
                double reach;
                if (kind == Sensor.Eyes) reach = MinRange;
                else if (kind == Sensor.Radar) reach = RadarK * Math.Pow(power * Math.PI * r * r, 0.25);
                else
                {
                    // optical: sunlight off it (none in shadow, none near the sun); infrared: its own warmth
                    double optical = lit && Angle(at - o, sun - o) >= SunExclusion ? OpticalK * r * Math.Sqrt(albedo * Phase(o, at, sun)) : 0;
                    reach = Math.Max(optical, ir);
                }
                if (!(d <= reach) || !Clear(o, at)) continue;
                return kind == Sensor.Eyes ? "sight" : kind == Sensor.Radar ? "radar" : "telescope";
            }
            return null;
        }

        var newGrids = new List<(string name, double dist, string by)>();
        var newRocks = new Dictionary<string, int>();
        // Grids that are not yours.
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer) continue;
            lock (_grids) if (_grids.Contains(g.Id)) continue;
            if (!IsContact(g)) continue;
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(GridMembers.Position(g), t, out m, out _, out _);
            if (!ok) continue;
            string by = Seen(m, GridSize, GridAlbedo, GridTempK);
            if (by == null) continue;
            lock (_grids) _grids.Add(g.Id);
            newGrids.Add((g.DisplayName, (m - eye).Length(), by));
        }
        // Planet ring rocks (seeded) and belt asteroids.
        foreach (var b in RingRocks.Belts())
        {
            Vector3D o = b.Body.OriginInRoot(t).Position;
            for (int i = 0; i < b.R.Length; i++)
            {
                string label = $"{b.Name} #{b.Number[i]}";
                if (KnownRock(label)) continue;
                if (Seen(o + RingRocks.At(b, i, t), b.Cluster[i] ? 2600 : 1500, RockAlbedo, RockTempK) == null) continue;
                lock (_rocks) _rocks.Add(label);
                newRocks.TryGetValue(b.Name, out int n); newRocks[b.Name] = n + 1;
            }
        }
        foreach (var (belt, label, home, cluster) in AsteroidFrames.All())
        {
            if (KnownRock(label)) continue;
            if (Seen(SectorHomes.Where(home, reg, t), cluster ? 2600 : 1500, RockAlbedo, RockTempK) == null) continue;
            lock (_rocks) _rocks.Add(label);
            newRocks.TryGetValue(belt, out int n); newRocks[belt] = n + 1;
        }

        if (newGrids.Count > 0 || newRocks.Count > 0)
        {
            Version++;
            var lines = new List<string>();
            foreach (var (name, dist, by) in newGrids) lines.Add($"{name}  ·  {HudPanel.Km(dist)}  ·  {by}");
            foreach (var kv in newRocks) lines.Add(kv.Value == 1 ? $"{kv.Key}: a rock spotted" : $"{kv.Key}: {kv.Value} rocks spotted");
            if (lines.Count > 4) { int more = lines.Count - 3; lines.RemoveRange(3, lines.Count - 3); lines.Add($"and {more} more"); }
            if (!MapView.Visible) GameUi.Toast(session, "contact", newGrids.Count > 0 ? "Contact" : "Asteroids", string.Join("\n", lines), 4);
            Log.Default?.Info("[ORBIT-CONTACT] " + string.Join("; ", lines));
        }
        int ng; lock (_grids) ng = _grids.Count;
        int nr; lock (_rocks) nr = _rocks.Count;
        int nt = obs.FindAll(o => o.kind == Sensor.Telescope).Count, nrad = obs.FindAll(o => o.kind == Sensor.Radar).Count;
        Status = $"known: {ng} grid(s), {nr} rock(s); looking: eyes, {nt} telescope(s), {nrad} radar(s){(Loud ? " (loud)" : "")}";
    }

    // ── save / load ──
    public static List<string> RockKeys() { lock (_rocks) return new List<string>(_rocks); }
    public static List<long> GridKeys() { lock (_grids) return new List<long>(_grids); }
    public static void RestoreRock(string label) { lock (_rocks) _rocks.Add(label); Version++; }
    public static void RestoreGrid(long id) { lock (_grids) _grids.Add(id); Version++; }

    /// <summary>Harness: contacts reveal | forget | (status).</summary>
    public static string Command(string[] a)
    {
        switch (a.Length > 1 ? a[1] : "")
        {
            case "forget": lock (_rocks) _rocks.Clear(); lock (_grids) _grids.Clear(); Version++; return "contacts forgotten";
            case "reveal":
                lock (_rocks)
                {
                    foreach (var b in RingRocks.Belts()) for (int i = 0; i < b.R.Length; i++) _rocks.Add($"{b.Name} #{b.Number[i]}");
                    foreach (var r in AsteroidFrames.All()) _rocks.Add(r.label);
                }
                foreach (var g in GridMembers.All()) if (g.IsServer && IsContact(g)) lock (_grids) _grids.Add(g.Id);
                Version++; return "all revealed | " + Status;
            case "sensor": DevSensor = a.Length > 2 && a[2] == "telescope" ? Sensor.Telescope : a.Length > 2 && a[2] == "radar" ? Sensor.Radar : (Sensor?)null; return "stand-in sensor: " + (DevSensor?.ToString() ?? "none");
            case "on": Enabled = true; Version++; return "contacts on";
            case "off": Enabled = false; Version++; return "contacts off (everything known)";
        }
        int unknown = 0;
        foreach (var g in GridMembers.All()) if (g.IsServer && IsContact(g) && !KnownGrid(g.Id)) unknown++;
        return $"{Status}; {unknown} contact grid(s) unknown";
    }
}
