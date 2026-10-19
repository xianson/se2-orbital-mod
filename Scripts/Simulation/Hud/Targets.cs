using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// What can be targeted, and where it is: one key per target in <see cref="Maneuvers.Target"/>.
///  - a planet or moon: its name ("Verdure")
///  - a sector's site or a belt asteroid: its sector name / label (EncounterFrames.TargetSite)
///  - a ring rock: its label ("Oblivara #12": RingRocks, its seeded circular orbit - 2026-10-19, targetable once seen)
///  - a ship or station: "grid:&lt;entity id&gt;" (<see cref="OfGrid"/>) - on rails, its frame's orbit plus its place in the
///    frame; in a planet's space, the orbit its last two positions give (a free flier's Kepler arc about the planet)
/// <see cref="At"/> is the target's state about its parent body at any time, for the closest approach, the relative
/// plot and the Rendezvous tab alike.
/// </summary>
public static class Targets
{
    public const string GridPrefix = "grid:";
    public static string OfGrid(long id)
    {
        // (made once per grid: the ship list is rebuilt twice a second, two keys a ship, and the HUD asks for them as well)
        lock (_gridKeys)
        {
            if (!_gridKeys.TryGetValue(id, out var k))
            {
                if (_gridKeys.Count > 20000) _gridKeys.Clear();   // (bounded)
                _gridKeys[id] = k = GridPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return k;
        }
    }
    static readonly Dictionary<long, string> _gridKeys = new Dictionary<long, string>();
    public static bool GridOf(string key, out long id)
    {
        id = 0;
        // (the digits parsed in place: a Substring per call was a string for every ship on the HUD every frame)
        return key != null && key.StartsWith(GridPrefix, StringComparison.Ordinal)
            && long.TryParse(key.AsSpan(GridPrefix.Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    /// <summary>A planet or moon (not the star).</summary>
    public static GravityBody Body(string key)
    {
        var b = key != null && !key.StartsWith(GridPrefix, StringComparison.Ordinal) ? SystemHost.Registry?.Find(key) : null;
        return b != null && !b.IsRoot ? b : null;
    }

    /// <summary>The name shown for a target: a body's display name, a ship's (its site's label when it has one), else the key.</summary>
    public static string Name(string key)
    {
        if (key == null) return null;
        if (ContactsTab.Names.TryGetValue(key, out var mine)) return mine;   // (your name for it, from the Contacts tab)
        if (GridOf(key, out long id))
        {
            lock (ServerFrames.FramesLock)
            {
                var f = SystemHost.Frames?.FindByMember(id);
                var site = f != null ? EncounterFrames.SiteOf(f.Id) : null;
                if (site?.Label != null) return site.Label;
            }
            if (ServerFrames.View(id, out var v) && !string.IsNullOrEmpty(v.Name)) return v.Name;
            return "Ship";
        }
        if (key.StartsWith(ContactsTab.PlacePrefix, StringComparison.Ordinal)) return ContactsTab.PlaceName(key);
        return Body(key) != null ? SystemHost.DisplayName(key) : key;
    }

    /// <summary>A place (a GPS point): fixed where it is in its window - on a planet it turns with the ground, in a frame's
    /// berth it goes round with the frame's orbit (FrameMarkers.ModelOf at that time).</summary>
    static bool PlaceAt(string key, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        if (!ContactsTab.PlaceWorld(key, out var world)) return false;
        Vector3D m0 = default, m1 = default; bool ok;
        lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(world, tk, out m0, out _, out _) && FrameMarkers.ModelOf(world, tk + 1, out m1, out _, out _);
        if (!ok) return false;
        var reg = SystemHost.Registry;
        parent = reg.Root.DeepestSoiContaining(m0, tk) ?? reg.Root;
        var o = parent.OriginInRoot(tk);
        rel = new StateVector(m0 - o.Position, (m1 - m0) - o.Velocity);
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>The target's state about its parent body at tk. False: unknown or gone.</summary>
    public static bool At(string key, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        if (key == null) return false;
        var reg = SystemHost.Registry;
        if (reg == null) return false;
        if (GridOf(key, out long id)) return GridAt(id, tk, out parent, out rel);
        if (key.StartsWith(ContactsTab.PlacePrefix, StringComparison.Ordinal)) return PlaceAt(key, tk, out parent, out rel);
        var body = Body(key);
        if (body != null && body.Parent != null) { parent = body.Parent; rel = body.StateInParentAt(tk); return true; }
        if (RingRockAt(key, tk, out parent, out rel)) return true;
        var site = EncounterFrames.TargetSite(key);
        return site != null && EncounterFrames.Ephemeris(site, tk, out parent, out rel);
    }

    /// <summary>A ring rock's label ("Oblivara #12"), made once per ring (Contacts' own labels).</summary>
    public static string RingRockLabel(RingRocks.Belt b, int i) => Contacts.RockLabels(b.Name, b.Number).labels[i];

    /// <summary>A ring rock's state about its planet at tk (its seeded circular orbit: RingRocks.At, the velocity by a
    /// central difference). False: not a ring rock's label.</summary>
    static bool RingRockAt(string key, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        int hash = key.LastIndexOf(" #", StringComparison.Ordinal);
        if (hash <= 0 || !int.TryParse(key.AsSpan(hash + 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int num)) return false;
        foreach (var b in RingRocks.Belts())
        {
            if (b.Name.Length != hash || string.CompareOrdinal(b.Name, 0, key, 0, hash) != 0) continue;
            for (int i = 0; i < b.Number.Length; i++)
            {
                if (b.Number[i] != num) continue;
                const double h = 0.5;
                Vector3D p = RingRocks.At(b, i, tk), v = (RingRocks.At(b, i, tk + h) - RingRocks.At(b, i, tk - h)) / (2 * h);
                parent = b.Body; rel = new StateVector(p, v);
                return IsFinite(rel.Position) && IsFinite(rel.Velocity);
            }
        }
        return false;
    }

    /// <summary>The target's sun-centred state at tk.</summary>
    public static bool RootAt(string key, double tk, out StateVector root)
    {
        root = default;
        if (!At(key, tk, out var p, out var rel)) return false;
        var o = p.OriginInRoot(tk);
        root = new StateVector(o.Position + rel.Position, o.Velocity + rel.Velocity);
        return IsFinite(root.Position) && IsFinite(root.Velocity);
    }

    /// <summary>The target's state about a given body at tk (any body: positions are sun-centred underneath).</summary>
    public static bool About(string key, GravityBody about, double tk, out StateVector rel)
    {
        rel = default;
        if (about == null || !RootAt(key, tk, out var root)) return false;
        var o = about.OriginInRoot(tk);
        rel = new StateVector(root.Position - o.Position, root.Velocity - o.Velocity);
        return true;
    }

    /// <summary>A body target's radius (closest approaches are told as altitudes), else 0.</summary>
    public static double Radius(string key)
    {
        var b = Body(key);
        return b != null ? SystemHost.Registry?.FindDefinition(b.Name)?.RadiusMeters ?? 0 : 0;
    }

    /// <summary>The key names this grid's ship: the grid itself, or another grid of the same frame (a ship is its frame).</summary>
    public static bool SameShip(string key, long gridId)
    {
        if (!GridOf(key, out long id)) return false;
        if (id == gridId) return true;
        lock (ServerFrames.FramesLock)
        {
            var a = SystemHost.Frames?.FindByMember(id);
            return a != null && ReferenceEquals(a, SystemHost.Frames.FindByMember(gridId));
        }
    }

    /// <summary>The ship target is in your own frame: you are there.</summary>
    public static bool InOwnFrame(string key)
    {
        var f = FrameHost.PlayerFrame;
        if (f == null || !GridOf(key, out long id)) return false;
        lock (ServerFrames.FramesLock) return f.HasMember(id);
    }

    // ── ships and stations ──

    // a ship in a planet's space (not on rails): its last two model positions give its velocity, its orbit
    private sealed class Track { public Vector3D P0, P1; public double T0 = double.NaN, T1 = double.NaN; public string Body; public KeplerianElements El; public bool HasEl; }
    private static readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();

    static bool GridAt(long id, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        var reg = SystemHost.Registry;
        if (!ServerFrames.View(id, out var v)) return false;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.FindByMember(id);
            if (f != null && reg.Find(f.ParentBodyName) is GravityBody fp)
            {
                // on rails: the frame's orbit, and where the ship sits in its berth
                var s = OrbitPropagation.StateAt(f.Elements, tk);
                parent = fp;
                rel = new StateVector(s.Position + (v.Position - f.BerthCenter), s.Velocity);
                return IsFinite(rel.Position) && IsFinite(rel.Velocity);
            }
        }
        // on the ground, low over it, or nowhere real (fx-flight's B1 rule): its place on the turning ground - no orbit -, or
        // nothing at all (2026-10-19: its "orbit" from two positions put a landed ship through the planet's centre)
        if (ShipSurface.Of(id, out var sk, out _, out _, out _) && sk != SEAerospace.Flight.SurfaceKind.Orbit)
            return sk != SEAerospace.Flight.SurfaceKind.Unknown && ShipSurface.GroundState(id, tk, out parent, out rel);
        // in a planet's space: its orbit from its motion
        double now = SystemHost.Now;
        Vector3D m; bool ok;
        lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(v.Position, now, out m, out _, out _);
        if (!ok) return false;
        Track tr;
        lock (_tracks)
        {
            if (!_tracks.TryGetValue(id, out tr)) _tracks[id] = tr = new Track();
            if (double.IsNaN(tr.T1) || now - tr.T1 >= 0.5 || now < tr.T1)
            {
                if (!double.IsNaN(tr.T1) && now > tr.T1 && now - tr.T1 < 30) { tr.P0 = tr.P1; tr.T0 = tr.T1; }
                else tr.T0 = double.NaN;
                tr.P1 = m; tr.T1 = now; tr.HasEl = false;
            }
            // (its real velocity when the view has one - ShipSurface.InertialState - rather than two positions half a second apart)
            if (!tr.HasEl && ShipSurface.InertialState(id, out var ip, out var isv) && ip != null)
            {
                if (isv.Velocity.LengthSquared() < 1e-6) isv = new StateVector(isv.Position, Vector3D.Cross(Vector3D.UnitY, isv.Position) * 1e-6);
                tr.El = OrbitalMath.ToElements(isv, ip.Mu, now);
                tr.Body = ip.Name; tr.HasEl = true;
            }
            if (!tr.HasEl)
            {
                var pb = reg.Root.DeepestSoiContaining(m, now) ?? reg.Root;
                if (pb.IsRoot) return false;
                Vector3D vel = double.IsNaN(tr.T0) ? Vector3D.Zero : (tr.P1 - tr.P0) / (tr.T1 - tr.T0);
                var o = pb.OriginInRoot(now);
                // (at rest relative to the planet until a second sample comes: the planet's own velocity)
                var sv = new StateVector(m - o.Position, double.IsNaN(tr.T0) ? Vector3D.Zero : vel - o.Velocity);
                if (sv.Velocity.LengthSquared() < 1e-6) sv = new StateVector(sv.Position, Vector3D.Cross(Vector3D.UnitY, sv.Position) * 1e-6);   // (a needle orbit, not NaN)
                tr.El = OrbitalMath.ToElements(sv, pb.Mu, now);
                tr.Body = pb.Name; tr.HasEl = true;
            }
            parent = reg.Find(tr.Body);
            if (parent == null) return false;
            rel = OrbitPropagation.StateAt(tr.El, tk);
        }
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>A new world: no ship tracks.</summary>
    public static void ResetWorld() { lock (_tracks) _tracks.Clear(); }

    // ── what the player may target: ships and stations you know of (yours, and contacts you have seen), one per group ──

    public struct Entry { public string Key, Name; public bool Yours, Tracked; }

    /// <summary>The ships and stations you can target: your own grids and the contacts you know of, one per frame (its
    /// anchor), or one per cluster in a planet's space (grids within 300 m are one target).</summary>
    public static List<Entry> Ships()
    {
        var l = new List<Entry>();
        var seenFrames = new HashSet<long>();
        var placed = new List<Vector3D>();
        long seat = ServerFrames.PlayerSeatGrid;
        var pf = FrameHost.PlayerFrame;
        var views = ServerFrames.ViewValues;   // (the snapshot's own collection: replaced whole, never changed - no copy needed)
        // (the ship you sit in, and what is joined to it, are not targets)
        if (seat != 0 && ServerFrames.View(seat, out var sv)) placed.Add(sv.Position);
        foreach (var v in views)
        {
            bool contact = Contacts.IsContact(v);
            if (contact && !Contacts.KnownGrid(v.Id)) continue;
            if (v.Id == seat) continue;
            ProximityFrame f;
            lock (ServerFrames.FramesLock) f = SystemHost.Frames?.FindByMember(v.Id);
            if (f != null)
            {
                if (pf != null && f.Id == pf.Id) continue;   // (with you)
                var site = EncounterFrames.SiteOf(f.Id);
                if (site != null && site.Anchor && !HasGrid(f)) continue;   // (a sector's empty anchor: the sector is its target)
                if (!seenFrames.Add(f.Id)) continue;
                long key = f.AnchorEntityId != 0 && GridMembers.IsGridId(f.AnchorEntityId) ? f.AnchorEntityId : v.Id;
                l.Add(new Entry { Key = OfGrid(key), Name = Name(OfGrid(key)), Yours = !contact, Tracked = !contact || Contacts.TrackedGrid(v.Id) });
                continue;
            }
            // in a planet's space: grids together are one target (a ship and its sub-grids, a base)
            bool near = false;
            foreach (var p in placed) if ((p - v.Position).LengthSquared() < 300.0 * 300.0) { near = true; break; }
            if (near) continue;
            placed.Add(v.Position);
            l.Add(new Entry { Key = OfGrid(v.Id), Name = string.IsNullOrEmpty(v.Name) ? "Ship" : v.Name, Yours = !contact, Tracked = !contact || Contacts.TrackedGrid(v.Id) });
        }
        return l;
    }

    private static List<Entry> _ships = new List<Entry>();
    private static double _shipsAt = -1;
    /// <summary><see cref="Ships"/>, refreshed twice a second (the map and its list ask every frame).</summary>
    public static List<Entry> ShipsCached()
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _shipsAt > 0.5 || now < _shipsAt) { _ships = Ships(); _shipsAt = now; }
        return _ships;
    }

    /// <summary>A ship target's orbit: its frame's on rails, else the one its motion gives in a planet's space.</summary>
    public static bool Orbit(string key, out GravityBody parent, out KeplerianElements el)
    {
        parent = null; el = default;
        if (!GridOf(key, out long id) || SystemHost.Registry == null) return false;
        // (a contact not tracked yet has no orbit to draw: position only - the tracking gate, 2026-10-19; a ship on the ground,
        //  low over it or nowhere real has none either: fx-flight's B1 rule)
        if (!SEAerospace.Sensing.Tracking.CanPredict(Contacts.TrackingProgress(key)) || ShipSurface.IsOnSurface(key)) return false;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.FindByMember(id);
            if (f != null) { parent = SystemHost.Registry.Find(f.ParentBodyName); el = f.Elements; return parent != null; }
        }
        if (!GridAt(id, SystemHost.Now, out parent, out _)) return false;
        lock (_tracks) { if (!_tracks.TryGetValue(id, out var tr) || !tr.HasEl) return false; el = tr.El; }
        return true;
    }

    /// <summary>
    /// Why this key cannot be your target now (the words to say), or null when it can (2026-10-19: N or a click on a ship on
    /// the ground took it, with an impossible orbit; nothing said why a pick failed). Landed: fx-flight's rule
    /// (Contacts.LandedOn) - a ship on the ground has no orbit to meet.
    /// </summary>
    public static string WhyNot(string key)
    {
        if (key == null || !GridOf(key, out long id)) return null;
        string body = Contacts.LandedOn(id);
        var why = SEAerospace.Sensing.Reasons.Of(body, true);
        return SEAerospace.Sensing.Reasons.Text(why, Name(key), body != null ? SystemHost.DisplayName(body) : null);
    }

    public static bool HasGrid(ProximityFrame f) { foreach (long id in f.Members) if (GridMembers.IsGridId(id)) return true; return false; }

}
