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
    /// <summary>DEV: draw the old LOCAL/SYSTEM debug layers when the unified map is not drawing.</summary>
    public static bool LegacyLayers;
    public static string Status = "map closed";
    public static bool DiagCross;
    /// <summary>The map globe model's own radius (1.03 units): globes render at radius x this.</summary>
    public const double GlobeModelRadius = 1.03;
    /// <summary>Last map-space position of each body's globe (for the spectator camera).</summary>
    public static readonly Dictionary<string, Vector3D> GlobePos = new Dictionary<string, Vector3D>();
    public static bool Visible { get; private set; }

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
        Visible = map != null && map.IsVisible;
        if (map == null || !map.IsVisible || !SystemHost.Built) { MapCamera.Release(session); CleanMap.ResetView(); if (map != null) UnifiedMap.RestoreGame(map); MapGlobes.HideAll(); Clear(); Status = map == null ? "no map component" : "map closed"; return; }
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        if (sectors == null) { Status = "no sectors component"; return; }

        FrameHost.Guard("MapCamera", () => { bool hm = UnifiedMap.TryMouse(map, out var mm); MapCamera.Tick(session, map, hm, mm); if (hm) DebugPanel.Tick(session, mm); });

        _builder ??= CreateBuilder(session);
        if (_builder == null) { Status = "no mesh builder"; return; }

        _cam = camera.Position;
        Vector3D mapPos = map.MapEntity.Data.GetWorldTransform().Position;
        Quaternion orient = map.Orientation;
        // Vertices relative to the map: the diorama is ~1 m across, hundreds of km from the origin,
        // where float positions only resolve centimetres (orbit rings collapsed; seen in game).
        _builder.SetPrimitiveOffset(mapPos);
        _builder.UpdateEntityTransform(new WorldTransform(mapPos));   // the batch sits at the offset
        Vector3D origin = sectors.MapWorldPosition;
        double scale = sectors.MapWorldScale;
        double planetScale = PlanetScale(map);
        double dist = (camera.Position - mapPos).Length();
        double span = Math.Max(1e-6, map.MaxDistance - map.MinDistance);
        double zoom = Math.Max(0, Math.Min(1, (dist - map.MinDistance) / span));
        bool system = Mode == ViewMode.System || (Mode == ViewMode.Auto && zoom >= SystemZoomFraction);

        var reg = SystemHost.Registry;
        bool unified;
        lock (ServerFrames.FramesLock) unified = UnifiedMap.Draw(session, _builder, map, sectors, camera.Position, mapPos, orient, t);
        if (unified) { Status = UnifiedMap.Status; }
        // The old debug layers only on request (a world without map data drew their yellow text).
        else if (LegacyLayers) lock (ServerFrames.FramesLock)
        {
            if (system) DrawSystem(reg, mapPos, orient, map.MaxDistance, t);
            else
            {
                DrawLocal(reg, mapPos, orient, origin, scale, planetScale, t);
                DrawSectorOrbits(session, map, sectors, mapPos, orient, origin, scale, t);
            }
        }
        if (DiagCross)
        {
            // DEV: a bright cross at the map entity, 0.5 map units each way, plus a sphere.
            var red = new ColorSRGB(1f, 0.1f, 0.1f);
            _builder.AddLine(mapPos - Rot(orient, new Vector3D(0.5, 0, 0)), mapPos + Rot(orient, new Vector3D(0.5, 0, 0)), red, true);
            _builder.AddLine(mapPos - Rot(orient, new Vector3D(0, 0, 0.5)), mapPos + Rot(orient, new Vector3D(0, 0, 0.5)), red, false);
            _builder.AddLine(mapPos, mapPos + Rot(orient, new Vector3D(0, 0.5, 0)), red, (float)0.01, false);
            _builder.AddSphere(new WorldTransform(mapPos, Quaternion.Identity), 0.05, red, red, false);
        }
        _builder.Commit();
        _drew = true;
        if (!unified) Status = $"{(system ? "SYSTEM" : "LOCAL")} zoom={zoom:F2} dist={dist:F3} scale={scale:E2} planetScale={planetScale:F1}";
    }

    // ───────────────────────────── local layer ─────────────────────────────


    // ───────────────────────────── band 1: the chart coming alive ─────────────────────────────

    /// <summary>Sectors farther than this from their planet go to its Trojan points (design section 6).</summary>
    public const double TrojanThreshold = 6.0e6;   // m: farther on the chart from every planet, a sector is Delfos's
    public static bool SectorOrbits = true;
    private static readonly ColorSRGB SectorColor = new ColorSRGB(0.45f, 0.85f, 1f);
    private static readonly ColorSRGB SectorSelColor = new ColorSRGB(1f, 0.85f, 0.2f);
    private static readonly ColorSRGB ArcColor = new ColorSRGB(0.35f, 0.6f, 0.8f);

    /// <summary>
    /// Each deep sector orbits its nearest planet at its charted distance, starting (t = 0) at its
    /// charted bearing, so at the epoch the chart IS the system. Draw where each sector is now: a
    /// marker on its orbit around the planet, a faint arc back to its charted cell, and for the
    /// selected sector its whole orbit and period.
    /// </summary>
    private static void DrawSectorOrbits(Keen.VRage.Core.Game.Systems.Session session, ColonizationMapSessionComponent map,
        SectorsSessionComponent sectors, Vector3D mapPos, Quaternion orient, Vector3D origin, double scale, double t)
    {
        if (!SectorOrbits || scale <= 0) return;
        var reg = SystemHost.Registry;
        Vector3D ToMap(Vector3D world) => ColonizationMapSessionComponent.WorldToMapPosition(world, mapPos, orient, origin, (float)scale);

        Keen.VRage.Library.Utils.StringId? selected = null;
        try
        {
            // MapSectorsRenderComponent derives from a type in VRage.Game.Client (not referenceable).
            object renderer = PlanetRenderBridge.GetMember(map, "SectorsRenderer");
            if (renderer != null && PlanetRenderBridge.GetMember(renderer, "SelectedSector") is Keen.VRage.Library.Utils.StringId sid) selected = sid;
        }
        catch { }

        foreach (var sc in sectors.Sectors)
        {
            Vector3D c = sc.Area.Center;
            // Host: the nearest planet (not a moon). Sectors that contain a planet are that planet's own.
            string host = null; Vector3D hostC = default; double best = double.MaxValue; bool ownsPlanet = false;
            foreach (var kv in SystemHost.BeaconOf)
            {
                var node = reg.Find(kv.Key);
                if (node == null || node.Parent == null || node.Parent.Parent != null) continue;   // planets only
                Vector3D pc = kv.Value.Center;
                if (sc.Contains(pc)) ownsPlanet = true;
                double d = (new Vector3D(c.X, pc.Y, c.Z) - pc).Length();
                if (d < best) { best = d; host = kv.Key; hostC = pc; }
            }
            if (host == null || ownsPlanet) continue;
            bool sel = selected.HasValue && selected.Value == Keen.VRage.Library.Utils.StringId.Get(sc.Name);
            var color = sel ? SectorSelColor : SectorColor;
            Vector3D chartMap = ToMap(c);

            if (best > TrojanThreshold)
            {
                // A Trojan region: millions of km away along the planet's orbit, not on this chart.
                _builder.AddText(chartMap, $"{sc.Name}: {host} Trojan", color, sel ? 0.55f : 0.4f);
                continue;
            }

            double mu = reg.Find(host).Mu;
            double r = best;
            double omega = Math.Sqrt(mu / (r * r * r));
            double theta0 = Math.Atan2(c.Z - hostC.Z, c.X - hostC.X);
            double theta = theta0 + omega * t;
            Vector3D At(double th) => ToMap(hostC + new Vector3D(Math.Cos(th) * r, 0, Math.Sin(th) * r));
            Vector3D now = At(theta);

            // Arc from the charted position to where it is now (at most one turn).
            double swept = omega * t;
            double sweep = Math.Min(Math.Abs(swept), 2 * Math.PI);
            int n = Math.Max(2, (int)(sweep / (2 * Math.PI) * 96));
            Vector3D prev = At(theta0);
            for (int i = 1; i <= n; i++)
            {
                Vector3D p = At(theta0 + Math.Sign(swept) * sweep * i / n);
                _builder.AddLine(prev, p, ArcColor, Width(prev, p, 0.6), true);
                prev = p;
            }
            if (sel)
            {
                // The selected sector: its whole orbit.
                Vector3D q = At(0);
                for (int i = 1; i <= 128; i++) { Vector3D p = At(2 * Math.PI * i / 128); _builder.AddLine(q, p, SectorSelColor, Width(q, p, 0.8), true); q = p; }
            }
            double size = Math.Max(1e-4, (_cam - now).Length() * 0.006);
            _builder.AddSphere(new WorldTransform(now, Quaternion.Identity), size, color, color, true);
            double period = 2 * Math.PI / omega;
            _builder.AddText(now, sel ? $"{sc.Name}  r {r / 1e6:F2} Mm  T {period / 3600:F1} h  v {Math.Sqrt(mu / r):F0} m/s" : sc.Name, color, sel ? 0.55f : 0.42f);
        }
    }

    private static void DrawLocal(SystemRegistry reg, Vector3D mapPos, Quaternion orient, Vector3D origin, double scale,
                                  double planetScale, double t)
    {
        if (scale <= 0) return;
        double k = planetScale / scale;   // world metres about a planet -> map units at its globe
        foreach (var body in reg.Bodies)
        {
            if (body.IsRoot || !VoxelBerthRegistry.TryGetCell(body.Name, reg, out Vector3D cell)) continue;
            Vector3D globe = ColonizationMapSessionComponent.WorldToMapPosition(cell, mapPos, orient, origin, (float)scale);
            GlobePos[body.Name] = globe;
            var def = reg.FindDefinition(body.Name);
            double R = def?.RadiusMeters ?? 0;
            // Laid flat on the map plane (the map is a flat chart; KSP's map does the same): (x, y, z) -> (x, z, y).
            Vector3D ToMap(Vector3D rel) => globe + Rot(orient, new Vector3D(rel.X, rel.Z, rel.Y) * k * GlobeModelRadius);

            // Keep ring: where the frames take over (the planet's local space ends).
            if (def != null) Ring(globe, orient, PlanetBerths.KeepRadius(def) * k * GlobeModelRadius, SoiColor);

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
        _builder.AddText(center + Rot(orient, new Vector3D(0, 0.03 * maxDistance, 0)), root.Name == "Star" ? CleanMap.StarName : root.Name, TextColor, 0.6f);

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
            if (IsFinite(a) && IsFinite(b)) _builder.AddLine(a, b, color, Width(a, b, 1.0), true);
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
            if (i > 0) _builder.AddLine(prev, p, color, Width(prev, p, 0.6), true);
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
