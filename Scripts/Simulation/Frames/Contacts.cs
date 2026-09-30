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
/// a ring rock or belt asteroid. Seen = within a range that grows with its size (DetectAngle: a 150 m
/// station from ~75 km, a 1.5 km rock from ~750 km; never under MinRange), with no planet or moon
/// between. Once seen it is known for good (saved with the world): on the map, targetable, its
/// rendezvous predicted. Knowledge never feeds the physics: what is unknown is simulated all the same.
/// A new contact is announced (rocks by the batch: "Oblivara: 14 rocks spotted").
/// </summary>
public static class Contacts
{
    public static bool Enabled = true;
    public static string Status = "";
    /// <summary>Seen when its size is at least this angle (rad) across, or within MinRange (m).</summary>
    public const double DetectAngle = 2e-3, MinRange = 20000;
    const double GridSize = 150, TickSeconds = 0.5;

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

        // What can stand in the way: every body (the star too).
        var block = new List<(Vector3D c, double r)>();
        foreach (var b in reg.Bodies)
        {
            double r = b.IsRoot ? SystemHost.StarRadius : reg.FindDefinition(b.Name)?.RadiusMeters ?? 0;
            if (r > 0) block.Add((b.OriginInRoot(t).Position, r));
        }
        bool Sees(Vector3D at, double size)
        {
            Vector3D d = at - eye; double dist = d.Length();
            if (!(dist <= Math.Max(MinRange, size / DetectAngle))) return false;
            foreach (var (c, r) in block)
            {
                double k = Math.Clamp(Vector3D.Dot(c - eye, d) / (dist * dist), 0, 1);
                if ((eye + d * k - c).Length() < r * 0.98) return false;   // (0.98: not blocked by the ground it sits on)
            }
            return true;
        }

        var newGrids = new List<(string name, double dist)>();
        var newRocks = new Dictionary<string, int>();
        // Grids that are not yours.
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer) continue;
            lock (_grids) if (_grids.Contains(g.Id)) continue;
            if (!IsContact(g)) continue;
            Vector3D m; bool ok;
            lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(GridMembers.Position(g), t, out m, out _, out _);
            if (!ok || !Sees(m, GridSize)) continue;
            lock (_grids) _grids.Add(g.Id);
            newGrids.Add((g.DisplayName, (m - eye).Length()));
        }
        // Planet ring rocks (seeded) and belt asteroids.
        foreach (var b in RingRocks.Belts())
        {
            Vector3D o = b.Body.OriginInRoot(t).Position;
            for (int i = 0; i < b.R.Length; i++)
            {
                string label = $"{b.Name} #{b.Number[i]}";
                if (KnownRock(label)) continue;
                if (!Sees(o + RingRocks.At(b, i, t), b.Cluster[i] ? 2600 : 1500)) continue;
                lock (_rocks) _rocks.Add(label);
                newRocks.TryGetValue(b.Name, out int n); newRocks[b.Name] = n + 1;
            }
        }
        foreach (var (belt, label, home, cluster) in AsteroidFrames.All())
        {
            if (KnownRock(label)) continue;
            if (!Sees(SectorHomes.Where(home, reg, t), cluster ? 2600 : 1500)) continue;
            lock (_rocks) _rocks.Add(label);
            newRocks.TryGetValue(belt, out int n); newRocks[belt] = n + 1;
        }

        if (newGrids.Count > 0 || newRocks.Count > 0)
        {
            Version++;
            var lines = new List<string>();
            foreach (var (name, dist) in newGrids) lines.Add($"{name}  ·  {HudPanel.Km(dist)}");
            foreach (var kv in newRocks) lines.Add(kv.Value == 1 ? $"{kv.Key}: a rock spotted" : $"{kv.Key}: {kv.Value} rocks spotted");
            if (lines.Count > 4) { int more = lines.Count - 3; lines.RemoveRange(3, lines.Count - 3); lines.Add($"and {more} more"); }
            if (!MapView.Visible) GameUi.Toast(session, "contact", newGrids.Count > 0 ? "Contact" : "Asteroids", string.Join("\n", lines), 4);
            Log.Default?.Info("[ORBIT-CONTACT] " + string.Join("; ", lines));
        }
        int ng; lock (_grids) ng = _grids.Count;
        int nr; lock (_rocks) nr = _rocks.Count;
        Status = $"known: {ng} grid(s), {nr} rock(s)";
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
            case "on": Enabled = true; Version++; return "contacts on";
            case "off": Enabled = false; Version++; return "contacts off (everything known)";
        }
        int unknown = 0;
        foreach (var g in GridMembers.All()) if (g.IsServer && IsContact(g) && !KnownGrid(g.Id)) unknown++;
        return $"{Status}; {unknown} contact grid(s) unknown";
    }
}
