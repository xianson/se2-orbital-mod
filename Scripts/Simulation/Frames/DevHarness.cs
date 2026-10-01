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
    private static readonly List<string> _log = new List<string>();

    private static float _clientGravityMultiplier = float.NaN;
    public static string LastShot = "";

    public static void Poll(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera)
    {
        if (!OrbitalConfig.DevHarness) return;
        _statusSession = session; _lastCamera = camera;
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

    private static WorldTransform _lastCamera;

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
        try { return Execute(session, _lastCamera, line); }
        catch (Exception e) { return "ERROR " + e.Message; }
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
                    if (ob == null && (a[1].Equals("Delfos", StringComparison.OrdinalIgnoreCase) || a[1].Equals("Star", StringComparison.OrdinalIgnoreCase)))
                        ob = SystemHost.Registry?.Root?.Name;   // about the star itself
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
                    case "sun": return SpecCam.Sun();
                    case "mapbody": return SpecCam.MapBody(a[2], D(a[3]), a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 60);
                    case "map": return SpecCam.Map(D(a[2]), a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 60);
                }
                return "cam off|planet|player|map";

            case "testship":   // testship [charge 0..1]: a test ship (battery, telescope, radar) 60 m ahead, yours
                return DevTestShip.Spawn(session, camera, a.Length > 1 ? D(a[1]) : 1.0);

            case "sensornopower":   // sensornopower on|off: sensors count without power (harness test blocks)
                SensorBlocks.DevIgnorePower = On(a[1]);
                return "sensors without power: " + SensorBlocks.DevIgnorePower;

            case "radarpower":   // radarpower <0..1>: every radar's transmit power (as the terminal slider)
            {
                int n = 0;
                lock (SensorBlocks.Radars) foreach (var r in SensorBlocks.Radars) { r.Power = (float)D(a[1]); n++; }
                return $"{n} radar(s) at {D(a[1]):P0}";
            }

            case "sensorblocks":   // sensorblocks: the sensor blocks' definitions, unlock and count
                return SensorBlocks.Describe(session);

            case "buildmenu":   // buildmenu on|off: the terminal's Build tab (block catalogue)
                if (On(a[1])) MapView.OpenBuild(session); else MapView.Close();
                return On(a[1]) ? "opening the build menu" : "closing";

            case "map":
            {
                if (On(a[1])) MapView.Open(session); else MapView.Close();
                return On(a[1]) ? "opening map (terminal Map tab)" : "closing map";
            }

            case "legacy":
                OrbitalConfig.CaptureLegacySpace = On(a[1]);
                return $"captureLegacySpace={OrbitalConfig.CaptureLegacySpace}";

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

            case "blockhealth":   // blockhealth: every server grid with a block under full health
            {
                var sb2 = new System.Text.StringBuilder();
                foreach (var g in GridMembers.All())
                    if (g.IsServer && EntryDamage.Damaged(g)) sb2.Append($"{g.Id} '{g.DisplayName}': {EntryDamage.Health(g)}; ");
                return sb2.Length > 0 ? sb2.ToString() : "every grid at full health";
            }

            case "debugdraw":   // debugdraw on|off: the engine's global debug draw (the aero mod turns it on)
                Keen.VRage.Core.GlobalDebugSettings.Default.EnabledDebugDraw = On(a[1]);
                return "engine debug draw " + Keen.VRage.Core.GlobalDebugSettings.Default.EnabledDebugDraw;

            case "aeroflames":   // aeroflames on|off: thruster flames for the aero controller's sharing
                return PlanetRenderBridge.SetForeignFlag("AeroMod.ThrustTorque", "FlamesEnabled", On(a[1]));

            case "gc":   // gc: a full blocking collection now (test only: it pauses the game), then the live heap
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                System.GC.Collect(2, System.GCCollectionMode.Forced, true, true);
                return $"full GC {t.ElapsedMilliseconds} ms: {GcMem()}";
            }

            case "aeroexport":   // aeroexport on|off: the aero mod writes each grid it builds to %TEMP%\AeroMod\*.boxes (for Tests/AeroBench)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroExport", "Enabled", On(a[1]));

            case "aeroflight":   // aeroflight on|off: the aero mod's scripted flight test (the grid with the most wings)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroGridComponent+AeroFlightTest", "Enabled", On(a[1]));

            case "aerocost":   // aerocost on|off: the aero mod's [AERO-COST] line (where its time goes, once a second)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroCost", "Log", On(a[1]));

            case "aero":   // aero on|off: the whole aero mod's simulation (A/B tests)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroSwitch", "Enabled", On(a[1]));

            case "gridspin":   // gridspin <id> <wx> <wy> <wz>: set a grid's angular velocity (rad/s, world)
                return DevStress.Spin((long)D(a[1]), new Vector3((float)D(a[2]), (float)D(a[3]), (float)D(a[4])));

            case "gridatt":   // gridatt <id>: a grid's orientation and spin
                return DevStress.Attitude((long)D(a[1]));

            case "gridhold":   // gridhold <id> <m/s>: keep a grid moving forward at that speed (0 releases)
                return DevStress.Hold((long)D(a[1]), D(a[2]));

            case "gridclone":   // gridclone <id> <count> <spacing m>: copies of a grid in rows of ten above it
                return DevStress.Clone((long)D(a[1]), (int)D(a[2]), D(a[3]));

            case "breakblocks":   // breakblocks <id> <n> [every s]: destroy n of its blocks, one every s (0: all at once)
                return DevStress.Break((long)D(a[1]), (int)D(a[2]), a.Length > 3 ? D(a[3]) : 0.1, a.Length > 4 ? D(a[4]) : 0);   // [radius m: a hit]

            case "bigs":   // bigs [n]: the n biggest server grids by block count (id, name, blocks, mass, km from you)
            {
                var l = new List<(int n, OrbitalGridComponent g)>();
                foreach (var g in GridMembers.All())
                {
                    if (!g.IsServer) continue;
                    var cg = g.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>();
                    int n = 0;
                    cg?.VisitAllBlocksWithComponent<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>(b => n++, false);
                    l.Add((n, g));
                }
                l.Sort((x, y) => y.n.CompareTo(x.n));
                var sb2 = new System.Text.StringBuilder($"{l.Count} grid(s): ");
                for (int i = 0; i < Math.Min(l.Count, a.Length > 1 ? (int)D(a[1]) : 12); i++)
                    sb2.Append($"[{l[i].g.Id} '{l[i].g.DisplayName}' {l[i].n} blocks {GridMembers.Mass(l[i].g) / 1000:F0} t dyn={GridMembers.IsDynamic(l[i].g)} {(GridMembers.Position(l[i].g) - camera.Position).Length() / 1000:F1} km at {GridMembers.Position(l[i].g).X:F0} {GridMembers.Position(l[i].g).Y:F0} {GridMembers.Position(l[i].g).Z:F0}] ");
                return sb2.ToString();
            }

            case "joins":   // joins <gridId>: what a grid is joined to (its connected group; static ones marked)
            {
                var g = GridMembers.Get((long)D(a[1]));
                if (g == null) return "no such grid";
                var svc = g.Session?.Get<Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities>();
                if (svc == null) return "no connection search";
                var sb2 = new System.Text.StringBuilder($"'{g.DisplayName}' static-joined={svc.IsConnectedToStaticEntity(g.Entity)}: ");
                using (var buf = new Keen.VRage.Library.Memory.Buffer<Entity>(Keen.VRage.Library.Memory.Allocator.Pool, "OrbitalJoins"))
                {
                    var args = new Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities.SearchArguments { IncludeRoot = false, IncludeConstrained = true };
                    svc.FindConnectedEntities(g.Entity, (Keen.VRage.Library.Memory.BufferReference<Entity>)buf, args);
                    var en = ((Keen.VRage.Library.Memory.BufferReference<Entity>)buf).GetEnumerator();
                    while (en.MoveNext())
                        if (en.Current != null)
                        {
                            Entity cur = en.Current;
                            OrbitalGridComponent og = null;
                            foreach (var x in GridMembers.All()) if (x.IsServer && ReferenceEquals(x.Entity, cur)) og = x;
                            sb2.Append($"[{cur.DebugName}{(og != null ? $" grid {og.Id} dyn={GridMembers.IsDynamic(og)}" : "")}] ");
                        }
                    en.Dispose();
                }
                return sb2.ToString();
            }

            case "adopt":   // adopt <grid name>: every server grid of that name (and its blocks) becomes yours (tests on the world's ships)
            {
                string want = string.Join(" ", a, 1, a.Length - 1);
                var server = ServerPlanetBeacon.ServerSession;
                var own = server?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent>();
                var me = session.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().LocalPlayerIdentity;
                if (own == null) return "no ownership component";
                int grids = 0, parts = 0;
                foreach (var g in GridMembers.All())
                {
                    if (!g.IsServer || !g.DisplayName.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                    grids++;
                    if (own.TryTransferOwnership(g.Entity, me)) parts++;
                    var hc = g.Entity.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>();
                    if (hc != null) foreach (var c in hc.Children) if (own.TryTransferOwnership(c, me)) parts++;
                }
                return $"adopted {grids} grid(s) named '{want}' ({parts} ownership transfer(s))";
            }

            case "entrycap":   // entrycap <m/s>|off: reentry's cap for a test (the world's own cap stays)
                EntryHost.TestCap = a[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? double.NaN : D(a[1]);
                return "entry cap " + EntryHost.Cap;

            case "entry":   // entry on|off: reentry's braking band
                EntryHost.Enabled = On(a[1]);
                return "entry " + EntryHost.Enabled;

            case "starproxy":   // starproxy on|off: Delfos drawn in the sky in flight
                StarProxy.Enabled = On(a[1]);
                return "star proxy " + StarProxy.Enabled;

            case "sun":
                SunDriver.Enabled = On(a[1]);
                return $"sun driver={SunDriver.Enabled}";

            case "mapdiag":
                MapView.DiagCross = On(a[1]);
                return "mapdiag=" + MapView.DiagCross;

            case "sectors":
            {
                // DEV: log every colonization sector (centre, size, nearest body and distance).
                var sec = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Colonization.SectorsSessionComponent>();
                if (sec == null) return "no sectors component";
                int n = 0;
                foreach (var sc in sec.Sectors)
                {
                    var c = sc.Area.Center;
                    string near = "-"; double nd = double.MaxValue;
                    foreach (var p in PlanetBeacons.All())
                    {
                        double d = (p.Center - c).Length();
                        if (d < nd) { nd = d; near = PlanetName(p); }
                    }
                    Log.Default?.Info($"[ORBIT-DEV] sector '{sc.Name}' centre=({c.X / 1000:F0}, {c.Y / 1000:F0}, {c.Z / 1000:F0}) km size={sc.Area.Size / 1000:F0} km hole={sc.IsHole} nearest={near} at {nd / 1000:F0} km");
                    n++;
                }
                Log.Default?.Info($"[ORBIT-DEV] map world pos={sec.MapWorldPosition} scale={sec.MapWorldScale}");
                return $"{n} sectors logged";
            }

            case "clock":
                // clock +<hours> | clock set <hours>
                if (a[1].Equals("set", StringComparison.OrdinalIgnoreCase)) SystemHost.DevAdvanceClock(D(a[2]) * 3600 - SystemHost.Now);
                else SystemHost.DevAdvanceClock(D(a[1]) * 3600);
                return $"universe clock t={SystemHost.Now / 3600:F1} h";

            case "encounters":
                Log.Default?.Info("[ORBIT-DEV] " + EncounterFrames.Describe());
                return EncounterFrames.Describe().Replace((char)10, '|');

            case "roids":
            {
                // roids status | list | on | off | goto <i> [behindKm] | spawn <i> | despawn <i>: the belts' asteroid frames.
                string r = AsteroidFrames.Command(a);
                Log.Default?.Info("[ORBIT-DEV] " + r);
                return r.Replace((char)10, '|');
            }

            case "gps":
                return FrameMarkers.Describe(session) + " | " + FrameMarkers.Status;

            case "gpsat":
            {
                // gpsat <markerIndex> <encounterIndex> [offsetKm]: a copy of a marker at an encounter frame's berth.
                SEAerospace.Frames.ProximityFrame ef;
                lock (ServerFrames.FramesLock) ef = EncounterFrames.Nth((int)D(a[2]));
                if (ef == null) return "no such encounter frame";
                double off = a.Length > 3 ? D(a[3]) * 1000 : 0;
                string nm = (EncounterFrames.SiteOf(ef.Id)?.Label ?? "Encounter") + " (test)";
                return FrameMarkers.DevCloneAt(session, (int)D(a[1]), ef.BerthCenter + new Vector3D(off, 0, 0), nm);
            }

            case "node":
            {
                // node add <minFromNow> <pro> <nor> <rad> | node clear | node list | node select <i>
                double tn = SystemHost.Now;
                switch (a.Length > 1 ? a[1] : "list")
                {
                    case "add":
                        Maneuvers.Restore(tn + D(a[2]) * 60, a.Length > 3 ? D(a[3]) : 0, a.Length > 4 ? D(a[4]) : 0, a.Length > 5 ? D(a[5]) : 0);
                        return Maneuvers.Describe(tn);
                    case "clear":
                        lock (Maneuvers.Nodes) Maneuvers.Nodes.Clear();
                        Maneuvers.Selected = null;
                        return "nodes cleared";
                    case "select":
                    {
                        var l = new List<Maneuvers.Node>(); lock (Maneuvers.Nodes) l.AddRange(Maneuvers.Nodes);
                        l.Sort((x, y) => x.T.CompareTo(y.T));
                        int i = (int)D(a[2]);
                        Maneuvers.Selected = i >= 0 && i < l.Count ? l[i] : null;
                        return Maneuvers.Describe(tn);
                    }
                    case "auto":   // node auto <i> on|off
                    {
                        var l = new List<Maneuvers.Node>(); lock (Maneuvers.Nodes) l.AddRange(Maneuvers.Nodes);
                        l.Sort((x, y) => x.T.CompareTo(y.T));
                        int i = (int)D(a[2]);
                        if (i < 0 || i >= l.Count) return "no such node";
                        l[i].Auto = a.Length < 4 || On(a[3]);
                        return $"node {i} auto-burn {(l[i].Auto ? "on" : "off")}";
                    }
                    case "capture":   // node capture: a capture burn at the next periapsis
                        return Maneuvers.DevCapture(tn);
                    case "findarr":   // node findarr <body> <peMinKm> <peMaxKm>: a pass whose periapsis radius is in the window
                        return Maneuvers.DevFindArrival(a[2], tn, D(a[3]) * 1000, D(a[4]) * 1000);
                    case "findenc":   // node findenc <body>
                        return Maneuvers.DevFindEncounter(a[2], tn);
                    case "pull":   // node pull <P|R|N|AN|RO|RI> <px> <seconds>
                        Maneuvers.DevPull(a[2], D(a[3]), D(a[4]));
                        return "pulling " + a[2];
                    case "clickat":   // node clickat <minFromNow>
                        Maneuvers.DevClickAt(D(a[2]));
                        return "click queued";
                    case "slide":   // node slide <i> <minFromNow>: drag node i along the path, then release
                        Maneuvers.DevSlide((int)D(a[2]), D(a[3]));
                        return "slide queued";
                    case "rclick":   // node rclick <i>
                        Maneuvers.DevRightClickNode((int)D(a[2]));
                        return "right-click queued";
                    default:
                        return Maneuvers.Describe(tn) + " | " + Maneuvers.Status;
                }
            }

            case "seat":
                // seat <grid name>: the local character into that grid's nearest cockpit
                return DevFlight.Seat(session, a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : null) + " | " + DevFlight.Status;

            case "burn":
                return DevFlight.Burn(a.Length > 1 ? D(a[1]) : 60);

            case "seats":
                return DevFlight.ListSeats(session, FrameHost.PlayerPosition);

            case "thrust":
                // thrust <x> <y> <z> <seconds>: hold a pilot command on the seated grid (x right, y up, z back)
                return DevFlight.Thrust(new Vector3((float)D(a[1]), (float)D(a[2]), (float)D(a[3])), D(a[4]));

            case "flight":
                return $"seated={FrameHost.Seated} frame={(FrameHost.PlayerFrame != null ? "#" + FrameHost.PlayerFrame.Id : "-")} {DevFlight.Status} | auto: {AutoBurn.Status} | {DevFlight.Attitude} | {GameUi.SpeedStatus} | top input: {GameUi.TopInputScreen} | {DevFlight.Info}";

            case "floaters":
            {
                // floaters: every floating object (server) and its distance from each planet centre
                var srv = ServerPlanetBeacon.ServerSession;
                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var e in srv.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.FloatingObjects.FloatingObjectComponent>())
                {
                    var p = e.Data.GetWorldTransform().Position;
                    string near = ""; double nd = double.MaxValue;
                    foreach (var b in PlanetBeacons.All()) { double d = (b.Center - p).Length(); if (d < nd) { nd = d; near = PlanetName(b); } }
                    if (n++ < 12) sb.Append($"{ServerPlanetBeacon.Fmt(p)} {nd / 1000:F1} km from {near}; ");
                }
                return $"{n} floating object(s): {sb}";
            }

            case "freegrids":
                ServerFrames.DevFreeGrids = true;
                return "free grid census queued (see log)";

            case "ui":
                return "gameui: " + (GameUi.LastError.Length > 0 ? GameUi.LastError : "ok");

            case "numdialog":
                return GameUi.NumberDialog(session, "Test (m/s)", 12.5, v => Log.Default?.Info($"[ORBIT-DEV] numdialog -> {v}")) ? "dialog open" : "dialog failed: " + GameUi.LastError;

            case "rclickat":
                // rclickat <fx> <fy>: right click at a screen fraction (the map's context menu)
                GameMap.DevMouse = new Vector2((float)D(a[1]), (float)D(a[2]));
                MapInput.DevRightClick();
                return $"right click at {a[1]},{a[2]}";

            case "menu":
                // menu <i>: choose item i of the open context menu
                MapMenu.DevChoose((int)D(a[1]));
                return "menu " + a[1];

            case "key":
                // key <period|comma|slash>: press a warp key for one frame
                MapInput.DevKeys.Add(a[1]);
                return "key " + a[1];

            case "framedv":
            {
                // framedv <prograde m/s>: a burn on the player's rails frame (as thrust folded into it)
                lock (ServerFrames.FramesLock)
                {
                    var pf = FrameHost.PlayerFrame;
                    if (pf == null) return "player not framed";
                    double tt = SystemHost.Now;
                    var st = SEAerospace.Orbital.OrbitPropagation.StateAt(pf.Elements, tt);
                    var v = st.Velocity + Vector3D.Normalize(st.Velocity) * D(a[1]);
                    pf.Elements = CaptureMath.CaptureElements(new SEAerospace.Orbital.StateVector(st.Position, v), pf.Elements.Mu, tt);
                    pf.VirtualVelocity = v;
                    return $"frame #{pf.Id} +{a[1]} m/s prograde";
                }
            }

            case "devbtn":
                // devbtn left down|up|off | devbtn right click|off
                if (a[1] == "left") MapInput.DevLeft = a[2] == "off" ? (bool?)null : a[2] == "down";
                else if (a[2] == "click") MapInput.DevRightClick();
                else MapInput.DevRight = null;
                return $"devbtn {a[1]} {a[2]}";

            case "pickat":
                // pickat <fx> <fy> | pickat off: a mouse position for the map pick (screen fractions)
                if (a[1] == "off") { GameMap.DevMouse = null; return "pickat off"; }
                GameMap.DevMouse = new Vector2((float)D(a[1]), (float)D(a[2]));
                return $"pickat {a[1]},{a[2]}";

            case "mapclick":
                GameMap.DevClick = true;
                return "mapclick queued";

            case "devfar":
                EncounterFrames.RequestDevFar(a.Length > 1 ? (long)D(a[1]) : 0);
                return "devfar queued";

            case "devsite":
                // devsite <gridId> <sector name...>: make a test site of a grid at that sector's centre (0 = pick one).
                EncounterFrames.RequestDevSite((long)D(a[1]), string.Join(" ", a, 2, a.Length - 2));
                return "devsite queued";

            case "lagstop":
            {
                // lagstop: a node where the path comes closest to the Lagrange point, retrograde by your drift (you park).
                var lp = Maneuvers.Lag;
                if (lp == null || double.IsNaN(lp.BestT)) return "no Lagrange plan";
                // Circularise round the point: sideways at w r (the sense you already turn), as a KSP circularise.
                Vector3D side = Vector3D.Cross(lp.Axis, lp.BestX);
                if (side.LengthSquared() < 1e-9) return "at the point";
                side = Vector3D.Normalize(side) * SectorHomes.LagrangeCircleSpeed(lp.BestD, lp.W, lp.Core);
                if (Vector3D.Dot(side, lp.BestV) < 0) side = -side;
                if (lp.BestD <= lp.Core) side = -Vector3D.Cross(lp.Omega, lp.BestX);   // in the calm core: at rest (no drift at all)
                Vector3D dv = side - lp.BestV;
                Maneuvers.Axes(new SEAerospace.Orbital.StateVector(lp.BestX, lp.BestV), out var P, out var N, out var R);
                Maneuvers.Restore(lp.BestT, Vector3D.Dot(dv, P), Vector3D.Dot(dv, N), Vector3D.Dot(dv, R));
                Maneuvers.Selected = null;
                return $"node at {(lp.BestT - SystemHost.Now) / 3600:F1} h: {lp.BestD / 1000:F0} km from {lp.Site.Sector}'s point, dv {dv.Length():F1} m/s (drift {lp.BestU:F1} -> {side.Length():F1} round the point)";
            }

            case "lagtest":
            {
                // lagtest <sector> <alongKm> <radialKm> <vAlong> <vRadial>: put the player this far from a Lagrange
                // sector's point (along its orbit / out from its star), moving this fast relative to it (m/s).
                var reg = SystemHost.Registry;
                EncounterFrames.Site site = null;
                foreach (var ls in EncounterFrames.LagrangeSites()) if (ls.Sector.Equals(a[1], StringComparison.OrdinalIgnoreCase)) site = ls;
                if (site == null) return "no such Lagrange sector";
                double t = SystemHost.Now;
                SectorHomes.LagrangeState(site.Home, reg, t, out var lp, out var lv);
                SectorHomes.Where(site.Home, reg, t, out var centre);
                var host = reg.Find(site.Home.Host);
                var hs = host.StateInParentAt(t);
                Vector3D eR = Vector3D.Normalize(lp - centre), nH = Vector3D.Normalize(Vector3D.Cross(hs.Position, hs.Velocity)), eA = Vector3D.Cross(nH, eR);
                // Along the orbit (round the star), not along a straight tangent: that tilts the velocity.
                double phi = D(a[2]) * 1000 / (lp - centre).Length();
                Vector3D eR2 = Maneuvers.Turn(eR, nH, phi), eA2 = Maneuvers.Turn(eA, nH, phi);
                Vector3D pos = centre + Maneuvers.Turn(lp - centre, nH, phi) + eR2 * (D(a[3]) * 1000);
                Vector3D vel = Maneuvers.Turn(lv, nH, phi) + eA2 * D(a[4]) + eR2 * D(a[5]);
                var b = reg.Root.DeepestSoiContaining(pos, t) ?? reg.Root;
                var o0 = b.OriginInRoot(t);
                var rel = new SEAerospace.Orbital.StateVector(pos - o0.Position, vel - o0.Velocity);
                var el = CaptureMath.CaptureElements(rel, b.Mu, t);
                double halfLen = (lp - centre).Length() * Math.Sin(2 * Math.PI * 0.035);
                lock (ServerFrames.FramesLock)
                {
                    var pf = SystemHost.Frames.FindByMember(FrameHost.PlayerId);
                    if (pf != null && !pf.IsEncounter) { pf.Elements = el; pf.ParentBodyName = b.Name; pf.VirtualVelocity = rel.Velocity; }
                    else FrameHost.SetPendingOrbit(b.Name, el);
                }
                return $"placed near {site.Sector} L{site.Home.Point} about {b.Name}: region half-length {halfLen / 1000:F0} km, half-width {0.035 * (lp - centre).Length() / 1000:F0} km; inside={SectorHomes.InLagrangeRegion(site.Home, reg, t, pos)}; |lp|={lp.Length()/1000:F0} km |lv|={lv.Length():F1} vcirc={Math.Sqrt(b.Mu/(pos-o0.Position).Length()):F1} host |v|={hs.Velocity.Length():F1} r={hs.Position.Length()/1000:F0} km; Pe {el.PeriapsisRadius/1000:F0} Ap {el.ApoapsisRadius/1000:F0} km";
            }

            case "gotosite":
            {
                // gotosite <i> [behindKm]: put the player on the i-th encounter frame's orbit, this far behind it.
                int idx = a.Length > 1 ? (int)D(a[1]) : 0;
                double behind = a.Length > 2 ? D(a[2]) * 1000 : 6000;
                lock (ServerFrames.FramesLock)
                {
                    var f = EncounterFrames.Nth(idx);
                    if (f == null) return "no such encounter frame";
                    double t = SystemHost.Now;
                    var st = SEAerospace.Orbital.OrbitPropagation.StateAt(f.Elements, t);
                    double dtb = behind / Math.Max(1, st.Velocity.Length());
                    var sb = SEAerospace.Orbital.OrbitPropagation.StateAt(f.Elements, t - dtb);
                    var el = CaptureMath.CaptureElements(sb, f.Elements.Mu, t);
                    var pf = SystemHost.Frames.FindByMember(FrameHost.PlayerId);
                    if (pf != null && !pf.IsEncounter) { pf.Elements = el; pf.ParentBodyName = f.ParentBodyName; pf.VirtualVelocity = sb.Velocity; return $"player frame #{pf.Id} -> {behind / 1000:F1} km behind encounter #{f.Id}"; }
                    FrameHost.SetPendingOrbit(f.ParentBodyName, el);
                    return $"stowing {behind / 1000:F1} km behind encounter #{f.Id} about {f.ParentBodyName}";
                }
            }

            case "sectororbits":
                MapView.SectorOrbits = On(a[1]);
                return "sectororbits=" + MapView.SectorOrbits;

            case "sectorvis":   // sectorvis on|off: the map's sector renderer (our mesh) shown or not
            {
                var mp = MapView.Map(session);
                if (mp == null) return "no map";
                PlanetRenderBridge.SetRenderComponentVisible(PlanetRenderBridge.GetMember(mp, "SectorsRenderer"), On(a[1]));
                return "sector renderer visible=" + On(a[1]);
            }

            case "hiresglobe":   // hiresglobe on|off | status | face <i> <tex> <flipU> <flipV> <rot> | clouds on|off | wind ccw|cw | cloudsuv <u> <v>
            {
                string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
                switch (sub)
                {
                    case "on": case "off":
                        PlanetMesh.SetEnabled(sub == "on");
                        return $"hi-res globe {(PlanetMesh.Enabled ? "ON" : "OFF")} (proxies swap on their next update; gen {PlanetMesh.Generation})";
                    case "status":
                        return PlanetMesh.Status();
                    case "face":
                        if (a.Length < 7) return "hiresglobe face <i 0-5> <texIndex 0-5> <flipU 0|1> <flipV 0|1> <rot 0-3>";
                        return PlanetMesh.SetFace(int.Parse(a[2]), int.Parse(a[3]), On(a[4]), On(a[5]), int.Parse(a[6]));
                    case "clouds":
                        PlanetMesh.SetClouds(On(a[2]));
                        return "hi-res globe clouds=" + PlanetMesh.Clouds;
                    case "wind":
                        PlanetMesh.SetWinding(!a[2].Equals("cw", StringComparison.OrdinalIgnoreCase));
                        return "hi-res globe front faces " + (PlanetMesh.CcwOutside ? "counter-clockwise" : "clockwise") + " from outside";
                    case "cloudsuv":
                        PlanetMesh.CloudUv = new Vector2((float)D(a[2]), (float)D(a[3]));
                        PlanetMesh.SetClouds(PlanetMesh.Clouds);   // rebuild
                        return $"cloud shell samples its displacement atlas at ({PlanetMesh.CloudUv.X:F3}, {PlanetMesh.CloudUv.Y:F3})";
                    default:
                        return "hiresglobe on|off|status|face|clouds|wind|cloudsuv";
                }
            }

            case "proxyring":   // proxyring on|off | status | hide on|off | optics on|off
            {
                string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
                switch (sub)
                {
                    case "on": case "off":
                        PlanetRings.Reset();
                        PlanetRings.ProxyRings = sub == "on";
                        return "proxy rings " + (PlanetRings.ProxyRings ? "ON" : "OFF");
                    case "hide":
                        PlanetRings.Reset();
                        PlanetRings.HideGameRings = On(a[2]);
                        return "hide the game's ring with its real planet=" + PlanetRings.HideGameRings;
                    case "optics":
                        PlanetRings.Reset();
                        PlanetRings.ScaleOptics = On(a[2]);
                        return "proxy ring optics scaled=" + PlanetRings.ScaleOptics;
                    case "status":
                        return PlanetRings.Status();
                    default:
                        return "proxyring on|off|status|hide on|off|optics on|off";
                }
            }

            case "hidesectors":
                GameMap.HideGameSectors = On(a[1]);
                return "hide game sector mesh=" + GameMap.HideGameSectors;

            case "perf":   // GPU / render / main thread ms (average/max since the last stats log)
                return PlanetRenderBridge.FrameStats(session) + $" | mapcam {MapCamera.Enabled}: {MapCamera.Status}";
            case "meshrebuild":
                CleanMap.MeshRebuildSeconds = D(a[1]);
                return $"sector mesh rebuild every {CleanMap.MeshRebuildSeconds} s";

            case "debugpanel":
                DebugPanel.DevOpen = true;
                return "debug panel toggled";

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

            case "mapring":   // mapring on|off | look <dim> [hue sat tint] | depth <v> | spec <a> [fresnel] | mesh on|off
            {
                string w = a.Length > 1 ? a[1] : "";
                if (w == "spec") { MapRingMesh.SpecA = float.Parse(a[2]); if (a.Length > 3) MapRingMesh.Fresnel = float.Parse(a[3]); MapRingMesh.Reset(); }
                else if (w == "look") { MapRingMesh.Dim = float.Parse(a[2]); if (a.Length > 3) MapRingMesh.Hue = float.Parse(a[3]); if (a.Length > 4) MapRingMesh.Sat = float.Parse(a[4]); if (a.Length > 5) MapRingMesh.Tint = float.Parse(a[5]); MapRingMesh.Reset(); }
                else if (w == "depth") { MapRingMesh.Depth = float.Parse(a[2]); MapRingMesh.Reset(); }
                else if (w == "mesh") { MapRingMesh.Enabled = On(a[2]); MapRingMesh.Reset(); }
                else if (w.Length > 0) PlanetRings.MapRings = On(w);
                PlanetRings.Reset();   // rebuilt on the next map frame
                return MapRingMesh.Status + $" | map rings {PlanetRings.MapRings} look dim {MapRingMesh.Dim} hue {MapRingMesh.Hue} sat {MapRingMesh.Sat}";
            }

            case "ringrocks":   // ringrocks [n <perRing>] : the rings' seeded rocks and the passes on your plan
            {
                if (a.Length > 2 && a[1] == "n") RingRocks.PerRing = int.Parse(a[2]);
                RingRocks.Belts();
                var sb = new System.Text.StringBuilder(RingRocks.Status);
                double tn = SystemHost.Now;
                if (Maneuvers.Trajectory(tn, out var legs, out _))
                {
                    var ps = RingRocks.Passes(legs, tn);
                    sb.Append($" | {ps.Count} pass(es) on the plan");
                    foreach (var q in ps.Take(10)) sb.Append($" | {q.Label} {q.D / 1000:F1} km in {Maneuvers.Clock(q.T - tn)} {q.V:F0} m/s");
                }
                return sb.ToString();
            }

            case "rvtab":   // rvtab on|off: the map's Rendezvous tab (as a click on it)
                if (On(a[1])) RendezvousView.Open(session); else RendezvousView.Close();
                return "rendezvous tab " + (RendezvousView.Active ? "open" : "closed") + " | " + RendezvousView.TabStatus + " | top " + (GameUi.TopScreenObject(session)?.GetType().FullName ?? "none") + " | " + RendezvousView.Status;

            case "gridlaunch":   // gridlaunch <gridId> <altKm> (DEV: a circular orbit over the planet it is at, joined group and all)
                ServerFrames.GridLaunch.Enqueue((long.Parse(a[1]), D(a[2])));
                return "grid launch queued (server, next tick)";

            case "griddamp":   // griddamp <gridId> on|off (DEV: the grid's dampeners, as the game toggles them)
                ServerFrames.GridDamp.Enqueue((long.Parse(a[1]), On(a[2])));
                return "grid dampeners queued (server, next tick)";

            case "gridmove":   // gridmove <gridId> <x> <y> <z> [group] (m, world; DEV: refuses jointed grids unless 'group' moves all joined together)
                ServerFrames.GridMove.Enqueue((long.Parse(a[1]), new Vector3D(D(a[2]), D(a[3]), D(a[4])), a.Length > 5 && a[5] == "group"));
                return "grid move queued (server, next tick)";

            case "npcrel":   // npcrel on|off (DEV: NPC grids feel relative motion, for tests)
                ServerFrames.DevNpcRelative = On(a[1]);
                return "npc relative motion " + ServerFrames.DevNpcRelative;

            case "selrock":   // selrock <rock label> | selrock off (as a click on its map marker)
                CleanMap.SelectedRock = a[1] == "off" ? null : string.Join(" ", a, 1, a.Length - 1);
                return "selected rock " + (CleanMap.SelectedRock ?? "none");

            case "target":   // target <sector name> | target off
                Maneuvers.Target = a[1] == "off" ? null : string.Join(" ", a, 1, a.Length - 1);
                return "target " + (Maneuvers.Target ?? "none");

            case "warphere":   // warphere <minFromNow>: as the map menu's Warp here
                SystemHost.WarpStopAt = SystemHost.Now + D(a[1]) * 60;
                WarpControl.SetLevel(WarpControl.Levels.Length - 1);
                return $"warp to t={SystemHost.WarpStopAt:F0} (now {SystemHost.Now:F0}), x{SystemHost.Timescale}";

            case "dblclick":   // dblclick <x> <y>: screen fractions
                GameMap.DevMouse = new Vector2((float)D(a[1]), (float)D(a[2]));
                MapInput.DevDoubleClick();
                return "double-click queued";

            case "focus":
                return CleanMap.DevFocus(a[1]);

            case "mapcamon":
                MapCamera.Enabled = On(a[1]);
                return "map camera " + MapCamera.Enabled;

            case "starscale":   // starscale <n>: Delfos on the map n times its model's size
                if (a.Length > 1) GameMap.StarScale = Math.Max(1, D(a[1]));
                return "star scale " + GameMap.StarScale;

            case "mapcam":
                return MapCamera.Dev(a);

            case "clean":
                if (a[1].Equals("focus", StringComparison.OrdinalIgnoreCase)) { CleanMap.Focus = a[2]; return "clean focus=" + a[2]; }
                CleanMap.Enabled = On(a[1]);
                return "clean map=" + CleanMap.Enabled;

            case "unified":
                GameMap.Enabled = On(a[1]);
                return "unified map=" + GameMap.Enabled;

            case "mapview":
                MapView.Mode = (MapView.ViewMode)Enum.Parse(typeof(MapView.ViewMode), a[1], ignoreCase: true);
                return $"mapview={MapView.Mode}";

            case "contacts":   // contacts [reveal|forget|on|off]
                return Contacts.Command(a);

            case "stores":   // stores: every battery / tank on a railed grid
                return WarpBill.Describe(ServerPlanetBeacon.ServerSession ?? session);

            case "warpbill":   // warpbill on|off: warp ages the railed grids' batteries and tanks
                WarpBill.Enabled = On(a[1]);
                return "warp bill " + WarpBill.Enabled + " | " + WarpBill.Status;

            case "bodymarkers":   // bodymarkers on|off: planet and moon markers in flight
                BodyMarkers.Enabled = On(a[1]);
                return "body markers " + BodyMarkers.Enabled + " | " + BodyMarkers.Status;

            case "curvecull":   // curvecull on|off: skip map curve pieces wholly off the view
                CleanMap.CullCurves = On(a[1]);
                return "curve cull " + CleanMap.CullCurves;

            case "shotui":
                PlanetRenderBridge.ShotWithoutUi = !On(a[1]);
                return $"screenshots with UI={On(a[1])}";

            case "kick":
                // kick <prograde m/s> [radial] [normal]  (HighSpeed only)
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
        foreach (string tag in new[] { "ColonizationMapPlanetVisual", "_ColonizationMap", "ColonizationMap", "MapVisual" })
        {
            int cut = file.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (cut > 0) { file = file.Substring(0, cut); break; }
        }
        return file.TrimEnd('_', '.', ' ');
    }

    private static double PlanetRadius(PlanetBeacon p) => p.Gravity.R0 > 0 ? p.Gravity.R0 : 0;

    private static Keen.VRage.Core.Game.Systems.Session _statusSession;
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
