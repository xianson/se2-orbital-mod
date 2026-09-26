using System.IO;
using Keen.VRage.Core;

#pragma warning disable
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
public static class DevHarness
{
    /// <summary>The only save the harness ever writes (a copy of the test world).</summary>
    public const string TestWorldContainer = "Orbital Test World";

    private static async void SaveAndLog(Keen.Game2.Simulation.Replication.IGameServer server)
    {
        try
        {
            var r = await server.TrySaveGame(TestWorldContainer);
            Log.Default?.Info($"[ORBIT-DEV] save -> '{TestWorldContainer}': {r}");
            LastSave = $"{DateTime.Now:HH:mm:ss} {r}";
        }
        catch (Exception e) { Log.Default?.Info($"[ORBIT-DEV] save failed: {e.Message}"); LastSave = "failed: " + e.Message; }
    }
    public static string LastSave = "none";

    public const double PollSeconds = 0.5;

    private static long _lastPoll;
    private static string _dir;
    private static readonly List<string> _log = new List<string>();

    private static float _clientGravityMultiplier = float.NaN;
    public static string LastShot = "";

    public static void Poll(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera)
    {
        if (!OrbitalConfig.DevHarness) return;
        try
        {
            SpecCam.Tick(session, name => { var pl = FindPlanet(name); return pl != null ? PlanetWorldPos(pl) : (Vector3D?)null; },
                         FrameHost.PlayerPosition);
        }
        catch (Exception e) { SpecCam.Status = "error " + e.Message; }
        try { _clientGravityMultiplier = session.Get<Keen.VRage.Physics.IPhysics>().GravityMultiplier; } catch { }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastPoll < PollSeconds * System.Diagnostics.Stopwatch.Frequency) return;
        _lastPoll = now;

        try
        {
            _dir ??= Path.Combine(Path.GetTempPath(), "OrbitalMod");
            Directory.CreateDirectory(_dir);
            string cmdPath = Path.Combine(_dir, "cmd.txt");
            if (File.Exists(cmdPath))
            {
                string[] lines = File.ReadAllLines(cmdPath);
                File.Delete(cmdPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string result;
                    try { result = Execute(session, camera, line); }
                    catch (Exception e)
                    {
                        result = "ERROR " + e.Message;
                        string st = e.ToString().Replace('\n', ' ');
                        Log.Default?.Info("[ORBIT-DEV] stack: " + st.Substring(0, Math.Min(1500, st.Length)));
                    }
                    Note($"> {line}  =>  {result}");
                    Log.Default?.Info($"[ORBIT-DEV] {line} => {result}");
                }
            }
            WriteStatus(camera);
        }
        catch (Exception e)
        {
            Log.Default?.Warning($"[ORBIT-DEV] poll failed: {e.Message}");
        }
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
                Vector3D gp = default; bool found = false;
                if (long.TryParse(a[1], out long gid)) lock (ServerFrames.GridPositions) found = ServerFrames.GridPositions.TryGetValue(gid, out gp);
                if (!found) return "unknown grid (see status)";
                double off = a.Length > 2 ? D(a[2]) : 150.0;
                Vector3D from = gp + Vector3D.Normalize(camera.Position - gp) * off;
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
                    if (ob == null) return "no such planet";
                    return FrameHost.SetOrbit(ob, D(a[2]), D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 0);
                }
                OrbitalConfig.ShowOrbit = On(a[1]);
                return $"orbit={OrbitalConfig.ShowOrbit}";

            case "hsfold":
                FrameHost.HsThrustFold = On(a[1]);
                return $"hsfold={FrameHost.HsThrustFold}";

            case "save":
            {
                // ALWAYS into the test world's container, whatever world is loaded: the harness must
                // never write a player's own save.
                var server = ServerPlanetBeacon.ServerSession?.Get<Keen.Game2.Simulation.Replication.IGameServer>();
                if (server == null) return "no IGameServer";
                SaveAndLog(server);
                return $"saving to '{TestWorldContainer}'";
            }

            case "cam":
                // cam off | cam planet <planet> <distKm> [bearing] [elev] | cam player <distKm> [bearing] [elev] | cam map <units> [bearing] [elev]
                switch (a[1].ToLowerInvariant())
                {
                    case "off": return SpecCam.Off(session);
                    case "planet": return SpecCam.Planet(a[2], D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 30);
                    case "player": return SpecCam.Player(D(a[2]), a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 30);
                    case "sun": return SpecCam.Sun();
                    case "map": return SpecCam.Map(D(a[2]), a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 60);
                }
                return "cam off|planet|player|map";

            case "map":
            {
                if (On(a[1])) MapView.Open(session); else MapView.Close();
                return On(a[1]) ? "opening map (terminal Map tab)" : "closing map";
            }

            case "legacy":
                OrbitalConfig.CaptureLegacySpace = On(a[1]);
                return $"captureLegacySpace={OrbitalConfig.CaptureLegacySpace}";

            case "sun":
                SunDriver.Enabled = On(a[1]);
                return $"sun driver={SunDriver.Enabled}";

            case "omap":
                // omap on|off | omap focus <system|auto|planet> | omap size <m> | omap view <bearing> <elev> [zoom] | omap spin <deg/s> | omap auto on|off
                switch (a[1].ToLowerInvariant())
                {
                    case "on": return OrbitalMap.Open();
                    case "off": return OrbitalMap.Close(session);
                    case "focus": OrbitalMap.Focus = a[2]; return "focus=" + a[2];
                    case "size": OrbitalMap.Size = D(a[2]); return "size=" + a[2];
                    case "view":
                        OrbitalMap.Bearing = D(a[2]); OrbitalMap.Elevation = D(a[3]);
                        if (a.Length > 4) OrbitalMap.ZoomFactor = D(a[4]);
                        return $"view {OrbitalMap.Bearing}/{OrbitalMap.Elevation} zoom {OrbitalMap.ZoomFactor}";
                    case "spin": OrbitalMap.SpinDegPerSec = D(a[2]); return "spin=" + a[2];
                    case "auto": OrbitalMap.AutoWithMapTab = On(a[2]); return "auto=" + On(a[2]);
                }
                return "omap on|off|focus|size|view|spin|auto";

            case "mapview":
                MapView.Mode = (MapView.ViewMode)Enum.Parse(typeof(MapView.ViewMode), a[1], ignoreCase: true);
                return $"mapview={MapView.Mode}";

            case "kick":
                // kick <prograde m/s> [radial] [normal]  (HighSpeed only)
                return FrameHost.Kick(D(a[1]), a.Length > 2 ? D(a[2]) : 0, a.Length > 3 ? D(a[3]) : 0);

            case "gridorbit":
            {
                // gridorbit <gridId> <planet> <apoAltKm> <periAltKm> [incDeg] [phaseDeg]
                if (a.Length < 5) return "usage: gridorbit <gridId> <planet> <apoAltKm> <periAltKm> [incDeg] [phaseDeg]";
                var gp = FindPlanet(a[2]);
                string gb = gp != null ? SystemHost.BodyNameOf(gp) : null;
                if (gb == null) return "no such planet";
                if (!FrameHost.OrbitElements(gb, D(a[3]), D(a[4]), a.Length > 5 ? D(a[5]) : 0, a.Length > 6 ? D(a[6]) : 0, out var gel))
                    return "degenerate orbit";
                ServerFrames.GridOrbit.Enqueue(new ServerFrames.GridOrbitRequest { GridId = long.Parse(a[1]), Body = gb, El = gel });
                return "grid orbit queued (server, next tick)";
            }

            case "aerospike":
                // aerospike on|off: the aero mod's experiment (holds every grid at 50 m/s, freezes the sim).
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroSpeedSpike", "Enabled", On(a[1]));

            case "gridsstop":
                ServerFrames.StopAllGrids = true;
                return "stopping all dynamic grids (server, next tick)";

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
                return "unknown command";
        }
    }

    /// <summary>Keen's admin teleport. Facing = world direction to look along (null keeps orientation).</summary>
    private static void Teleport(Keen.VRage.Core.Game.Systems.Session session, Vector3D position, Vector3D? facing, Quaternion? keep = null)
    {
        Quaternion q = keep ?? Quaternion.Identity;
        if (facing.HasValue && facing.Value.LengthSquared() > 1e-6)
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

    private static List<PlanetBeacon> Planets()
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
        int cut = file.IndexOf("MapVisual", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? file.Substring(0, cut) : file;
    }

    private static double PlanetRadius(PlanetBeacon p) => p.Gravity.R0 > 0 ? p.Gravity.R0 : 0;

    private static void WriteStatus(WorldTransform camera)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"restore {SavedState.LastRestore} | save {LastSave}");
        sb.AppendLine($"mapview {MapView.Mode}: {MapView.Status} | omap {OrbitalMap.Status} | cam {SpecCam.Status}");
        sb.AppendLine($"sun {SunDriver.Status}");
        sb.AppendLine($"time {DateTime.Now:HH:mm:ss.fff} ticks/s client={TickRate.Client.PerSecond:F1} server={TickRate.Server.PerSecond:F1} rails t={SystemHost.Now:F1} x{SystemHost.Timescale} clock={SystemHost.ClockSource}");
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
}
