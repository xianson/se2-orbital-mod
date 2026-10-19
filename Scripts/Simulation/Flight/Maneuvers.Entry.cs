using SEAerospace;
using SEAerospace.Entry;
using SEAerospace.Flight;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── re-entry on the PLAN (2026-10-18, the UI audit U3): the entry marks came only from the orbit you are on now
    //    (EntryHost.Prediction), so a planned deorbit leg showed none; where a path comes down was never said; a pass that
    //    stays above the air said nothing. Data here; the map and the card draw it. ──

    /// <summary>A planned leg's pass through a body's braking band.</summary>
    public struct EntryMark { public Leg Leg; public Pass Pass; public string Body; }

    /// <summary>Where the path comes to the ground: the body, when (universe time), latitude / longitude (degrees), what is
    /// near (your own ship or station on that ground, within NearRange; null when none) and how far (m), and the words.</summary>
    public struct LandingEstimate
    {
        public bool Ok;
        public string Body, Near, Text;
        public double T, Lat, Lon, NearDistance;
        /// <summary>The point about the body (inertial, at T) - for a map mark (Loc(body, At, T)).</summary>
        public Vector3D At;
        /// <summary>The leg it comes down on.</summary>
        public Leg Leg;
    }

    /// <summary>"near X" only within this (m) of the landing point.</summary>
    public const double NearRange = 150000;

    static readonly List<EntryMark> _pe = new List<EntryMark>();
    static long _peSig = long.MinValue; static double _peAt = double.NegativeInfinity; static LandingEstimate _land;

    /// <summary>The passes through the air on the PLANNED legs of your path (after a maneuver): one per leg that brakes in a
    /// band. Recomputed when the plan changes, at most once a second (wall) otherwise. The list is reused: read it now.</summary>
    public static List<EntryMark> PlannedEntries(double t)
    {
        Refresh(t);
        return _pe;
    }

    /// <summary>Where your path (the plan, or the orbit you are on) comes down, as words and a point. False: it does not.</summary>
    public static bool Landing(double t, out LandingEstimate e)
    {
        Refresh(t);
        e = _land;
        return e.Ok;
    }

    static void Refresh(double t)
    {
        double wall = Wall();
        if (!Trajectory(t, out var legs, out _)) { _pe.Clear(); _land = default; _peSig = long.MinValue; return; }
        long sig = _cSig;
        // (the path's signature moves with the base orbit too - a planet cell re-derives it every frame: at most 3 a second
        //  on a change, once a second without)
        if (wall - _peAt < (sig == _peSig ? 1.0 : 0.3)) return;
        _peSig = sig; _peAt = wall;
        _pe.Clear(); _land = default;
        double cap = EntryHost.Cap;
        var pf = FrameHost.PlayerFrame;
        double beta = Reentry.DragDensity > 0 ? EntryHost.BetaCached(pf) : 0;
        foreach (var l in legs)
        {
            var band = EntryHost.BandOf(l.Body.Name);
            Pass pass = null;
            if (l.Planned)
            {
                pass = EntryForecast.ForLeg(l.El, band, EntryHost.SpinOf(l.Body.Name), cap, l.T0, l.T1, beta);
                if (pass != null) _pe.Add(new EntryMark { Leg = l, Pass = pass, Body = l.Body.Name });
            }
            else if (EntryHost.Prediction is Pass cur && EntryHost.PredictedBody == l.Body.Name && cur.EnterTime >= l.T0 && cur.EnterTime <= l.T1) pass = cur;
            if (!_land.Ok && !l.Body.IsRoot && (l.Impact || (pass != null && pass.Landing))) _land = LandingOn(l, pass);
            if (_land.Ok || l.Impact) break;   // (nothing is flown after the ground)
        }
    }

    static LandingEstimate LandingOn(Leg l, Pass pass)
    {
        var e = new LandingEstimate { Leg = l, Body = l.Body.Name };
        double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
        if (!EntryForecast.Landing(l.El, l.T0, l.T1, R, pass, 3600, out double tl, out Vector3D at)) return e;
        e.T = tl; e.At = at;
        var chart = Chart.Of(l.Body.Name, tl);
        Vector3D ground = chart.FromInertial(at);   // (the planet-fixed point: the cell's axes)
        GroundPoint.LatLon(ground, chart.Spin ? chart.Axis : Vector3D.UnitY, out e.Lat, out e.Lon);
        // what of yours is near it: a ship or station on that ground (the views: the cell's own axes)
        e.NearDistance = double.NaN;
        var reg = SystemHost.Registry;
        if (reg != null && VoxelBerthRegistry.TryGetCell(l.Body.Name, reg, out Vector3D cell))
        {
            foreach (var s in Targets.ShipsCached())
            {
                if (!s.Yours || !Targets.GridOf(s.Key, out long id) || !ServerFrames.View(id, out var v)) continue;
                Vector3D rc = v.Position - cell;
                if (rc.Length() > R + SurfaceRule.Band(R)) continue;   // (not on that ground)
                double d = GroundPoint.Arc(ground, rc, R);
                if (d < NearRange && !(d >= e.NearDistance)) { e.NearDistance = d; e.Near = s.Name; }
            }
        }
        string where = GroundPoint.Text(e.Lat, e.Lon);
        e.Text = $"Lands on {SystemHost.DisplayName(l.Body.Name)}"
                 + (e.Near != null ? $" near {e.Near} ({HudPanel.Km(e.NearDistance)})" : "")
                 + (where.Length > 0 ? "  ·  " + where : "");
        e.Ok = true;
        return e;
    }

    /// <summary>"Pe 84 km: above the air (needs < 19 km)": your path's last leg about a body with air keeps its periapsis
    /// above the braking band, by less than AirGapShown band depths - you are trying to get in and are not there yet. Null:
    /// nothing to say. (U3, 2026-10-18: a pass that stays above the air showed nothing.)</summary>
    public static string AirGapLine(double t)
    {
        if (!Trajectory(t, out var legs, out _) || legs.Count == 0) return null;
        var l = legs[legs.Count - 1];
        if (l.Impact) return null;
        var band = EntryHost.BandOf(l.Body.Name);
        double R = SystemHost.Registry?.FindDefinition(l.Body.Name)?.RadiusMeters ?? 0;
        if (!band.IsValid || !(R > 0)) return null;
        double gap = EntryForecast.AirGap(l.El.PeriapsisRadius, band);
        if (double.IsNaN(gap) || gap > AirGapShown * (band.Top - R)) return null;
        int key = (int)(l.El.PeriapsisRadius / 100) ^ (int)(band.Top / 100) * 31;
        if (key == _gapKey && _gapLine != null) return _gapLine;
        _gapKey = key;
        return _gapLine = $"Pe {HudPanel.Km(l.El.PeriapsisRadius - R)}: above the air (needs < {HudPanel.Km(band.Top - R)})";
    }
    /// <summary>The air gap is told while the periapsis is within this many band depths (top over the surface) above it.</summary>
    public const double AirGapShown = 3.0;
    static int _gapKey = int.MinValue; static string _gapLine;
}
