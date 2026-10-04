using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.Sensing;

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
    private static double _lastWatchT = double.NaN;
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>Your own eyes (and the cockpit's): everything within this, in line of sight (m).</summary>
    public const double MinRange = SensorModel.EyeRange;
    // (the sensor physics: Core/Sensing/SensorModel, tested offline in Tests/SensingTests)
    const double GridSize = 150, GridAlbedo = 0.5, GridTempK = 290, RockAlbedo = 0.15, RockTempK = 250, TickSeconds = 0.5;
    public enum Sensor { Eyes, Telescope, Radar }
    /// <summary>A radar of yours is transmitting: it gives you away (to whoever listens: not modelled yet).</summary>
    public static bool Loud;
    /// <summary>Your strongest radar's power now (0 when none transmits).</summary>
    public static double RadarPowerNow;
    /// <summary>For the orbit card: you are transmitting (radar or lidar), and roughly how far off you can be
    /// seen (an echo-free listener hears you twice as far as your radar sees a ship), or null when silent.</summary>
    public static string LoudLine()
    {
        if (!Enabled || !Loud) return null;
        if (RadarPowerNow > 0)
        {
            double reveal = 2 * SensorModel.RadarReach(GridSize, RadarPowerNow);
            return $"Radar transmitting  ·  seen ~{HudPanel.Km(SEAerospace.Sensing.Tracking.Rough(reveal))} away" + (Lidar ? "  ·  lidar on target" : "");
        }
        return Lidar ? "Lidar ranging your target  ·  it can see you" : null;
    }

    /// <summary>Harness: a stand-in sensor at the camera (contacts sensor telescope|radar|off).</summary>
    public static Sensor? DevSensor;


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
        var bodies = new List<SensorModel.Body>();
        foreach (var b in reg.Bodies)
        {
            double r = b.IsRoot ? SystemHost.StarRadius : reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
            if (r > 0) bodies.Add(new SensorModel.Body { Centre = b.OriginInRoot(t).Position, Radius = r, IsSun = b.IsRoot, Albedo = reg.FindDefinition(b.Name)?.Albedo ?? 0 });
        }
        Vector3D sun = reg.Root?.OriginInRoot(t).Position ?? Vector3D.Zero;

        // Who is looking: your eyes (where the camera is), and every working sensor on your grids.
        var yours = new Dictionary<Entity, bool>();
        foreach (var g in GridMembers.All()) if (g.IsServer && g.Entity != null) yours[g.Entity] = !IsContact(g);
        var obs = new List<SensorModel.Looker> { new SensorModel.Looker { At = eye, Kind = SensorModel.Kind.Eyes } };
        void AddSensor(Component c, SensorModel.Kind kind, bool working, double power)
        {
            try
            {
                var top = c.Entity?.GetTopLevelParent();
                if (top == null || !yours.TryGetValue(top, out bool mine) || !mine || !working) return;
                Vector3D m; bool ok;
                lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(c.Entity.Data.GetWorldTransform().Position, t, out m, out _, out _);
                if (ok) obs.Add(new SensorModel.Looker { At = m, Kind = kind, Power = power });
            }
            catch { }
        }
        // (copied under their own locks, used outside them: AddSensor takes FramesLock, and the save (server thread) takes
        //  FramesLock then the Radars lock - holding Radars here while taking FramesLock deadlocked a save with the
        //  contacts tick)
        TelescopeComponent[] scopes; lock (SensorBlocks.Telescopes) scopes = SensorBlocks.Telescopes.ToArray();
        RadarComponent[] radars; lock (SensorBlocks.Radars) radars = SensorBlocks.Radars.ToArray();
        foreach (var c in scopes) AddSensor(c, SensorModel.Kind.Telescope, c.Working, 0);
        foreach (var c in radars) AddSensor(c, SensorModel.Kind.Radar, c.Working, c.Power);
        // Harness: a stand-in sensor where you are (tests the physics without building the block).
        if (DevSensor.HasValue) obs.Add(new SensorModel.Looker { At = eye, Kind = DevSensor.Value == Sensor.Radar ? SensorModel.Kind.Radar : SensorModel.Kind.Telescope, Power = 1 });
        Loud = obs.Exists(o => o.Kind == SensorModel.Kind.Radar);
        RadarPowerNow = 0; foreach (var o in obs) if (o.Kind == SensorModel.Kind.Radar && o.Power > RadarPowerNow) RadarPowerNow = o.Power;

        // Seen by whom (null: by none): r its radius (m), its albedo, its temperature (a powered hull, a rock).
        string Seen(Vector3D at, double r, double albedo, double tempK) { var k = SeenKind(at, r, albedo, tempK); return k == null ? null : Name(k.Value); }
        SensorModel.Kind? SeenKind(Vector3D at, double r, double albedo, double tempK)
        {
            int i = SensorModel.SeenBy(obs, new SensorModel.Target { At = at, Radius = r, Albedo = albedo, TempK = tempK }, bodies, sun);
            return i < 0 ? (SensorModel.Kind?)null : obs[i].Kind;
        }
        static string Name(SensorModel.Kind k) => k == SensorModel.Kind.Eyes ? "sight" : k == SensorModel.Kind.Radar ? "radar" : "telescope";
        // Watching: what is seen and not yet tracked is watched (the telescope's lidar on your target speeds it).
        string target = Maneuvers.Target;
        bool lidar = false;
        // (watching time is the sim's: none while paused, faster under warp - it was a fixed 0.5 s of wall time per tick;
        //  capped so one clock jump does not track everything at once)
        double simNow = SystemHost.Now, watchDt = double.IsNaN(_lastWatchT) ? TickSeconds : Math.Max(0, Math.Min(60, simNow - _lastWatchT));
        _lastWatchT = simNow;
        var newlyTracked = new List<string>();
        void Watch(string key, string name, SensorModel.Kind k)
        {
            bool targeted = target != null && name == target;
            if (targeted && k == SensorModel.Kind.Telescope) lidar = true;
            lock (_dwell)
            {
                _dwell.TryGetValue(key, out double d0);
                if (d0 >= Tracking.PassiveSeconds) return;
                double d1 = Tracking.Watch(d0, k, targeted, watchDt);
                _dwell[key] = d1;
                if (d1 >= Tracking.PassiveSeconds) { newlyTracked.Add(name); Version++; }
            }
        }


        var newGrids = new List<(string name, double dist, string by)>();
        var newRocks = new Dictionary<string, int>();
        // Grids that are not yours.
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer) continue;
            bool known; lock (_grids) known = _grids.Contains(g.Id);
            if (known && TrackedGrid(g.Id)) continue;
            if (!IsContact(g)) continue;
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(GridMembers.Position(g), t, out m, out _, out _);
            if (!ok) continue;
            var k = SeenKind(m, GridSize, GridAlbedo, GridTempK);
            if (k == null) continue;
            Watch("g:" + g.Id, GroupName(g), k.Value);
            if (known) continue;
            lock (_grids) _grids.Add(g.Id);
            newGrids.Add((g.DisplayName, (m - eye).Length(), Name(k.Value)));
        }
        // Planet ring rocks (seeded) and belt asteroids.
        foreach (var b in RingRocks.Belts())
        {
            Vector3D o = b.Body.OriginInRoot(t).Position;
            for (int i = 0; i < b.R.Length; i++)
            {
                string label = $"{b.Name} #{b.Number[i]}";
                bool known = KnownRock(label);
                if (known && TrackedRock(label)) continue;
                var k = SeenKind(o + RingRocks.At(b, i, t), b.Cluster[i] ? 2600 : 1500, RockAlbedo, RockTempK);
                if (k == null) continue;
                Watch("r:" + label, label, k.Value);
                if (known) continue;
                lock (_rocks) _rocks.Add(label);
                newRocks.TryGetValue(b.Name, out int n); newRocks[b.Name] = n + 1;
            }
        }
        foreach (var (belt, label, home, cluster) in AsteroidFrames.All())
        {
            bool known = KnownRock(label);
            if (known && TrackedRock(label)) continue;
            var k = SeenKind(SectorHomes.Where(home, reg, t), cluster ? 2600 : 1500, RockAlbedo, RockTempK);
            if (k == null) continue;
            Watch("r:" + label, label, k.Value);
            if (known) continue;
            lock (_rocks) _rocks.Add(label);
            newRocks.TryGetValue(belt, out int n); newRocks[belt] = n + 1;
        }

        Lidar = lidar;
        if (lidar) Loud = true;
        if (newlyTracked.Count > 0)
        {
            int rocks = newlyTracked.FindAll(x => x.Contains(" #")).Count;
            var tl = newlyTracked.FindAll(x => !x.Contains(" #"));
            if (rocks > 0) tl.Add(rocks == 1 ? "a rock's orbit" : $"{rocks} rocks' orbits");
            if (tl.Count > 3) { int more = tl.Count - 2; tl.RemoveRange(2, tl.Count - 2); tl.Add($"and {more} more"); }
            if (!MapView.Visible && newGrids.Count == 0 && newRocks.Count == 0) GameUi.Toast(session, "tracked", "Tracked", string.Join("\n", tl), 3);
            Log.Default?.Info("[ORBIT-CONTACT] tracked: " + string.Join("; ", tl));
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
        int nt = obs.FindAll(o => o.Kind == SensorModel.Kind.Telescope).Count, nrad = obs.FindAll(o => o.Kind == SensorModel.Kind.Radar).Count;
        int ntr; lock (_dwell) { ntr = 0; foreach (var kv in _dwell) if (kv.Value >= Tracking.PassiveSeconds) ntr++; }
        Status = $"known: {ng} grid(s), {nr} rock(s), {ntr} tracked; looking: eyes, {nt} telescope(s), {nrad} radar(s){(lidar ? ", lidar on the target" : "")}{(Loud ? " (loud)" : "")}";
    }

    // ── tracking (Core/Sensing/Tracking: detected -> tracked) ──
    static readonly Dictionary<string, double> _dwell = new Dictionary<string, double>();
    /// <summary>A telescope of yours is ranging your target with its laser (lidar): it gives you away.</summary>
    public static bool Lidar;
    static double DwellOf(string key) { if (!Enabled) return Tracking.PassiveSeconds; lock (_dwell) return _dwell.TryGetValue(key, out double d) ? d : 0; }
    public static bool TrackedRock(string label) => !Enabled || label == null || DwellOf("r:" + label) >= Tracking.PassiveSeconds;
    public static bool TrackedGrid(long id) => !Enabled || DwellOf("g:" + id) >= Tracking.PassiveSeconds || !IsContact(GridMembers.Get(id));
    /// <summary>A rock target's tracking: null when it is not a rock contact (a sector, a body) or tracking is off.</summary>
    public static double? RockProgress(string label)
    {
        if (!Enabled || label == null) return null;
        bool isRock = false;
        foreach (var b in RingRocks.Belts()) if (label.StartsWith(b.Name + " #")) isRock = true;
        if (!isRock) foreach (var r in AsteroidFrames.All()) if (r.label == label) isRock = true;
        return isRock ? Tracking.Progress(DwellOf("r:" + label)) : (double?)null;
    }
    /// <summary>The name a grid is targeted and marked by: its site's label, else its own.</summary>
    static string GroupName(OrbitalGridComponent g)
    {
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.FindByMember(g.Id);
            var site = f != null ? EncounterFrames.SiteOf(f.Id) : null;
            if (site?.Label != null) return site.Label;
        }
        return g.DisplayName;
    }

    /// <summary>The grids you know of (ids), for the flight HUD's contact markers.</summary>
    public static List<long> KnownGrids() { lock (_grids) return new List<long>(_grids); }

    // ── save / load ──
    public static List<string> RockKeys() { lock (_rocks) return new List<string>(_rocks); }
    public static List<long> GridKeys() { lock (_grids) return new List<long>(_grids); }
    public static void RestoreRock(string label) { lock (_rocks) _rocks.Add(label); Version++; }
    public static List<string> TrackedKeys() { var l = new List<string>(); lock (_dwell) foreach (var kv in _dwell) if (kv.Value >= Tracking.PassiveSeconds) l.Add(kv.Key); return l; }
    public static void RestoreTracked(string key) { lock (_dwell) _dwell[key] = Tracking.PassiveSeconds; Version++; }
    public static void RestoreGrid(long id) { lock (_grids) _grids.Add(id); Version++; }

    /// <summary>Harness: contacts reveal | forget | (status).</summary>
    public static string Command(string[] a)
    {
        switch (a.Length > 1 ? a[1] : "")
        {
            case "forget": lock (_rocks) _rocks.Clear(); lock (_grids) _grids.Clear(); lock (_dwell) _dwell.Clear(); Version++; return "contacts forgotten";
            case "reveal":
                lock (_rocks)
                {
                    foreach (var b in RingRocks.Belts()) for (int i = 0; i < b.R.Length; i++) _rocks.Add($"{b.Name} #{b.Number[i]}");
                    foreach (var r in AsteroidFrames.All()) _rocks.Add(r.label);
                }
                foreach (var g in GridMembers.All()) if (g.IsServer && IsContact(g)) { lock (_grids) _grids.Add(g.Id); lock (_dwell) _dwell["g:" + g.Id] = Tracking.PassiveSeconds; }
                lock (_rocks) lock (_dwell) foreach (var r in _rocks) _dwell["r:" + r] = Tracking.PassiveSeconds;
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
