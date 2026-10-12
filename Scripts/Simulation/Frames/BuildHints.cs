using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// BUILD HINTS (2026-10-10 - the user: how stable a design is "in many machs and orientations", "something while you're
/// building", "a calculated top speed at sea level"; 2026-10-11: "subsonic and supersonic as separate dots", "the source of
/// the downstream the player's camera", "cruise speed and max speed", "a toggleable drag visualization overlay", "3d
/// surfaces", "a top left panel", "neutral if it's fairly close to com", "controllable if the rcs can overcome the torques",
/// "it needs to handle subgrids"): while you hold a block to place, on the ship you are aiming at - the grid and every grid
/// jointed to it (the Aerodynamics Mod's design probe sums them) -
///  - THE WIND COMES FROM YOUR CAMERA, for everything: the ship flies toward you (look at its nose: forward flight);
///  - its centre of mass (a yellow ring);
///  - the centre of pressure for that wind at two speeds - subsonic (SubMach) a filled dot, supersonic (SuperMach) a ring,
///    a faint line between them, short fading trails as you look around - coloured by what the air does with the ship
///    (Classify: green stable, grey neutral, red turns it away);
///  - its thrust along that way: a blue line through the centre of thrust with an arrow, and when it misses the centre of
///    mass an amber arm to it with the distance (that much leverage);
///  - THE DRAG OVERLAY (O: off / subsonic / supersonic): the faces the air meets - turned to your camera, not shadowed -
///    as translucent quads on the hull (the engine's debug mesh, depth-tested), green little through yellow to orange most;
///  - FOUR BOXES in the game's SPD / ALT readouts' exact style, above and below each: left the top speed and L/D, right
///    the stability at the overlay's Mach (state and static margin) and thrust to weight.
/// Everything from the Aerodynamics Mod's design probe (AeroApi 4; 5 for cruise, mass, control and the overlay; by
/// reflection). Display only.
/// </summary>
public static class BuildHints
{
    public static bool On = true;
    public static string Status = "-";
    public static float SubMach = 0.5f, SuperMach = 2f, CruiseThrottle = 0.75f;
    /// <summary>The drag overlay: 0 off, 1 at SubMach, 2 at SuperMach (O cycles: the game binds J, I and O are free).</summary>
    public static int Overlay;
    public static int OverlayMaxFaces = 60000;
    /// <summary>NEUTRAL: the centre of pressure within this fraction of the ship's length of the centre of mass, along the flow.</summary>
    public static float NeutralFraction = 0.05f;
    /// <summary>How long a centre of pressure's trail lasts (s).</summary>
    public static double TrailSeconds = 1.2;
    /// <summary>DEV: show the hints for this grid without building (0: off) - the harness cannot hold a block.</summary>
    public static long DevGridId;

    static MethodInfo _design, _top, _topAt, _thrust, _com, _mass, _pressures, _control, _flight, _forces; static bool _resolved;
    static void Resolve()
    {
        if (_resolved) return; _resolved = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t; try { t = asm.GetType("AeroMod.AeroApi"); } catch { continue; }
            if (t == null) continue;
            _design = t.GetMethod("TryDesign"); _top = t.GetMethod("GetTopSpeed"); _thrust = t.GetMethod("GetThrustAlong");
            _com = t.GetMethod("TryGetCenterOfMass"); _topAt = t.GetMethod("GetTopSpeedAt"); _mass = t.GetMethod("GetMass");
            _pressures = t.GetMethod("GetDesignPressures"); _control = t.GetMethod("GetControlRatio");
            _flight = t.GetMethod("TryGetFlight"); _forces = t.GetMethod("TryGetForces");
            break;
        }
    }

    static Entity _grid; static double _gridSeen;
    // the trails (grid-local centres of pressure, their state, their time): subsonic and supersonic
    static readonly List<(Vector3 cop, int state, double at)> _trail = new(), _trailSup = new();
    // the panel's figures, for the direction they were worked out along
    static Vector3 _panelDir; static double _panelAt;
    static float _topSpeed = float.NaN, _cruise = float.NaN, _thrustN, _massKg, _thrustOff = float.NaN, _length = 10f;
    static int _stateSub = -9, _stateSup = -9; static float _ld = float.NaN;
    static Vector3 _thrustCentre, _comL, _copSubL, _copSupL, _boxMin, _boxMax; static bool _haveThrust, _haveCom, _haveBox, _haveCopSub, _haveCopSup;
    // the overlay's faces (grid-local), their smoothed pressure, for the wind they were worked out for
    static readonly List<Vector3> _ovPos = new(), _ovNrm = new(); static readonly List<float> _ovArea = new(), _ovCp = new(), _ovSmooth = new();
    static Vector3 _ovTravel; static int _ovMode; static double _ovAt; static Entity _ovGrid; static float _ovTop;
    // the side view's silhouette and up
    static readonly List<Vector3> _silPos = new(), _silNrm = new(); static readonly List<float> _silA = new(), _silCp = new();
    static double _silAt; static Entity _silGrid; static Vector3 _upL = Vector3.UnitY;

    static bool Design(Entity grid, Vector3 dir, float mach, out Vector3 cop, out float stab) => Design(grid, dir, mach, out cop, out stab, out _);
    static bool Design(Entity grid, Vector3 dir, float mach, out Vector3 cop, out float stab, out Vector3 force)
    {
        cop = default; stab = 0f; force = default;
        if (_design == null) return false;
        var args = new object[] { grid, dir, mach, null, null, null, null, null, null };
        if (!(bool)_design.Invoke(null, args)) return false;
        cop = (Vector3)args[3]; force = (Vector3)args[4]; stab = Math.Min((float)args[6], (float)args[7]);
        return true;
    }

    // ── the states: 1 stable, 0 neutral, 2 controllable, -1 unstable (-9 unknown) ──
    /// <summary>What the air does with the ship flying along travel (grid-local) with this centre of pressure: neutral when
    /// it sits within NeutralFraction of the ship's length of the centre of mass along the flow; else stable when the air
    /// turns it back (stab, the probe's); else - when control is asked for - controllable when its gyros and thrusters can
    /// hold a 5 degree upset at that speed (AeroApi.GetControlRatio), else unstable.</summary>
    static int Classify(Entity grid, Vector3 travel, Vector3 cop, float stab, float speed, bool control)
    {
        if (!_haveCom) return stab > 0f ? 1 : -1;
        float margin = Vector3.Dot(_comL - cop, travel) / Math.Max(1f, _length);
        if (MathF.Abs(margin) < NeutralFraction) return 0;
        if (stab > 0f) return 1;
        if (control && _control != null)
        {
            float r = (float)_control.Invoke(null, new object[] { grid, travel, speed });
            if (r >= 1f) return 2;
        }
        return -1;
    }
    static ColorSRGB StateColor(int st, float a) => st switch
    {
        1 => new ColorSRGB(0.35f, 1f, 0.45f, a),
        0 => new ColorSRGB(0.78f, 0.8f, 0.84f, a),
        2 => new ColorSRGB(1f, 0.82f, 0.3f, a),
        -1 => new ColorSRGB(1f, 0.35f, 0.3f, a),
        _ => new ColorSRGB(0.6f, 0.62f, 0.66f, a),
    };
    static string StateWord(int st) => st switch { 1 => "stable", 0 => "neutral", 2 => "controllable", -1 => "unstable", _ => "-" };

    static Entity AimedGrid(Keen.VRage.Core.Game.Systems.Session session)
    {
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer.BlockPlacerEntityComponent>())
        {
            var bp = e.TryGet<Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer.BlockPlacerEntityComponent>();
            if (bp == null || !bp.IsActive()) continue;
            var tgt = bp.AlignedGrid?.Target;
            if (tgt != null && tgt.RootEntity != null) return tgt.RootEntity;
        }
        return null;
    }

    static void ClearAll() { _trail.Clear(); _trailSup.Clear(); _ovPos.Clear(); _ovSmooth.Clear(); _ovGrid = null; _panelAt = 0; }

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!On) { Status = "off"; return; }
        var ch = FrameHost.PlayerCharacter(session);
        var st = ch?.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.BlockPlacerStateProviderComponent>();
        bool dev = DevGridId != 0;
        if (!dev && (st == null || !st.BuildingMode)) { Status = "not building"; ClearAll(); DrawFlight(session); return; }
        Resolve();
        if (_design == null) { Status = "no aero design probe (Aerodynamics Mod API 4)"; return; }
        if (MapInput.KeyPressed(KeyboardInputs.O) | MapInput.DevKeys.Remove("hinto")) Overlay = (Overlay + 1) % 3;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var aimed = dev ? GridMembers.Get(DevGridId)?.Entity : AimedGrid(session);
        if (aimed != null) { if (!ReferenceEquals(aimed, _grid)) { _grid = aimed; ClearAll(); } _gridSeen = now; }
        else if (_grid != null && now - _gridSeen > 3) { _grid = null; ClearAll(); }   // (looked away for 3 s)
        var grid = _grid;
        if (grid == null || !(grid.Data.Scene?.IsEntityAlive(grid.Data.Entity) ?? false)) { Status = "building: aim at a ship"; return; }

        // (the HUD batch first: it is what sets the camera up)
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            if (MapPipeline.CameraPosition == null) { Status = "building: no camera"; return; }
            var wt = grid.Data.GetWorldTransform();
            Vector3D World(Vector3 local) => WorldTransform.Transform((Vector3D)local, in wt);
            // the wind comes from the camera: the ship moves through the air toward it (look at the nose: forward flight)
            Vector3 lookW = Vector3.Transform(Vector3.Forward, MapPipeline.CameraOrientation);
            Vector3 travel = -Vector3.Normalize(WorldTransform.TransformDirectionInv(lookW, wt));

            // the ship's centre of mass and length
            _haveCom = false;
            if (_com != null) { var ca = new object[] { grid, null }; if ((bool)_com.Invoke(null, ca)) { _comL = (Vector3)ca[1]; _haveCom = true; } }
            if (grid.Data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var bb)) { _boxMin = bb.BoundingBox.Min; _boxMax = bb.BoundingBox.Max; var ext = _boxMax - _boxMin; _length = Math.Max(ext.X, Math.Max(ext.Y, ext.Z)); _haveBox = true; }

            // the centres of pressure for the camera's wind, both speeds
            if (Design(grid, travel, SubMach, out var cop, out float stab)) _trail.Add((cop, Classify(grid, travel, cop, stab, 0f, false), now));
            if (Design(grid, travel, SuperMach, out var copS, out float stabS)) _trailSup.Add((copS, Classify(grid, travel, copS, stabS, 0f, false), now));
            while (_trail.Count > 0 && now - _trail[0].at > TrailSeconds) _trail.RemoveAt(0);
            while (_trailSup.Count > 0 && now - _trailSup[0].at > TrailSeconds) _trailSup.RemoveAt(0);

            // the panel's figures along the same way: again when it turns 3 degrees, or every second
            if (now - _panelAt > 1.0 || Vector3.Dot(travel, _panelDir) < 0.9986f) Figures(grid, travel, now);
            // the side view's up: the camera's, square to the flight
            var upC = WorldTransform.TransformDirectionInv(Vector3.Transform(Vector3.Up, MapPipeline.CameraOrientation), wt);
            upC -= travel * Vector3.Dot(upC, travel);
            if (upC.LengthSquared() > 1e-6f) _upL = Vector3.Normalize(upC);


            // the overlay's faces: again when the wind turns 1.5 degrees or the speed changes, or every half second
            if (Overlay != 0 && _pressures != null && (now - _ovAt > 0.5 || _ovMode != Overlay || !ReferenceEquals(_ovGrid, grid) || Vector3.Dot(travel, _ovTravel) < 0.99966f))
            {
                _ovAt = now; _ovMode = Overlay; _ovGrid = grid; _ovTravel = travel;
                _pressures.Invoke(null, new object[] { grid, travel, Overlay == 2 ? SuperMach : SubMach, _ovPos, _ovNrm, _ovArea, _ovCp, OverlayMaxFaces });
                Smooth(travel);
            }
            if (Overlay != 0 && _ovSmooth.Count > 0 && ReferenceEquals(_ovGrid, grid)) DrawOverlay3D(session, World);

            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            // the trails, fading: subsonic dots, supersonic rings
            for (int i = 0; i < _trail.Count; i++)
            {
                var (p, s, at) = _trail[i];
                if (!MapPipeline.ToScreen(World(p), out var sp)) continue;
                MapPipeline.ScreenDisc(sp, 2.2f * u, StateColor(s, 0.85f * (1f - (float)((now - at) / TrailSeconds))));
            }
            for (int i = 0; i < _trailSup.Count; i++)
            {
                var (p, s, at) = _trailSup[i];
                if (!MapPipeline.ToScreen(World(p), out var sp)) continue;
                MapPipeline.ScreenCircle(sp, 2.6f * u, StateColor(s, 0.85f * (1f - (float)((now - at) / TrailSeconds))), 1f * u);
            }
            // the centre of mass: a yellow ring with a cross
            if (_haveCom && MapPipeline.ToScreen(World(_comL), out var cs))
            {
                var yc = new ColorSRGB(1f, 0.85f, 0.2f, 1f);
                MapPipeline.ScreenCircle(cs, 7f * u, yc, 1.6f * u);
                MapPipeline.ScreenLine(cs - new Vector2(10f * u, 0), cs + new Vector2(10f * u, 0), yc, 1.2f * u);
                MapPipeline.ScreenLine(cs - new Vector2(0, 10f * u), cs + new Vector2(0, 10f * u), yc, 1.2f * u);
            }
            // the centres of pressure now: subsonic (a dot) and supersonic (a ring), a faint line between
            Vector2 psSub = default, psSup = default;
            bool haveSub = _trail.Count > 0 && now - _trail[^1].at < 0.25 && MapPipeline.ToScreen(World(_trail[^1].cop), out psSub);
            bool haveSup = _trailSup.Count > 0 && now - _trailSup[^1].at < 0.25 && MapPipeline.ToScreen(World(_trailSup[^1].cop), out psSup);
            if (haveSub && haveSup) MapPipeline.ScreenLine(psSub, psSup, new ColorSRGB(0.85f, 0.88f, 0.92f, 0.45f), 1.2f * u);
            if (haveSub) MapPipeline.ScreenDisc(psSub, 5f * u, StateColor(_trail[^1].state, 1f));
            if (haveSup) MapPipeline.ScreenCircle(psSup, 6f * u, StateColor(_trailSup[^1].state, 1f), 2f * u);
            DrawThrust(World, u);
            DrawPanel(grid, World, u);
        }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>The panel's figures for flying along dir (grid-local): sea-level top speed and cruise, thrust, mass, the
    /// state either side of Mach 1 (with control), the thrust's offset from the centre of mass.</summary>
    static void Figures(Entity grid, Vector3 dir, double now)
    {
        _panelAt = now; _panelDir = dir;
        _topSpeed = _top != null ? (float)_top.Invoke(null, new object[] { grid, dir }) : float.NaN;
        _cruise = _topAt != null ? (float)_topAt.Invoke(null, new object[] { grid, dir, CruiseThrottle }) : float.NaN;
        _massKg = _mass != null ? (float)_mass.Invoke(null, new object[] { grid }) : 0f;
        _haveCopSub = Design(grid, dir, SubMach, out var c0, out float s0, out var f0); _copSubL = c0;
        // L/D: the force across the flight over the force against it (the wind from the camera: as you look, so the angle)
        { float drag = -Vector3.Dot(f0, dir); var lift = f0 + dir * drag; _ld = _haveCopSub && drag > 1f ? lift.Length() / drag : float.NaN; }
        _haveCopSup = Design(grid, dir, SuperMach, out var c1, out float s1); _copSupL = c1;
        _stateSub = _haveCopSub ? Classify(grid, dir, c0, s0, SubMach * 340.3f, true) : -9;
        _stateSup = _haveCopSup ? Classify(grid, dir, c1, s1, SuperMach * 340.3f, true) : -9;
        _haveThrust = false; _thrustOff = float.NaN; _thrustN = 0f;
        if (_thrust != null)
        {
            var ta = new object[] { grid, dir, SubMach, null };
            _thrustN = (float)_thrust.Invoke(null, ta);
            if (_thrustN > 0f && _haveCom)
            {
                _thrustCentre = (Vector3)ta[3]; _haveThrust = true;
                var off = _thrustCentre - _comL; off -= dir * Vector3.Dot(off, dir);
                _thrustOff = off.Length();
            }
        }
    }

    /// <summary>THE THRUST along the flight: its line through the centre of thrust with an arrow the way it pushes; the arm
    /// from the centre of mass to that line, amber with the distance, when it misses (that much leverage turns the ship).</summary>
    static void DrawThrust(Func<Vector3, Vector3D> World, float u)
    {
        if (!_haveThrust || !_haveCom) return;
        var dir = _panelDir;
        float reach = Math.Max(6f, _length * 0.6f);
        var foot = _thrustCentre + dir * Vector3.Dot(_comL - _thrustCentre, dir);   // (the line's nearest point to the centre of mass)
        var blue = new ColorSRGB(0.35f, 0.7f, 1f, 0.95f);
        if (MapPipeline.ToScreen(World(foot - dir * reach), out var l0) && MapPipeline.ToScreen(World(foot + dir * reach), out var l1))
        {
            MapPipeline.ScreenLine(l0, l1, HudPanel.Alpha(blue, 0.55f), 1.4f * u);
            var d = l1 - l0; float dl = d.Length();
            if (dl > 12f * u)
            {
                var dh = d / dl; var nr = new Vector2(-dh.Y, dh.X);
                MapPipeline.ScreenLine(l1, l1 - dh * 10f * u + nr * 5f * u, blue, 1.8f * u);
                MapPipeline.ScreenLine(l1, l1 - dh * 10f * u - nr * 5f * u, blue, 1.8f * u);
            }
        }
        if (MapPipeline.ToScreen(World(_thrustCentre), out var ts))
        {
            float h = 5f * u;
            MapPipeline.ScreenLine(ts + new Vector2(0, -h), ts + new Vector2(h, 0), blue, 1.6f * u);
            MapPipeline.ScreenLine(ts + new Vector2(h, 0), ts + new Vector2(0, h), blue, 1.6f * u);
            MapPipeline.ScreenLine(ts + new Vector2(0, h), ts + new Vector2(-h, 0), blue, 1.6f * u);
            MapPipeline.ScreenLine(ts + new Vector2(-h, 0), ts + new Vector2(0, -h), blue, 1.6f * u);
        }
        if (_thrustOff >= 0.25f && MapPipeline.ToScreen(World(_comL), out var a0) && MapPipeline.ToScreen(World(foot), out var a1))
        {
            var amber = new ColorSRGB(1f, 0.7f, 0.2f, 1f);
            MapPipeline.ScreenLine(a0, a1, amber, 1.6f * u);
        }
    }

    /// <summary>
    /// THE FOUR BOXES (2026-10-11, the user: "not the same color, not the same size, chamfered edges? - top speed on the left,
    /// L/D as well on left, then stability right, with one more on right - icons would be dope"): the game's own SPD and ALT
    /// readouts copied exactly (Game2.Client HUDAltimeter.axaml: a BeveledBorder, Gray-200-A70 fill = (0, 12, 16) at 70%,
    /// a 1 px HighContrast = white frame, 5 px bevels on its outer corners only, padding 4 / 1; the label with a 20 px icon,
    /// the value under it, both 18 px with a black shadow 2 px down and right), two above and below each of them: left (by
    /// SPD) the top speed over it and L/D under it; right (by ALT) the stability at the overlay's Mach over it, thrust to
    /// weight under it. Our own icons (MapIcons: speed, wing, balance, thrust). Sizes in the HUD's 1080p units x u.
    /// </summary>
    /// <summary>The boxes (DrawPanel's while building; DrawFlight's right pair in the air).</summary>
    static void DrawBoxes(float u, bool left, string top, ColorSRGB topCol, string tw, string stabLabel, int st, string margin, string ld)
    {
        var scr = MapPipeline.ScreenSize;
        float cx = scr.X * 0.5f, cy = scr.Y * 0.5f, H = scr.Y;
        // (the SPD box: 0.327 to 0.424 screen-heights left of centre, +-0.026 about the middle; ALT its mirror)
        float inner = 0.327f * H, outer = 0.424f * H, hh = 0.026f * H, gap = 0.008f * H;
        // (measured on the game's own at 4K: SPD spans 0.327-0.424 left of centre, ALT 0.3278-0.4194 right of it - narrower)
        float innerR = 0.3278f * H, outerR = 0.4194f * H;
        var fill = new ColorSRGB(0f, 12f / 255f, 16f / 255f, 0.70f);
        var white = new ColorSRGB(1f, 1f, 1f, 1f); var shadow = new ColorSRGB(0f, 0f, 0f, 0.85f);
        float bev = 5f * u, padX = 4f * u, ts = 0.42f * u;
        void Text(Vector2 at, string t, ColorSRGB c, float scale)
        {
            MapPipeline.ScreenText(at + new Vector2(2f * u, 2f * u), t, shadow, scale);
            MapPipeline.ScreenText(at, t, c, scale);
        }
        void Box(bool right, float top, string icon, string label, string value, ColorSRGB valueCol, string corner = null, ColorSRGB? cornerCol = null)
        {
            float x0 = right ? cx + innerR : cx - outer, x1 = right ? cx + outerR : cx - inner, y0 = top, y1 = top + 2f * hh;
            // the outline, bevelled on the outer side's two corners
            var pts = right
                ? new List<Vector2> { new(x0, y0), new(x1 - bev, y0), new(x1, y0 + bev), new(x1, y1 - bev), new(x1 - bev, y1), new(x0, y1) }
                : new List<Vector2> { new(x0 + bev, y0), new(x1, y0), new(x1, y1), new(x0 + bev, y1), new(x0, y1 - bev), new(x0, y0 + bev) };
            MapPipeline.ScreenFill(pts, fill);
            for (int k = 0; k < pts.Count; k++) MapPipeline.ScreenLine(pts[k], pts[(k + 1) % pts.Count], white, 1f * u);
            float hl = MapPipeline.MeasureText("A", ts).Y;
            // (the game's own: the label row 2.5 px (1080p) higher than centred; SPD's text right-aligned 7 px in from its
            //  inner edge, ALT's left-aligned 4.5 px in - measured at 4K - so ours by SPD right-align, ours by ALT left-align)
            float y = y0 + (2f * hh - 2f * hl) * 0.5f - 2.5f * u;
            float lw = MapPipeline.MeasureText(label, ts).X;
            float isz = 20f * u * 0.5f;
            if (right)
            {
                float lx = x0 + 4.5f * u;
                Text(new Vector2(lx, y), label, white, ts);
                MapPipeline.ScreenIcon(icon, new Vector2(lx + lw + 6f * u + isz, y + hl * 0.5f), isz, white);
                // (the stability's margin right-aligned on the value row, at the value's own size and line; the word and it
                //  shrunk together - by one factor - only when the pair is wider than the row: shrunk apart, they did not match)
                float rightEdge = x1 - padX - bev * 0.6f, room = rightEdge - lx;
                float vs = ts, vw = MapPipeline.MeasureText(value, ts).X;
                float cw = string.IsNullOrEmpty(corner) ? 0f : MapPipeline.MeasureText(corner, ts).X;
                float need = vw + (cw > 0f ? 6f * u + cw : 0f);
                if (need > room && need > 0f) vs = ts * room / need;
                if (cw > 0f)
                {
                    float cwS = MapPipeline.MeasureText(corner, vs).X;
                    Text(new Vector2(rightEdge - cwS, y + hl), corner, cornerCol ?? white, vs);
                }
                Text(new Vector2(lx, y + hl), value, valueCol, vs);
            }
            else
            {
                // (as SPD: the icon left of the label, both against the right; the value under them against the right)
                float rx = x1 - 7f * u;
                Text(new Vector2(rx - lw, y), label, white, ts);
                MapPipeline.ScreenIcon(icon, new Vector2(rx - lw - 6f * u - isz, y + hl * 0.5f), isz, white);
                float vs = ts, room = rx - (x0 + bev + padX), vw = MapPipeline.MeasureText(value, vs).X;
                if (vw > room && vw > 0f) { vs *= room / vw; vw = room; }
                Text(new Vector2(rx - vw, y + hl), value, valueCol, vs);
            }
        }
        float above = cy - hh - gap - 2f * hh, below = cy + hh + gap;
        // (ALT's middle sits 0.0028 screen-heights over the centre - the gaps came out 15 and 25 px)
        float cyR = cy - 0.0028f * H, aboveR = cyR - hh - gap - 2f * hh, belowR = cyR + hh + gap;
        // left, by SPD: the top speed, thrust to weight
        if (left)
        {
            Box(false, above, "speed", "TOP", top, topCol);
            Box(false, below, "thrust", "T/W", tw, white);
        }
        // right, by ALT: the stability, L/D
        Box(true, aboveR, "balance", stabLabel, StateWord(st), StateColor(st, 1f), margin, StateColor(st, 1f));
        Box(true, belowR, "wing", "L/D", ld, white);
    }

    /// <summary>
    /// THE BUILD BOXES (2026-10-12, the user: "swap L/D and T/W"): by SPD the top speed over it and thrust to weight
    /// under it; by ALT the stability at the overlay's Mach over it and L/D under it - for the wind from the camera.
    /// </summary>
    static void DrawPanel(Entity grid, Func<Vector3, Vector3D> World, float u)
    {
        string top = _thrustN <= 0f ? "no thrust" : float.IsNaN(_topSpeed) ? "-" : float.IsPositiveInfinity(_topSpeed) ? ">3000m/s" : $"{_topSpeed:N0}m/s";
        string tw = _massKg > 0f && _thrustN > 0f ? $"{_thrustN / (_massKg * 9.81f):0.00}" : "-";
        bool sup = Overlay == 2;
        int st = sup ? _stateSup : _stateSub;
        bool have = sup ? _haveCopSup : _haveCopSub; var cop = sup ? _copSupL : _copSubL;
        string margin = !have || !_haveCom ? null : $"{100f * Vector3.Dot(_comL - cop, _panelDir) / Math.Max(1f, _length):+0;-0}%";
        DrawBoxes(u, true, top, _thrustN <= 0f ? HudPanel.Warn : new ColorSRGB(1f, 1f, 1f, 1f), tw, $"M{(sup ? SuperMach : SubMach):0.#}", st, margin, float.IsNaN(_ld) ? "-" : $"{_ld:0.0}");
        Status = $"building: top {top}, L/D {_ld:0.0}, margin {margin}, below {StateWord(_stateSub)}, above {StateWord(_stateSup)}, overlay {Overlay} ({_ovSmooth.Count} faces)";
    }

    /// <summary>
    /// THE FLIGHT BOXES (2026-10-12, the user: "show the right side ones during atmospheric flight with dynamic values"):
    /// seated, flying through air (over FlightMinSpeed, air over FlightMinDensity), the pair by ALT live - the stability at
    /// the Mach you fly and the way you are going (the design probe along your travel, its state with your control at
    /// your airspeed; labelled with your Mach), and L/D now (the aero model's lift over its drag). Every FlightEvery s.
    /// </summary>
    public static float FlightMinSpeed = 20f, FlightMinDensity = 0.01f; public static double FlightEvery = 0.25;
    static double _flAt; static Entity _flGrid; static int _flState = -9; static string _flMargin, _flLd = "-", _flLabel = "M0";
    static void DrawFlight(Keen.VRage.Core.Game.Systems.Session session)
    {
        var grid = FrameHost.SeatGrid;
        if (!FrameHost.Seated || grid == null || MapView.Visible) return;
        Resolve();
        if (_flight == null || _design == null) return;
        var fa = new object[] { grid, null, null, null, null, null };
        if (!(bool)_flight.Invoke(null, fa)) return;
        var travel = (Vector3)fa[1]; float airspeed = (float)fa[2], mach = (float)fa[3], density = (float)fa[4];
        if (airspeed < FlightMinSpeed || density < FlightMinDensity || travel.LengthSquared() < 0.5f) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _flAt > FlightEvery || !ReferenceEquals(grid, _flGrid))
        {
            _flAt = now; _flGrid = grid;
            // L/D now
            _flLd = "-";
            if (_forces != null)
            {
                var fo = new object[] { grid, null, null, null, null, null };
                if ((bool)_forces.Invoke(null, fo)) { float drag = (float)fo[3], lift = (float)fo[4]; if (drag > 1f) _flLd = $"{lift / drag:0.0}"; }
            }
            // the stability, along the travel at this Mach (Classify reads the centre of mass and length: this grid's)
            _haveCom = false;
            if (_com != null) { var ca = new object[] { grid, null }; if ((bool)_com.Invoke(null, ca)) { _comL = (Vector3)ca[1]; _haveCom = true; } }
            if (grid.Data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var bb)) { var ext = bb.BoundingBox.Max - bb.BoundingBox.Min; _length = Math.Max(ext.X, Math.Max(ext.Y, ext.Z)); }
            var dir = Vector3.Normalize(travel);
            if (Design(grid, dir, Math.Max(0.05f, mach), out var cop, out float stab))
            {
                _flState = Classify(grid, dir, cop, stab, airspeed, true);
                _flMargin = _haveCom ? $"{100f * Vector3.Dot(_comL - cop, dir) / Math.Max(1f, _length):+0;-0}%" : null;
            }
            else { _flState = -9; _flMargin = null; }
            _flLabel = $"M{mach:0.00}";
        }
        if (!FrameMarkers.BeginHud(session)) return;
        try { DrawBoxes(Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f), false, null, default, null, _flLabel, _flState, _flMargin, _flLd); }
        finally { MapPipeline.UiEnd(); }
    }

    /// <summary>A clean scale step: 1, 2 or 5 x 10^n at or over x.</summary>
    static double Nice(double x)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(x))), f = x / mag;
        return mag * (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
    }

    // ── THE OVERLAY, 3D ──
    /// <summary>
    /// THE OVERLAY AS QUADS (2026-10-11; the user, after the spheres - "very laggy" - and an opaque halftone - "bugged":
    /// "take the old quad overlay"): each face the air meets (turned to the camera, not shadowed) a translucent quad on its
    /// own cell face, lifted 3 cm, coloured by the drag there (smoothed over SmoothMetres, frontal-area weighted) - green
    /// little, yellow, orange most - more opaque the more it drags. Each set lives OverlayLife, a new one every OverlayPush
    /// (from this pass the engine's debug mesh keeps only what is given a life).
    /// </summary>
    public static float SmoothMetres = 1.0f;
    static readonly Dictionary<long, (float a, float cpa)> _cells = new();
    static long CellKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    static void Smooth(Vector3 travel)
    {
        _ovSmooth.Clear(); _cells.Clear(); _ovTop = 0f;
        float k = 1f / Math.Max(0.1f, SmoothMetres);
        for (int i = 0; i < _ovPos.Count; i++)
        {
            float c = Vector3.Dot(_ovNrm[i], travel); if (c <= 0.02f) continue;
            var p = _ovPos[i] * k; long key = CellKey((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
            _cells.TryGetValue(key, out var e); float a = _ovArea[i] * c;
            _cells[key] = (e.a + a, e.cpa + _ovCp[i] * a);
        }
        for (int i = 0; i < _ovPos.Count; i++)
        {
            float c = Vector3.Dot(_ovNrm[i], travel);
            if (c <= 0.02f) { _ovSmooth.Add(float.NaN); continue; }   // (turned away: hidden from the camera)
            var p = _ovPos[i] * k; int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
            float sa = 0f, sc = 0f;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue(CellKey(x + dx, y + dy, z + dz), out var e)) { sa += e.a; sc += e.cpa; }
            float v = sa > 0f ? sc / sa : 0f;
            _ovSmooth.Add(v); _ovTop = Math.Max(_ovTop, v);
        }
    }

    public static int OverlayMaxDrawn = 25000;
    static void DrawOverlay3D(Keen.VRage.Core.Game.Systems.Session session, Func<Vector3, Vector3D> World)
    {
        if (_ovTop <= 1e-4f) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _ovPushed < OverlayPush) return;
        // (on a fixed cadence, each set living just past it: the old and the new overlap a frame at most - by a 50 ms overlap
        //  the translucent quads doubled up twice a second: the flicker)
        _ovPushed = now - _ovPushed < 2 * OverlayPush ? _ovPushed + OverlayPush : now;
        var life = TimeSpan.FromSeconds(OverlayLife);
        Keen.VRage.Core.Render.MeshBuilder mb;
        try { mb = session.Get<Keen.VRage.Core.Game.RuntimeSystems.DebugDraw.IDebugDrawProvider>()?.GlobalBuilder; } catch { mb = null; }
        if (mb == null) { Status = "building: no debug mesh for the overlay"; return; }
        int n = _ovSmooth.Count, stride = Math.Max(1, n / Math.Max(1, OverlayMaxDrawn));
        for (int i = 0; i < n; i += stride)
        {
            float v = _ovSmooth[i]; if (float.IsNaN(v)) continue;
            float x = Math.Clamp(v / _ovTop, 0f, 1f);
            var col = x < 0.5f ? Lerp(new ColorSRGB(0.3f, 0.95f, 0.45f, 0.22f), new ColorSRGB(1f, 0.9f, 0.25f, 0.4f), x * 2f)
                               : Lerp(new ColorSRGB(1f, 0.9f, 0.25f, 0.4f), new ColorSRGB(1f, 0.5f, 0.1f, 0.6f), (x - 0.5f) * 2f);
            var nrm = _ovNrm[i];
            Vector3 t1 = MathF.Abs(nrm.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 t2 = Vector3.Normalize(Vector3.Cross(nrm, t1)); t1 = Vector3.Cross(t2, nrm);
            float h = 0.5f * MathF.Sqrt(_ovArea[i]) * (stride > 1 ? MathF.Sqrt(stride) : 1f);
            var c = _ovPos[i] + nrm * 0.03f;
            var p0 = World(c - t1 * h - t2 * h); var p1 = World(c + t1 * h - t2 * h); var p2 = World(c + t1 * h + t2 * h); var p3 = World(c - t1 * h + t2 * h);
            mb.AddTriangle(p0, p1, p2, col, true, life);
            mb.AddTriangle(p0, p2, p3, col, true, life);
        }
    }

    public static double OverlayPush = 0.5, OverlayLife = 0.512;
    static double _ovPushed;

    static ColorSRGB Lerp(ColorSRGB a, ColorSRGB b, float t)
        => new ColorSRGB(a.R / 255f + (b.R - a.R) / 255f * t, a.G / 255f + (b.G - a.G) / 255f * t, a.B / 255f + (b.B - a.B) / 255f * t, a.A / 255f + (b.A - a.A) / 255f * t);
}
