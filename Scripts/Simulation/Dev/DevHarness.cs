using System.IO;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV ONLY. Lets an external tool drive the game for visual testing: teleport the player, aim
/// the view, switch proxy modes, while screenshots are taken from outside the game.
///
/// Protocol: write commands (one per line) to  %TEMP%\OrbitalMod\cmd.txt . The client polls it
/// every <see cref="PollSeconds"/>, executes and deletes it, and rewrites %TEMP%\OrbitalMod\status.txt
/// with the camera and planet state. Lines starting with # are ignored.
///
///   planets                          list planets in status (index, name, center, radius)
///   view &lt;planet&gt; &lt;distKm&gt; [bearing]    stand distKm from the planet CENTER, facing it.
///                                    bearing: keep (current direction, default) | sun | +x -x +y -y +z -z
///   tp &lt;x&gt; &lt;y&gt; &lt;z&gt;                     teleport to world position (metres), keep orientation
///   look &lt;x&gt; &lt;y&gt; &lt;z&gt;                   stay, turn to face a world position
///   lookat &lt;planet&gt;                  stay, turn to face a planet
///   mode Frame|AlwaysProxy|AlwaysReal|Alternate
///   hide on|off                       OrbitalConfig.HideRealPlanets
///   front on|off                      OrbitalConfig.DebugProxyInFront
///   orbit on|off                      OrbitalConfig.ShowOrbit
///
/// &lt;planet&gt; is an index from `planets`, or a name prefix (Verdure, Kemik, ...).
/// Teleports go through Keen's own EntityAdmin.TeleportPlayer (admin; moves the ship if piloting)
/// with motion cleared. Single player / listen host only: planet data comes from server beacons.
/// Off unless <see cref="OrbitalConfig.DevHarness"/> is true. File access is allowed today only
/// because the mod whitelist admits all of CoreLib (System.IO included).
/// </summary>
public static partial class DevHarness
{
    /// <summary>The only save the harness ever writes (a copy of the test world).</summary>
    public const string TestWorldContainer = "Orbital Test World";

    private static async void SaveAndLog(Keen.Game2.Simulation.Replication.IGameServer server, string target)
    {
        try
        {
            var r = await server.TrySaveGame(target);
            Log.Default?.Info($"[ORBIT-DEV] save -> '{target}': {r}");
            LastSave = $"{DateTime.Now:HH:mm:ss} {r}";
        }
        catch (Exception e) { Log.Default?.Info($"[ORBIT-DEV] save failed: {e.Message}"); LastSave = "failed: " + e.Message; }
    }
    public static string LastSave = "none";

    public const double PollSeconds = 0.5;

    private static long _lastPoll;
    private static string _dir;
    private static long _cmdAt, _statusAt;
    /// <summary>The status at every poll, used or not (statusfast on: load-test scripts that only read it).</summary>
    public static bool StatusAlways;
    private static readonly List<string> _log = new List<string>();

    private static float _clientGravityMultiplier = float.NaN;
    public static string LastShot = "";

    // the client frame clock (Poll runs once a client frame): every frame's length, for the 'fps' command
    private static long _lastFrameTick;
    private static readonly double[] _frameMs = new double[600];
    private static int _frameIdx, _frameCount;

    public static void Poll(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera)
    {
        if (!OrbitalConfig.DevHarness) return;
        {
            long now0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastFrameTick != 0)
            {
                _frameMs[_frameIdx] = (now0 - _lastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _frameIdx = (_frameIdx + 1) % _frameMs.Length; if (_frameCount < _frameMs.Length) _frameCount++;
            }
            _lastFrameTick = now0;
        }
        _statusSession = session; _lastCamera = camera;
        try
        {
            SpecCam.Tick(session, name => { var pl = FindPlanet(name); return pl != null ? PlanetWorldPos(pl) : (Vector3D?)null; },
                         FrameHost.PlayerPosition);
        }
        catch (Exception e) { SpecCam.Status = "error " + e.Message; }
        GridGen.ClientTick(); // (DEV: generated grids - hinge heads spawned and flaps grown, here as spawning works here)
        try { _clientGravityMultiplier = session.Get<Keen.VRage.Physics.IPhysics>().GravityMultiplier; } catch { }
        // (a command file waiting on the server thread: finished the frame its answer is in - ServerAsks; the status then, as
        //  the scripts read it a second after the file is taken)
        if (_batch.Busy || _panel.Busy)
        {
            try
            {
                if (_panel.Busy) _panel.Pump(l => RunLine(session, camera, l), Finished, ServerWaitSeconds);
                if (_batch.Busy)
                {
                    _batch.Pump(l => RunLine(session, camera, l), Finished, ServerWaitSeconds);
                    if (!_batch.Busy) { _statusAt = System.Diagnostics.Stopwatch.GetTimestamp(); WriteStatus(camera); }
                }
            }
            catch (Exception e) { Log.Default?.Warning($"[ORBIT-DEV] poll failed: {e.Message}"); }
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastPoll < PollSeconds * System.Diagnostics.Stopwatch.Frequency) return;
        _lastPoll = now;

        try
        {
            _dir ??= Path.Combine(Path.GetTempPath(), "OrbitalMod");
            Directory.CreateDirectory(_dir);
            string cmdPath = Path.Combine(_dir, "cmd.txt");
            // (the next file only once the last one's lines are all answered: they run in order)
            if (!_batch.Busy && File.Exists(cmdPath))
            {
                string[] lines = File.ReadAllLines(cmdPath);
                _cmdAt = System.Diagnostics.Stopwatch.GetTimestamp();
                File.Delete(cmdPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    _batch.Add(line);
                }
                _batch.Pump(l => RunLine(session, camera, l), Finished, ServerWaitSeconds);
            }
            // (the status - a page of text - twice a second only while the harness is being used: a command in the last 30 s;
            //  else every 5 s. It was 160 KB/s of garbage in everyone's game - a profile, 2026-10-14)
            long nowS = System.Diagnostics.Stopwatch.GetTimestamp(); double fq = System.Diagnostics.Stopwatch.Frequency;
            if (StatusAlways || _cmdAt != 0 && nowS - _cmdAt < 30 * fq || nowS - _statusAt > 5 * fq) { _statusAt = nowS; WriteStatus(camera); }
        }
        catch (Exception e)
        {
            Log.Default?.Warning($"[ORBIT-DEV] poll failed: {e.Message}");
        }
    }

    private static WorldTransform _lastCamera;

    /// <summary>The command file's lines, in order; one waiting on the server thread holds the rest (ServerAsks).</summary>
    private static readonly ServerAsks.Batch _batch = new ServerAsks.Batch();
    /// <summary>A debug panel command (Run) waiting on the server thread.</summary>
    private static readonly ServerAsks.Batch _panel = new ServerAsks.Batch();
    /// <summary>How long a command waits for the server thread's answer before "timed out" (the old in-frame wait's 3 s).</summary>
    public static double ServerWaitSeconds = 3;

    private static string RunLine(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line)
    {
        try { return Execute(session, camera, line); }
        catch (Exception e)
        {
            string st = e.ToString().Replace('\n', ' ');
            Log.Default?.Info("[ORBIT-DEV] stack: " + st.Substring(0, Math.Min(1500, st.Length)));
            return "ERROR " + e.Message;
        }
    }

    private static void Finished(string line, string result)
    {
        Note($"> {line}  =>  {result}");
        Log.Default?.Info($"[ORBIT-DEV] {line} => {result}");
    }

    /// <summary>
    /// Toggle the camera in the phase where the game's input does it (before the camera update). Called
    /// from inside our own job instead, the new camera started from an invalid orientation and the
    /// renderer crashed on a NaN distance.
    /// </summary>
    private static async void ToggleCameraLater(Entity ch, object cs)
    {
        try
        {
            await ch.Scene.MoveToDCS<Keen.Game2.Client.ControlledEntityInputMovementUpdate>();
            PlanetRenderBridge.CallPrivate(cs, "ToggleCameraView");
        }
        catch (Exception e) { Log.Default?.Warning("[ORBIT-DEV] camera toggle failed: " + e.Message); }
    }
    static bool IsFiniteV(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

    /// <summary>Run one harness command now (the debug panel's entries), as the file-driven poll would.</summary>
    public static string Run(Keen.VRage.Core.Game.Systems.Session session, string line)
    {
        string r;
        try { r = Execute(session, _lastCamera, line); }
        catch (Exception e) { return "ERROR " + e.Message; }
        if (r != ServerAsks.Pending) return r;
        // (waiting on the server thread: finished by the poll - its answer in the log and the status notes)
        return _panel.Hold(line) ? "asked the server thread: the answer goes to the harness log"
                                 : "not finished: the last panel command still waits on the server thread";
    }

    private static string Execute(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line)
    {
        string[] a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string verb = a[0].ToLowerInvariant();
        switch (verb)
        {
            case "planets":
                return $"{Planets().Count} planets (see status)";

            case "view":
            {
                var p = FindPlanet(a[1]);
                if (p == null) return "no such planet";
                double dist = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) * 1000.0;
                Vector3D dir = Bearing(a.Length > 3 ? a[3] : "keep", camera.Position - p.Center);
                Vector3D pos = p.Center + dir * dist;
                Teleport(session, pos, p.Center - pos);
                return $"-> {PlanetName(p)} at {dist / 1000:F1} km from center (alt {(dist - PlanetRadius(p)) / 1000:F1} km)";
            }

            case "tpgrid":
            {
                // tpgrid <gridId> [offsetM]: stand next to a grid (server snapshot position), facing it.
                // (never seated: a character held in a seat is a jointed body - teleported, Havok failed to migrate the joint
                //  and asserted: the game crashed, 2026-10-05. `unseat` first)
                if (FrameHost.Seated) return "seated: not teleporting (the seat's joint cannot be migrated) - unseat first";
                Vector3D gp = default; bool found = false;
                if (long.TryParse(a[1], out long gid)) lock (ServerFrames.GridPositions) found = ServerFrames.GridPositions.TryGetValue(gid, out gp);
                if (!found) return "unknown grid (see status)";
                double off = a.Length > 2 ? D(a[2]) : 150.0;
                Vector3D away = camera.Position - gp;   // (the camera on the grid - after a relaunch - had no direction: NaN, an invalid quaternion)
                if (!(away.LengthSquared() > 1e-6) || !GridMembers.Finite(away)) away = Vector3D.UnitY;
                Vector3D from = gp + Vector3D.Normalize(away) * off;
                Teleport(session, from, gp - from);
                return $"-> {off:F0} m from grid {gid}";
            }

            case "tp":
            {
                var pos = new Vector3D(D(a[1]), D(a[2]), D(a[3]));
                Teleport(session, pos, null, camera.Orientation);
                return $"-> {ServerPlanetBeacon.Fmt(pos)}";
            }

            case "look":
            {
                var target = new Vector3D(D(a[1]), D(a[2]), D(a[3]));
                Teleport(session, camera.Position, target - camera.Position);
                return "turned";
            }

            case "lookat":
            {
                var p = FindPlanet(a[1]);
                if (p == null) return "no such planet";
                Teleport(session, camera.Position, PlanetWorldPos(p) - camera.Position);
                return $"facing {PlanetName(p)}";
            }

            case "mode":
                OrbitalConfig.Mode = (ProxyMode)Enum.Parse(typeof(ProxyMode), a[1], ignoreCase: true);
                return $"mode={OrbitalConfig.Mode}";

            case "hide":
                OrbitalConfig.HideRealPlanets = On(a[1]);
                return $"hide={OrbitalConfig.HideRealPlanets}";

            case "front":
                OrbitalConfig.DebugProxyInFront = On(a[1]);
                return $"front={OrbitalConfig.DebugProxyInFront}";

            case "fakevel":
            {
                // fakevel off | fakevel <x> <y> <z> | fakevel circular <planet> [factor] [+x..-z]
                if (a[1].Equals("off", StringComparison.OrdinalIgnoreCase)) { OrbitDisplay.VelocityOverride = null; OrbitDisplay.Freeze = false; return "fakevel off"; }
                if (a[1].Equals("freeze", StringComparison.OrdinalIgnoreCase)) { OrbitDisplay.Freeze = a.Length < 3 || On(a[2]); return $"freeze={OrbitDisplay.Freeze}"; }
                if (a[1].Equals("circular", StringComparison.OrdinalIgnoreCase))
                {
                    var p = FindPlanet(a[2]);
                    if (p == null) return "no such planet";
                    double factor = a.Length > 3 ? D(a[3]) : 1.0;
                    Vector3D r = camera.Position - p.Center;
                    double d = r.Length();
                    var law = p.Gravity; law.Multiplier = _clientGravityMultiplier;
        double mu = law.MuAt(d);
                    if (mu <= 0) return "no gravity here";
                    // Horizontal direction: perpendicular to r, in the plane of r and the given axis.
                    Vector3D axis = Bearing(a.Length > 4 ? a[4] : "+y", Vector3D.UnitY);
                    Vector3D h = Vector3D.Cross(r, axis);
                    if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(r, Vector3D.UnitX);
                    Vector3D along = Vector3D.Normalize(Vector3D.Cross(h, r));
                    double v = Math.Sqrt(mu / d) * factor;
                    OrbitDisplay.VelocityOverride = along * v;
                    return $"fakevel circular×{factor:F2} = {v:F0} m/s";
                }
                OrbitDisplay.VelocityOverride = new Vector3D(D(a[1]), D(a[2]), D(a[3]));
                return "fakevel set";
            }

            case "shot":
            {
                // Engine screenshot into the game temp folder (works with the display asleep).
                string name = a.Length > 1 ? a[1] : $"orbital-{DateTime.Now:HHmmss}.png";
                string path = PlanetRenderBridge.EngineScreenshot(name);
                LastShot = path ?? "FAILED";
                return "shot -> " + LastShot;
            }

            case "orbit":
                if (a.Length >= 4 && !On(a[1]) && !a[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    // orbit <planet> <apoAltKm> <periAltKm> [incDeg]
                    var op = FindPlanet(a[1]);
                    string ob = op != null ? SystemHost.BodyNameOf(op) : null;
                    if (ob == null && (a[1].Equals("Delfos", StringComparison.OrdinalIgnoreCase) || a[1].Equals("Star", StringComparison.OrdinalIgnoreCase)))
                        ob = SystemHost.Registry?.Root?.Name;   // about the star itself
                    if (ob == null) return "no such planet";
                    return FrameHost.SetOrbit(ob, D(a[2]), D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 0);
                }
                OrbitalConfig.ShowOrbit = On(a[1]);
                return $"orbit={OrbitalConfig.ShowOrbit}";

            case "hsfold":
                // (inert since the player HighSpeed removal: sets FrameHost.HsThrustFold, which nothing reads; answers as ever)
                FrameHost.HsThrustFold = On(a[1]);
                return $"hsfold={FrameHost.HsThrustFold}";

            case "save":
            {
                // ALWAYS into the test world's container, whatever world is loaded: the harness must
                // never write a player's own save.
                var server = ServerPlanetBeacon.ServerSession?.Get<Keen.Game2.Simulation.Replication.IGameServer>();
                if (server == null) return "no IGameServer";
                string target = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : TestWorldContainer;
                if (!target.StartsWith("Orbital Test", StringComparison.Ordinal)) return "refused: the harness only saves to 'Orbital Test...' worlds";
                SaveAndLog(server, target);
                return $"saving to '{target}'";
            }

            case "cam":
                // cam off | cam planet <planet> <distKm> [bearing] [elev] | cam player <distKm> [bearing] [elev] | cam map <units> [bearing] [elev]
                switch (a[1].ToLowerInvariant())
                {
                    case "off": return SpecCam.Off(session);
                    case "planet": return SpecCam.Planet(a[2], D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 30);
                    case "player": return SpecCam.Player(D(a[2]), a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 30);
                    case "fov": return SpecCam.Fov(session, D(a[2]));   // cam fov <deg> (0: the player's setting) (2026-10-18)
                    case "lock": return SpecCam.GridLock(session, (long)D(a[2]), camera);   // cam lock <id>: keep the camera where it is, relative to that grid (2026-10-18)
                    case "grid": return SpecCam.Grid((long)D(a[2]), D(a[3]), a.Length > 4 ? D(a[4]) : 90, a.Length > 5 ? D(a[5]) : 15);   // cam grid <id> <distKm> [bearing from its nose] [elev]
                    case "sun": return SpecCam.Sun();
                    case "mapbody": return SpecCam.MapBody(a[2], D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 60);
                    case "map": return SpecCam.Map(D(a[2]), a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 60);
                }
                return "cam off|planet|player|grid|map";

            case "approach":   // approach <body> <altKm> <speed m/s> <gammaDeg below the horizon>: your frame on that state
            {
                var op = FindPlanet(a[1]);
                string ob = op != null ? SystemHost.BodyNameOf(op) : null;
                var node = ob != null ? SystemHost.Registry?.Find(ob) : null;
                var def = ob != null ? SystemHost.Registry?.FindDefinition(ob) : null;
                if (node == null || def == null) return "no such planet";
                double r = def.RadiusMeters + D(a[2]) * 1000, v = D(a[3]), g = D(a[4]) * Math.PI / 180, tt = SystemHost.Now;
                var st = new SEAerospace.Orbital.StateVector(new Vector3D(r, 0, 0), new Vector3D(-v * Math.Sin(g), 0, v * Math.Cos(g)));
                var el = CaptureMath.CaptureElements(st, node.Mu, tt);
                return FrameHost.SetOrbitTo(ob, el, $"approach at {D(a[2])} km, {v} m/s, {D(a[4])} deg down (Pe {(el.PeriapsisRadius - def.RadiusMeters) / 1000:F1} km)");
            }

            case "flyby":   // flyby <body> <periAltKm> <vInf m/s>: you on a hyperbolic pass, inbound at 0.9 of its SOI (an arrival from outside)
            {
                var fp = FindPlanet(a[1]); string fb = fp != null ? SystemHost.BodyNameOf(fp) : null;
                var node = fb != null ? SystemHost.Registry?.Find(fb) : null;
                var fdef = fb != null ? SystemHost.Registry?.FindDefinition(fb) : null;
                if (node == null || fdef == null || !(node.SoiRadius > 0) || double.IsInfinity(node.SoiRadius)) return "no such body (or no SOI)";
                double mu = node.Mu, rp = fdef.RadiusMeters + D(a[2]) * 1000, vinf = D(a[3]), r0 = 0.9 * node.SoiRadius;
                if (!(rp < r0) || !(vinf > 0)) return "periapsis outside the SOI, or no speed";
                double v0 = Math.Sqrt(vinf * vinf + 2 * mu / r0), vp = Math.Sqrt(vinf * vinf + 2 * mu / rp);
                double vt = rp * vp / r0, vr = -Math.Sqrt(Math.Max(0, v0 * v0 - vt * vt));   // (inbound: radial speed negative)
                var st = new SEAerospace.Orbital.StateVector(new Vector3D(r0, 0, 0), new Vector3D(vr, 0, -vt));
                var el = CaptureMath.CaptureElements(st, mu, SystemHost.Now);
                double vb = Math.Sqrt(vinf * vinf + 2 * mu / SEAerospace.PlanetBerths.ShellRadius(fdef));
                return FrameHost.SetOrbitTo(fb, el, $"flyby: Pe {D(a[2])} km, v-inf {vinf} m/s") + $" | at its border {vb:F0} m/s";
            }

            case "orbitnow":   // orbitnow: the orbit readout, one line (what the card is built from)
                return "orbitnow " + (OrbitDisplay.LastReadout ?? "none").Replace("\n", " | ");

            case "camview":   // camview: toggle first / third person as the game's V key does, in its input phase
            {
                var ch = FrameHost.PlayerCharacter(session);
                var cs = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>()?.LocalPlayerController?.CameraSystem;
                if (cs == null || ch == null) return "no camera system";
                ToggleCameraLater(ch, cs);
                return "camera toggle queued (input phase)";
            }

            case "camdiag":   // orientations the third-person camera starts from: character, controller, render camera
            {
                string Q(Quaternion q) => $"({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) |q|={Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W):F6} valid={q.IsValidAndRotationIsNormalized()}";
                var ch = FrameHost.PlayerCharacter(session);
                var pl = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>()?.LocalPlayerController;
                var ctrl = pl?.CameraSystem?.ActiveCameraController;
                var rc = SpecCam.CameraOf(session);
                var sb = new System.Text.StringBuilder();
                if (ch != null) { var w = ch.Data.GetWorldTransform(); sb.Append($"char {Q(w.Orientation)} pos ok={IsFiniteV(w.Position)}; "); }
                if (ctrl != null) sb.Append($"ctrl {Q(ctrl.Data.GetWorldTransform().Orientation)}; ");
                if (rc != null) sb.Append($"render {Q(rc.Data.GetWorldTransform().Orientation)}; ");
                sb.Append($"mode {pl?.CameraSystem?.ActiveCameraModeIndex}");
                return sb.ToString();
            }

            case "kick":
                // kick <prograde m/s> [radial] [normal]  (HighSpeed only: the player's is gone, so always "not in HighSpeed")
                return FrameHost.Kick(D(a[1]), a.Length > 2 ? D(a[2]) : 0, a.Length > 3 ? D(a[3]) : 0);

            case "devrider":
            {
                // devrider on <dxKm> [dyKm dzKm] | off: ride your own frame about its centre, placed this far from it.
                FrameHost.DevRider = On(a[1]);
                if (!FrameHost.DevRider) return "devrider off";
                var pf = FrameHost.PlayerFrame;
                if (pf == null) return "not in a frame";
                var off = new Vector3D(a.Length > 2 ? D(a[2]) * 1000 : 5000, a.Length > 3 ? D(a[3]) * 1000 : 0, a.Length > 4 ? D(a[4]) * 1000 : 0);
                Teleport(session, pf.BerthCenter + off, null, camera.Orientation);
                return $"devrider on: {off.Length() / 1000:F1} km from frame #{pf.Id}'s centre";
            }

            case "stow":
                FrameHost.ForceStow = true;
                return "stow requested (next tick)";

            case "warp":
                SystemHost.Timescale = Math.Max(0.0, D(a[1]));
                return $"warp x{SystemHost.Timescale}";

            case "player":
            {
                // player dampeners on|off | player vel x y z | player vel circular <planet> [factor] [axis]
                var req = new PlayerRequest();
                if (a[1].Equals("dampeners", StringComparison.OrdinalIgnoreCase)) req.Dampeners = On(a[2]);
                else if (a[1].Equals("vel", StringComparison.OrdinalIgnoreCase))
                {
                    if (a[2].Equals("circular", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = FindPlanet(a[3]);
                        if (p == null) return "no such planet";
                        Vector3D? v = CircularVelocity(p, camera.Position, a.Length > 4 ? D(a[4]) : 1.0, a.Length > 5 ? a[5] : "+y");
                        if (!v.HasValue) return "no gravity here";
                        req.Velocity = v;
                    }
                    else req.Velocity = new Vector3D(D(a[2]), D(a[3]), D(a[4]));
                }
                else return "player dampeners on|off | player vel ...";
                // Merge with anything still queued, so "dampeners" + "vel" in one batch both land.
                var prev = ServerPlanetBeacon.PendingPlayer;
                if (prev != null)
                {
                    req.Dampeners ??= prev.Dampeners;
                    req.Velocity ??= prev.Velocity;
                }
                ServerPlanetBeacon.PendingPlayer = req;
                // The local character is likely client-authoritative for movement: apply here too.
                string local = ServerPlanetBeacon.ApplyToCharacter(session, req, "client");
                return $"queued player {(req.Dampeners.HasValue ? "dampeners=" + req.Dampeners : "")}{(req.Velocity.HasValue ? $" vel={req.Velocity.Value.Length():F1} m/s" : "")}; {local}";
            }

            case "gravity":
            {
                // gravity <planet> inverse <reachKm> | gravity <planet> vanilla
                var p = FindPlanet(a[1]);
                if (p == null) return "no such planet";
                if (a[2].Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                {
                    p.PendingGravity = new GravityRequest { Restore = true };
                    return $"queued restore for {PlanetName(p)}";
                }
                float reach = (float)(D(a[3]) * 1000.0);
                p.PendingGravity = new GravityRequest { Falloff = 2f, Reach = reach };
                return $"queued inverse-square for {PlanetName(p)}, reach {reach / 1000:F0} km";
            }

            default:
                return ExecuteGrids(session, camera, line, a, verb);
        }
    }

    /// <summary>Keen's admin teleport. Facing = world direction to look along (null keeps orientation).</summary>
    private static void Teleport(Keen.VRage.Core.Game.Systems.Session session, Vector3D position, Vector3D? facing, Quaternion? keep = null)
    {
        Quaternion q = keep ?? Quaternion.Identity;
        if (facing.HasValue && facing.Value.LengthSquared() > 1e-6 && GridMembers.Finite(facing.Value))
        {
            Vector3 fwd = (Vector3)Vector3D.Normalize(facing.Value);
            Vector3 up = Math.Abs(fwd.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
            q = Quaternion.CreateFromForwardUp(fwd, up);
        }
        RunTeleport(session, new WorldTransform(position, q));
    }

    private static void RunTeleport(Keen.VRage.Core.Game.Systems.Session session, WorldTransform target)
    {
        if (!PlanetRenderBridge.TeleportPlayer(session, target))
            Log.Default?.Warning("[ORBIT-DEV] teleport failed (see bridge warning)");
    }

    private static Vector3D Bearing(string spec, Vector3D keep)
    {
        switch (spec.ToLowerInvariant())
        {
            case "+x": return Vector3D.UnitX;
            case "-x": return -Vector3D.UnitX;
            case "+y": return Vector3D.UnitY;
            case "-y": return -Vector3D.UnitY;
            case "+z": return Vector3D.UnitZ;
            case "-z": return -Vector3D.UnitZ;
            default:
                return keep.LengthSquared() > 1e-6 ? Vector3D.Normalize(keep) : Vector3D.UnitY;
        }
    }

    /// <summary>Circular-orbit velocity at <paramref name="pos"/> for the planet's current law, times factor.</summary>
    private static Vector3D? CircularVelocity(PlanetBeacon p, Vector3D pos, double factor, string axisSpec)
    {
        Vector3D r = pos - p.Center;
        double d = r.Length();
        var law = p.Gravity; law.Multiplier = _clientGravityMultiplier;
        double mu = law.MuAt(d);
        if (mu <= 0 || d <= 0) return null;
        Vector3D axis = Bearing(axisSpec, Vector3D.UnitY);
        Vector3D h = Vector3D.Cross(r, axis);
        if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(r, Vector3D.UnitX);
        Vector3D along = Vector3D.Normalize(Vector3D.Cross(h, r));
        return along * (Math.Sqrt(mu / d) * factor);
    }

    /// <summary>Where the observer SEES the planet: through the published frame when framed, else its real centre.</summary>
    private static Vector3D PlanetWorldPos(PlanetBeacon p)
    {
        string body = SystemHost.BodyNameOf(p);
        var reg = SystemHost.Registry;
        if (body != null && reg != null && FrameHost.Observer.HasValue && reg.Find(body) is SEAerospace.Orbital.GravityBody node)
            return SEAerospace.PlanetBerths.WorldFromCelestial(FrameHost.Observer.Value, node.OriginInRoot(SystemHost.Now).Position);
        return p.Center;
    }

    private static double ModelG(PlanetBeacon p, Vector3D pos)
    {
        var law = p.Gravity; law.Multiplier = _clientGravityMultiplier;
        return law.At((pos - p.Center).Length());
    }

    internal static List<PlanetBeacon> Planets()
    {
        var list = new List<PlanetBeacon>();
        foreach (var b in PlanetBeacons.All()) list.Add(b);
        return list;
    }

    private static PlanetBeacon FindPlanet(string key)
    {
        var list = Planets();
        if (int.TryParse(key, out int i)) return i >= 0 && i < list.Count ? list[i] : null;
        foreach (var p in list)
        {
            if (PlanetName(p).StartsWith(key, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>"Verdure" from "...\Planets\Verdure\VerdureMapVisualPrefab.def".</summary>
    public static string PlanetName(PlanetBeacon p)
    {
        string s = p?.MapVisual?.DebugName;
        if (string.IsNullOrEmpty(s)) return p?.Name ?? "?";
        string file = s.Replace('/', '\\');
        int slash = file.LastIndexOf('\\');
        file = slash >= 0 ? file.Substring(slash + 1) : file;
        foreach (string tag in new[] { "ColonizationMapPlanetVisual", "_ColonizationMap", "ColonizationMap", "MapVisual" })
        {
            int cut = file.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (cut > 0) { file = file.Substring(0, cut); break; }
        }
        return file.TrimEnd('_', '.', ' ');
    }

    private static double PlanetRadius(PlanetBeacon p) => p.Gravity.R0 > 0 ? p.Gravity.R0 : 0;

    private static Keen.VRage.Core.Game.Systems.Session _statusSession;
    static readonly Dictionary<long, (string body, SEAerospace.Orbital.KeplerianElements el)> _celMark = new Dictionary<long, (string, SEAerospace.Orbital.KeplerianElements)>();
    private static void WriteStatus(WorldTransform camera)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"restore {SavedState.LastRestore} | save {LastSave}");
        sb.AppendLine($"mapview {MapView.Mode}: {MapView.Status} | cam {SpecCam.Status}");
        sb.AppendLine("roids " + AsteroidBridge.Status);
        sb.AppendLine("libration " + Maneuvers.LibStart + " | " + Maneuvers.LibDebug);
        sb.AppendLine(FrameHost.RiderDiag);
        sb.AppendLine(OrbitDisplay.PredDiag);
        { var hr = OrbitHud.Current; if (hr?.Relative != null) sb.AppendLine($"relnow along {hr.RelNow.along:F2} m radial {hr.RelNow.radial:F2} m vel ({hr.RelVel.along:F3}, {hr.RelVel.radial:F3}) m/s holding {hr.Holding} dampeners {FrameHost.Dampeners} t {SystemHost.Now:F1}"); }
        sb.AppendLine(DelfosHeat.Status);
        sb.AppendLine(SpawnGuard.Status);
        lock (AsteroidBridge.RingInfo) foreach (var ri in AsteroidBridge.RingInfo) sb.AppendLine("  ring " + ri);
        sb.AppendLine($"sun {SunDriver.Status}");
        sb.AppendLine($"star {StarProxy.Status}");
        sb.AppendLine($"frame server worst={TickRate.Server.WorstMs:F0}ms mean={TickRate.Server.MeanMs:F1}ms draw worst={TickRate.Draw.WorstMs:F0}ms mean={TickRate.Draw.MeanMs:F1}ms | stress {DevStress.Status}");
        sb.AppendLine($"entry {EntryHost.Status} | {EntryHost.CardLine(SystemHost.Now) ?? "no pass ahead"}");
        try
        {
            var session = _statusSession; var sec = session?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Colonization.SectorsSessionComponent>();
            sb.AppendLine($"colonization sectors={(sec != null ? sec.Sectors.Count.ToString() : "none")} mapVisible={MapView.Map(session)?.IsVisible}");
        }
        catch (Exception e) { sb.AppendLine("colonization ? " + e.Message); }
        try
        {
            var vb = SystemHost.Registry?.Find("Verdure");
            if (vb != null) sb.AppendLine($"spin Verdure T={vb.RotationPeriodSeconds:F0}s theta={vb.RotationAngleAt(SystemHost.Now) * 180 / Math.PI:F1}deg worldSunPeriod={SystemHost.WorldSunPeriod:F0}s");
        }
        catch { }
        sb.AppendLine($"modcost ms avg/max: client {ModCost.Client} server {ModCost.Server} map {ModCost.Map} | gc gen0 {System.GC.CollectionCount(0)} gen1 {System.GC.CollectionCount(1)} gen2 {System.GC.CollectionCount(2)} pause {System.GC.GetTotalPauseDuration().TotalMilliseconds:F0}ms alloc {System.GC.GetTotalAllocatedBytes() / 1048576}MB {GcMem()}");
        sb.AppendLine($"mapcost ms avg/max: {ModCost.SectionList()}");
        sb.AppendLine($"bodymarkers {BodyMarkers.Status}");
        sb.AppendLine($"warpbill {WarpBill.Status}");
        sb.AppendLine($"contacts {Contacts.Status}");
        sb.AppendLine($"time {DateTime.Now:HH:mm:ss.fff} ticks/s client={TickRate.Client.PerSecond:F1} draw={TickRate.Draw.PerSecond:F1} server={TickRate.Server.PerSecond:F1} rails t={SystemHost.Now:F1} x{SystemHost.Timescale} clock={SystemHost.ClockSource}");
        sb.AppendLine($"camera {camera.Position.X:F0} {camera.Position.Y:F0} {camera.Position.Z:F0}");
        sb.AppendLine($"physics gravityMultiplier client={_clientGravityMultiplier} server={ServerPlanetBeacon.ServerGravityMultiplier}");
        sb.AppendLine($"config mode={OrbitalConfig.Mode} hide={OrbitalConfig.HideRealPlanets} front={OrbitalConfig.DebugProxyInFront} orbit={OrbitalConfig.ShowOrbit}");
        var list = Planets();
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            double d = (p.Center - camera.Position).Length();
            sb.AppendLine($"planet {i} {PlanetName(p)} center={p.Center.X:F0},{p.Center.Y:F0},{p.Center.Z:F0} " +
                          $"dist={d / 1000:F1}km r0={p.Gravity.R0 / 1000:F1}km g0={p.Gravity.G0:F2} falloff={p.Gravity.Falloff:F2} reach={p.Gravity.Reach / 1000:F1}km");
        }
        sb.AppendLine($"system built={SystemHost.Built} t={SystemHost.Now:F1}s warp=x{SystemHost.Timescale} observer={(FrameHost.PlayerFrame != null ? "conjunction #" + FrameHost.PlayerFrame.Id : FrameHost.ObserverPlanet != null ? "planet " + FrameHost.ObserverPlanet : "legacy")} frames={(SystemHost.Frames != null ? SystemHost.Frames.Count : 0)}");
        if (FrameHost.PlayerFrame != null) sb.AppendLine($"frame #{FrameHost.PlayerFrame.Id} parent={FrameHost.PlayerFrame.ParentBodyName} berth={ServerPlanetBeacon.Fmt(FrameHost.PlayerFrame.BerthCenter)} a={FrameHost.PlayerFrame.Elements.SemiMajorAxis / 1000:F1}km e={FrameHost.PlayerFrame.Elements.Eccentricity:F3} pendingDv={FrameHost.PlayerFrame.PendingDrainDv.Length():F2}");
        sb.AppendLine("lastEvent " + FrameHost.LastEvent);
        sb.AppendLine("host " + FrameHost.Debug + $" highSpeed={FrameHost.HighSpeedActive} hsV={FrameHost.HighSpeedVelocity.Length():F0} hsEl={FrameHost.HighSpeedElements} hsResid avg={FrameHost.HsResidualAvg:F3} max={FrameHost.HsResidualMax:F3} folds={FrameHost.HsFolds} {FrameHost.HsDiag}");
        sb.AppendLine("shot " + LastShot);
        sb.Append(ServerFrames.GridSnapshot); // built on the server thread (client-side reads froze the game)
        if (OrbitDisplay.LastReadout != null) sb.AppendLine("orbit " + OrbitDisplay.LastReadout.Replace("\n", " | "));
        Vector3D mv = OrbitDisplay.MeasuredVelocity;
        var near = list.Count > 0 ? list[0] : null;
        foreach (var p in list) if ((p.Center - camera.Position).LengthSquared() < (near.Center - camera.Position).LengthSquared()) near = p;
        if (near != null)
        {
            Vector3D rhat = Vector3D.Normalize(camera.Position - near.Center);
            sb.AppendLine($"measured |v|={mv.Length():F2} m/s radial={Vector3D.Dot(mv, rhat):F2} m/s (vs {PlanetName(near)}) g_model={ModelG(near, camera.Position):F3} m/s²");
        }
        lock (_log) foreach (string l in _log) sb.AppendLine(l);
        File.WriteAllText(Path.Combine(_dir, "status.txt"), sb.ToString());
    }

    private static void Note(string s)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {s}");
            if (_log.Count > 20) _log.RemoveAt(0);
        }
    }

    private static double D(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    private static bool On(string s) => s.Equals("on", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The managed heap after the last collection, its fragmentation, and the machine's memory load.</summary>
    static string GcMem()
    {
        var m = System.GC.GetGCMemoryInfo();
        return $"heap {m.HeapSizeBytes / 1048576}MB frag {m.FragmentedBytes / 1048576}MB load {m.MemoryLoadBytes * 100 / System.Math.Max(1, m.TotalAvailableMemoryBytes)}% (high {m.HighMemoryLoadThresholdBytes * 100 / System.Math.Max(1, m.TotalAvailableMemoryBytes)}%) compacted {m.Compacted} gen{m.Generation}";
    }
}
