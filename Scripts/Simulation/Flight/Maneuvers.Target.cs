using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── the target: its brackets, the closest approach and rendezvous, ring rocks and sector crossings on the path ──
    static readonly ColorSRGB SectorIn = new ColorSRGB(0.45f, 1.00f, 0.55f, 1f);
    static readonly ColorSRGB SectorOut = new ColorSRGB(0.70f, 0.85f, 0.75f, 0.9f);

    /// <summary>The target (a <see cref="Targets"/> key: a body, a sector's site or rock, a ship): the path's closest approach
    /// to it is marked, and the target itself bracketed.</summary>
    public static string Target;

    /// <summary>Four corner brackets round a screen point: the target.</summary>
    static void TargetBrackets(Vector2 c, float r, float u)
    {
        float a = Math.Max(4f * u, r * 0.35f);
        for (int i = 0; i < 4; i++)
        {
            float sx = (i & 1) == 0 ? -1f : 1f, sy = (i & 2) == 0 ? -1f : 1f;
            var k = c + new Vector2(sx * r, sy * r);
            MapPipeline.ScreenLine(k, k - new Vector2(sx * a, 0), TargetColor, 2f * u);
            MapPipeline.ScreenLine(k, k - new Vector2(0, sy * a), TargetColor, 2f * u);
        }
    }
    static readonly ColorSRGB TargetColor = new ColorSRGB(0.95f, 0.45f, 0.85f, 1f);
    /// <summary>A ring rock's rendezvous (the rocks' colour on the map).</summary>
    public static readonly ColorSRGB RockColor = new ColorSRGB(1.00f, 0.80f, 0.45f, 1f);

    private static double _caAt = -1, _caGameT; private static string _caSig; private static (bool ok, double t, double d, Leg leg) _ca;
    private static double _caAt2 = -1, _caGameT2; private static string _caSig2; private static (bool ok, double t, double d, Leg leg) _ca2;

    /// <summary>
    /// Closest approach of the path to the target's site, on the legs about the site's own body (as
    /// KSP's intercept markers): time and distance. Recomputed at most once a second or on a change.
    /// </summary>
    static (bool ok, double t, double d, Leg leg) ClosestApproach(List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        // Keyed on the plan (not the base orbit, which a planet cell re-derives every frame), and
        // refreshed once a second of real time or 30 s of game time (warp).
        // (also on the base orbit - steady now that the local orbit is gated, OwnOrbit.GetState - and on which legs: the map
        //  asks with the path cut at a Lagrange entry, the card with all of it; one shared slot gave whichever came first)
        var ks = new System.Text.StringBuilder(Target ?? "");
        ks.Append('#').Append(_cSig).Append('#').Append(legs.Count).Append('#').Append(legs.Count > 0 ? legs[legs.Count - 1].T1 : 0);
        lock (Nodes) foreach (var nd in Nodes) ks.Append('|').Append(nd.T).Append(',').Append(nd.Pro).Append(',').Append(nd.Nor).Append(',').Append(nd.Rad);
        string sig = ks.ToString();
        double gt = SystemHost.Now;
        if (sig == _caSig && now - _caAt < 1.0 && Math.Abs(gt - _caGameT) < 30) return _ca;
        if (sig == _caSig2 && now - _caAt2 < 1.0 && Math.Abs(gt - _caGameT2) < 30) return _ca2;
        _caSig2 = _caSig; _caAt2 = _caAt; _caGameT2 = _caGameT; _ca2 = _ca;   // (the other caller's, kept)
        _caSig = sig; _caAt = now; _caGameT = gt; _ca = default;
        string key = Target;
        if (!Targets.At(key, gt, out _, out _)) return _ca;
        // (every leg, about whatever body it is: the target's place is taken about that body - a ship, a site or a planet
        //  alike; a planet's own legs give its periapsis)
        double best = double.MaxValue, bestT = double.NaN; Leg bestLeg = default;
        foreach (var l in legs)
        {
            var leg = l;
            var a = SEAerospace.Frames.RendezvousPlot.Closest(
                tk => OrbitPropagation.StateAt(leg.El, tk),
                tk => Targets.About(key, leg.Body, tk, out var rel) ? rel : (StateVector?)null,
                leg.T0, leg.T1);
            if (a.Ok && a.Distance < best) { best = a.Distance; bestT = a.T; bestLeg = leg; }
        }
        if (best < double.MaxValue) _ca = (true, bestT, best, bestLeg);
        return _ca;
    }

    /// <summary>
    /// The rendezvous with the target on your path, for the flight disc: when, how close, and where you and
    /// the target are then (offsets from the body the path is about then). False: no target or no approach.
    /// </summary>
    public static bool Rendezvous(double t, out GravityBody body, out double at, out double dist, out Vector3D you, out Vector3D target)
    {
        body = null; at = dist = double.NaN; you = target = default;
        if (Target == null || !Trajectory(t, out var legs, out _)) return false;
        var ca = ClosestApproach(legs);
        if (!ca.ok || ca.d > RendezvousRange || Targets.Body(Target) != null || !Targets.About(Target, ca.leg.Body, ca.t, out var trel)) return false;
        body = ca.leg.Body; at = ca.t; dist = ca.d;
        you = OrbitPropagation.StateAt(ca.leg.El, ca.t).Position;
        target = trel.Position;
        return true;
    }

    /// <summary>A rendezvous is only one when the path passes this close (m): where frames merge on arrival (a sector's Entry).</summary>
    public static double RendezvousRange => ServerFrames.Rendezvous.EnterRangeMeters;   // (the merge's own: ScreenMerges)

    /// <summary>Speed relative to the target at the closest approach (m/s), NaN when unknown.</summary>
    static double RelSpeed((bool ok, double t, double d, Leg leg) ca)
    {
        if (!ca.ok || !Targets.About(Target, ca.leg.Body, ca.t, out var rel)) return double.NaN;
        return (OrbitPropagation.StateAt(ca.leg.El, ca.t).Velocity - rel.Velocity).Length();
    }

    /// <summary>
    /// The closest approach told: a rendezvous when within the merge range, else how close the path comes (any
    /// distance: how far off you are is what a correction needs); a planet's as the altitude you pass at.
    /// </summary>
    static string ApproachText((bool ok, double t, double d, Leg leg) ca, double t)
    {
        double r = Targets.Radius(Target);
        double v = RelSpeed(ca);
        string vs = double.IsNaN(v) ? "" : $"  ·  {v:N0} m/s";
        if (r > 0) return ca.d <= r ? $"impact in {Clock(ca.t - t)}" : $"closest {HudPanel.Km(ca.d - r)} up in {Clock(ca.t - t)}{vs}";
        return ca.d <= RendezvousRange ? $"rendezvous {HudPanel.Km(ca.d)} in {Clock(ca.t - t)}{vs}" : $"closest {HudPanel.Km(ca.d)} in {Clock(ca.t - t)}{vs}";
    }

    /// <summary>The target's closest approach, as values (the map's target panel).</summary>
    public struct Approach
    {
        public bool Ok, Here, Rendezvous, Impact, Body, Tracking;
        public double At, Dist, Alt, RelV;
        public string TrackingText;
    }
    public static Approach TargetApproach(double t)
    {
        var a = new Approach();
        if (Target == null) return a;
        if (Contacts.RockProgress(Target) is double prog && prog < 1)
        { a.Tracking = true; a.TrackingText = $"{prog:P0}{(Contacts.Lidar ? " (lidar ranging)" : " (bearing only)")}"; return a; }
        if (RendezvousView.TargetIsOwnFrame(Target)) { a.Here = true; return a; }
        if (!Trajectory(t, out var legs, out _)) return a;
        var ca = ClosestApproach(legs);
        if (!ca.ok) return a;
        double r = Targets.Radius(Target);
        a.Ok = true; a.At = ca.t; a.Dist = ca.d; a.RelV = RelSpeed(ca);
        a.Body = r > 0; a.Alt = ca.d - r; a.Impact = r > 0 && ca.d <= r;
        a.Rendezvous = r <= 0 && ca.d <= RendezvousRange;
        return a;
    }

    /// <summary>Where the target was bracketed on the map last frame (world), for "show on map"; null when not drawn.</summary>
    public static Vector3D? TargetWorld;

    /// <summary>The target's closest approach as a line for the orbit card (null: no target or no approach).</summary>
    public static string TargetLine(double t)
    {
        if (Target == null) return null;
        // a rock seen but not tracked: no orbit yet, so no closest approach (a telescope's lidar on it speeds the fit)
        if (Contacts.RockProgress(Target) is double prog && prog < 1)
            return $"Target ≈ {Target}  ·  tracking {prog:P0}{(Contacts.Lidar ? "  ·  lidar ranging" : "  ·  bearing only")}";
        string name = Targets.Name(Target);
        if (RendezvousView.TargetIsOwnFrame(Target)) return $"Target {name}  ·  here";
        if (!Trajectory(t, out var legs, out _)) return null;
        var ca = ClosestApproach(legs);
        if (!ca.ok) return Targets.At(Target, t, out _, out _) ? $"Target {name}  ·  no approach on this path" : $"Target {name}  ·  lost";
        return $"Target {name}  ·  {ApproachText(ca, t)}";
    }

    public struct Crossing { public string Name; public double T; public bool Entry; public Leg Leg; }
    private static List<Crossing> _cross = new List<Crossing>();
    private static double _crossAt = -1; private static long _crossSig = long.MinValue; private static object _crossSites;

    /// <summary>
    /// When the path comes within the merge range (10 km) of a sector's site, and when it is 20 km away
    /// again (the split range), on every leg about the site's own body. Recomputed at most once a
    /// second, or when the plan changes.
    /// </summary>
    public static List<Crossing> SectorCrossings(double t, List<Leg> legs)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        long sig = _cSig;
        // (they change only with the plan or the sites: recomputed then - ~7 ms - and as a fallback every 10 s; it
        //  was every second, a 7 ms frame each second with the map open)
        object sites = EncounterFrames.Sites;
        if (sig == _crossSig && ReferenceEquals(sites, _crossSites) && now - _crossAt < 10.0) return _cross;
        _crossSig = sig; _crossAt = now; _crossSites = sites;
        var list = new List<Crossing>();
        double enter = ServerFrames.Rendezvous.EnterRangeMeters, leave = ServerFrames.SlotRadius;   // (the merge's and the split's own)
        foreach (var site in EncounterFrames.Sites)
        {
            if (site.Home?.Kind == SectorHomes.Kind.Lagrange) continue;   // its region is its sphere of influence (the Lagrange preview)
            foreach (var l in legs)
            {
                double span = l.T1 - l.T0;
                if (!(span > 0)) continue;
                int n = Math.Min(600, Math.Max(60, (int)(span / 20)));
                double Dist(double tk)
                {
                    if (!EncounterFrames.Ephemeris(site, tk, out var p, out var rel) || p != l.Body) return double.MaxValue;
                    return (OrbitPropagation.StateAt(l.El, tk).Position - rel.Position).Length();
                }
                double Cross(double a, double b, double level)
                {
                    for (int k = 0; k < 30; k++) { double m = 0.5 * (a + b); if ((Dist(a) < level) == (Dist(m) < level)) a = m; else b = m; }
                    return 0.5 * (a + b);
                }
                double prevT = l.T0, prevD = Dist(prevT);
                bool inside = prevD < enter;
                for (int k = 1; k <= n; k++)
                {
                    double tk = l.T0 + span * k / n, d = Dist(tk);
                    if (d == double.MaxValue) { prevT = tk; prevD = d; continue; }
                    if (!inside && d < enter && prevD >= enter) { list.Add(new Crossing { Name = site.Label, T = Cross(prevT, tk, enter), Entry = true, Leg = l }); inside = true; }
                    else if (inside && d > leave && prevD <= leave) { list.Add(new Crossing { Name = site.Label, T = Cross(prevT, tk, leave), Entry = false, Leg = l }); inside = false; }
                    prevT = tk; prevD = d;
                }
            }
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        _cross = list;
        return list;
    }
}
