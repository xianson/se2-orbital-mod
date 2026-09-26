using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Render;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The orbital map: a KSP-style map view that works in every world (SE2's own strategic map only
/// exists where the world has colonization sectors). While active, the renderer runs in map mode
/// (only Map-type models drawn, the world hidden: exactly what the strategic map does), and a small
/// diorama is built at the player's position from Map-type meshes and the planets' own textured map
/// globes; the camera orbits it.
///  - Focus "system": the star, every planet on its heliocentric orbit (live positions), SOI rings,
///    and frames on heliocentric orbits.
///  - Focus a planet: its globe to scale, the keep ring, the player's orbit and every frame's orbit
///    about it, with Ap/Pe labels.
/// Opens automatically with the terminal's Map tab when the world has no colonization map.
/// </summary>
public static class OrbitalMap
{
    public static bool Active { get; private set; }
    public static string Focus = "auto";   // "auto" | "system" | body name
    /// <summary>Diorama radius, metres (the camera sits ~2.6x this away).</summary>
    public static double Size = 5000.0;   // big: the diorama sits thousands of km from the origin (float precision)
    public static double Bearing = 30, Elevation = 55, ZoomFactor = 1.8;
    public static double SpinDegPerSec = 0;   // slow turntable for screenshots / idle
    /// <summary>Turntable when opened from the Map tab: no player camera controls yet (the map's input
    /// actions live in an assembly mods cannot reference), so a slow orbit gives the view depth.</summary>
    public static double AutoSpinDegPerSec = 4;
    public static bool AutoWithMapTab = true;
    public static string Status = "off";

    private static MeshBuilder _builder;
    private static bool _drew;
    private static readonly Dictionary<string, PlanetRenderBridge.PlanetHandles> _handles = new Dictionary<string, PlanetRenderBridge.PlanetHandles>();
    private static readonly Dictionary<string, PlanetRenderBridge.Proxy> _globes = new Dictionary<string, PlanetRenderBridge.Proxy>();
    private static Vector3D _anchor;
    private static bool _manual;
    private static bool _shotUi = true;
    private static long _lastSpin;   // opened by command (not by the Map tab)
    private const int PathPoints = 180;

    private static readonly ColorSRGB PlayerColor = new ColorSRGB(1f, 0.85f, 0.1f);
    private static readonly ColorSRGB GridColor = new ColorSRGB(0.3f, 0.9f, 1f);
    private static readonly ColorSRGB OrbitColor = new ColorSRGB(0.55f, 0.7f, 0.95f);
    private static readonly ColorSRGB RingColor = new ColorSRGB(0.35f, 0.45f, 1.0f);
    private static readonly ColorSRGB StarColor = new ColorSRGB(1f, 0.85f, 0.4f);
    private static readonly ColorSRGB TextColor = new ColorSRGB(0.92f, 0.96f, 1f);

    private static readonly List<(PlanetRenderBridge.PlanetHandles h, Vector3D center)> _pendingHandles = new List<(PlanetRenderBridge.PlanetHandles, Vector3D)>();

    /// <summary>A planet's render handles and its world center (matched to a body by its cell center).</summary>
    public static void RegisterGlobe(PlanetRenderBridge.PlanetHandles h, Vector3D center)
    {
        if (h != null && h.HasProxyModel) _pendingHandles.Add((h, center));
    }

    /// <summary>A body's render handles (matched to its cell centre), or null.</summary>
    public static PlanetRenderBridge.PlanetHandles HandlesFor(string body)
    {
        if (_handles.TryGetValue(body, out var h)) return h;
        if (VoxelBerthRegistry.TryGetCell(body, SystemHost.Registry, out Vector3D cell))
            foreach (var ph in _pendingHandles) if ((ph.center - cell).Length() < 1000) { _handles[body] = ph.h; return ph.h; }
        return null;
    }

    public static string Open(bool manual = true)
    {
        if (Active) return "orbital map already open";
        _shotUi = PlanetRenderBridge.ShotWithoutUi; PlanetRenderBridge.ShotWithoutUi = false;   // keep the 3D labels in shots
        Active = true; _manual = manual;
        _anchor = FrameHost.PlayerPosition;
        PlanetRenderBridge.SetDraw3DMap(true);
        return "orbital map open";
    }

    public static string Close(Session session)
    {
        if (!Active) return "orbital map closed";
        Active = false;
        PlanetRenderBridge.ShotWithoutUi = _shotUi;
        PlanetRenderBridge.SetDraw3DMap(false);
        foreach (var g in _globes.Values) PlanetRenderBridge.SetProxyVisible(g, false);
        if (_builder != null && _drew) { _builder.Commit(); _drew = false; }
        SpecCam.Off(session);
        Status = "off";
        return "orbital map closed";
    }

    /// <summary>Client tick (after the frame host).</summary>
    public static void Tick(Session session, double t)
    {
        if (AutoWithMapTab && !_manual)
        {
            bool tab = MapTabOpen(session) && !ColonizationMapAvailable(session);
            if (tab && !Active) { Open(manual: false); SpinDegPerSec = AutoSpinDegPerSec; }
            else if (!tab && Active) Close(session);
        }
        if (!Active || !SystemHost.Built) return;

        _builder ??= CreateBuilder(session);
        if (_builder == null) { Status = "no mesh builder"; return; }
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double wdt = _lastSpin == 0 ? 0 : (nowTicks - _lastSpin) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastSpin = nowTicks;
        if (SpinDegPerSec != 0 && wdt > 0 && wdt < 0.5) Bearing = (Bearing + SpinDegPerSec * wdt) % 360;
        SpecCam.Orbit(_anchor, Size * ZoomFactor, Bearing, Elevation);

        _builder.SetPrimitiveOffset(_anchor);
        _builder.UpdateEntityTransform(new WorldTransform(_anchor));
        var reg = SystemHost.Registry;
        string focus = ResolveFocus(reg);
        var used = new HashSet<string>();
        lock (ServerFrames.FramesLock)
        {
            if (focus == "system") DrawSystem(reg, t, used);
            else DrawPlanet(reg, focus, t, used);
        }
        foreach (var kv in _globes) if (!used.Contains(kv.Key)) PlanetRenderBridge.SetProxyVisible(kv.Value, false);
        _builder.Commit();
        _drew = true;
        Status = $"open focus={focus} size={Size} bearing={Bearing:F0} elev={Elevation:F0}";
    }

    private static string ResolveFocus(SystemRegistry reg)
    {
        if (Focus != "auto") return reg.Find(Focus) != null && Focus != reg.Root?.Name ? Focus : "system";
        // KSP: focus the body you orbit; the system when you orbit the star.
        var f = FrameHost.PlayerFrame;
        if (f != null) return f.ParentBodyName == reg.Root?.Name ? "system" : f.ParentBodyName;
        return FrameHost.ObserverPlanetName ?? "system";
    }

    // ───────────────────────────── system focus ─────────────────────────────

    private static void DrawSystem(SystemRegistry reg, double t, HashSet<string> used)
    {
        var root = reg.Root;
        double maxR = 1;
        foreach (var b in root.Children)
        {
            var el = OrbitalMath.ToElements(b.StateInParentAt(t), root.Mu, t);
            maxR = Math.Max(maxR, IsFinite(el.SemiMajorAxis) && el.IsElliptic ? el.ApoapsisRadius : b.StateInParentAt(t).Position.Length());
        }
        foreach (var f in SystemHost.Frames.Frames)
            if (f.ParentBodyName == root.Name && f.Elements.IsElliptic && IsFinite(f.Elements.ApoapsisRadius))
                maxR = Math.Max(maxR, Math.Min(f.Elements.ApoapsisRadius, 3 * maxR));
        double k = Size / maxR;
        // The ecliptic (celestial XY, normal +Z) lies flat: celestial (x, y, z) -> world (x, z, y).
        Vector3D Map(Vector3D cel) => _anchor + new Vector3D(cel.X, cel.Z, cel.Y) * k;

        _builder.AddSphere(new WorldTransform(_anchor, Quaternion.Identity), Size * 0.04, StarColor, StarColor, true);
        _builder.AddText(_anchor + new Vector3D(0, Size * 0.08, 0), "Sun", StarColor, 1.2f);
        foreach (var b in root.Children)
        {
            var el = OrbitalMath.ToElements(b.StateInParentAt(t), root.Mu, t);
            if (IsFinite(el.SemiMajorAxis)) Polyline(OrbitSampler.SamplePath(el, PathPoints), Map, OrbitColor);
            Vector3D p = Map(b.StateInParentAt(t).Position);
            double globeR = Size * 0.035;
            Globe(b.Name, p, globeR, used);
            if (IsFinite(b.SoiRadius)) Ring(p, Math.Max(b.SoiRadius * k, globeR * 1.6), RingColor);
            bool you = (FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.ParentBodyName == b.Name) ||
                       (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanetName == b.Name);
            int n = 0;
            foreach (var f in SystemHost.Frames.Frames) if (f.ParentBodyName == b.Name) n++;
            string label = b.Name + (you ? "  < you" : "") + (n > 0 ? $"  [{n} in orbit]" : "");
            _builder.AddText(p + new Vector3D(0, globeR * 2.2, 0), label, you ? PlayerColor : TextColor, 1.0f);
        }
        foreach (var f in SystemHost.Frames.Frames)
        {
            if (f.ParentBodyName != root.Name) continue;
            bool mine = FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == f.Id;
            Conic(f.Elements, double.PositiveInfinity, t, Map, mine ? PlayerColor : GridColor, 0, mine ? "you" : $"#{f.Id}", k);
        }
    }

    // ───────────────────────────── planet focus ─────────────────────────────

    private static void DrawPlanet(SystemRegistry reg, string body, double t, HashSet<string> used)
    {
        var node = reg.Find(body);
        var def = reg.FindDefinition(body);
        if (node == null || def == null) { DrawSystem(reg, t, used); return; }
        double R = def.RadiusMeters;
        double keep = PlanetBerths.KeepRadius(def);
        // Fit: the widest orbit shown (capped at the SOI), at least the keep ring.
        double fit = keep;
        foreach (var f in SystemHost.Frames.Frames)
            if (f.ParentBodyName == body && f.Elements.IsElliptic && IsFinite(f.Elements.ApoapsisRadius))
                fit = Math.Max(fit, Math.Min(f.Elements.ApoapsisRadius, node.SoiRadius));
        if (FrameHost.PlayerFrame == null && FrameHost.TryGetLocalOrbit(body, t, out var lel) && lel.IsElliptic && IsFinite(lel.ApoapsisRadius))
            fit = Math.Max(fit, Math.Min(lel.ApoapsisRadius, node.SoiRadius));
        double k = Size / (fit * 1.1);
        // Planet cells are 1:1 with celestial axes; show the orbit plane flat when it is (x,y,z) -> (x,z,y).
        Vector3D Map(Vector3D rel) => _anchor + new Vector3D(rel.X, rel.Z, rel.Y) * k;

        Globe(body, _anchor, R * k, used);
        Ring(_anchor, keep * k, RingColor);
        _builder.AddText(_anchor + new Vector3D(0, R * k * 1.3 + Size * 0.03, 0), body, TextColor, 1.2f);
        _builder.AddText(Map(new Vector3D(keep, 0, 0)), "keep", RingColor, 0.75f);

        foreach (var f in SystemHost.Frames.Frames)
        {
            if (f.ParentBodyName != body) continue;
            bool mine = FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == f.Id;
            int grids = 0; foreach (long m in f.Members) if (GridMembers.IsGridId(m)) grids++;
            string label = mine ? (grids > 0 ? $"you + {grids} grid{(grids > 1 ? "s" : "")}" : "you") : $"#{f.Id} ({grids} grid{(grids != 1 ? "s" : "")})";
            Conic(f.Elements, node.SoiRadius, t, Map, mine ? PlayerColor : GridColor, R, label, k);
        }
        if (FrameHost.PlayerFrame == null && FrameHost.TryGetLocalOrbit(body, t, out var el))
            Conic(el, node.SoiRadius, t, Map, PlayerColor, R, FrameHost.HighSpeedActive ? "you (HighSpeed)" : "you", k);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static void Globe(string body, Vector3D center, double radius, HashSet<string> used)
    {
        if (!_handles.ContainsKey(body) && VoxelBerthRegistry.TryGetCell(body, SystemHost.Registry, out Vector3D cell))
            foreach (var ph in _pendingHandles) if ((ph.center - cell).Length() < 1000) { _handles[body] = ph.h; break; }
        if (_handles.TryGetValue(body, out var h))
        {
            if (!_globes.TryGetValue(body, out var g)) { g = PlanetRenderBridge.CreateProxy(h, center, mapOnly: true); if (g != null) _globes[body] = g; }
            if (g != null)
            {
                PlanetRenderBridge.UpdateProxy(h, g, center, radius);
                PlanetRenderBridge.SetProxyVisible(g, true);
                used.Add(body);
                return;
            }
        }
        _builder.AddSphere(new WorldTransform(center, Quaternion.Identity), radius, OrbitColor, TextColor, true);
    }

    private static void Conic(KeplerianElements el, double soi, double t, Func<Vector3D, Vector3D> map, ColorSRGB color,
                              double bodyRadius, string label, double k)
    {
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        Polyline(OrbitSampler.SamplePath(el, PathPoints, soi), map, color);
        var cur = OrbitPropagation.StateAt(el, t);
        if (IsFinite(cur.Position))
        {
            Vector3D p = map(cur.Position);
            _builder.AddSphere(new WorldTransform(p, Quaternion.Identity), Size * 0.012, color, color, true);
            _builder.AddText(p + new Vector3D(0, Size * 0.035, 0), label, color, 0.9f);
        }
        if (el.IsElliptic)
        {
            Vector3D pe = map(OrbitSampler.PositionAtTrueAnomaly(el, 0)), ap = map(OrbitSampler.PositionAtTrueAnomaly(el, Math.PI));
            _builder.AddSphere(new WorldTransform(pe, Quaternion.Identity), Size * 0.006, color, color, true);
            _builder.AddSphere(new WorldTransform(ap, Quaternion.Identity), Size * 0.006, color, color, true);
            _builder.AddText(pe + new Vector3D(0, Size * 0.025, 0), $"Pe {(el.PeriapsisRadius - bodyRadius) / 1000:F0} km", color, 0.75f);
            _builder.AddText(ap + new Vector3D(0, Size * 0.025, 0), $"Ap {(el.ApoapsisRadius - bodyRadius) / 1000:F0} km", color, 0.75f);
        }
    }

    private static void Polyline(OrbitPath path, Func<Vector3D, Vector3D> map, ColorSRGB color)
    {
        var pts = path.Points;
        if (pts == null || pts.Length < 2) return;
        int n = pts.Length, seg = path.IsClosed ? n : n - 1;
        float w = (float)(Size * 0.004);
        for (int i = 0; i < seg; i++)
        {
            Vector3D a = map(pts[i]), b = map(pts[(i + 1) % n]);
            if (IsFinite(a) && IsFinite(b)) _builder.AddLine(a, b, color, true);
        }
    }

    private static void Ring(Vector3D c, double r, ColorSRGB color)
    {
        const int n = 96;
        float w = (float)(Size * 0.0025);
        Vector3D prev = default;
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector3D p = c + new Vector3D(Math.Cos(a), 0, Math.Sin(a)) * r;
            if (i > 0) _builder.AddLine(prev, p, color, true);
            prev = p;
        }
    }

    private static bool MapTabOpen(Session session)
    {
        try
        {
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            object vm = term != null ? PlanetRenderBridge.GetMember(term, "_openVM") : null;
            if (vm == null || PlanetRenderBridge.GetMember(term, "_openTerminalScreen") == null) return false;
            object id = Keen.Game2.Client.UI.TerminalScreen.TerminalCategoryId.From(Keen.Game2.Client.UI.TerminalScreen.TerminalScreenMode.Map);
            return PlanetRenderBridge.CallBool(vm, "IsSelected", id);
        }
        catch { return false; }
    }

    private static bool ColonizationMapAvailable(Session session)
    {
        try
        {
            var s = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Colonization.SectorsSessionComponent>();
            return s != null && s.Sectors.Count > 0;
        }
        catch { return false; }
    }

    private static MeshBuilder CreateBuilder(Session session)
    {
        try { return session.Get<IDebugDraw>().MeshBuilderFactory.CreateMeshBuilder(MeshBuilder.Type.Map, 0L, 8192, "OrbitalMapView"); }
        catch { return null; }
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
