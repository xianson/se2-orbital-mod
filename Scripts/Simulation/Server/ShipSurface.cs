using SEAerospace;
using SEAerospace.Flight;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// LANDED SHIPS (2026-10-18, the UI audit B1). A grid in a planet's space (not on rails) had its orbit made from two of its
/// positions half a second apart: at rest on the ground that is a fall through the planet ("Ap -1.4 km, Pe -63 km, Period
/// 2 min"), and one read at the planet's very centre gave "Ap -63 km, Period 0 s, 96,955 m/s" - and the closest approach
/// on the HUD was predicted from them. Here a grid is classified from its view (its real place and velocity, the planet
/// cell being the planet's rotating ground frame) by SurfaceRule: Orbit, Landed, Low (in the air near the ground) or
/// Unknown (deep inside the body: no real place). A Landed / Low ship is a place on the ground that turns with the planet
/// (GroundState) - no Ap / Pe / period, and a closest approach measured to that place; an Unknown one is nowhere: never a
/// target state. Client-safe: reads the published views only. No allocation.
/// </summary>
public static class ShipSurface
{
    /// <summary>A grid's kind, its planet, its height over the surface (m) and its speed over the ground (m/s). False: no
    /// view, or not in a planet's cell (deep space). On rails: Orbit (its frame's orbit is a real one).</summary>
    public static bool Of(long gridId, out SurfaceKind kind, out string body, out double altitude, out double groundSpeed)
    {
        kind = SurfaceKind.Orbit; body = null; altitude = groundSpeed = double.NaN;
        var reg = SystemHost.Registry;
        if (reg == null || !ServerFrames.View(gridId, out var v)) return false;
        lock (ServerFrames.FramesLock)
        {
            var f = SystemHost.Frames?.FindByMember(gridId);
            if (f != null) { body = f.ParentBodyName; return true; }
        }
        if (!Cell(v, reg, out body, out var rc, out double R, out var gb)) return false;
        double r = rc.Length();
        altitude = r - R;
        if (v.HighSpeed) { kind = r < SurfaceRule.InsideShare * R ? SurfaceKind.Unknown : SurfaceKind.Orbit; return true; }   // (on its conic)
        groundSpeed = v.Velocity.Length();
        double t = SystemHost.Now;
        var chart = Chart.Of(body, t);
        double pe = SurfaceRule.Periapsis(chart.ToInertial(rc), chart.VelToInertial(rc, v.Velocity), gb.Mu);
        kind = SurfaceRule.Classify(r, R, groundSpeed, pe);
        return true;
    }

    /// <summary>The grid is landed (at rest on a planet's ground): which planet. (fx-detect's ask, the same rule.)</summary>
    public static bool Landed(long gridId, out string body)
        => Of(gridId, out var k, out body, out _, out _) && k == SurfaceKind.Landed;

    /// <summary>A target key (a ship's) with no orbit to tell: landed, low over the ground, or nowhere real.</summary>
    public static bool IsOnSurface(string key)
        => Targets.GridOf(key, out long id) && Of(id, out var k, out _, out _, out _) && SurfaceRule.NoOrbit(k);

    /// <summary>The player's words for a ship target that has no orbit ("Landed on Verdure", "Flying low over Verdure",
    /// "Position unknown"); null for one on an orbit (or not a ship).</summary>
    public static string Describe(string key)
    {
        if (!Targets.GridOf(key, out long id) || !Of(id, out var k, out string body, out _, out _)) return null;
        switch (k)
        {
            case SurfaceKind.Landed: return "Landed on " + SystemHost.DisplayName(body);
            case SurfaceKind.Low: return "Flying low over " + SystemHost.DisplayName(body);
            case SurfaceKind.Unknown: return "Position unknown";
            default: return null;
        }
    }

    /// <summary>A grid's place as a point on its planet's ground at tk (it turns with the planet): about the planet, inertial.
    /// False: no view or not in a planet's cell.</summary>
    public static bool GroundState(long gridId, double tk, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        var reg = SystemHost.Registry;
        if (reg == null || !ServerFrames.View(gridId, out var v) || !Cell(v, reg, out string body, out var rc, out _, out var gb)) return false;
        var chart = Chart.Of(body, tk);
        parent = gb;
        rel = new StateVector(chart.ToInertial(rc), chart.VelToInertial(rc, Vector3D.Zero));
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>A grid's state now from its view - its real place and velocity, the cell's spin taken out: about its planet,
    /// inertial. False: no view, not in a planet's cell, on rails, or riding its conic (HighSpeed: no physical velocity).</summary>
    public static bool InertialState(long gridId, out GravityBody parent, out StateVector rel)
    {
        parent = null; rel = default;
        var reg = SystemHost.Registry;
        if (reg == null || !ServerFrames.View(gridId, out var v) || v.HighSpeed) return false;
        lock (ServerFrames.FramesLock) if (SystemHost.Frames?.FindByMember(gridId) != null) return false;
        if (!Cell(v, reg, out string body, out var rc, out _, out var gb)) return false;
        var chart = Chart.Of(body, SystemHost.Now);
        parent = gb;
        rel = new StateVector(chart.ToInertial(rc), chart.VelToInertial(rc, v.Velocity));
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>Targets.At, surface-aware: a ship with no orbit is its ground point (Landed / Low) or nothing (Unknown).</summary>
    public static bool TargetAt(string key, double tk, out GravityBody parent, out StateVector rel)
    {
        if (Targets.GridOf(key, out long id) && Of(id, out var k, out _, out _, out _) && SurfaceRule.NoOrbit(k))
        {
            parent = null; rel = default;
            return k != SurfaceKind.Unknown && GroundState(id, tk, out parent, out rel);
        }
        return Targets.At(key, tk, out parent, out rel);
    }

    /// <summary>Targets.About, surface-aware (TargetAt): the target's state about a given body at tk.</summary>
    public static bool TargetAbout(string key, GravityBody about, double tk, out StateVector rel)
    {
        rel = default;
        if (about == null || !TargetAt(key, tk, out var p, out var r) || p == null) return false;
        if (ReferenceEquals(p, about)) { rel = r; return true; }
        var o = p.OriginInRoot(tk); var a = about.OriginInRoot(tk);
        rel = new StateVector(o.Position + r.Position - a.Position, o.Velocity + r.Velocity - a.Velocity);
        return IsFinite(rel.Position) && IsFinite(rel.Velocity);
    }

    /// <summary>The view's planet cell: its body, the grid's offset from the cell centre (chart axes), the surface radius and
    /// the body. False: in no cell, or the body has no surface radius.</summary>
    static bool Cell(ServerFrames.GridView v, SEAerospace.SystemDef.SystemRegistry reg, out string body, out Vector3D rc, out double R, out GravityBody gb)
    {
        rc = default; R = 0; gb = null;
        if (!VoxelBerthRegistry.TryCellContaining(v.Position, reg, out body, out Vector3D cell)) return false;
        gb = reg.Find(body);
        R = reg.FindDefinition(body)?.RadiusMeters ?? 0;
        if (gb == null || !(R > 0)) return false;
        rc = v.Position - cell;
        return IsFinite(rc);
    }
}

public static partial class ServerFrames
{
    /// <summary>The grid is landed (at rest on a planet's ground): which planet - ShipSurface's rule (B1, 2026-10-18).</summary>
    public static bool Landed(long gridId, out string bodyName) => ShipSurface.Landed(gridId, out bodyName);
}
