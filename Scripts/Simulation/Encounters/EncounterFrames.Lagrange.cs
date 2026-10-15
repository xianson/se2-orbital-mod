using System.Collections.Concurrent;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

// EncounterFrames (partial): Lagrange sectors as sites - their rides (a frame on the point's loop) and the relative
// dynamics members feel there.
public static partial class EncounterFrames
{
    public static bool LagrangeDynamics(long frameId, double t, out Func<Vector3D, Vector3D, Vector3D> relAccel)
    {
        relAccel = null;
        if (!_sites.TryGetValue(frameId, out var s) || s.Home == null || s.Home.Kind != SectorHomes.Kind.Lagrange) return false;
        var body = SystemHost.Registry?.Find(s.Home.Host);
        if (!SectorHomes.LagrangeOrbit(body, t, out var omega, out var w)) return false;
        double core = SectorHomes.LagrangeCore(s.Home, SystemHost.Registry);
        Vector3D omDot = SectorHomes.LagrangeTurnRate(body, t);
        // (one holder per frame and thread, its delegate made once and its values set each call: a new closure and delegate
        //  per frame per tick were 0.17 MB/s - a profile, 2026-10-14. Callers use it within their tick)
        var map = _lagAccel ??= new Dictionary<long, LagAccel>();
        if (!map.TryGetValue(frameId, out var la)) { map[frameId] = la = new LagAccel(); if (map.Count > 64) Prune(map); }
        la.Omega = omega; la.W = w; la.Core = core; la.OmDot = omDot;
        relAccel = la.Fn;
        return true;
    }
    sealed class LagAccel
    {
        public Vector3D Omega, OmDot; public double W, Core;
        public readonly Func<Vector3D, Vector3D, Vector3D> Fn;
        public LagAccel() { Fn = (d, v) => SectorHomes.LagrangeAccel(d, v, Omega, W, Core, OmDot); }
    }
    [ThreadStatic] static Dictionary<long, LagAccel> _lagAccel;
    static void Prune(Dictionary<long, LagAccel> map)
    {
        // (rare - over 64 frames seen on this thread; _sites is the server's: read racily from the client, so guarded)
        var gone = new List<long>();
        try { foreach (var k in map.Keys) if (!_sites.ContainsKey(k)) gone.Add(k); } catch { map.Clear(); return; }
        foreach (var k in gone) map.Remove(k);
    }

    // ───────────────────────────── Lagrange sectors as spheres of influence ─────────────────────────────

    /// <summary>A frame inside a Lagrange sector: its offset and velocity from the point (non-turning axes), as of T.</summary>
    public sealed class Ride
    {
        public Site Site;
        public Vector3D D, V;
        public double T;
        public KeplerianElements Set;   // the elements we last gave the frame (a change means a burn)
    }
    private static readonly Dictionary<long, Ride> _rides = new Dictionary<long, Ride>();

    /// <summary>The Lagrange sites (a sector's point each).</summary>
    public static List<Site> LagrangeSites()
    {
        // (kept until the sites or the bodies change: built anew on every call - the server's tick and the
        //  planner - it was lists, an iterator and a lambda per zone each time)
        IEnumerable<Site> snap = Sites;   // (as IEnumerable: a var of a mod type array is banned in game, VRS1001 - ModCheck misses it)
        var virt = VirtualLagrange();
        // (the kept list without the lock - the server's rides and the client's planner both ask, per frame: the lock was
        //  waited on, ~10 ms a second in a profile, 2026-10-14. The three are published together under the lock; a racing
        //  read that sees a new key with an old list falls to the locked path)
        var kept = System.Threading.Volatile.Read(ref _lagSites);
        if (ReferenceEquals(snap, System.Threading.Volatile.Read(ref _lagFor)) && ReferenceEquals(virt, System.Threading.Volatile.Read(ref _lagVirtFor))
            && ReferenceEquals(kept, System.Threading.Volatile.Read(ref _lagSites))) return kept;
        lock (_lagLock)
        {
            if (ReferenceEquals(snap, _lagFor) && ReferenceEquals(virt, _lagVirtFor)) return _lagSites;
            var l = new List<Site>();
            if (snap != null) foreach (var s in snap) if (s.Home?.Kind == SectorHomes.Kind.Lagrange) l.Add(s);
            int nReal = l.Count;
            // Every other Lagrange zone on the map (a body's L1-L5 with its parent, sector or not) is a sphere
            // of influence too: entered, ridden and left the same way.
            foreach (var v in virt)
            {
                bool dup = false;
                for (int i = 0; i < nReal && !dup; i++) dup = l[i].Home.Host == v.Home.Host && l[i].Home.Point == v.Home.Point;
                if (!dup) l.Add(v);
            }
            // (the list first, its keys after: a lock-free reader that matches both keys reads this list or a newer one)
            System.Threading.Volatile.Write(ref _lagSites, l); System.Threading.Volatile.Write(ref _lagFor, snap); System.Threading.Volatile.Write(ref _lagVirtFor, virt);
            return l;
        }
    }
    private static readonly object _lagLock = new object();
    private static List<Site> _lagSites = new List<Site>(), _lagVirtFor;
    private static object _lagFor;

    private static List<Site> _virtual;
    private static int _virtualKey = -1;
    /// <summary>A zone per body and point (L1-L5 with its parent), named "Palatine L2": no frame of its own.</summary>
    static List<Site> VirtualLagrange()
    {
        var reg = SystemHost.Registry;
        if (reg == null) return new List<Site>();
        int key = SystemHost.BeaconOf.Count * 1000 + reg.Bodies.Count;
        if (_virtual != null && key == _virtualKey) return _virtual;
        var list = new List<Site>();
        foreach (var b in reg.Bodies)
        {
            if (b.IsRoot || b.Parent == null || !SystemHost.BeaconOf.ContainsKey(b.Name)) continue;
            for (int p = 1; p <= 5; p++)
            {
                string name = $"{SystemHost.DisplayName(b.Name)} L{p}";
                list.Add(new Site
                {
                    FrameId = -1, Sector = name, Host = b.Name, Label = name,
                    Home = new SectorHomes.Home { Kind = SectorHomes.Kind.Lagrange, Host = b.Name, Point = p, Sector = name },
                });
            }
        }
        _virtual = list; _virtualKey = key;
        return list;
    }

    /// <summary>A frame's ride in a Lagrange sector, if it has one.</summary>
    public static Ride RideOf(long frameId) => System.Threading.Volatile.Read(ref _rideMap).TryGetValue(frameId, out var r) ? r : null;
    // (published copy for the client, as the sites': replaced whenever a ride is added or removed)
    private static Dictionary<long, Ride> _rideMap = new Dictionary<long, Ride>();
    private static void RidesChanged() => System.Threading.Volatile.Write(ref _rideMap, new Dictionary<long, Ride>(_rides));

    static bool SameElements(KeplerianElements a, KeplerianElements b) =>
        a.SemiMajorAxis == b.SemiMajorAxis && a.Eccentricity == b.Eccentricity && a.TrueAnomaly == b.TrueAnomaly && a.Epoch == b.Epoch
        && a.Inclination == b.Inclination && a.Raan == b.Raan && a.ArgPeriapsis == b.ArgPeriapsis;

    /// <summary>
    /// Frames entering a Lagrange sector's region (its teardrop; SectorHomes.InLagrangeRegion) switch onto
    /// its Lagrange orbit, as into a planet's sphere of influence: stepped with the sector's model (a pull
    /// toward the point in the frame turning with the planet, none in the calm core) and re-osculated every
    /// tick; a burn (the frame's elements changed under us) seeds it afresh; leaving the region drops it
    /// back onto its plain orbit.
    /// </summary>
    private static void RefreshRides(double t)
    {
        var reg = SystemHost.Registry;
        foreach (var f in SystemHost.Frames.Frames)
        {
            if (f.IsEncounter || f.Members.Count == 0) continue;
            var parent = reg.Find(f.ParentBodyName);
            if (parent == null) continue;
            StateVector po = parent.OriginInRoot(t), st = OrbitPropagation.StateAt(f.Elements, t);
            Vector3D pos = po.Position + st.Position, vel = po.Velocity + st.Velocity;
            if (!IsFinite(pos) || !IsFinite(vel)) continue;
            if (_rides.TryGetValue(f.Id, out var ride))
            {
                var body = reg.Find(ride.Site.Home.Host);
                if (!SameElements(f.Elements, ride.Set))
                {
                    // A burn: seed from the frame's own state now.
                    SectorHomes.LagrangeState(ride.Site.Home, reg, t, out var lp0, out var lv0);
                    ride.D = pos - lp0; ride.V = vel - lv0; ride.T = t;
                }
                // Step the ride to now (substeps of at most a minute: warp too).
                double dtAll = t - ride.T;
                int nSub = Math.Max(1, (int)Math.Ceiling(dtAll / 60.0));
                double h = dtAll / nSub, tt = ride.T;
                for (int k = 0; k < nSub && dtAll > 0; k++)
                {
                    if (!SectorHomes.LagrangeOrbit(body, tt, out var om, out var w)) break;
                    double core = SectorHomes.LagrangeCore(ride.Site.Home, reg);
                    Vector3D omDot = SectorHomes.LagrangeTurnRate(body, tt);
                    Vector3D a1 = SectorHomes.LagrangeAccel(ride.D, ride.V, om, w, core, omDot);
                    Vector3D dMid = ride.D + ride.V * (h / 2), vMid = ride.V + a1 * (h / 2);
                    Vector3D a2 = SectorHomes.LagrangeAccel(dMid, vMid, om, w, core, omDot);
                    ride.D += vMid * h; ride.V += a2 * h;
                    tt += h;
                }
                ride.T = t;
                SectorHomes.LagrangeState(ride.Site.Home, reg, t, out var lp, out var lv);
                Vector3D np = lp + ride.D, nv = lv + ride.V;
                if (!SectorHomes.InLagrangeRegion(ride.Site.Home, reg, t, np))
                {
                    _rides.Remove(f.Id); RidesChanged();
                    Event($"LAGRANGE exit: frame #{f.Id} leaves {ride.Site.Sector}");
                }
                var np2 = reg.Root.DeepestSoiContaining(np, t) ?? reg.Root;
                StateVector o2 = np2.OriginInRoot(t);
                var el = CaptureMath.CaptureElements(new StateVector(np - o2.Position, nv - o2.Velocity), np2.Mu, t);
                if (!IsFinite(el.SemiMajorAxis)) continue;
                f.ParentBodyName = np2.Name; f.Elements = el; f.VirtualVelocity = nv - o2.Velocity;
                if (_rides.TryGetValue(f.Id, out var still)) still.Set = el;
                continue;
            }
            foreach (var s in LagrangeSites())
            {
                if (!SectorHomes.InLagrangeRegion(s.Home, reg, t, pos)) continue;
                SectorHomes.LagrangeState(s.Home, reg, t, out var lp, out var lv);
                _rides[f.Id] = new Ride { Site = s, D = pos - lp, V = vel - lv, T = t, Set = f.Elements }; RidesChanged();
                Event($"LAGRANGE entry: frame #{f.Id} enters {s.Sector} ({(pos - lp).Length() / 1000:F0} km from its point, {(vel - lv).Length():F1} m/s)");
                break;
            }
        }
    }
}
