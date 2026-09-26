using Keen.Game2.Client.WorldObjects.ColonizationMap;
using Keen.Game2.Simulation.GameSystems.Colonization;
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
/// KSP-style orbits inside SE2's own strategic map (the colonization map).
///
/// The colonization map is a small diorama built at the player's position (map units are metres
/// at the diorama; the camera sits 0.2 to 1 unit away). Everything here draws with a Map-type
/// mesh builder, which the renderer shows only while the map is open, on top of the map's own
/// sectors, planet globes and markers. Two layers, chosen by how far the map camera is zoomed out:
///  - LOCAL (zoomed in): around the map's planet globes, the player's orbit and every frame's
///    orbit, at the globe's own scale (inclination kept: the map flattens positions, not these).
///  - SYSTEM (zoomed out): the star system laid flat, KSP style: the star, each planet's
///    heliocentric orbit and live position, SOI rings, and frames on heliocentric orbits.
/// </summary>
public static class MapView
{
    public enum ViewMode { Auto, Local, System }
    public static ViewMode Mode = ViewMode.Auto;
    /// <summary>Zoom fraction (0 = closest, 1 = farthest) above which Auto shows the system layer.</summary>
    public static double SystemZoomFraction = 0.7;
    public static string Status = "map closed";

    private static MeshBuilder _builder;
    private static bool _drew;
    private const int PathPoints = 160;

    private static readonly ColorSRGB PlayerColor = new ColorSRGB(1f, 0.85f, 0.1f);
    private static readonly ColorSRGB GridColor = new ColorSRGB(0.3f, 0.9f, 1f);
    private static readonly ColorSRGB PlanetOrbitColor = new ColorSRGB(0.55f, 0.65f, 0.8f);
    private static readonly ColorSRGB SoiColor = new ColorSRGB(0.35f, 0.4f, 0.5f);
    private static readonly ColorSRGB StarColor = new ColorSRGB(1f, 0.9f, 0.5f);
    private static readonly ColorSRGB TextColor = new ColorSRGB(0.9f, 0.95f, 1f);

    private static IDisposable _terminal;

    /// <summary>Open the strategic map the way the map key does (the terminal's Map tab).</summary>
    public static async void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            if (ch == null || term == null) { Status = "cannot open map (no character/terminal)"; return; }
            var sel = new Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition.MapScreenSelection
                { Panel = Keen.Game2.Client.UI.TerminalScreen.MapScreenMode.Colonization };
            _terminal = await term.OpenTerminal(sel, ch, ch);
        }
        catch (Exception e) { Status = "open map failed: " + e.Message; }
    }

    public static void Close()
    {
        try { _terminal?.Dispose(); } catch { }
        _terminal = null;
    }

    public static ColonizationMapSessionComponent Map(Keen.VRage.Core.Game.Systems.Session session)
    {
        try { return session?.SessionComponents.TryGet<ColonizationMapSessionComponent>(); } catch { return null; }
    }

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        var map = Map(session);
        if (map == null || !map.IsVisible || !SystemHost.Built) { Clear(); Status = map == null ? "no map component" : "map closed"; return; }
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        if (sectors == null) { Status = "no sectors component"; return; }

        _builder ??= CreateBuilder(session);
        if (_builder == null) { Status = "no mesh builder"; return; }

        _cam = camera.Position;
        Vector3D mapPos = map.MapEntity.Data.GetWorldTransform().Position;
        Quaternion orient = map.Orientation;
        Vector3D origin = sectors.MapWorldPosition;
        double scale = sectors.MapWorldScale;
        double planetScale = PlanetScale(map);
        double dist = (camera.Position - mapPos).Length();
        double span = Math.Max(1e-6, map.MaxDistance - map.MinDistance);
        double zoom = Math.Max(0, Math.Min(1, (dist - map.MinDistance) / span));
        bool system = Mode == ViewMode.System || (Mode == ViewMode.Auto && zoom >= SystemZoomFraction);

        var reg = SystemHost.Registry;
        lock (ServerFrames.FramesLock)
        {
            if (system) DrawSystem(reg, mapPos, orient, map.MaxDistance, t);
            else DrawLocal(reg, mapPos, orient, origin, scale, planetScale, t);
        }
        _builder.Commit();
        _drew = true;
        Status = $"{(system ? "SYSTEM" : "LOCAL")} zoom={zoom:F2} dist={dist:F3} scale={scale:E2} planetScale={planetScale:F1}";
    }

    // ───────────────────────────── local layer ─────────────────────────────

    private static void DrawLocal(SystemRegistry reg, Vector3D mapPos, Quaternion orient, Vector3D origin, double scale,
                                  double planetScale, double t)
    {
        if (scale <= 0) return;
        double k = planetScale / scale;   // world metres about a planet -> map units at its globe
        foreach (var body in reg.Bodies)
        {
            if (body.IsRoot || !VoxelBerthRegistry.TryGetCell(body.Name, reg, out Vector3D cell)) continue;
            Vector3D globe = ColonizationMapSessionComponent.WorldToMapPosition(cell, mapPos, orient, origin, (float)scale);
            var def = reg.FindDefinition(body.Name);
            double R = def?.RadiusMeters ?? 0;
            Vector3D ToMap(Vector3D rel) => globe + Rot(orient, rel * k);

            // Keep ring: where the frames take over (the planet's local space ends).
            if (def != null) Ring(globe, orient, PlanetBerths.KeepRadius(def) * k, SoiColor);

            foreach (var f in SystemHost.Frames.Frames)
            {
                if (f.ParentBodyName != body.Name) continue;
                bool mine = FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == f.Id;
                DrawConic(f.Elements, body.SoiRadius, t, ToMap, mine ? PlayerColor : GridColor, R,
                          mine ? "You" : $"#{f.Id} ({f.Members.Count})");
            }
            // The player materialized near this planet: the osculating orbit (HighSpeed: its conic).
            if (FrameHost.PlayerFrame == null && FrameHost.TryGetLocalOrbit(body.Name, t, out KeplerianElements el))
                DrawConic(el, body.SoiRadius, t, ToMap, PlayerColor, R, "You");
        }
    }

    // ───────────────────────────── system layer ─────────────────────────────

    private static void DrawSystem(SystemRegistry reg, Vector3D mapPos, Quaternion orient, double maxDistance, double t)
    {
        var root = reg.Root;
        if (root == null) return;
        // Fit the farthest planet's apoapsis into ~40% of the zoom range.
        double maxR = 1;
        foreach (var b in root.Children)
        {
            var el = ElementsInParent(b, t);
            maxR = Math.Max(maxR, IsFinite(el.SemiMajorAxis) && el.IsElliptic ? el.ApoapsisRadius : b.StateInParentAt(t).Position.Length());
        }
        double k = 0.4 * maxDistance / maxR;
        Vector3D center = mapPos + Rot(orient, new Vector3D(0, 0.02 * maxDistance, 0));
        // The ecliptic (celestial XY, normal +Z) is laid flat on the map plane (map XZ).
        Vector3D ToMap(Vector3D cel) => center + Rot(orient, new Vector3D(cel.X, cel.Z, cel.Y) * k);

        _builder.AddSphere(new WorldTransform(center, Quaternion.Identity), 0.012 * maxDistance, StarColor, StarColor, true);
        _builder.AddText(center + Rot(orient, new Vector3D(0, 0.03 * maxDistance, 0)), root.Name == "Star" ? "Sun" : root.Name, TextColor, 0.6f);

        foreach (var b in root.Children)
        {
            var el = ElementsInParent(b, t);
            if (IsFinite(el.SemiMajorAxis)) Polyline(OrbitSampler.SamplePath(el, PathPoints), ToMap, PlanetOrbitColor);
            Vector3D p = ToMap(b.StateInParentAt(t).Position);
            var def = reg.FindDefinition(b.Name);
            _builder.AddSphere(new WorldTransform(p, Quaternion.Identity), 0.006 * maxDistance, PlanetOrbitColor, TextColor, true);
            if (IsFinite(b.SoiRadius)) Ring(p, orient, Math.Max(b.SoiRadius * k, 0.009 * maxDistance), SoiColor);
            // Frames about this planet are too small to resolve here: list them at the planet.
            int n = 0; bool you = false;
            foreach (var f in SystemHost.Frames.Frames)
                if (f.ParentBodyName == b.Name) { n++; you |= FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == f.Id; }
            bool youHere = you || (FrameHost.PlayerFrame == null && FrameHost.ObserverPlanetName == b.Name);
            string label = b.Name + (youHere ? "  < You" : "") + (n > (you ? 1 : 0) ? $"  ({n} frame{(n > 1 ? "s" : "")})" : "");
            _builder.AddText(p + Rot(orient, new Vector3D(0, 0.02 * maxDistance, 0)), label, youHere ? PlayerColor : TextColor, 0.5f);
        }
        // Frames on heliocentric orbits (between the planets).
        foreach (var f in SystemHost.Frames.Frames)
        {
            if (f.ParentBodyName != root.Name) continue;
            bool mine = FrameHost.PlayerFrame != null && FrameHost.PlayerFrame.Id == f.Id;
            DrawConic(f.Elements, double.PositiveInfinity, t, ToMap, mine ? PlayerColor : GridColor, 0, mine ? "You" : $"#{f.Id}");
        }
    }

    // ───────────────────────────── drawing helpers ─────────────────────────────

    private static void DrawConic(KeplerianElements el, double soi, double t, Func<Vector3D, Vector3D> toMap,
                                  ColorSRGB color, double bodyRadius, string label)
    {
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        Polyline(OrbitSampler.SamplePath(el, PathPoints, soi), toMap, color);
        var cur = OrbitPropagation.StateAt(el, t);
        if (IsFinite(cur.Position))
        {
            Vector3D p = toMap(cur.Position);
            double size = Math.Max(1e-4, (toMap(cur.Position * 1.02) - p).Length());
            _builder.AddSphere(new WorldTransform(p, Quaternion.Identity), size, color, color, false);
            _builder.AddText(p, label, color, 0.45f);
        }
        if (el.IsElliptic)
        {
            var pe = OrbitSampler.PositionAtTrueAnomaly(el, 0);
            var ap = OrbitSampler.PositionAtTrueAnomaly(el, Math.PI);
            _builder.AddText(toMap(pe), $"Pe {(el.PeriapsisRadius - bodyRadius) / 1000:F0} km", color, 0.35f);
            _builder.AddText(toMap(ap), $"Ap {(el.ApoapsisRadius - bodyRadius) / 1000:F0} km", color, 0.35f);
        }
    }

    private static void Polyline(OrbitPath path, Func<Vector3D, Vector3D> toMap, ColorSRGB color)
    {
        var pts = path.Points;
        if (pts == null || pts.Length < 2) return;
        int n = pts.Length, seg = path.IsClosed ? n : n - 1;
        for (int i = 0; i < seg; i++)
        {
            Vector3D a = toMap(pts[i]), b = toMap(pts[(i + 1) % n]);
            if (IsFinite(a) && IsFinite(b)) _builder.AddLine(a, b, color, Width(a, b, 1.0), false);
        }
    }

    /// <summary>A circle lying in the map plane.</summary>
    private static void Ring(Vector3D center, Quaternion orient, double radius, ColorSRGB color)
    {
        const int n = 64;
        Vector3D prev = default;
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Vector3D p = center + Rot(orient, new Vector3D(Math.Cos(a), 0, Math.Sin(a)) * radius);
            if (i > 0) _builder.AddLine(prev, p, color, Width(prev, p, 0.6), false);
            prev = p;
        }
    }

    private static Vector3D _cam;
    /// <summary>Line width in world metres, scaled by the distance to the camera (constant on screen).</summary>
    public static double LineWidth = 0.003;
    private static float Width(Vector3D a, Vector3D b, double k) => (float)(((a + b) * 0.5 - _cam).Length() * LineWidth * k);

    private static KeplerianElements ElementsInParent(GravityBody b, double t)
    {
        if (b.Parent == null) return default;
        return OrbitalMath.ToElements(b.StateInParentAt(t), b.Parent.Mu, t);
    }

    private static Vector3D Rot(Quaternion q, Vector3D v) => (QuaternionD)q * v;

    /// <summary>The map's globe size multiplier (ColonizationMapConfiguration.PlanetScale; private config).</summary>
    private static double PlanetScale(ColonizationMapSessionComponent map)
    {
        try
        {
            var cfg = PlanetRenderBridge.GetMember(map, "_configuration");
            var v = cfg != null ? PlanetRenderBridge.GetMember(cfg, "PlanetScale") : null;
            if (v is float f && f > 0) return f;
            if (v is double d && d > 0) return d;
        }
        catch { }
        return 1.0;
    }

    private static MeshBuilder CreateBuilder(Keen.VRage.Core.Game.Systems.Session session)
    {
        try { return session.Get<IDebugDraw>().MeshBuilderFactory.CreateMeshBuilder(MeshBuilder.Type.Map, 0L, 4096, "OrbitalModMap"); }
        catch { return null; }
    }

    public static void Clear()
    {
        if (_builder != null && _drew) { _builder.Commit(); _drew = false; }
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
