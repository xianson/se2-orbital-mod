using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Frames;
using SEAerospace.Sensing;
using SEAerospace.Flight;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// CONTACTS BY SIGHT (SE1's tracking, simplified: no error bands). What is not yours is unknown until you have
/// seen it: an NPC station or encounter, another's ship (or an unowned wreck - no owner is not yours), a ring rock or
/// belt asteroid. Seen by your eyes (MinRange), or by the sensor blocks on your grids (SensorBlocks): a telescope
/// (sunlight off it, its warmth, its engines' plume, or its radar heard) or a radar (its echo, or another's radar
/// heard); always with no planet or moon between. Each ship's SIGNATURE is its own (Core/Sensing/Signature, from the
/// server's snapshot: its box, its firing thrusters, its transmitting radars) - it was a fixed 150 m / 290 K ball.
/// Once seen it is known (saved with the world): on the map, targetable; its rendezvous predicted once TRACKED (an
/// orbit fixed - Core/Sensing/Tracking; detected only: a bearing and a rough range, "position only"). Contacts AGE
/// (Core/Sensing/Aging): when each was last seen is kept; a moving ship unseen for StaleMinutes is stale (its orbit
/// an estimate), for ForgetHours forgotten. Knowledge never feeds the physics: what is unknown is simulated all the same.
/// A new ship is announced at once; rocks by one summary a pass ("Oblivara: 14 rocks spotted" - Aging.Batcher).
/// YOU ARE LOUD while a radar of yours transmits (or a telescope's lidar ranges your target): LoudLine, and a toast
/// when it starts - anyone's telescope or radar hears it far off (Signature.EmissionReach).
/// </summary>
public static class Contacts
{
    private static double _lastWatchT = double.NaN;
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>Your own eyes (and the cockpit's): everything within this, in line of sight (m).</summary>
    public const double MinRange = SensorModel.EyeRange;
    // (the sensor physics: Core/Sensing/SensorModel, tested offline in Tests/SensingTests)
    // (a hull's albedo and warmth, a rock's; a ship's SIZE and HEAT are its own now - GridTarget)
    const double GridAlbedo = 0.5, GridTempK = 290, RockAlbedo = 0.15, RockTempK = 250, TickSeconds = 0.5;
    public enum Sensor { Eyes, Telescope, Radar }
    /// <summary>A radar of yours is transmitting (or a telescope's lidar ranges your target): it gives you away - every
    /// telescope and radar within <see cref="LoudReach"/> hears it (Signature.EmissionReach; they see you as you see them).</summary>
    public static bool Loud;
    /// <summary>How far your loudest radar is heard now (m; 0 quiet), and the line the HUD shows for it (null: quiet).</summary>
    public static double LoudReach;
    public static string LoudLine;
    static bool _wasLoud;
    /// <summary>A moving ship contact not seen for this long (game minutes) is STALE: its orbit shown as an estimate, its
    /// marker faded (0: never). A setting (Hud.ContactStaleMinutes).</summary>
    public static double StaleMinutes = 30;
    /// <summary>A moving ship contact not seen for this long (game hours) is forgotten - find it again (0: never).
    /// Stations, wrecks and rocks never are. A setting (Hud.ContactForgetHours).</summary>
    public static double ForgetHours = 24;
    /// <summary>Your strongest radar's power now (0 when none transmits).</summary>
    public static double RadarPowerNow;

    /// <summary>Harness: a stand-in sensor at the camera (contacts sensor telescope|radar|off).</summary>
    public static Sensor? DevSensor;


    static readonly HashSet<string> _rocks = new HashSet<string>();
    static readonly HashSet<long> _grids = new HashSet<long>();
    /// <summary>Changes whenever something becomes known (caches that filter by knowledge key on it).</summary>
    public static int Version;
    static double _next;

    public static bool KnownRock(string label) { if (!Enabled || label == null) return true; lock (_rocks) return _rocks.Contains(label); }
    public static bool KnownGrid(long id) { if (!Enabled) return true; lock (_grids) if (_grids.Contains(id)) return true; return !IsContactId(id); }
    /// <summary>A frame is shown when any of its grids is known (seeing one part of a station shows it).</summary>
    public static bool KnownFrame(ProximityFrame f)
    {
        if (!Enabled || f == null) return true;
        var site = EncounterFrames.SiteOf(f.Id);
        foreach (long id in f.Members) if (GridMembers.IsGridId(id) && KnownGrid(id) && IsContactId(id)) return true;
        // a ring rock's own site (no grids yet): its rock
        return site != null && site.Label != null && KnownRock(site.Label);
    }

    /// <summary>Not yours (an encounter's, or someone else's), from the server's snapshot (ServerFrames.View): the client
    /// read the server's grid entities for this.</summary>
    public static bool IsContactId(long id) => ServerFrames.View(id, out var v) && IsContact(v);
    /// <summary>Not yours: an NPC's or an encounter's, or owned by someone else (or no one).</summary>
    public static bool IsContact(ServerFrames.GridView v)
    {
        if (v == null) return false;
        if (v.Encounter) return true;
        try
        {
            var players = _session?.Get<ClientPlayersSessionComponent>();
            if (!v.HasOwner || players == null) return false;
            // (no owner is a contact whoever you are - it was "not yours" only by comparing with your identity)
            return ContactRule(false, true, v.Owner.Equals(default(IdentityId)), v.Owner.Equals(players.LocalPlayerIdentity));
        }
        catch { return false; }
    }

    public static bool IsContact(OrbitalGridComponent g)
    {
        if (g == null || g.Entity == null) return false;
        if (EncounterFrames.IsEncounterGrid(g)) return true;
        try
        {
            var own = g.Session?.SessionComponents.TryGet<OwnershipSessionComponent>();
            var players = _session?.Get<ClientPlayersSessionComponent>();
            if (own == null || players == null) return false;
            var owner = own.TryGetOwnerOf(g.Entity);
            return ContactRule(false, true, owner.Equals(default(IdentityId)), owner.Equals(players.LocalPlayerIdentity));
        }
        catch { return false; }
    }

    static Keen.VRage.Core.Game.Systems.Session _session;

    /// <summary>Client tick (twice a second): what is in sight now becomes known.</summary>
    // (a ring's rock labels and their watch keys, made once per ring - every rock's two strings were remade twice a second:
    //  most of this system's garbage, 2026-10-14)
    static readonly Dictionary<string, (int[] numbers, string[] labels, string[] keys)> _rockLabels = new();
    static readonly Dictionary<long, string> _gridKeys = new();
    // (the tick's working collections, reused: ~300 grids in a world - a list and a dictionary of them twice a second)
    static readonly List<SensorModel.Body> _bodies = new();
    static readonly Dictionary<long, bool> _yours = new();
    static readonly List<SensorModel.Looker> _obs = new();
    internal static (string[] labels, string[] keys) RockLabels(string belt, int[] numbers)
    {
        if (_rockLabels.TryGetValue(belt, out var c) && ReferenceEquals(c.numbers, numbers)) return (c.labels, c.keys);
        var labels = new string[numbers.Length]; var keys = new string[numbers.Length];
        for (int i = 0; i < numbers.Length; i++) { labels[i] = $"{belt} #{numbers[i]}"; keys[i] = "r:" + labels[i]; }
        _rockLabels[belt] = (numbers, labels, keys);
        return (labels, keys);
    }

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
        var bodies = _bodies; bodies.Clear();
        foreach (var b in reg.Bodies)
        {
            double r = b.IsRoot ? SystemHost.StarRadius : reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
            if (r > 0) bodies.Add(new SensorModel.Body { Centre = b.OriginInRoot(t).Position, Radius = r, IsSun = b.IsRoot, Albedo = reg.FindDefinition(b.Name)?.Albedo ?? 0 });
        }
        Vector3D sun = reg.Root?.OriginInRoot(t).Position ?? Vector3D.Zero;

        // Who is looking: your eyes (where the camera is), and every working sensor on your grids.
        // (the server's snapshot - its grids and its sensor blocks - not its entities: ServerFrames.View / SensorViews)
        var views = ServerFrames.ViewValues;   // (the snapshot's own collection: no copy - it is replaced whole, never changed)
        var yours = _yours; yours.Clear();
        foreach (var v in views) yours[v.Id] = !IsContact(v);
        var obs = _obs; obs.Clear(); obs.Add(new SensorModel.Looker { At = eye, Kind = SensorModel.Kind.Eyes });
        foreach (var sv in ServerFrames.SensorViews)
        {
            if (!sv.Working || !yours.TryGetValue(sv.Grid, out bool mine) || !mine) continue;
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(sv.Position, t, out m, out _, out _);
            if (ok) obs.Add(new SensorModel.Looker { At = m, Kind = sv.Radar ? SensorModel.Kind.Radar : SensorModel.Kind.Telescope, Power = sv.Radar ? sv.Power : 0 });
        }
        // Harness: a stand-in sensor where you are (tests the physics without building the block).
        if (DevSensor.HasValue) obs.Add(new SensorModel.Looker { At = eye, Kind = DevSensor.Value == Sensor.Radar ? SensorModel.Kind.Radar : SensorModel.Kind.Telescope, Power = 1 });
        bool radarOn = false;
        RadarPowerNow = 0; foreach (var o in obs) if (o.Kind == SensorModel.Kind.Radar) { radarOn = true; if (o.Power > RadarPowerNow) RadarPowerNow = o.Power; }
        Loud = radarOn;
        // Others' radars transmitting: heard by your telescopes and radars (their grid -> how far its loudest is heard)
        var emit = _emit; emit.Clear();
        foreach (var sv in ServerFrames.SensorViews)
        {
            if (!sv.Radar || !sv.Working || !(sv.Power > 0) || yours.TryGetValue(sv.Grid, out bool own) && own) continue;
            double reach = Signature.EmissionReach(sv.Power);
            if (!emit.TryGetValue(sv.Grid, out double r0) || reach > r0) emit[sv.Grid] = reach;
        }

        // Seen how (Via.None: by none): a rock's radius (m), albedo, temperature, its radar albedo.
        SensorModel.Via SeenRock(Vector3D at, double r)
        {
            SensorModel.Detect(obs, new SensorModel.Target { At = at, Radius = r, Albedo = RockAlbedo, TempK = RockTempK, RadarAlbedo = Signature.RockRadarAlbedo }, bodies, sun, out var via);
            return via;
        }
        // Watching: what is seen and not yet tracked is watched (the telescope's lidar on your target speeds it).
        string target = Maneuvers.Target;
        bool lidar = false;
        // (watching time is the sim's: none while paused, faster under warp - it was a fixed 0.5 s of wall time per tick;
        //  capped so one clock jump does not track everything at once)
        double simNow = SystemHost.Now, watchDt = double.IsNaN(_lastWatchT) ? TickSeconds : Math.Max(0, Math.Min(60, simNow - _lastWatchT));
        _lastWatchT = simNow;
        var newlyTracked = _newlyTracked; newlyTracked.Clear();   // (the tick's lists, reused: cleared here, read only below)
        void Watch(string key, string name, SensorModel.Via via)
        {
            var k = Tracking.KindFor(via);
            bool targeted = target != null && (name == target || key.StartsWith("g:", StringComparison.Ordinal) && target == Targets.GridPrefix + key.Substring(2));
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


        var newGrids = _newGrids; newGrids.Clear();
        var newRocks = _newRocks; newRocks.Clear();
        // Grids that are not yours: every one, every tick (it was skipped once tracked: now when it was last seen matters).
        foreach (var g in views)
        {
            if (!IsContact(g)) continue;
            bool known; lock (_grids) known = _grids.Contains(g.Id);
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(g.Position, t, out m, out _, out _);
            if (!ok) continue;
            emit.TryGetValue(g.Id, out double heard);
            if (SensorModel.Detect(obs, GridTarget(g, m, heard), bodies, sun, out var via) < 0) continue;
            string key = GridKey(g.Id);
            double range = (m - eye).Length();
            lock (_dwell) _seen[key] = new Seen { At = simNow, Via = via, Range = range };
            if (!TrackedKey(key)) Watch(key, GroupName(g), via);
            if (known) continue;
            lock (_grids) _grids.Add(g.Id);
            newGrids.Add((g.Name, range, Signature.Name(via)));
        }
        AgeGrids(simNow);
        // Planet ring rocks (seeded) and belt asteroids.
        foreach (var b in RingRocks.Belts())
        {
            Vector3D o = b.Body.OriginInRoot(t).Position;
            var (labels, keys) = RockLabels(b.Name, b.Number);
            for (int i = 0; i < b.R.Length; i++)
            {
                string label = labels[i];
                bool known = KnownRock(label);
                if (known && TrackedKey(keys[i])) continue;   // (its watch key, made once: TrackedRock built "r:" + label anew for every known rock)
                var k = SeenRock(o + RingRocks.At(b, i, t), b.Cluster[i] ? 2600 : 1500);
                if (k == SensorModel.Via.None) continue;
                lock (_dwell) _seen[keys[i]] = new Seen { At = simNow, Via = k, Range = double.NaN };
                Watch(keys[i], label, k);
                if (known) continue;
                lock (_rocks) _rocks.Add(label);
                newRocks.TryGetValue(b.Name, out int n); newRocks[b.Name] = n + 1;
            }
        }
        foreach (var (belt, label, home, cluster) in AsteroidFrames.All())
        {
            bool known = KnownRock(label);
            if (known && TrackedRock(label)) continue;
            var k = SeenRock(SectorHomes.Where(home, reg, t), cluster ? 2600 : 1500);
            if (k == SensorModel.Via.None) continue;
            lock (_dwell) _seen[RockKey(label)] = new Seen { At = simNow, Via = k, Range = double.NaN };
            Watch(RockKey(label), label, k);
            if (known) continue;
            lock (_rocks) _rocks.Add(label);
            newRocks.TryGetValue(belt, out int n); newRocks[belt] = n + 1;
        }

        Lidar = lidar;
        if (lidar) Loud = true;
        // you are loud: the HUD's line, and a toast when it starts (2026-10-19: "anyone sees you" was only words)
        LoudReach = radarOn ? Signature.EmissionReach(RadarPowerNow) : 0;
        LoudLine = !Loud ? null : radarOn ? $"RADAR TRANSMITTING  ·  heard to ~{HudPanel.Km(LoudReach)}" : "LIDAR ON TARGET  ·  it can see you";
        if (Loud && !_wasLoud) Messages.Say(MessageKinds.Contacts, radarOn
            ? $"You are transmitting: any telescope or radar within ~{HudPanel.Km(LoudReach)} hears your radar. Lower its Transmit Power (its control panel) or switch it off to go quiet."
            : "You are transmitting: your telescope's lidar ranges your target - it can see you.", 6);
        _wasLoud = Loud;
        // newly tracked: ships by name (rocks' orbits quietly: the map and the Contacts tab show them - a toast for them
        // came with every batch)
        if (newlyTracked.Count > 0)
        {
            var tl = newlyTracked.FindAll(x => !x.Contains(" #"));
            if (tl.Count > 3) { int more = tl.Count - 2; tl.RemoveRange(2, tl.Count - 2); tl.Add($"and {more} more"); }
            if (tl.Count > 0 && newGrids.Count == 0) Messages.Say(MessageKinds.Contacts, "Tracked: " + string.Join(", ", tl), 4);
            if (OrbitalLog.Contacts) Log.Default?.Info("[ORBIT-CONTACT] tracked: " + string.Join("; ", newlyTracked));
        }
        // a new ship: at once; new rocks: gathered, one summary per pass (2026-10-19: a toast per batch, 7 in 90 s)
        foreach (var kv in newRocks) _rockToasts.Add(kv.Key, kv.Value, wall);
        if (newGrids.Count > 0 || newRocks.Count > 0) Version++;
        if (newGrids.Count > 0)
        {
            var lines = new List<string>();
            foreach (var (name, dist, by) in newGrids) lines.Add($"{name}  ·  {HudPanel.Km(dist)}  ·  {by}");
            if (lines.Count > 4) { int more = lines.Count - 3; lines.RemoveRange(3, lines.Count - 3); lines.Add($"and {more} more"); }
            Messages.Say(MessageKinds.Contacts, "Contact: " + string.Join("; ", lines), 5);
            if (OrbitalLog.Contacts) Log.Default?.Info("[ORBIT-CONTACT] " + string.Join("; ", lines));
        }
        if (_rockToasts.Due(wall))
        {
            var lines = _rockToasts.Take(wall);
            if (lines.Count > 4) { int more = lines.Count - 3; lines.RemoveRange(3, lines.Count - 3); lines.Add($"and {more} more"); }
            Messages.Say(MessageKinds.Contacts, string.Join("; ", lines), 5);
            if (OrbitalLog.Contacts) Log.Default?.Info("[ORBIT-CONTACT] " + string.Join("; ", lines));
        }
        int ng; lock (_grids) ng = _grids.Count;
        int nr; lock (_rocks) nr = _rocks.Count;
        int nt = 0, nrad = 0;   // (counted: FindAll made two lists to count them)
        foreach (var o in obs) { if (o.Kind == SensorModel.Kind.Telescope) nt++; else if (o.Kind == SensorModel.Kind.Radar) nrad++; }
        int ntr; lock (_dwell) { ntr = 0; foreach (var kv in _dwell) if (kv.Value >= Tracking.PassiveSeconds) ntr++; }
        Status = $"known: {ng} grid(s), {nr} rock(s), {ntr} tracked; looking: eyes, {nt} telescope(s), {nrad} radar(s){(lidar ? ", lidar on the target" : "")}{(Loud ? " (loud)" : "")}";
    }

    // ── tracking (Core/Sensing/Tracking: detected -> tracked) ──
    static readonly Dictionary<string, double> _dwell = new Dictionary<string, double>();
    /// <summary>When and how each contact was last seen (watch key -> game time, how, range from you; lock _dwell).</summary>
    struct Seen { public double At, Range; public SensorModel.Via Via; }
    static readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>();
    static readonly Dictionary<long, double> _emit = new Dictionary<long, double>();
    static readonly Batcher _rockToasts = new Batcher();
    static readonly List<long> _forget = new List<long>();

    /// <summary>A grid as a target for the sensors: its size from its box (mean cross-section), its engines' plume, its
    /// radar heard (2026-10-19: a fixed 150 m / 290 K ball for every grid).</summary>
    static SensorModel.Target GridTarget(ServerFrames.GridView g, Vector3D at, double heard)
    {
        double x = 0, y = 0, z = 0;
        if (g.HasBox) { x = g.Box.Max.X - g.Box.Min.X; y = g.Box.Max.Y - g.Box.Min.Y; z = g.Box.Max.Z - g.Box.Min.Z; }
        return new SensorModel.Target
        {
            At = at, Radius = Signature.EquivalentRadius(Signature.MeanArea(x, y, z)), Albedo = GridAlbedo, TempK = GridTempK,
            Plume = Signature.PlumeReach(g.Firing), Emit = heard,
        };
    }

    /// <summary>AGING (Core/Sensing/Aging): a known grid never stamped (restored from an old save) is stamped now; a moving
    /// one unseen past ForgetHours is forgotten (detect it again). Once a second.</summary>
    static double _agedAt = double.NegativeInfinity;
    static void AgeGrids(double simNow)
    {
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (wall - _agedAt < 1.0 && wall >= _agedAt) return;
        _agedAt = wall;
        var forget = _forget; forget.Clear();
        lock (_grids)
            foreach (long id in _grids)
            {
                string key = GridKey(id);
                Seen s;
                lock (_dwell) { if (!_seen.TryGetValue(key, out s)) { _seen[key] = new Seen { At = simNow, Range = double.NaN }; continue; } }
                bool moving = ServerFrames.View(id, out var v) && v.Dynamic;
                if (Aging.Of(simNow - s.At, StaleMinutes * 60, ForgetHours * 3600, moving) == Aging.State.Lost) forget.Add(id);
            }
        if (forget.Count == 0) return;
        foreach (long id in forget)
        {
            string key = GridKey(id);
            lock (_grids) _grids.Remove(id);
            lock (_dwell) { _dwell.Remove(key); _seen.Remove(key); }
            if (OrbitalLog.Contacts) Log.Default?.Info($"[ORBIT-CONTACT] lost: grid {id} not seen for {ForgetHours:G3} h");
        }
        Version++;
        Messages.Say(MessageKinds.Contacts, forget.Count == 1 ? $"Contact lost: a ship not seen for {ForgetHours:G3} h (find it again)"
                                                              : $"Contacts lost: {forget.Count} ships not seen for {ForgetHours:G3} h (find them again)");
        forget.Clear();
    }

    /// <summary>The contact rule (2026-10-19, item 9: the docs say an unowned wreck must be found like any contact): an
    /// encounter's grid, one with no owner, or one owned by someone else. Not yours = a contact.</summary>
    public static bool ContactRule(bool encounter, bool hasOwner, bool ownerIsNobody, bool ownerIsYou)
        => Signature.IsContact(encounter, hasOwner, ownerIsNobody, ownerIsYou);
    /// <summary>A telescope of yours is ranging your target with its laser (lidar): it gives you away.</summary>
    public static bool Lidar;
    static double DwellOf(string key) { if (!Enabled) return Tracking.PassiveSeconds; lock (_dwell) return _dwell.TryGetValue(key, out double d) ? d : 0; }
    public static bool TrackedRock(string label) => !Enabled || label == null || DwellOf(RockKey(label)) >= Tracking.PassiveSeconds;
    static bool TrackedKey(string key) => !Enabled || DwellOf(key) >= Tracking.PassiveSeconds;
    /// <summary>A rock's watch key ("r:" + label), made once per label: the map asks for hundreds of rocks a frame.</summary>
    static string RockKey(string label)
    {
        lock (_rockKeys)
        {
            if (!_rockKeys.TryGetValue(label, out var k)) { if (_rockKeys.Count > 100000) _rockKeys.Clear(); _rockKeys[label] = k = "r:" + label; }
            return k;
        }
    }
    static readonly Dictionary<string, string> _rockKeys = new Dictionary<string, string>();
    static readonly List<string> _newlyTracked = new List<string>();
    static readonly List<(string name, double dist, string by)> _newGrids = new List<(string, double, string)>();
    static readonly Dictionary<string, int> _newRocks = new Dictionary<string, int>();
    public static bool TrackedGrid(long id) => !Enabled || DwellOf(GridKey(id)) >= Tracking.PassiveSeconds || !IsContactId(id);
    /// <summary>A grid's watch key ("g:" + id), made once per grid.</summary>
    static string GridKey(long id) { lock (_gridKeys) { if (!_gridKeys.TryGetValue(id, out var k)) _gridKeys[id] = k = "g:" + id; return k; } }
    /// <summary>A rock target's tracking: null when it is not a rock contact (a sector, a body) or tracking is off.</summary>
    public static double? RockProgress(string label)
    {
        if (!Enabled || label == null) return null;
        bool isRock = false;
        foreach (var b in RingRocks.Belts()) if (label.StartsWith(b.Name + " #")) isRock = true;
        if (!isRock) foreach (var r in AsteroidFrames.All()) if (r.label == label) isRock = true;
        return isRock ? Tracking.Progress(DwellOf(RockKey(label))) : (double?)null;
    }
    // ── what is known of one contact (the Contacts tab, the target panel, the map's lists; 2026-10-19) ──

    /// <summary>One contact as known: whether it is one at all (yours / a body / a sector: not), detected, tracked (and how
    /// far along), how it was last seen and how long ago (game seconds), its kind, where it is landed.</summary>
    public struct Info
    {
        public bool Contact, Known, Tracked, Rock;
        public double Progress, Age;
        public SensorModel.Via Via;
        public Signature.Class Class;
        public Aging.State State;
        public string LandedOn;
        public bool Stale => State == Aging.State.Stale;
    }

    /// <summary>A target key's contact state (a ship's "grid:id" - its frame's grids together: the best known of them -,
    /// or a rock's label). False: not a contact (yours, a body, a sector, a place) or detection is off.</summary>
    public static bool InfoOf(string key, out Info info)
    {
        info = default;
        if (!Enabled || key == null) return false;
        double now = SystemHost.Now;
        if (Targets.GridOf(key, out long id))
        {
            if (!ServerFrames.View(id, out var v0) || !IsContact(v0)) return false;
            info.Contact = true;
            info.Class = ClassOf(v0);
            info.LandedOn = LandedOn(id);
            info.Age = double.NaN; info.Progress = 0;
            bool moving = false;
            ProximityFrame f;
            lock (ServerFrames.FramesLock) f = SystemHost.Frames?.FindByMember(id);
            if (f == null) Merge(ref info, id, now, ref moving);
            else
            {
                // (a frame's grids are one ship: its best-known grid tells; a member list copy - the frame changes under the lock)
                var members = _members; members.Clear();
                lock (ServerFrames.FramesLock) foreach (long m in f.Members) if (GridMembers.IsGridId(m)) members.Add(m);
                foreach (long m in members) if (IsContactId(m)) Merge(ref info, m, now, ref moving);
                members.Clear();
            }
            info.State = info.Known ? Aging.Of(info.Age, StaleMinutes * 60, ForgetHours * 3600, moving) : Aging.State.Lost;
            return true;
        }
        if (RockProgress(key) is double p)
        {
            info.Contact = info.Rock = true; info.Class = Signature.Class.Rock;
            info.Known = KnownRock(key); info.Progress = p; info.Tracked = p >= 1;
            lock (_dwell) if (_seen.TryGetValue(RockKey(key), out var s)) { info.Age = now - s.At; info.Via = s.Via; } else info.Age = double.NaN;
            info.State = Aging.Of(info.Age, 0, 0, false);
            return true;
        }
        return false;
    }
    static readonly List<long> _members = new List<long>();

    static void Merge(ref Info info, long id, double now, ref bool moving)
    {
        bool known; lock (_grids) known = _grids.Contains(id);
        if (!known) return;
        string key = GridKey(id);
        double prog = Tracking.Progress(DwellOf(key));
        Seen s; bool has; lock (_dwell) has = _seen.TryGetValue(key, out s);
        double age = has ? now - s.At : double.NaN;
        if (!info.Known || prog > info.Progress) info.Progress = prog;
        if (!info.Known || has && (double.IsNaN(info.Age) || age < info.Age)) { info.Age = age; if (has) info.Via = s.Via; }
        info.Known = true;
        info.Tracked = info.Progress >= 1;
        if (ServerFrames.View(id, out var v) && v.Dynamic) moving = true;
    }

    /// <summary>What a grid looks like (Signature.Classify): its box's largest side, fixed or not, its owner, its mass.</summary>
    public static Signature.Class ClassOf(ServerFrames.GridView v)
    {
        double side = 0;
        if (v.HasBox) side = Math.Max(v.Box.Max.X - v.Box.Min.X, Math.Max(v.Box.Max.Y - v.Box.Min.Y, v.Box.Max.Z - v.Box.Min.Z));
        bool unowned = v.HasOwner && !v.Encounter && v.Owner.Equals(default(IdentityId));
        return Signature.Classify(!v.Dynamic, unowned, side, v.Mass);
    }

    /// <summary>Where a grid is landed (a body's name), else null - fx-flight's B1 rule (ServerFrames.Landed).</summary>
    public static string LandedOn(long gridId) => ServerFrames.Landed(gridId, out var body) ? body : null;

    /// <summary>How far along its orbit fit a contact is (0..1); null when the key is not a contact (yours, a body, a
    /// sector, a place) or detection is off. THE GATE: no closest approach, rendezvous or orbit below 1
    /// (Tracking.CanPredict) - ships as rocks (2026-10-19: ships were predicted from the first glimpse).</summary>
    public static double? TrackingProgress(string key)
    {
        if (!Enabled || key == null) return null;
        // (a contact not known - never seen, or forgotten - has no orbit at all: 0)
        if (Targets.GridOf(key, out _)) return InfoOf(key, out var i) ? (i.Known ? i.Progress : 0) : (double?)null;
        return RockProgress(key);
    }

    /// <summary>How bright to draw a contact (1 fresh .. 0.4 long unseen; 1 when not a contact): Aging.Fade.</summary>
    public static double Fade(string key) => InfoOf(key, out var i) ? FadeOf(i) : 1;

    /// <summary><see cref="Fade"/> from a contact's info already read (the lists read it once a row).</summary>
    public static double FadeOf(in Info i)
    {
        if (i.Rock) return 1;
        bool moving = i.Class != Signature.Class.Station && i.Class != Signature.Class.Wreck;
        return Aging.Fade(i.Age, StaleMinutes * 60, ForgetHours * 3600, moving);
    }

    /// <summary>The kind in words, with where it is landed: "large ship", "station", "small ship · landed on Verdure".</summary>
    public static string KindText(in Info i)
        => Signature.Name(i.Class) + (i.LandedOn != null ? $"  ·  landed on {SystemHost.DisplayName(i.LandedOn)}" : "");

    /// <summary>The short state for a list row: "≈ 40%" while tracking, "last seen 2 h ago" when not in sight, "" fresh.</summary>
    public static string StateText(in Info i)
    {
        if (!i.Known) return "";
        string s = i.Tracked ? "" : $"tracking {i.Progress:P0}";
        if (i.State != Aging.State.InSight) s += (s.Length > 0 ? "  ·  " : "") + "seen " + Aging.Ago(i.Age);
        if (i.Stale) s += "  ·  estimate";
        return s;
    }

    /// <summary>Harness / log: one contact's state.</summary>
    public static string Describe(in Info i)
        => $"{KindText(i)} | {(i.Known ? i.Tracked ? "tracked" : $"tracking {i.Progress:P0}" : "unknown")} | by {Signature.Name(i.Via)} | {Aging.Ago(i.Age)} ({i.State})";

    // ── save: when each grid contact was last seen (game time) ──
    public static double LastSeenGrid(long id) { lock (_dwell) return _seen.TryGetValue(GridKey(id), out var s) ? s.At : double.NaN; }
    /// <summary>A known grid from a save, with when it was last seen (NaN: stamped at the next look).</summary>
    public static void RestoreGridSeen(long id, double lastSeen)
    {
        lock (_grids) _grids.Add(id);
        if (!double.IsNaN(lastSeen)) lock (_dwell) _seen[GridKey(id)] = new Seen { At = lastSeen, Range = double.NaN };
        Version++;
    }

    /// <summary>The name a grid is targeted and marked by: its site's label, else its own.</summary>
    static string GroupName(ServerFrames.GridView g)
    {
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.FindByMember(g.Id);
            var site = f != null ? EncounterFrames.SiteOf(f.Id) : null;
            if (site?.Label != null) return site.Label;
        }
        return g.Name;
    }

    // ── save / load ──
    public static List<string> RockKeys() { lock (_rocks) return new List<string>(_rocks); }
    public static List<long> GridKeys() { lock (_grids) return new List<long>(_grids); }
    public static void RestoreRock(string label) { lock (_rocks) _rocks.Add(label); Version++; }
    public static List<string> TrackedKeys() { var l = new List<string>(); lock (_dwell) foreach (var kv in _dwell) if (kv.Value >= Tracking.PassiveSeconds) l.Add(kv.Key); return l; }
    public static void RestoreTracked(string key) { lock (_dwell) _dwell[key] = Tracking.PassiveSeconds; Version++; }
    public static void RestoreGrid(long id) { lock (_grids) _grids.Add(id); Version++; }
    /// <summary>A new world (WorldReset): nothing known of the last one (2026-10-15: its contacts showed in the next world
    /// and were written into its save; only the dev "forget" cleared them).</summary>
    public static void ResetWorld()
    {
        lock (_rocks) _rocks.Clear(); lock (_grids) _grids.Clear(); lock (_dwell) { _dwell.Clear(); _seen.Clear(); } lock (_gridKeys) _gridKeys.Clear();
        _rockToasts.Clear(); _wasLoud = false; Loud = false; LoudLine = null; LoudReach = 0;
        Version++;
    }

    /// <summary>Harness: contacts reveal | forget | (status).</summary>
    public static string Command(string[] a)
    {
        switch (a.Length > 1 ? a[1] : "")
        {
            case "forget": lock (_rocks) _rocks.Clear(); lock (_grids) _grids.Clear(); lock (_dwell) { _dwell.Clear(); _seen.Clear(); } Version++; return "contacts forgotten";
            case "age":   // harness: contacts age <seconds> - every contact seen that much earlier (tests staleness / forgetting)
            {
                double by = a.Length > 2 && double.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec) ? sec : 3600;
                lock (_dwell) foreach (var k in new List<string>(_seen.Keys)) { var sv = _seen[k]; sv.At -= by; _seen[k] = sv; }
                _agedAt = double.NegativeInfinity;
                return $"contacts aged {by:G4} s";
            }
            case "info": return a.Length > 2 && InfoOf(string.Join(" ", a, 2, a.Length - 2), out var inf) ? Describe(inf) : "no such contact";
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
