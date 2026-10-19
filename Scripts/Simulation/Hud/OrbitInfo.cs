using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// ORBITAL ELEMENTS for the map, as panels: a title, a line saying what it is and what it goes round, then short sections
/// of labelled values - Orbit, Now, From you - with the technical elements folded under "More". The target's panel takes
/// the map's right column (CleanMap.TargetPanel); a hover gets a small one by the cursor. Elements are the osculating ones
/// about the body each thing goes round; heights are above that body's surface, a star's orbits are told as distances.
/// </summary>
public static class OrbitInfo
{
    public struct Orbit
    {
        public bool Ok;
        public GravityBody Parent;
        public KeplerianElements El;
        public StateVector Rel;
        public double R;          // the parent's radius (0: the star - distances, not heights)
        public bool Surface;      // a place on the ground: no orbit to tell
        public string Where;      // a ship's place in words ("Landed on Verdure", "Flying low over Verdure"); null: none
    }

    /// <summary>A panel: a title and its subtitle, sections of rows, an optional foot (a hint).</summary>
    public sealed class Info
    {
        public string Title, Sub, Foot;
        public bool Target;
        public readonly List<Section> Sections = new List<Section>();
        public Section Add(string head) { var s = new Section { Head = head }; Sections.Add(s); return s; }
    }
    public sealed class Section
    {
        public string Head;
        public bool Folded;   // (its rows hidden, its head a "More" toggle)
        public readonly List<Row> Rows = new List<Row>();
        public Section Row(string k, string v, int tone = 0) { Rows.Add(new Row { K = k, V = v, Tone = tone }); return this; }
    }
    /// <summary>Tone: 0 plain, 1 good (a rendezvous), 2 warning (an impact).</summary>
    public struct Row { public string K, V; public int Tone; }

    /// <summary>A target key's orbit now (any key: a body, a sector's site, a rock, a ship, a place).</summary>
    public static Orbit Of(string key, double t)
    {
        var o = new Orbit();
        if (!Targets.At(key, t, out var p, out var rel) || p == null) return o;
        o.Parent = p; o.Rel = rel; o.Ok = true;
        o.R = p.IsRoot ? 0 : SystemHost.Registry?.FindDefinition(p.Name)?.RadiusMeters ?? 0;
        o.Surface = key.StartsWith(ContactsTab.PlacePrefix, StringComparison.Ordinal) && rel.Position.Length() < o.R * 1.05;
        o.Surface |= ShipSurface.IsOnSurface(key);   // (a ship landed, or flying low: no orbit to tell - audit B1, fx-flight's rule)
        o.Where = ShipSurface.Describe(key);
        o.El = OrbitalMath.ToElements(rel, p.Mu, t);
        return o;
    }

    /// <summary>An orbit given by its elements (a leg of your path), at t.</summary>
    public static Orbit Of(GravityBody parent, KeplerianElements el, double t)
    {
        var o = new Orbit { Ok = parent != null, Parent = parent, El = el };
        if (!o.Ok) return o;
        o.R = parent.IsRoot ? 0 : SystemHost.Registry?.FindDefinition(parent.Name)?.RadiusMeters ?? 0;
        o.Rel = OrbitPropagation.StateAt(el, t);
        o.El = OrbitalMath.ToElements(o.Rel, parent.Mu, t);
        return o;
    }

    // ── words ──

    static string Km(double m) => HudPanel.Km(m);
    static string Deg(double rad) => $"{rad * 180 / Math.PI:F1}°";
    static string Body(GravityBody b) => b.IsRoot ? CleanMap.StarName : SystemHost.DisplayName(b.Name);

    /// <summary>A duration in words: "45 s", "16 min 11 s", "2 h 15 min", "3 d 4 h".</summary>
    public static string Dur(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
        s = Math.Max(0, s);
        if (s < 60) return $"{s:F0} s";
        if (s < 3600) return $"{Math.Floor(s / 60):F0} min {Math.Floor(s % 60):F0} s";
        if (s < 86400) return $"{Math.Floor(s / 3600):F0} h {Math.Floor(s % 3600 / 60):F0} min";
        return $"{Math.Floor(s / 86400):F0} d {Math.Floor(s % 86400 / 3600):F0} h";
    }

    /// <summary>What the target is: "Ship · yours", "Planet", "Moon of Verdure", "Sector", "Lagrange point", "Rock", "GPS point".</summary>
    public static string Kind(string key)
    {
        if (Targets.GridOf(key, out long id))
        {
            foreach (var e in Targets.ShipsCached()) if (e.Key == key) return e.Yours ? "Ship  ·  yours" : e.Tracked ? "Contact" : "Contact  ·  not yet tracked";
            return "Ship";
        }
        if (key.StartsWith(ContactsTab.PlacePrefix, StringComparison.Ordinal)) return "GPS point";
        var b = Targets.Body(key);
        if (b != null) return b.Parent == null || b.Parent.IsRoot ? "Planet" : $"Moon of {SystemHost.DisplayName(b.Parent.Name)}";
        var site = EncounterFrames.TargetSite(key);
        if (site?.Home?.Kind == SectorHomes.Kind.Lagrange) return "Lagrange point";
        if (Contacts.RockProgress(key) != null) return "Rock";
        return "Sector";
    }

    /// <summary>The orbit's own rows: apoapsis and periapsis (a star's as nearest / farthest), inclination, period.</summary>
    static void OrbitRows(Section s, Orbit o)
    {
        var e = o.El;
        bool star = o.R <= 0;
        if (e.IsElliptic) s.Row(star ? "Farthest" : "Apoapsis", Km(e.SemiMajorAxis * (1 + e.Eccentricity) - o.R));
        s.Row(star ? "Nearest" : "Periapsis", Km(e.PeriapsisRadius - o.R));
        if (!e.IsElliptic) s.Row("Escaping at", $"{e.HyperbolicExcessSpeed:N0} m/s");
        s.Row("Inclination", Deg(e.Inclination));
        if (e.IsElliptic && IsFinite(e.Period)) s.Row("Period", Dur(e.Period));
    }

    /// <summary>A hover's panel: title, what it is, its orbit (four rows), a hint.</summary>
    public static Info Hover(string title, string kind, Orbit o, string foot)
    {
        var i = new Info { Title = title, Foot = foot };
        if (!o.Ok) { i.Sub = kind; return i; }
        i.Sub = (kind != null ? kind + "  ·  " : "") + (o.Where ?? (o.Surface ? $"on {Body(o.Parent)}" : $"around {Body(o.Parent)}"));
        if (!o.Surface) OrbitRows(i.Add(null), o);
        return i;
    }

    /// <summary>The target's panel: Orbit, Now, From you, and the technical elements folded under More.</summary>
    public static Info Panel(string key, double t, bool more)
    {
        var o = Of(key, t);
        var i = new Info { Title = Targets.Name(key), Target = true };
        string kind = Kind(key);
        // (2026-10-18, UI audit C7: "lost: not where it was" - say what it means)
        if (!o.Ok) { i.Sub = kind; i.Add(null).Row("Where", "unknown: not seen where it was", 2); return i; }
        i.Sub = kind + "  ·  " + (o.Where ?? (o.Surface ? $"on {Body(o.Parent)}" : $"around {Body(o.Parent)}"));

        var ap = Maneuvers.TargetApproach(t);
        if (o.Surface)
        {
            i.Add("Where").Row("On the ground", Body(o.Parent));
        }
        else
        {
            var e = o.El;
            OrbitRows(i.Add("Orbit"), o);
            // now: height, speed, the next apsis
            var now = i.Add("Now");
            now.Folded = more;   // (an accordion with More: one open at a time, so the panel keeps its size)
            double r = o.Rel.Position.Length();
            now.Row(o.R > 0 ? "Altitude" : "Distance", Km(r - o.R));
            now.Row("Speed", $"{o.Rel.Velocity.Length():N0} m/s");
            if (e.IsElliptic && e.Eccentricity > 0.002)
            {
                double ta = OrbitPropagation.TimeToTrueAnomaly(e, Math.PI), tp = OrbitPropagation.TimeToTrueAnomaly(e, 0);
                if (ta < tp) now.Row("Apoapsis in", Dur(ta)); else now.Row("Periapsis in", Dur(tp));
            }
        }

        // from you: the closest approach on your path, and how the orbits sit (round the same body)
        var you = i.Add("From you");
        if (ap.Here) you.Row("You are", "there", 1);
        else if (ap.Tracking) you.Row("Tracking", ap.TrackingText);
        else if (ap.Ok)
        {
            if (ap.Impact) you.Row("Your path", $"hits it in {Dur(ap.At - t)}", 2);
            else
            {
                you.Row(ap.Body ? "Closest pass" : "Closest approach", Km(ap.Body ? ap.Alt : ap.Dist) + (ap.Body ? " up" : "") + $"  in {Dur(ap.At - t)}", ap.Rendezvous ? 1 : 0);
                if (!double.IsNaN(ap.RelV)) you.Row("Relative speed", $"{ap.RelV:N0} m/s");
                if (ap.Rendezvous) you.Row("Rendezvous", "on this path", 1);
            }
        }
        else you.Row("Closest approach", "none on this path");
        if (!o.Surface && Maneuvers.Trajectory(t, out var legs, out _) && legs.Count > 0 && !ap.Here)
        {
            var me = Of(legs[0].Body, legs[0].El, t);
            if (me.Ok && me.Parent == o.Parent)
            {
                Vector3D h1 = Vector3D.Cross(me.Rel.Position, me.Rel.Velocity), h2 = Vector3D.Cross(o.Rel.Position, o.Rel.Velocity);
                if (h1.LengthSquared() > 0 && o.Rel.Position.LengthSquared() > 0)
                {
                    Vector3D n = Vector3D.Normalize(h1), r1 = me.Rel.Position, r2 = o.Rel.Position;
                    Vector3D p2 = r2 - n * Vector3D.Dot(r2, n);
                    double deg = Math.Atan2(Vector3D.Dot(Vector3D.Cross(r1, p2), n), Vector3D.Dot(r1, p2)) * 180 / Math.PI;
                    you.Row("Along the orbit", Math.Abs(deg) < 0.5 ? "level with you" : $"{Math.Abs(deg):F0}° {(deg > 0 ? "ahead" : "behind")}");
                }
                if (h1.LengthSquared() > 0 && h2.LengthSquared() > 0)
                    you.Row("Plane offset", Deg(Math.Acos(Math.Clamp(Vector3D.Dot(Vector3D.Normalize(h1), Vector3D.Normalize(h2)), -1, 1))));
            }
            else if (me.Ok) you.Row("You orbit", Body(me.Parent));
        }

        if (!o.Surface)
        {
            var m = i.Add("More elements");
            m.Folded = !more;
            var e = o.El;
            m.Row("Eccentricity", $"{e.Eccentricity:F4}");
            m.Row("Semi-major axis", Km(Math.Abs(e.SemiMajorAxis)));
            m.Row("Ascending node", Deg(Norm(e.Raan)));
            m.Row("Arg. of periapsis", Deg(Norm(e.ArgPeriapsis)));
            m.Row("True anomaly", Deg(Norm(e.TrueAnomaly)));
            if (EncounterFrames.TargetSite(key)?.Home?.Kind == SectorHomes.Kind.Lagrange) m.Row("Note", "a Lagrange point: osculating");
        }
        return i;
    }

    static double Norm(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
}
