using Keen.Game2.Client.WorldObjects.ColonizationMap;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The game's colonization map, as our map uses it: its own objects hidden (sector mesh, globes, star
/// label; the star model is moved to Delfos's place), its sectors classified into their places in the
/// system (SectorHomes, from the game's chart about Delfos), its mouse and selection, its zoom limits;
/// then CleanMap draws the map.
/// </summary>
public static class GameMap
{
    public static bool Enabled = true;
    public static double ZoomOutFactor = 6.0;
    public static string Status = "-";

    private static float _baseMax = -1, _baseMin = -1;
    /// <summary>The game's original farthest map zoom (the unit of CleanMap's zoom u).</summary>
    public static double BaseMax => _baseMax;
    /// <summary>The game's original opening zoom (camera distance).</summary>
    public static double DefaultDistance;
    /// <summary>The closest zoom (map units), set by the map for the body it is about.</summary>
    public static double MinZoom = 1e-5;
    private static bool _gameHidden;
    /// <summary>A new world: nothing of the last one's map (hidden flags, stashed collider, per-object state, zoom range).</summary>
    public static void ResetWorld()
    {
        _gameHidden = false; _stashedCollider = null; _baseMax = _baseMin = -1;
        _starModelTried = false;   // (the new world's star model looked for again)
        lock (_objShown) _objShown.Clear();
        lock (_names) _names.Clear();
    }

    // Colours: the colonization states, KSP conventions for orbits.
    private static readonly ColorSRGB Locked = new ColorSRGB(0.42f, 0.45f, 0.5f);
    private static readonly ColorSRGB Selected = new ColorSRGB(1f, 0.85f, 0.2f);
    private static readonly ColorSRGB You = new ColorSRGB(0.35f, 0.88f, 1.00f);

    private sealed class SectorInfo
    {
        public SectorComponent Sector;
        public string Host;            // nearest planet
        public bool OwnsPlanet;
        public SectorHomes.Home Home;
    }

    /// <summary>Draw everything for this frame. Returns false when the unified map is off.</summary>
    public static bool Draw(Keen.VRage.Core.Game.Systems.Session session, MeshBuilder b, ColonizationMapSessionComponent map,
                            SectorsSessionComponent sectors, Vector3D cam, Vector3D mapPos, Quaternion orient, double t)
    {
        if (!Enabled) { RestoreGame(map); return false; }
        long g0 = ModCost.Start();
        if (_baseMax < 0) _baseMax = map.MaxDistance;
        if (map.MaxDistance < _baseMax * (float)ZoomOutFactor) map.MaxDistance = _baseMax * (float)ZoomOutFactor;
        // And in much closer: a low orbit is a speck at the planet view's sector-wide frame (KSP zooms
        // right down to the ship).
        if (_baseMin < 0) _baseMin = map.MinDistance;
        // The zoom is virtual (MapCamera): no near plane to keep off; the closest zoom keeps the map's
        // body smaller than the camera's distance (CleanMap sets it).
        map.MinDistance = (float)Math.Max(1e-7, MinZoom);
        // Opening: the game starts at a percentage of its zoom range, which our wider range turned into
        // a far zoom (the system view, sometimes); open at its original default distance instead.
        if (!_gameHidden)
        {
            float def = _baseMin + (_baseMax - _baseMin) * map.DefaultDistancePercentage / 100f;
            DefaultDistance = def;
            if (def > 0) PlanetRenderBridge.SettleMapZoom(map, def, def);
        }
        HideGame(map);
        ModCost.Sec("gm.hide").Stop(g0); g0 = ModCost.Start();

        var reg = SystemHost.Registry;
        Vector3D origin = sectors.MapWorldPosition;
        float scale = sectors.MapWorldScale;
        double dist = MapCamera.Distance > 0 ? MapCamera.Distance : (cam - mapPos).Length();   // the zoom, not where a pan has taken the camera
        double u = dist / Math.Max(1e-6, _baseMax);
        double w12 = Smooth(1.0, 2.0, u), w23 = Smooth(2.5, 3.5, u);
        Vector3D C(Vector3D world) => ColonizationMapSessionComponent.WorldToMapPosition(world, mapPos, orient, origin, scale);
        Vector3D Rot(Vector3D v) => (QuaternionD)orient * v;
        double W(double k = 1.0) => dist * 0.0022 * k;   // line width: constant on screen

        IColonizationProgress progress = null;
        try { progress = session.Get<IColonizationProgress>(); } catch { }
        StringIdSel(map, out var selected);

        // ── planets: families, and their place around the sun ──
        var planets = new List<GravityBody>();
        foreach (var body in reg.Root.Children) if (SystemHost.BeaconOf.ContainsKey(body.Name)) planets.Add(body);
        var infos = Classify(sectors, reg, planets);
        ModCost.Sec("gm.classify").Stop(g0);
        if (CleanMap.Enabled)
        {
            var bands = new List<CleanMap.Band>();
            foreach (var si in infos)
            {
                var st0 = SectorColonizationState.Locked;
                try { if (progress != null) st0 = progress.GetGlobalProgressFor(si.Sector).State; } catch { }
                bands.Add(new CleanMap.Band
                {
                    Name = si.Sector.Name, Host = si.Host, Home = si.Home, State = st0,
                    Selected = selected.HasValue && selected.Value == Keen.VRage.Library.Utils.StringId.Get(si.Sector.Name),
                });
            }
            // No colonization sectors (Creative): the virtual ones, drawn and listed as sectors (no game state; the selected
            // one is your target; listed under their planet - a moon's points with its planet)
            if (infos.Count == 0)
                foreach (var z in SectorHomes.Virtual(reg))
                    bands.Add(new CleanMap.Band
                    {
                        Name = z.Name, Host = SectorHomes.PlanetOf(z.Home.Host, reg), Home = z.Home, Virtual = true,
                        State = SectorColonizationState.Unlocked, Selected = Maneuvers.Target == z.Name,
                    });
            string youPlanet = null; Vector3D youRel = default; KeplerianElements? youOrbit = null;
            if (SunDriver.TryObserverCelestial(FrameHost.PlayerPosition, t, out Vector3D ycel))
            {
                // The body you are about: your frame's, else the smallest sphere of influence you are in
                // (moons too: at Palatine it said Verdure), else the nearest planet.
                var pf = FrameHost.PlayerFrame;
                if (pf != null && reg.Find(pf.ParentBodyName) != null) youPlanet = pf.ParentBodyName;
                else
                {
                    double bestSoi = double.MaxValue;
                    foreach (var body in reg.Bodies)
                    {
                        if (body.IsRoot || !(body.SoiRadius < bestSoi)) continue;
                        if ((ycel - body.OriginInRoot(t).Position).Length() <= body.SoiRadius) { bestSoi = body.SoiRadius; youPlanet = body.Name; }
                    }
                    if (youPlanet == null)
                    {
                        double bestD = double.MaxValue;
                        foreach (var p in planets) { double d = (ycel - p.OriginInRoot(t).Position).Length(); if (d < bestD) { bestD = d; youPlanet = p.Name; } }
                    }
                }
                if (youPlanet != null)
                {
                    youRel = ycel - reg.Find(youPlanet).OriginInRoot(t).Position;
                    var f = FrameHost.PlayerFrame;
                    if (f != null && f.ParentBodyName == youPlanet)
                        youOrbit = Maneuvers.Base(t, out var yb, out var yel) && yb?.Name == youPlanet ? yel : f.Elements;   // (riding: yours, not the frame's)
                    else if (f == null && FrameHost.TryGetLocalOrbit(youPlanet, t, out var le)) youOrbit = le;
                }
            }
            // The game's sector renderer draws OUR model now: keep it visible.
            PlanetRenderBridge.SetRenderComponentVisible(PlanetRenderBridge.GetMember(map, "SectorsRenderer"), true);
            var usedGlobes = new HashSet<string>();
            if (TryMouse(map, out var mouseNow)) CleanMap.Mouse = mouseNow;
            long c0 = ModCost.Start();
            CleanMap.Draw(session, PlanetRenderBridge.GetMember(map, "SectorsRenderer"), PlanetRenderBridge.GetMember(map, "_configuration"),
                          bands, reg, mapPos, orient, u, t, youPlanet, youRel, youOrbit, usedGlobes);
            ModCost.Map.Stop(c0);
            MapGlobes.End(usedGlobes);
            PlaceStar(map, mapPos, orient);
            // Delfos is our own proxy globe (as the planets): its model from the map's star prefab.
            if (reg.Root != null && !MapGlobes.Has(reg.Root.Name) && !_starModelTried)
            {
                _starModelTried = true;
                var cfg = PlanetRenderBridge.GetMember(map, "_configuration");
                if (PlanetRenderBridge.GetMember(cfg, "StarVisualPrefab") is Keen.VRage.Core.Game.Definitions.PrefabDefinition sp)
                    MapGlobes.Set(reg.Root.Name, PlanetRenderBridge.ResolveModelOnly(sp, reg.Root.Name));
            }
            ApplyPick(map);
            if (DevClick) { DevClick = false; map.OnSelectSector(); }
            Status = $"clean u={u:F2} {CleanMap.Status} {PickStatus}";
            return true;
        }
        return false;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Every sector's home (its place in the solar system), by sector name.</summary>
    internal static Dictionary<string, SectorHomes.Home> HomesBySector(SectorsSessionComponent sectors, SystemRegistry reg)
    {
        if (sectors == null || sectors.Sectors.Count == 0)
        {
            // (no colonization sectors: the virtual ones - SectorHomes.Virtual)
            var v = new Dictionary<string, SectorHomes.Home>();
            foreach (var z in SectorHomes.Virtual(reg)) v[z.Name] = z.Home;
            return v;
        }
        var planets = new List<GravityBody>();
        foreach (var body in reg.Root.Children) if (SystemHost.BeaconOf.ContainsKey(body.Name)) planets.Add(body);
        var d = new Dictionary<string, SectorHomes.Home>();
        foreach (var si in Classify(sectors, reg, planets)) if (si.Home != null) d[si.Sector.Name] = si.Home;
        return d;
    }

    private static List<SectorInfo> Classify(SectorsSessionComponent sectors, SystemRegistry reg, List<GravityBody> planets)
    {
        // Each sector is a Body, a Ring or a Lagrange point (SectorHomes.For). The chart is about Delfos
        // (its own sector the centre cell): a sector's bearing from there places a ring's site and a body
        // still to come on their orbits.
        var list = new List<SectorInfo>();
        Vector3D starC = default; bool haveStar = false;
        foreach (var sc in sectors.Sectors) if (sc.Name == SectorHomes.StarSector) { starC = sc.Area.Center; haveStar = true; }
        foreach (var sc in sectors.Sectors)
        {
            Vector3D c = sc.Area.Center;
            string nearest = null; double best = double.MaxValue;
            foreach (var p in planets)
            {
                Vector3D pc = SystemHost.BeaconOf[p.Name].Center;
                double d = (new Vector3D(c.X, pc.Y, c.Z) - pc).Length();
                if (d < best) { best = d; nearest = p.Name; }
            }
            double bearing = haveStar ? Math.Atan2(c.Z - starC.Z, c.X - starC.X) : 0;
            var home = SectorHomes.For(sc.Name, nearest, bearing, sc.Area.Size, reg);
            list.Add(new SectorInfo { Sector = sc, Host = home.Host, Home = home, OwnsPlanet = home.Kind == SectorHomes.Kind.Body && !home.Future });
        }
        return list;
    }

    private static void HideGame(ColonizationMapSessionComponent map)
    {
        SetGameVisible(map, false);
        _gameHidden = true;
    }

    /// <summary>Put the game's own sector mesh and globes back (unified map off or map closing).</summary>
    public static void RestoreGame(ColonizationMapSessionComponent map)
    {
        if (!_gameHidden || map == null) return;
        MapPipeline.Restore(PlanetRenderBridge.GetMember(map, "SectorsRenderer"));
        CleanMap.Reset();
        SetGameVisible(map, true);
        if (_baseMax > 0) map.MaxDistance = _baseMax;
        if (_baseMin > 0) map.MinDistance = _baseMin;
        PlanetRenderBridge.SettleMapZoom(map, map.MinDistance, map.MaxDistance);
        _gameHidden = false;
    }

    public static bool HideGameSectors = true;
    private static object _stashedCollider;
    public static string PickStatus = "";
    /// <summary>DEV: a mouse position (screen fractions) for the pick, instead of the real cursor.</summary>
    public static Vector2? DevMouse;
    /// <summary>DEV: the game's own click handler (select the hovered sector).</summary>
    public static bool DevClick;

    /// <summary>
    /// Selection by what we draw: the sector whose orbit line, marker or list row is under the mouse
    /// becomes the game's hovered sector (its own click handler then selects it, with its side panel,
    /// highlight and contracts). The game's own raycast against its Voronoi collider is off meanwhile.
    /// </summary>
    public static bool TryMouse(ColonizationMapSessionComponent map, out Vector2 mouse)
    {
        mouse = default;
        try
        {
            object win = PlanetRenderBridge.GetMember(PlanetRenderBridge.GetMember(map, "_windows"), "Window");
            if (!(PlanetRenderBridge.GetMember(win, "ClientMousePosition") is Vector2 m)) return false;
            if (DevMouse.HasValue) { var sz = MapPipeline.ScreenSize; m = new Vector2(DevMouse.Value.X * sz.X, DevMouse.Value.Y * sz.Y); }
            mouse = m;
            return true;
        }
        catch { return false; }
    }

    private static void ApplyPick(ColonizationMapSessionComponent map)
    {
        try
        {
            if (!TryMouse(map, out var mouse)) return;
            string name = Maneuvers.ClaimsMouse || MapCamera.Dragging ? null : MapPipeline.ResolvePick(mouse, 14f);
            CleanMap.Hovered = name;
            int idx = -1;
            if (name != null && PlanetRenderBridge.GetMember(PlanetRenderBridge.GetMember(map, "SectorsRenderer"), "SectorIds") is System.Collections.IList ids)
            {
                var sid = Keen.VRage.Library.Utils.StringId.Get(name);
                for (int i = 0; i < ids.Count; i++) if (ids[i] != null && ids[i].Equals(sid)) { idx = i; break; }
            }
            PlanetRenderBridge.SetMember(map, "_sectorIndexUnderCursor", idx);
            PlanetRenderBridge.SetMember(map, "_hoverIndex", idx);
            PickStatus = $"mouse=({mouse.X:F0},{mouse.Y:F0}) pick={name ?? "-"} idx={idx}";
        }
        catch (Exception e) { PickStatus = "pick: " + e.Message; }
    }
    private static object _stashedController, _stashedFrom;

    /// <summary>Put the markers renderer's player controller back (only into the renderer it came from).</summary>
    static void RestoreController(object mr)
    {
        if (_stashedController == null) return;
        if (ReferenceEquals(mr, _stashedFrom) && PlanetRenderBridge.GetMember(mr, "_playerControllerComponent") == null)
            PlanetRenderBridge.SetMember(mr, "_playerControllerComponent", _stashedController);
        _stashedController = null; _stashedFrom = null;
    }

    /// <summary>
    /// Every frame, first thing (before anything that can fail): the markers renderer's controller is
    /// back whenever the map is not ours to draw, or the game has switched the renderer on again (its
    /// ShowMap does; its UpdateMarkers job dereferences the controller unguarded).
    /// </summary>
    public static void Safety(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (_stashedController == null) return;
        try
        {
            var map = MapView.Map(session);
            var mr = map != null ? PlanetRenderBridge.GetMember(map, "MarkersRenderer") : null;
            if (mr == null || !ReferenceEquals(mr, _stashedFrom)) { _stashedController = null; _stashedFrom = null; return; }   // another session: forget
            bool enabled = PlanetRenderBridge.GetMember(mr, "Enabled") is bool e && e;
            if (!map.IsVisible || enabled) RestoreController(mr);
        }
        catch { }
    }

    private static void SetGameVisible(ColonizationMapSessionComponent map, bool visible)
    {
        if ((HideGameSectors && !CleanMap.Enabled) || visible)
            PlanetRenderBridge.SetRenderComponentVisible(PlanetRenderBridge.GetMember(map, "SectorsRenderer"), visible);
        // Globes: shrink through the engine's own scale path instead of render flags.
        // The game's own markers ("You are here", GPS): we draw our own.
        // The game's sector picking raycasts its Voronoi collider: off while our map owns the view
        // (UpdateInteraction returns early without it); ApplyPick picks by our drawing instead.
        try
        {
            object sr = PlanetRenderBridge.GetMember(map, "SectorsRenderer");
            if (sr != null && CleanMap.Enabled && !visible)
            {
                var col = PlanetRenderBridge.GetMember(sr, "_proceduralSectorCollider");
                if (col != null) { _stashedCollider = col; PlanetRenderBridge.SetMember(sr, "_proceduralSectorCollider", null); }
            }
            else if (sr != null && visible && _stashedCollider != null)
            {
                // Regenerated meanwhile (sectors changed)? Keep the new one, drop ours.
                if (PlanetRenderBridge.GetMember(sr, "_proceduralSectorCollider") == null) PlanetRenderBridge.SetMember(sr, "_proceduralSectorCollider", _stashedCollider);
                else (_stashedCollider as IDisposable)?.Dispose();
                _stashedCollider = null;
            }
        }
        catch { }
        try
        {
            var mr = PlanetRenderBridge.GetMember(map, "MarkersRenderer");
            if (mr != null)
            {
                PlanetRenderBridge.SetMember(mr, "Enabled", visible);
                // "You are here" (the map UI's overlay) is placed through the markers renderer's
                // TryGetMapScreenPosition, which returns false without a player controller: stash it
                // while we own the map (its draw job is disabled above; every other use is guarded).
                if (!visible)
                {
                    var pc = PlanetRenderBridge.GetMember(mr, "_playerControllerComponent");
                    if (pc != null) { _stashedController = pc; _stashedFrom = mr; PlanetRenderBridge.SetMember(mr, "_playerControllerComponent", null); }
                }
                else RestoreController(mr);
            }
        }
        catch { }
        if (PlanetRenderBridge.GetMember(map, "_discoveredPlanets") is System.Collections.IDictionary planets)
            foreach (var v in planets.Values)
            {
                // Only on a change: UpdateScale rebuilds the globe's particle effects and collider.
                if (_objShown.TryGetValue(v, out bool was) && was == visible) continue;
                _objShown[v] = visible;
                PlanetRenderBridge.ScaleMapObject(v, visible ? 0f : 1e-4f);
                BlankName(v, !visible);
            }
        // The game's star (Delfos) too: our system view draws it at the centre. Its own Deactivate
        // (label and glow off). NOT UpdateScale: the star's render entity is not a model, and the scale
        // command crashes the render thread (seen in game, 2.4.0.95).
        object star = PlanetRenderBridge.GetMember(map, "_mainStar");
        if (star != null && (_starShown != visible || !ReferenceEquals(star, _star))) BlankName(star, !visible);
        if (star != null && (_starShown != visible || !ReferenceEquals(star, _star)))
        {
            // Its glow (looped particle effects) is what makes the star bright; Deactivate stopped it and
            // left a model lit only by the sun's direction (dark from most angles). Activate(false): glow
            // on, label off (ours names it).
            // Given back with the map closed: Deactivate, as the game would (Activate there left its orange
            // glow and model drawn at the map's spot, which follows you: a ball at your feet).
            try
            {
                bool open = map.IsVisible;
                if (visible && !open) star.GetType().GetMethod("Deactivate", Type.EmptyTypes)?.Invoke(star, null);
                else star.GetType().GetMethod("Activate", new[] { typeof(bool) })?.Invoke(star, new object[] { false });
            }
            catch { }
            // Its model too: the star's entity is moved far outside the map through its ordinary
            // transform (not its render model: scaling that crashed the renderer), and put back after.
            // Left in place it drew a brown-dwarf globe over whichever planet sat at its charted spot.
            try
            {
                if (!ReferenceEquals(star, _star)) _starMoved = false;   // a new map (session): nothing of ours on it
                var ent = PlanetRenderBridge.GetMember(star, "Entity") as Entity;
                if (ent != null)
                {
                    if (!visible)
                    {
                        if (!_starMoved) { _starRel = ent.Data.GetRelativeTransform(); _starMoved = true; }
                        ent.Data.Set(new RelativeTransform(_starRel.Position + new Vector3(0, 1e5f, 0), _starRel.Orientation));   // straight up: the map camera always looks down
                    }
                    else if (_starMoved)
                    {
                        ent.Data.Set(_starRel);
                        _starMoved = false;
                    }
                }
            }
            catch { }
            _starShown = visible; _star = star;
        }
    }
    /// <summary>
    /// Delfos is the game's own star model, moved each frame to where the map has the star (its
    /// ordinary transform; its render entity cannot be scaled, so it keeps its own size on screen).
    /// Put overhead, out of view, when the map has no place for it on screen.
    /// </summary>
    private static void PlaceStar(ColonizationMapSessionComponent map, Vector3D mapPos, Quaternion orient)
    {
        try
        {
            object star = PlanetRenderBridge.GetMember(map, "_mainStar");
            var ent = star != null ? PlanetRenderBridge.GetMember(star, "Entity") as Entity : null;
            if (ent == null || !_starMoved) { StarPlaced = false; return; }
            Vector3D? at = CleanMap.StarWorld;
            var scr = MapPipeline.ScreenSize;
            bool show = at.HasValue && MapPipeline.ToScreen(at.Value, out var s)
                && MapLayout.InOpenArea(s);
            if (show)
            {
                var q = (QuaternionD)orient;
                // Bigger on screen, same place: moved toward the camera along its line of sight (its
                // model cannot be scaled; scaling its render entity crashed the renderer).
                Vector3D atW = at.Value;
                var camP = MapPipeline.CameraPosition;
                if (camP.HasValue && StarScale > 1) atW = camP.Value + (atW - camP.Value) / StarScale;
                Vector3D d = atW - mapPos;
                var local = new Vector3((float)Vector3D.Dot(d, q * Vector3D.UnitX), (float)Vector3D.Dot(d, q * Vector3D.UnitY), (float)Vector3D.Dot(d, q * Vector3D.UnitZ));
                if (float.IsNaN(local.X) || float.IsNaN(local.Y) || float.IsNaN(local.Z)) show = false;
                else
                {
                    ent.Data.Set(new RelativeTransform(local, _starRel.Orientation));
                    var got = ent.Data.GetWorldTransform().Position;
                    StarDebug = $"star want {at.Value - mapPos} got {got - mapPos} rel {local}";
                }
            }
            if (!show) ent.Data.Set(new RelativeTransform(_starRel.Position + new Vector3(0, 1e5f, 0), _starRel.Orientation));
            // The game hides map objects by its own zoom, which ours is not: on the map, it is always drawn.

            StarPlaced = show;
        }
        catch { StarPlaced = false; }
    }
    /// <summary>The game's star model is on the map this frame (the map then draws no disc of its own).</summary>
    public static bool StarPlaced;
    private static bool _starModelTried;
    /// <summary>How much bigger than its own model Delfos shows on the map.</summary>
    public static double StarScale = 1.0;   // (moved off the map plane toward the camera, the model stopped drawing)
    public static string StarDebug = "-";

    private static bool? _starShown; private static object _star;
    private static bool _starMoved; private static RelativeTransform _starRel;
    private static readonly Dictionary<object, bool> _objShown = new Dictionary<object, bool>();
    private static readonly Dictionary<object, string> _names = new Dictionary<object, string>();

    /// <summary>
    /// The game's own globe and star labels (its planet names, upper case) are drawn at their charted
    /// places, not ours: while our layout shows, their names are blanked (the game re-shows the labels
    /// whenever its zoom moves, so hiding the label alone let them flash back, stacked). Never forced
    /// visible again: the game shows them itself, only while its map is open (forcing them on when the
    /// map closed left them stacked in flight).
    /// </summary>
    static void BlankName(object mapObject, bool blank)
    {
        try
        {
            if (blank)
            {
                if (!_names.ContainsKey(mapObject) && PlanetRenderBridge.GetMember(mapObject, "_name") is string n) _names[mapObject] = n;
                PlanetRenderBridge.SetMember(mapObject, "_name", "");
                mapObject.GetType().GetMethod("SetLabelVisible")?.Invoke(mapObject, new object[] { false });
            }
            else if (_names.TryGetValue(mapObject, out var n))
            {
                PlanetRenderBridge.SetMember(mapObject, "_name", n);
                _names.Remove(mapObject);
            }
        }
        catch { }
    }

    private static void StringIdSel(ColonizationMapSessionComponent map, out Keen.VRage.Library.Utils.StringId? sel)
    {
        sel = null;
        try
        {
            object renderer = PlanetRenderBridge.GetMember(map, "SectorsRenderer");
            if (renderer != null && PlanetRenderBridge.GetMember(renderer, "SelectedSector") is Keen.VRage.Library.Utils.StringId sid) sel = sid;
        }
        catch { }
    }


    /// <summary>A filled disc with an outline, lying in the map plane.</summary>
    private static double Smooth(double a, double b, double x)
    {
        double k = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
        return k * k * (3 - 2 * k);
    }
    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
