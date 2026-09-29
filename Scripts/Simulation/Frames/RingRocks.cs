using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// THE RINGS ARE FULL OF ROCKS, AS NUMBERS. Each planet's ring holds a seeded population of rocks, each on
/// its own circular orbit inside the ring (a small tilt within the ring's thickness). A rock is nothing but
/// its seed: where it is at any time is a function of the ring's name, its number and the universe clock, so
/// every machine computes the same rocks (synced for free), nothing is stored and nothing spawns.
///
/// Your planned path is checked against them where it runs through the ring: every pass within the
/// rendezvous range (<see cref="Maneuvers.RendezvousRange"/>) is a rendezvous, marked on the map and the
/// flight disc as the target's is, and moves as you shape the plan with maneuver nodes. A thick crossing
/// meets a few; a careful one threads between them.
///
/// The rocks sit only where the ring is clear of the planet's own space (outside 1.1 x its keep radius, as
/// every asteroid): nothing is ever met on a planet's border.
/// </summary>
public static class RingRocks
{
    public static bool Enabled = true;
    /// <summary>Rocks per ring (seeded): tuned so a straight crossing of Verdure's ring meets two or three.</summary>
    public static int PerRing = 160;
    /// <summary>How far ahead passes are looked for (s): the plan's legs, never past this.</summary>
    public static double Horizon = 3 * 3600;
    public static string Status = "-";

    public sealed class Belt
    {
        public string Name, Host;
        public GravityBody Body;
        public double Inner, Outer, Half;
        /// <summary>Sorted by radius.</summary>
        public double[] R, Phase, Tilt, Node;
        public int[] Number;
        /// <summary>A small cluster rather than a single rock (about a third, seeded).</summary>
        public bool[] Cluster;
    }

    public struct Pass
    {
        public Belt Belt;
        public int Rock;            // index into the belt's arrays
        public double T, D, V;      // when, how close (m), relative speed (m/s)
        public Maneuvers.Leg Leg;
        public string Label => $"{Belt.Name} #{Belt.Number[Rock]}";
    }

    private static List<Belt> _belts = new List<Belt>();
    private static string _beltsSig;

    /// <summary>The rings, from the game's tori (server) or the client's ring entities; rebuilt when they change.</summary>
    public static List<Belt> Belts()
    {
        var reg = SystemHost.Registry;
        if (reg == null || !Enabled) return _belts;
        var found = new List<(string planet, double inner, double outer, double half)>();
        foreach (var kv in SystemHost.BeaconOf)
            foreach (var (c, inn, outr, half) in PlanetRings.Known())
                if ((c - kv.Value.Center).Length() < Math.Max(5000.0, 0.05 * outr) && outr > inn) { found.Add((kv.Key, inn, outr, half)); break; }
        found.Sort((a, b) => string.CompareOrdinal(a.planet, b.planet));
        string sig = string.Join("|", found.ConvertAll(f => $"{f.planet}:{f.inner:F0}:{f.outer:F0}:{f.half:F0}")) + "|" + PerRing;
        if (sig == _beltsSig) return _belts;
        var list = new List<Belt>();
        foreach (var (planet, inner0, outer, half0) in found)
        {
            var body = reg.Find(planet);
            var def = reg.FindDefinition(planet);
            if (body == null || def == null) continue;
            double inner = Math.Max(inner0, PlanetBerths.KeepRadius(def) * 1.1);   // never on the planet's border
            if (!(outer > inner)) continue;
            double half = half0 > 0 ? half0 : 0.02 * (outer - inner);
            int n = Math.Max(0, PerRing);
            var rocks = new (double r, double ph, double ti, double no, int num, bool cl)[n];
            ulong s = Hash(planet + " ring");
            for (int i = 0; i < n; i++)
            {
                double r = Math.Sqrt(inner * inner + (outer * outer - inner * inner) * Unit(ref s));   // even over the ring's area
                double ph = 2 * Math.PI * Unit(ref s);
                double ti = Math.Atan2(half * (2 * Unit(ref s) - 1), r);                           // within the ring's thickness
                double no = 2 * Math.PI * Unit(ref s);
                bool cl = Unit(ref s) < 0.35;
                rocks[i] = (r, ph, ti, no, i + 1, cl);
            }
            Array.Sort(rocks, (a, b) => a.r.CompareTo(b.r));
            var belt = new Belt
            {
                Name = BeltName(planet), Host = planet, Body = body, Inner = inner, Outer = outer, Half = half,
                R = new double[n], Phase = new double[n], Tilt = new double[n], Node = new double[n], Number = new int[n], Cluster = new bool[n],
            };
            for (int i = 0; i < n; i++) { belt.R[i] = rocks[i].r; belt.Phase[i] = rocks[i].ph; belt.Tilt[i] = rocks[i].ti; belt.Node[i] = rocks[i].no; belt.Number[i] = rocks[i].num; belt.Cluster[i] = rocks[i].cl; }
            list.Add(belt);
        }
        _belts = list; _beltsSig = sig;
        Status = list.Count == 0 ? "no rings" : string.Join(", ", list.ConvertAll(b => $"{b.Name}: {b.R.Length} rocks {b.Inner / 1000:F0}-{b.Outer / 1000:F0} km"));
        return _belts;
    }

    /// <summary>The ring's sector name when the chart has one (Oblivara), else the planet's ring.</summary>
    static string BeltName(string planet)
    {
        foreach (var s in EncounterFrames.Sites)
            if (s.Home != null && s.Home.Kind == SectorHomes.Kind.Ring && s.Home.Host == planet && s.Home.Outer > s.Home.Inner) return s.Sector;
        return SystemHost.DisplayName(planet) + " ring";
    }

    /// <summary>A rock's place about its planet at t (planet-relative, model axes).</summary>
    public static Vector3D At(Belt b, int i, double t) => SectorHomes.Circular(b.Body.Mu, b.R[i], b.Phase[i], b.Tilt[i], b.Node[i], t);

    /// <summary>The rocks within range (m) of a planet-relative point at t, nearest first.</summary>
    public static List<(Belt b, int i, double d)> Near(GravityBody body, Vector3D p, double t, double range)
    {
        var l = new List<(Belt, int, double)>();
        double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        foreach (var b in Belts())
        {
            if (b.Body != body || Math.Abs(p.Z) > b.Half + range) continue;
            for (int i = Lower(b.R, r - range), hi = Lower(b.R, r + range); i < hi; i++)
            {
                double d = (At(b, i, t) - p).Length();
                if (d < range) l.Add((b, i, d));
            }
        }
        l.Sort((x, y) => x.Item3.CompareTo(y.Item3));
        return l;
    }

    /// <summary>The rock as a home (a one-orbit ring), for a site when it is met.</summary>
    public static SectorHomes.Home Home(Belt b, int i) => new SectorHomes.Home
    {
        Sector = $"{b.Name} #{b.Number[i]}", Kind = SectorHomes.Kind.Ring, Host = b.Host,
        Inner = b.R[i], Outer = b.R[i], Phase = b.Phase[i], Tilt = b.Tilt[i], Node = b.Node[i],
    };

    // ─────────────────────────────── passes ───────────────────────────────

    private static List<Pass> _passes = new List<Pass>();
    private static double _at = -1, _atGame;
    private static string _sig;

    /// <summary>
    /// The rendezvous with ring rocks on the planned path (the legs), soonest first. Recomputed at most once a
    /// second of real time (or 30 s of game time), or at once when the plan changes.
    /// </summary>
    public static List<Pass> Passes(List<Maneuvers.Leg> legs, double t)
    {
        if (!Enabled || legs == null) return _passes;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var ks = new System.Text.StringBuilder();
        lock (Maneuvers.Nodes) foreach (var nd in Maneuvers.Nodes) ks.Append('|').Append(nd.T).Append(nd.Pro).Append(nd.Nor).Append(nd.Rad);
        string sig = ks.ToString();
        if (sig == _sig && now - _at < 1.0 && Math.Abs(t - _atGame) < 30) return _passes;
        _sig = sig; _at = now; _atGame = t;
        var list = new List<Pass>();
        try
        {
            var belts = Belts();
            foreach (var l in legs)
                foreach (var b in belts)
                    if (b.Body == l.Body) Scan(b, l, t, list);
        }
        catch (Exception e) { Status = "passes failed: " + e.Message; }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        _passes = list;
        return _passes;
    }

    static void Scan(Belt b, Maneuvers.Leg l, double t, List<Pass> list)
    {
        double R = Maneuvers.RendezvousRange;
        double t0 = Math.Max(l.T0, t), t1 = Math.Min(l.T1, t + Horizon);
        if (!(t1 > t0) || b.R.Length == 0) return;
        // A step short against the fastest closing speed (both on orbits about the planet, head-on at worst).
        double vc = Math.Sqrt(b.Body.Mu / b.Inner);
        double dt = Math.Max(0.5, 0.4 * R / (2.5 * vc));
        // The ring's slab (with the range round it): only there can a pass happen.
        double rIn = b.Inner - R, rOut = b.Outer + R, zMax = b.Half + R;
        var open = new Dictionary<int, (double d, double tk, double last)>();
        void Close(int i, (double d, double tk, double last) c)
        {
            if (c.d > 4 * R) return;
            // refine the minimum round the best sample
            double a = c.tk - dt, bb = c.tk + dt;
            for (int q = 0; q < 30; q++)
            {
                double m1 = a + (bb - a) / 3, m2 = bb - (bb - a) / 3;
                if (Dist(b, i, l, m1) < Dist(b, i, l, m2)) bb = m2; else a = m1;
            }
            double tr = 0.5 * (a + bb), dr = Dist(b, i, l, tr);
            if (dr > c.d) { dr = c.d; tr = c.tk; }
            if (dr > R || tr < t0 || tr > t1) return;
            Vector3D vy = OrbitPropagation.StateAt(l.El, tr).Velocity;
            Vector3D vr = (At(b, i, tr + 0.5) - At(b, i, tr - 0.5));
            list.Add(new Pass { Belt = b, Rock = i, T = tr, D = dr, V = (vy - vr).Length(), Leg = l });
        }
        for (double tk = t0; tk <= t1; tk += dt)
        {
            Vector3D p = OrbitPropagation.StateAt(l.El, tk).Position;
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            if (r < rIn || r > rOut || Math.Abs(p.Z) > zMax) continue;
            // the rocks whose orbit radius is within range of yours
            int lo = Lower(b.R, r - R), hi = Lower(b.R, r + R);
            for (int i = lo; i < hi; i++)
            {
                double d = (At(b, i, tk) - p).Length();
                if (open.TryGetValue(i, out var c))
                {
                    if (tk - c.last > 3 * dt) { Close(i, c); open[i] = (d, tk, tk); }        // a new pass of the same rock
                    else open[i] = d < c.d ? (d, tk, tk) : (c.d, c.tk, tk);
                }
                else if (d < 4 * R) open[i] = (d, tk, tk);
            }
        }
        foreach (var kv in open) Close(kv.Key, kv.Value);
    }

    static double Dist(Belt b, int i, Maneuvers.Leg l, double tk) => (At(b, i, tk) - OrbitPropagation.StateAt(l.El, tk).Position).Length();

    /// <summary>First index with a[i] &gt;= x.</summary>
    static int Lower(double[] a, double x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < x) lo = m + 1; else hi = m; }
        return lo;
    }

    static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s ?? "") { h ^= c; h *= 1099511628211UL; }
        return h;
    }
    static ulong Next(ref ulong s)
    {
        s += 0x9E3779B97F4A7C15UL;
        ulong z = s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    static double Unit(ref ulong s) => (Next(ref s) >> 11) * (1.0 / (1UL << 53));
}
