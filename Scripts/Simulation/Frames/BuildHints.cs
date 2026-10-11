using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// BUILD HINTS (2026-10-10 - the user: how stable a design is "in many machs and orientations", "something while you're
/// building", "maybe the cop loops through?", "what about Mach", "a calculated top speed at sea level"): while you hold a
/// block to place, on the grid you are aiming at -
///  - its centre of mass (a yellow ring);
///  - a wind arrow circling the ship, and the CENTRE OF PRESSURE for that wind - a dot leaving a fading trail, green
///    where the air would turn the ship back into that wind, red where it turns it away, grey where it hardly cares;
///    the loop sweeps every direction (round the nose, up and down) and each lap is at the next Mach (0.5 0.9 1.2 3 6);
///  - its forward thrust's line (a blue diamond where it acts) against the centre of mass;
///  - one line: its nose (its most streamlined side), streamlining, the Mach wall it gives, top speed at sea level.
/// Everything from the Aerodynamics Mod's design probe (AeroApi version 4, by reflection). Display only.
/// </summary>
public static class BuildHints
{
    public static bool On = true;
    public static string Status = "-";
    /// <summary>One lap round the ship (s); Mach steps once per lap.</summary>
    public static double LapSeconds = 4.0, ElevationSeconds = 13.0;
    static readonly float[] Machs = { 0.5f, 0.9f, 1.2f, 3f, 6f };

    static MethodInfo _design, _top, _thrust, _com, _peak; static bool _resolved;
    static void Resolve()
    {
        if (_resolved) return; _resolved = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t; try { t = asm.GetType("AeroMod.AeroApi"); } catch { continue; }
            if (t == null) continue;
            _design = t.GetMethod("TryDesign"); _top = t.GetMethod("GetTopSpeed"); _thrust = t.GetMethod("GetThrustAlong");
            _com = t.GetMethod("TryGetCenterOfMass"); _peak = t.GetMethod("GetWavePeak");
            break;
        }
    }

    // the grid being built, its nose and its sea-level figures (cached a while: they change only as it is built)
    static Entity _grid; static double _gridSeen, _figuresAt;
    static Vector3 _nose; static float _noseStream = -1f, _topSpeed = float.NaN, _thrustOff = float.NaN, _peakNose = float.NaN;
    static Vector3 _thrustCentre; static bool _haveThrust;
    // the loop's trail (grid-local centres of pressure, their stability, their time)
    static readonly List<(Vector3 cop, float stab, double at)> _trail = new();
    static double _t0;

    static bool Design(Entity grid, Vector3 dir, float mach, out Vector3 cop, out float stab, out float stream)
    {
        cop = default; stab = 0f; stream = -1f;
        if (_design == null) return false;
        var args = new object[] { grid, dir, mach, null, null, null, null, null, null };
        if (!(bool)_design.Invoke(null, args)) return false;
        cop = (Vector3)args[3]; stab = Math.Min((float)args[6], (float)args[7]); stream = (float)args[8];
        return true;
    }

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

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!On) { Status = "off"; return; }
        var ch = FrameHost.PlayerCharacter(session);
        var st = ch?.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.BlockPlacerStateProviderComponent>();
        if (st == null || !st.BuildingMode) { Status = "not building"; _trail.Clear(); return; }
        Resolve();
        if (_design == null) { Status = "no aero design probe (Aerodynamics Mod API 4)"; return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var aimed = AimedGrid(session);
        if (aimed != null) { if (!ReferenceEquals(aimed, _grid)) { _grid = aimed; _figuresAt = 0; _trail.Clear(); _t0 = now; } _gridSeen = now; }
        else if (_grid != null && now - _gridSeen > 3) { _grid = null; _trail.Clear(); }   // (looked away for 3 s)
        var grid = _grid;
        if (grid == null || !(grid.Data.Scene?.IsEntityAlive(grid.Data.Entity) ?? false)) { Status = "building: aim at a ship"; return; }

        // the nose (its most streamlined side) and the sea-level figures, every 2 s
        if (now - _figuresAt > 2)
        {
            _figuresAt = now;
            float best = -1f; Vector3 nose = -Vector3.UnitZ;
            foreach (var d in new[] { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY })
                if (Design(grid, d, 0.5f, out _, out _, out float s) && s > best + 0.02f) { best = s; nose = d; }
            if (best < 0f) { Status = "building: the ship's air model is not ready yet"; _figuresAt = now - 1.5; return; }
            _nose = nose; _noseStream = best;
            _peakNose = _peak != null ? (float)_peak.Invoke(null, new object[] { best }) : float.NaN;
            _topSpeed = _top != null ? (float)_top.Invoke(null, new object[] { grid, nose }) : float.NaN;
            _haveThrust = false; _thrustOff = float.NaN;
            if (_thrust != null && _com != null)
            {
                var ta = new object[] { grid, nose, 0.5f, null };
                float thr = (float)_thrust.Invoke(null, ta);
                var ca = new object[] { grid, null };
                if (thr > 0f && (bool)_com.Invoke(null, ca))
                {
                    _thrustCentre = (Vector3)ta[3]; _haveThrust = true;
                    var off = _thrustCentre - (Vector3)ca[1]; off -= nose * Vector3.Dot(off, nose);
                    _thrustOff = off.Length();
                }
            }
        }

        // this frame's wind: round the nose, slowly up and down; the lap's Mach
        double tl = now - _t0;
        int lap = (int)(tl / LapSeconds);
        float mach = Machs[lap % Machs.Length];
        double az = 2 * Math.PI * tl / LapSeconds, el = 1.2 * Math.Sin(2 * Math.PI * tl / ElevationSeconds);
        Vector3 n = _nose, upAxis = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 side = Vector3.Normalize(Vector3.Cross(n, upAxis)), up = Vector3.Cross(side, n);
        Vector3 travel = Vector3.Normalize(n * (float)(Math.Cos(el) * Math.Cos(az)) + side * (float)(Math.Cos(el) * Math.Sin(az)) + up * (float)Math.Sin(el));
        if (Design(grid, travel, mach, out var cop, out float stab, out _)) _trail.Add((cop, stab, now));
        while (_trail.Count > 0 && now - _trail[0].at > LapSeconds * 0.9) _trail.RemoveAt(0);

        // draw: world from the grid's transform
        var wt = grid.Data.GetWorldTransform();
        Vector3D World(Vector3 local) => WorldTransform.Transform((Vector3D)local, in wt);
        Vector3 comL = default; bool haveCom = false;
        if (_com != null) { var ca = new object[] { grid, null }; if ((bool)_com.Invoke(null, ca)) { comL = (Vector3)ca[1]; haveCom = true; } }
        if (!FrameMarkers.BeginHud(session)) return;
        try
        {
            float u = Math.Max(1f, MapPipeline.ScreenSize.Y / 1080f);
            ColorSRGB StabColor(float s, float a)
            {
                const float k = 0.004f;   // (|stability| over this: clearly one way)
                float x = Math.Clamp(s / k, -1f, 1f);
                return x > 0.15f ? new ColorSRGB(0.35f, 1f, 0.45f, a) : x < -0.15f ? new ColorSRGB(1f, 0.35f, 0.3f, a) : new ColorSRGB(0.75f, 0.78f, 0.8f, a);
            }
            // the trail, fading
            for (int i = 0; i < _trail.Count; i++)
            {
                var (p, s, at) = _trail[i];
                if (!MapPipeline.ToScreen(World(p), out var sp)) continue;
                float age = (float)((now - at) / (LapSeconds * 0.9));
                MapPipeline.ScreenDisc(sp, 2.2f * u, StabColor(s, 0.85f * (1f - age)));
            }
            // the wind arrow: from upwind of the centre of mass into it
            if (haveCom && MapPipeline.ToScreen(World(comL), out var cs))
            {
                float reach = 0f; foreach (var tp in _trail) reach = Math.Max(reach, (tp.cop - comL).Length());
                float L = Math.Max(4f, reach * 1.8f + 6f);
                if (MapPipeline.ToScreen(World(comL + travel * L), out var ws))
                {
                    var d = cs - ws; float dl = d.Length();
                    if (dl > 10f * u)
                    {
                        var dh = d / dl; var tip = ws + dh * Math.Min(dl * 0.45f, 60f * u); var nr = new Vector2(-dh.Y, dh.X);
                        var wc = new ColorSRGB(0.7f, 0.85f, 1f, 0.85f);
                        MapPipeline.ScreenLine(ws, tip, wc, 1.6f * u);
                        MapPipeline.ScreenLine(tip, tip - dh * 8f * u + nr * 4.5f * u, wc, 1.6f * u);
                        MapPipeline.ScreenLine(tip, tip - dh * 8f * u - nr * 4.5f * u, wc, 1.6f * u);
                        MapPipeline.ScreenText(ws + new Vector2(6f * u, -16f * u), $"Mach {mach:0.#}", wc, 0.5f);
                    }
                }
                // the centre of mass: a yellow ring with a cross
                var yc = new ColorSRGB(1f, 0.85f, 0.2f, 1f);
                MapPipeline.ScreenCircle(cs, 7f * u, yc, 1.6f * u);
                MapPipeline.ScreenLine(cs - new Vector2(10f * u, 0), cs + new Vector2(10f * u, 0), yc, 1.2f * u);
                MapPipeline.ScreenLine(cs - new Vector2(0, 10f * u), cs + new Vector2(0, 10f * u), yc, 1.2f * u);
            }
            // the centre of pressure now
            if (_trail.Count > 0 && MapPipeline.ToScreen(World(_trail[^1].cop), out var ps))
                MapPipeline.ScreenDisc(ps, 5f * u, StabColor(_trail[^1].stab, 1f));
            // forward thrust: where it acts, along the nose
            if (_haveThrust && MapPipeline.ToScreen(World(_thrustCentre), out var ts))
            {
                var bc = new ColorSRGB(0.35f, 0.7f, 1f, 1f); float h = 6f * u;
                MapPipeline.ScreenLine(ts + new Vector2(0, -h), ts + new Vector2(h, 0), bc, 1.6f * u);
                MapPipeline.ScreenLine(ts + new Vector2(h, 0), ts + new Vector2(0, h), bc, 1.6f * u);
                MapPipeline.ScreenLine(ts + new Vector2(0, h), ts + new Vector2(-h, 0), bc, 1.6f * u);
                MapPipeline.ScreenLine(ts + new Vector2(-h, 0), ts + new Vector2(0, -h), bc, 1.6f * u);
                if (MapPipeline.ToScreen(World(_thrustCentre + _nose * 6f), out var tn)) MapPipeline.ScreenLine(ts, tn, HudPanel.Alpha(bc, 0.6f), 1.2f * u);
            }
            // the line: nose, streamlining, its Mach wall, top speed at sea level, thrust offset
            string top = float.IsNaN(_topSpeed) ? "-" : float.IsPositiveInfinity(_topSpeed) ? "over 3000 m/s" : _topSpeed < 1f ? "too little thrust" : $"{_topSpeed:N0} m/s (Mach {_topSpeed / 340.3f:0.00})";
            string line = $"Nose {AxisName(_nose)}  ·  streamlining {_noseStream:0.00}  ·  Mach 1 wall ×{_peakNose:0.0}  ·  top speed at sea level {top}"
                        + (float.IsNaN(_thrustOff) ? "" : $"  ·  thrust {(_thrustOff < 0.25f ? "through the centre of mass" : $"{_thrustOff:0.0} m off the centre of mass")}");
            var scr = MapPipeline.ScreenSize;
            var lw = MapPipeline.MeasureText(line, 0.62f);
            MapPipeline.ScreenText(new Vector2(0.5f * (scr.X - lw.X), scr.Y * 0.13f), line, new ColorSRGB(0.92f, 0.96f, 1f, 1f), 0.62f);
            const string key = "the dot: where the air pushes for that wind  ·  green: turns back into the wind  ·  red: turns away";
            var kw = MapPipeline.MeasureText(key, 0.5f);
            MapPipeline.ScreenText(new Vector2(0.5f * (scr.X - kw.X), scr.Y * 0.13f + 26f * u), key, new ColorSRGB(0.75f, 0.82f, 0.9f, 0.85f), 0.5f);
            Status = $"building: {line}";
        }
        finally { MapPipeline.UiEnd(); }
    }

    static string AxisName(Vector3 d) => d.Z < -0.5f ? "forward" : d.Z > 0.5f ? "back" : d.X > 0.5f ? "right" : d.X < -0.5f ? "left" : d.Y > 0.5f ? "up" : "down";
}
