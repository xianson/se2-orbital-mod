using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── the map's marks: colours, the air on your path, after-pass apses, axis labels, navball icons ──
    static readonly ColorSRGB AutoColor = new ColorSRGB(0.96f, 0.62f, 0.18f, 1f);
    static readonly ColorSRGB ImpactColor = new ColorSRGB(1.00f, 0.30f, 0.25f, 1f);
    /// <summary>Reentry's marks (the entry interface, max-q, an airless border): the colour of heat.</summary>
    static readonly ColorSRGB EntryColor = new ColorSRGB(1.00f, 0.62f, 0.20f, 1f);

    /// <summary>The air you are about to dip into, in your orbit's plane: its top (the planet frame's border - physics
    /// below; "Atmosphere"), solid and faint, and the braking band's top (the entry interface), dotted.</summary>
    static void AtmosphereRings(GravityBody body, KeplerianElements el, Func<Vector3D, double, Vector3D> Loc, Func<Vector3D, Vector3D> W, float u, double limit, double t)
    {
        var b = EntryHost.BandOf(body.Name);
        if (!b.IsValid) return;
        var faint = HudPanel.Alpha(EntryColor, 0.35f);
        // (the ring's directions from a CIRCULAR copy of the orbit - same plane: a hyperbola's own positions past its
        //  asymptotes come out at a negative radius, the direction flipped - chords across the planet)
        el.Eccentricity = 0; el.SemiMajorAxis = 1;
        void Ring(double radius, bool dotted, string label)
        {
            var run = new List<Vector2>();
            const int m = 96;
            Vector2? top = null;
            for (int k = 0; k <= m; k++)
            {
                double nu = 2 * Math.PI * k / m;
                Vector3D dir = OrbitSampler.PositionAtTrueAnomaly(el, nu);
                double len = dir.Length();
                if (!(len > 0)) continue;
                Vector3D loc = Loc(dir * (radius / len), t);
                if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
                { Flush(); continue; }
                run.Add(sp);
                if (!top.HasValue || sp.Y < top.Value.Y) top = sp;
            }
            Flush();
            if (label != null && top.HasValue) HudPanel.TagAt(top.Value + new Vector2(6f * u, -10f * u), label, faint, u, diamond: false);
            void Flush()
            {
                if (run.Count > 1) { if (dotted) MapStyle.Plan(run, false, faint, MapStyle.Thin(u), u); else MapPipeline.ScreenPath(run, false, faint, MapStyle.Thin(u)); }
                run.Clear();
            }
        }
        double R = SystemHost.Registry?.FindDefinition(body.Name)?.RadiusMeters ?? 0;
        Ring(b.Bottom, false, $"Atmosphere {HudPanel.Km(b.Bottom - R)}");
        Ring(b.Top, true, null);
    }

    /// <summary>The orbit a predicted aerobraking pass leaves you on: faint, dashed, in the entry colour, from the
    /// exit round (an ellipse: one period; an escape: as far as the view), with its Pe / Ap.</summary>
    static void AfterPass(GravityBody body, SEAerospace.Entry.Pass ep, Func<Vector3D, double, Vector3D> Loc, Func<Vector3D, Vector3D> W, float u, double limit)
    {
        var el = ep.After;
        if (!IsFinite(el.SemiMajorAxis)) return;
        double t0 = ep.ExitTime, span = el.IsElliptic && IsFinite(el.Period) ? el.Period : 6 * 3600;
        var faintCol = HudPanel.Alpha(EntryColor, 0.5f);
        var run = new List<Vector2>();
        const int m = 128;
        for (int k = 0; k <= m; k++)
        {
            double tk = t0 + span * k / m;
            Vector3D loc = Loc(OrbitPropagation.StateAt(el, tk).Position, t0);
            if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || MapPipeline.Occluded(W(loc)) || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp))
            { MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u); run.Clear(); continue; }
            run.Add(sp);
        }
        MapStyle.Plan(run, false, faintCol, MapStyle.Thin(u), u);
        double R = SystemHost.Registry?.FindDefinition(body.Name)?.RadiusMeters ?? 0;
        void Apsis(double nu, string what, double alt)
        {
            Vector3D loc = Loc(OrbitSampler.PositionAtTrueAnomaly(el, nu), t0);
            if (Math.Sqrt(loc.X * loc.X + loc.Z * loc.Z) > limit * 1.04 || !MapPipeline.ToScreen(W(loc), out var sp) || !InMapArea(sp)) return;
            HudPanel.TagAt(sp, $"{what} {HudPanel.Km(alt)} (after pass)", EntryColor, u);
        }
        if (el.IsElliptic && el.Eccentricity > 0.002) Apsis(Math.PI, "Ap", el.SemiMajorAxis * (1 + el.Eccentricity) - R);
    }

    /// <summary>Patches drawn after your orbit (KSP's conic patch limit).</summary>
    public static int MaxPatches = 3;

    static readonly ColorSRGB[] PatchColors =
    {
        // The plan (after a maneuver): orange as KSP, then each further patch its own colour.
        new ColorSRGB(1.00f, 0.62f, 0.25f, 0.95f), new ColorSRGB(0.85f, 0.50f, 1.00f, 0.95f),
        new ColorSRGB(0.55f, 1.00f, 0.55f, 0.95f), new ColorSRGB(1.00f, 0.45f, 0.65f, 0.95f),
    };

    /// <summary>A label centred on an axis from a point, its near edge `gap` beyond it.</summary>
    static (Vector2 min, Vector2 max) AxisLabel(Vector2 from, Vector2 dir, float gap, string text, ColorSRGB c, float u)
    {
        var ts = MapPipeline.MeasureText(text, 0.5f * u);
        float ext = Math.Abs(dir.X) * ts.X * 0.5f + Math.Abs(dir.Y) * ts.Y * 0.5f;   // half the box along the axis
        Vector2 centre = from + dir * (gap + ext + 4f * u);
        HudPanel.LabelAt(centre - new Vector2(ts.X * 0.5f, 0), text, c, u);
        return (centre - ts * 0.5f - new Vector2(4f * u, 4f * u), centre + ts * 0.5f + new Vector2(4f * u, 4f * u));
    }

    /// <summary>The map's open area: between the game's side panels (left details, right index and list) and the tab bars.</summary>
    static bool InMapArea(Vector2 s)
    {
        var sz = MapPipeline.ScreenSize;
        return s.X > MapLayout.X(0.255f) && s.X < MapLayout.X(0.772f) && s.Y > MapLayout.Y(MapLayout.TabRowBottom) && s.Y < MapLayout.Y(0.84f);   // above the warp bar and hints
    }

    static string HandleName(string l) => l switch
    {
        "P" => "Prograde", "R" => "Retrograde", "N" => "Normal", "AN" => "Anti-normal", "RO" => "Radial out", "RI" => "Radial in", _ => l,
    };

    /// <summary>KSP's navball symbols, drawn with screen lines.</summary>
    static void Icon(string kind, Vector2 c, ColorSRGB col, bool hot, float u)
    {
        // Our KSP icons (textures); the line drawings below when they cannot be drawn.
        string tex = kind switch { "P" => "prograde", "R" => "retrograde", "N" => "normal", "AN" => "antinormal", "RO" => "radialout", "RI" => "radialin", _ => null };
        if (tex != null && MapPipeline.ScreenIcon(tex, c, (hot ? 13f : 10.5f) * u, col)) return;
        float r = (hot ? 8f : 6.5f) * u, w = (hot ? 2.2f : 1.6f) * u, t = 3.5f * u;
        void L(Vector2 a, Vector2 b) => MapPipeline.ScreenLine(c + a, c + b, col, w);
        void Tri(float s, bool up)
        {
            float k = up ? 1 : -1;
            Vector2 a = new Vector2(0, -r * k * s), b = new Vector2(r * 0.87f * s, r * 0.5f * k * s), d = new Vector2(-r * 0.87f * s, r * 0.5f * k * s);
            L(a, b); L(b, d); L(d, a);
        }
        switch (kind)
        {
            case "P":   // circle, dot, three ticks (top, left, right)
                MapPipeline.ScreenCircle(c, r, col, w); MapPipeline.ScreenCircle(c, 1.6f * u, col, 2.4f * u);
                L(new Vector2(0, -r), new Vector2(0, -r - t)); L(new Vector2(-r, 0), new Vector2(-r - t, 0)); L(new Vector2(r, 0), new Vector2(r + t, 0));
                break;
            case "R":   // circle with an X, three ticks
                MapPipeline.ScreenCircle(c, r, col, w);
                float q = r * 0.62f;
                L(new Vector2(-q, -q), new Vector2(q, q)); L(new Vector2(-q, q), new Vector2(q, -q));
                L(new Vector2(0, -r), new Vector2(0, -r - t)); L(new Vector2(-r * 0.7f, r * 0.7f), new Vector2(-(r + t) * 0.7f, (r + t) * 0.7f)); L(new Vector2(r * 0.7f, r * 0.7f), new Vector2((r + t) * 0.7f, (r + t) * 0.7f));
                break;
            case "N":   // triangle, dot
                Tri(1f, true); MapPipeline.ScreenCircle(c, 1.6f * u, col, 2.4f * u);
                break;
            case "AN":  // triangle with its vertices extended
                Tri(0.8f, true);
                L(new Vector2(0, -r * 0.8f), new Vector2(0, -r - t)); L(new Vector2(r * 0.7f, r * 0.4f), new Vector2((r + t) * 0.87f, (r + t) * 0.5f)); L(new Vector2(-r * 0.7f, r * 0.4f), new Vector2(-(r + t) * 0.87f, (r + t) * 0.5f));
                break;
            case "RO":  // circle, four spokes out
                MapPipeline.ScreenCircle(c, r * 0.8f, col, w);
                for (int i = 0; i < 4; i++) { double a = Math.PI / 4 + i * Math.PI / 2; var d = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)); L(d * r * 0.8f, d * (r + t)); }
                break;
            case "RI":  // circle, four spokes in
                MapPipeline.ScreenCircle(c, r, col, w);
                for (int i = 0; i < 4; i++) { double a = Math.PI / 4 + i * Math.PI / 2; var d = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)); L(d * r, d * r * 0.35f); }
                break;
        }
    }
}
