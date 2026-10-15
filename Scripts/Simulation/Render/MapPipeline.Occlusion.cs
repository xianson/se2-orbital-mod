using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>What hides lines: bodies in front of them, sector areas; dotted lines clipped to the screen.</summary>
public static partial class MapPipeline
{
    // ── bodies hide the lines behind them (an orbit line ran across the planet's globe) ──
    private struct Occluder { public Vector2 C; public float R; public double Depth, RWorld; }
    private static readonly List<Occluder> _occluders = new List<Occluder>();

    // Sector areas on screen (outline polygons): orbit lines are not drawn through them. Two lists swapped each frame;
    // the outlines' own lists are pooled (a map frame draws several areas: no per-frame arrays).
    private static List<List<Vector2>> _areas = new List<List<Vector2>>(), _areasPrev = new List<List<Vector2>>();
    private static readonly List<List<Vector2>> _areaPool = new List<List<Vector2>>();
    private static double _areasAt;

    /// <summary>A sector's area on screen (its outline): orbit lines do not cross it.</summary>
    public static void OccludeArea(IList<Vector2> poly)
    {
        if (poly == null || poly.Count < 3) return;
        List<Vector2> a;
        if (_areaPool.Count > 0) { a = _areaPool[_areaPool.Count - 1]; _areaPool.RemoveAt(_areaPool.Count - 1); a.Clear(); }
        else a = new List<Vector2>(poly.Count);
        for (int i = 0; i < poly.Count; i++) a.Add(poly[i]);
        _areas.Add(a);
    }


    /// <summary>A screen point inside a polygon (even-odd).</summary>
    public static bool InPolygon(List<Vector2> p, Vector2 s)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Y > s.Y) != (p[j].Y > s.Y) && s.X < (p[j].X - p[i].X) * (s.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
        return inside;
    }

    /// <summary>Inside a sector's area on screen (as drawn last frame; none once the map has not drawn for a moment).</summary>
    public static bool InSectorArea(Vector2 s)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_areasPrev.Count == 0 || now - _areasAt > 0.3) return false;
        foreach (var p in _areasPrev)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].Y > s.Y) != (p[j].Y > s.Y) && s.X < (p[j].X - p[i].X) * (s.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    /// <summary>
    /// A dotted path, the dashes measured along its whole length (the batch's own dashing restarts on
    /// every segment, and a small shape of short segments came out solid).
    /// A dashed polyline. (2026-10-12: entering an orbit frame froze the game for tens of seconds and took the managed heap
    /// to 36 GB - the orbit HUD's disc, its scale easing in from a tiny value, drew a dotted circle millions of px round:
    /// millions of 3 px dashes a frame into the UI command buffer (one 1.4 GB array), and phase, summed in a float over the
    /// whole length, lost its precision and crept in slivers. Now: the phase kept within one period (double), each segment
    /// clipped to the screen first (what lies off it only moves the phase), and at most MaxDashes a call.)
    /// </summary>
    public static int MaxDashes = 4000;
    public static void ScreenDotted(IList<Vector2> pts, bool closed, ColorSRGB color, float width, float dash, float gap)
    {
        if (pts == null || pts.Count < 2 || !(dash > 0) || !(gap > 0)) return;
        double period = dash + gap, phase = 0;
        int n = closed ? pts.Count : pts.Count - 1, dashes = 0;
        float m = 16f;   // (a margin past the screen's edges)
        var lo = new Vector2(-m, -m); var hi = ScreenSize + new Vector2(m, m);
        for (int i = 0; i < n && dashes < MaxDashes; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
            float len = (b - a).Length();
            if (!(len > 0) || !float.IsFinite(len)) continue;
            Vector2 dir = (b - a) / len;
            // the part of the segment on screen (Liang-Barsky on the screen rectangle); before it, the phase moves on
            float t0 = 0f, t1 = len;
            if (!ClipParam(a, dir, lo, hi, ref t0, ref t1)) { phase = (phase + len) % period; continue; }
            phase = (phase + t0) % period;
            double at = t0;
            while (at < t1 && dashes < MaxDashes)
            {
                if (phase < dash)
                {
                    double on = Math.Min(dash - phase, t1 - at);
                    ScreenLine(a + dir * (float)at, a + dir * (float)(at + on), color, width);
                    dashes++;
                    at += on; phase += on;
                }
                else
                {
                    double off = Math.Min(period - phase, t1 - at);
                    at += off; phase += off;
                }
                if (phase >= period) phase -= period;
            }
            phase = (phase + (len - t1)) % period;   // (what lies past the screen)
        }
    }

    /// <summary>The span [t0, t1] of a + dir t (|dir| = 1) inside the rectangle lo..hi; false when none.</summary>
    static bool ClipParam(Vector2 a, Vector2 dir, Vector2 lo, Vector2 hi, ref float t0, ref float t1)
    {
        bool Side(float p, float q, ref float u0, ref float u1)
        {
            if (Math.Abs(p) < 1e-12f) return q >= 0f;
            float r = q / p;
            if (p < 0f) { if (r > u1) return false; if (r > u0) u0 = r; }
            else { if (r < u0) return false; if (r < u1) u1 = r; }
            return true;
        }
        return Side(-dir.X, a.X - lo.X, ref t0, ref t1) && Side(dir.X, hi.X - a.X, ref t0, ref t1)
            && Side(-dir.Y, a.Y - lo.Y, ref t0, ref t1) && Side(dir.Y, hi.Y - a.Y, ref t0, ref t1) && t1 > t0;
    }

    /// <summary>A body's globe this frame (world centre and radius): lines behind it are not drawn.</summary>
    public static void Occlude(Vector3D centre, double radius)
    {
        if (_cam == null || !(radius > 0) || !Screen(centre, out var c)) return;
        var wt = _cam.Entity.Data.GetWorldTransform();
        Vector3D fwd = (QuaternionD)wt.Orientation * Vector3D.Forward, right = (QuaternionD)wt.Orientation * Vector3D.Right;
        if (!Screen(centre + right * radius, out var e)) return;
        float r = (e - c).Length();
        if (r < 2f) return;
        _occluders.Add(new Occluder { C = c, R = r, Depth = Vector3D.Dot(centre - wt.Position, fwd), RWorld = radius });
    }

    /// <summary>Behind (or inside) a body's globe as seen from the camera.</summary>
    public static bool Occluded(Vector3D world)
    {
        if (_occluders.Count == 0 || _cam == null || !Screen(world, out var s)) return false;
        var wt = _cam.Entity.Data.GetWorldTransform();
        double depth = Vector3D.Dot(world - wt.Position, (QuaternionD)wt.Orientation * Vector3D.Forward);
        foreach (var o in _occluders)
            if ((s - o.C).LengthSquared() < o.R * o.R && depth > o.Depth - o.RWorld * 0.9) return true;
        return false;
    }
}
