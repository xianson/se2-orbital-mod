using Keen.VRage.Core;

#pragma warning disable
namespace OrbitalMod;

// DevHarness (partial): the harness commands for server grids: find, inspect, move, spin, hold, cut, clone, launch, level, seats and piloting.
// (Execute's switch, by theme: a verb not here falls through to the next group - DevHarness.cs has the order.)
// THREADS: the harness runs on the client's frame job. A command that enumerates the server's entities, reads their components
// or writes them runs on the server thread (GridGen.OnServerThread, waited for) - 2026-10-15: `hudcheck`'s seat search from
// here asserted 'Scene.IsCurrentThreadDCS()'; a hinge's SetVelocity from here once crashed the game. Positions and velocities
// alone (GridMembers.Position / Velocity, as the rest of the client side reads them) stay here.
public static partial class DevHarness
{
    private static string ExecuteGrids(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line, string[] a, string verb)
    {
        switch (verb)
        {
            case "testship":   // testship [charge 0..1]: a test ship (battery, telescope, radar) 60 m ahead, yours
                return DevTestShip.Spawn(session, camera, a.Length > 1 ? D(a[1]) : 1.0);

            case "blockhealth":   // blockhealth: every server grid with a block under full health
                return GridGen.OnServerThread(() =>
                {
                    var sb2 = new System.Text.StringBuilder();
                    foreach (var g in GridMembers.All())
                        if (g.IsServer && EntryDamage.Damaged(g)) sb2.Append($"{g.Id} '{g.DisplayName}': {EntryDamage.Health(g)}; ");
                    return sb2.Length > 0 ? sb2.ToString() : "every grid at full health";
                });

            case "gridspin":   // gridspin <id> <wx> <wy> <wz>: set a grid's angular velocity (rad/s, world); gridspin <id> yaw|pitch|roll <rad/s>: about its own axis
                return GridGen.OnServerThread(() =>   // (a write into the server grid's rigid body)
                {
                    if (a.Length > 3 && (a[2] == "yaw" || a[2] == "pitch" || a[2] == "roll"))
                    {
                        var sg = GridMembers.Get((long)D(a[1]));
                        if (sg?.Entity == null) return "no grid " + a[1];
                        var sq = sg.Entity.Data.GetWorldTransform().Orientation;
                        Vector3 ax = a[2] == "yaw" ? Vector3.Up : a[2] == "pitch" ? Vector3.Right : Vector3.Forward;   // (its own up, right, nose)
                        return DevStress.Spin((long)D(a[1]), Vector3.Transform(ax, sq) * (float)D(a[3]));
                    }
                    return DevStress.Spin((long)D(a[1]), new Vector3((float)D(a[2]), (float)D(a[3]), (float)D(a[4])));
                });

            case "flightdump":   // flightdump <id> <name>: the grid as the aero mod's thruster steering sees it (AeroMod.FlightFixture),
            {                    // one line, ' ;; ' between fixture lines (tools: Tests/data/<name>.flight from the log)
                var gi = GridMembers.Get((long)D(a[1]));
                if (gi?.Entity == null || !gi.IsServer) return "no server grid " + a[1];
                Type ft = null;
                ft = AeroLink.Find("AeroMod.FlightFixture");
                if (ft == null) return "aero mod's FlightFixture not found";
                var text = GridGen.OnServerThread(() => ft.GetMethod("Build", new[] { typeof(Entity), typeof(string) })?.Invoke(null, new object[] { gi.Entity, a.Length > 2 ? a[2] : "grid" }) as string);
                if (text != null && (text.StartsWith("not run:") || text.StartsWith("timed out"))) return text;
                return text == null ? "no aero component on " + a[1] : "FIXTURE " + text.TrimEnd('\n').Replace("\n", " ;; ") + " ;; END";
            }

            case "reentrytest":   // reentrytest <id>: arm the top-to-bottom reentry check on a grid (DevReentry); alone: the report
                return a.Length > 1 ? DevReentry.Arm((long)D(a[1])) : DevReentry.Report();

            case "gridinfo":   // gridinfo <id>: where a grid is (nearest planet, height), its frame, motion and aero
                return GridGen.OnServerThread(() =>
            {
                var gi = GridMembers.Get((long)D(a[1]));
                if (gi?.Entity == null) return "no grid " + a[1];
                Vector3D gp = GridMembers.Position(gi), gv = GridMembers.Velocity(gi);
                var sb = new System.Text.StringBuilder($"grid {gi.Id} '{gi.DisplayName}' {(gi.IsServer ? "server" : "client")} pos ({gp.X / 1000:F1}, {gp.Y / 1000:F1}, {gp.Z / 1000:F1}) km |v| {gv.Length():F1} m/s dyn {GridMembers.IsDynamic(gi)}");
                PlanetBeacon nb = null; double nd = double.MaxValue;
                foreach (var pb in Planets()) { double dd = (pb.Center - gp).Length(); if (dd < nd) { nd = dd; nb = pb; } }
                if (nb != null)
                {
                    string bn = SystemHost.BodyNameOf(nb);
                    double rad = SystemHost.Registry?.FindDefinition(bn)?.RadiusMeters ?? 0;
                    sb.Append($" | nearest {bn ?? PlanetName(nb)}: {nd / 1000:F1} km from centre, {(nd - rad) / 1000:F1} km above its radius {rad / 1000:F1} km");
                }
                SEAerospace.Frames.ProximityFrame gf; lock (ServerFrames.FramesLock) gf = SystemHost.Frames?.FindByMember(gi.Id);
                sb.Append(gf == null ? " | no frame" : $" | frame #{gf.Id} about {gf.ParentBodyName}, anchor {gf.AnchorEntityId}{(gf.AnchorEntityId == gi.Id ? " (this)" : "")}, {gf.Members.Count} members, a {gf.Elements.SemiMajorAxis / 1000:F1} km e {gf.Elements.Eccentricity:F3}");
                try { var we = gi.Entity.Data.Get<Keen.VRage.Physics.Data.RigidBodyData>(); sb.Append($" | w {((Vector3D)we.AngularVelocity).Length():F3} rad/s"); } catch { }
                sb.Append(" | aero: " + GridGen.AeroReadout(gi.Entity));
                return sb.ToString();
            });

            case "gridatt":   // gridatt <id>: a grid's orientation and spin
                return GridGen.OnServerThread(() => DevStress.Attitude((long)D(a[1])));

            case "gridhold":   // gridhold <id> <m/s>: keep a grid moving forward at that speed (0 releases)
                return DevStress.Hold((long)D(a[1]), D(a[2]));

            case "gridvel":   // gridvel <id> <m/s>: straight down along the local gravity, hold released (a crash)
                return DevStress.Dive((long)D(a[1]), D(a[2]));

            case "cutgrid":   // cutgrid <id> <thickness m>: a slab through the grid's middle, in one tick (it splits)
                return DevStress.Cut((long)D(a[1]), D(a[2]));

            case "gridclone":   // gridclone <id> <count> <spacing m>: copies of a grid in rows of ten above it
                return DevStress.Clone((long)D(a[1]), (int)D(a[2]), D(a[3]));

            case "breakblocks":   // breakblocks <id> <n> [every s]: destroy n of its blocks, one every s (0: all at once)
                return DevStress.Break((long)D(a[1]), (int)D(a[2]), a.Length > 3 ? D(a[3]) : 0.1, a.Length > 4 ? D(a[4]) : 0);   // [radius m: a hit]

            case "bigs":   // bigs [n]: the n biggest server grids by block count (id, name, blocks, mass, km from you)
                return GridGen.OnServerThread(() =>
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
            });

            case "joins":   // joins <gridId>: what a grid is joined to (its connected group; static ones marked)
                return GridGen.OnServerThread(() =>
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
            });

            case "adopt":   // adopt <grid name>: every server grid of that name (and its blocks) becomes yours (tests on the world's ships)
            {
                string want = string.Join(" ", a, 1, a.Length - 1);
                var server = ServerPlanetBeacon.ServerSession;
                var own = server?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent>();
                var me = session.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().LocalPlayerIdentity;
                if (own == null) return "no ownership component";
                return GridGen.OnServerThread(() =>   // (ownership written on the server's entities)
                {
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
                });
            }

            case "gridnudge":   // gridnudge <id> <dx> <dy> <dz> (m): move a grid by an offset from where it is (world axes)
            {
                var ng = GridMembers.Get((long)D(a[1]));
                if (ng == null || !ng.IsServer) return "no server grid " + a[1];
                ServerFrames.GridMove.Enqueue((ng.Id, new Vector3D(D(a[2]), D(a[3]), D(a[4])), true));   // (GridMove takes the offset)
                return $"grid {ng.Id} nudged by ({D(a[2]):F0}, {D(a[3]):F0}, {D(a[4]):F0}) m (server, next tick)";
            }

            case "findgrids":   // findgrids [name...]: the server grids (id, name, dynamic) - all, or those with this name - in one call
            {
                string want = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : null;
                var sb2 = new System.Text.StringBuilder("grids:");
                foreach (var g in GridMembers.All())
                {
                    if (!g.IsServer) continue;
                    string nm = g.DisplayName;
                    if (want != null && nm != want) continue;
                    sb2.Append($" {g.Id}={nm.Replace(' ', '_')}{(GridMembers.IsDynamic(g) ? "" : "(static)")}");
                }
                return sb2.ToString();
            }

            case "exitcheck":   // exitcheck <gridId>: you against that ship - distance and relative speed (server and client), frames, warp
            {
                var eg = GridMembers.Get((long)D(a[1]));
                if (eg == null || !eg.IsServer) return "no server grid " + a[1];
                Vector3D gp = GridMembers.Position(eg), gv = GridMembers.Velocity(eg);
                string srv = ServerPlanetBeacon.PlayerState(out Vector3D sp, out Vector3D sv) ? $"server d {(sp - gp).Length():F1} m dv {(sv - gv).Length():F2} m/s" : "server -";
                string cli = FrameHost.FootState(out Vector3D cp, out Vector3D cv) ? $"client d {(cp - gp).Length():F1} m dv {(cv - gv).Length():F2} m/s" : $"client (seated) d {(FrameHost.PlayerPosition - gp).Length():F1} m";
                SEAerospace.Frames.ProximityFrame pf2, gf2; lock (ServerFrames.FramesLock) { pf2 = SystemHost.Frames?.FindByMember(FrameHost.PlayerId); gf2 = SystemHost.Frames?.FindByMember(eg.Id); }
                return $"exitcheck seated {FrameHost.Seated} | {srv} | {cli} | you #{pf2?.Id.ToString() ?? "none"} (hud #{FrameHost.PlayerFrame?.Id.ToString() ?? "none"}) ship #{gf2?.Id.ToString() ?? "none"} | warp x{SystemHost.Timescale:F0} | ship |v| {gv.Length():F1} | rider frame {FrameHost.RiderFrame} offset {FrameHost.RiderOffset.Length():F1} m rigid {FrameHost.AnchorRigid} seat grid {FrameHost.SeatGridServerId} '{FrameHost.SeatedWhy}' | graph hidden {OrbitHud.EntryHidden}";
            }

            case "sunrel":   // sunrel <gridId>: the light in that grid's own axes (does it jump at a crossing?) and the planets' spin clock
            {
                var sg = GridMembers.Get(long.Parse(a[1]));
                if (sg?.Entity == null) return "no such grid";
                var swt = sg.Entity.Data.GetWorldTransform();
                Vector3D lc = WorldTransform.TransformDirectionInv(SunDriver.Light, swt);
                var vb = SystemHost.Registry?.Find("Verdure");
                return $"sunrel light local ({lc.X:F4},{lc.Y:F4},{lc.Z:F4}) frame {(FrameHost.PlayerFrame == null ? "planet" : "#" + FrameHost.PlayerFrame.Id)} | spin {SystemHost.SpinNow:F1} now {SystemHost.Now:F1} theta {(vb != null ? vb.RotationAngleAt(SystemHost.Now) * 180 / Math.PI : double.NaN):F3} | {SunDriver.Status}";
            }

            case "skcharge":   // skcharge <gridId>: holding station's charge - its thrusters' asks, held or not, and the suit's bill
            {
                var kg = GridMembers.Get((long)D(a[1]));
                if (kg == null || !kg.IsServer) return "no server grid " + a[1];
                bool held; lock (ServerFrames.FramesLock) held = ServerFrames.Holding.ContainsKey(kg.Id);
                string kd = GridGen.OnServerThread(() => StationKeepCharge.Describe(kg.Entity));
                return $"skcharge {kg.Id}: {(held ? "holding" : "free")}, {kd} | last: {StationKeepCharge.Status} | {ServerPlanetBeacon.SuitBill} | holdingStation(you) {FrameHost.HoldingStation} card {(OrbitHud.Current != null ? "shown" : "none")}";
            }

            case "griddel":   // griddel <id>: close a grid on the server (DEV: its frame elects another anchor)
                ServerFrames.GridDelete.Enqueue((long)D(a[1]));
                return "grid delete queued (server, next tick)";

            case "frameof":   // frameof <id>: the frame a grid (or the player: 'player') is a member of
            {
                long fid = a[1] == "player" ? FrameHost.PlayerId : (long)D(a[1]);
                SEAerospace.Frames.ProximityFrame ff; lock (ServerFrames.FramesLock) ff = SystemHost.Frames?.FindByMember(fid);
                return ff == null ? $"frameof {a[1]}: none" : $"frameof {a[1]}: #{ff.Id} anchor {ff.AnchorEntityId} members {ff.Members.Count}";
            }

            case "celmark":   // celmark <id>: remember where a grid is in space (its frame's state + its offset), and its orbit
            case "celcheck":  // celcheck <id>: how far it is now from where that orbit has it (a jump of the frame shows here)
            {
                long cid = (long)D(a[1]);
                var cg = GridMembers.Get(cid);
                if (cg == null || !cg.IsServer) return "no server grid " + cid;
                SEAerospace.Frames.ProximityFrame cf; lock (ServerFrames.FramesLock) cf = SystemHost.Frames?.FindByMember(cid);
                if (cf == null) return $"grid {cid}: in no frame";
                double ct = SystemHost.Now, cn = Math.Max(1.0, SystemHost.Timescale);
                SEAerospace.Orbital.StateVector fc; Vector3D off; lock (ServerFrames.FramesLock) { fc = SEAerospace.Orbital.OrbitPropagation.StateAt(cf.Elements, ct); off = GridMembers.Position(cg) - cf.BerthCenter; }
                var cs = new SEAerospace.Orbital.StateVector(fc.Position + off, fc.Velocity + GridMembers.Velocity(cg) / cn);
                var cb = SystemHost.Registry?.Find(cf.ParentBodyName);
                if (cb == null) return "no body";
                if (a[0] == "celmark")
                {
                    _celMark[cid] = (cf.ParentBodyName, SEAerospace.Orbital.OrbitalMath.ToElements(cs, cb.Mu, ct));
                    return $"celmark {cid}: frame #{cf.Id} about {cf.ParentBodyName} at r={cs.Position.Length() / 1000:F3} km";
                }
                if (!_celMark.TryGetValue(cid, out var mk)) return "celcheck: no mark";
                if (mk.body != cf.ParentBodyName) return $"celcheck {cid}: about {cf.ParentBodyName} now, marked about {mk.body}";
                var exp = SEAerospace.Orbital.OrbitPropagation.StateAt(mk.el, ct);
                return $"celcheck {cid}: {(exp.Position - cs.Position).Length():F1} m from its orbit (frame #{cf.Id}, anchor {cf.AnchorEntityId})";
            }

            case "seatid":   // seatid <gridId>: the character into that grid's cockpit
                return DevFlight.SeatId(session, (long)D(a[1])) + " | " + DevFlight.Status;

            case "unseat":   // unseat: the character out of its seat
                return DevFlight.Unseat(session);

            case "seat":
                // seat <grid name>: the local character into that grid's nearest cockpit
                return DevFlight.Seat(session, a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : null) + " | " + DevFlight.Status;

            case "burn":
                return DevFlight.Burn(a.Length > 1 ? D(a[1]) : 60);

            case "hold":   // hold off|prograde|retrograde|normal|antinormal|radialout|radialin: the attitude hold; alone: its status
                if (a.Length > 1 && Enum.TryParse<AttitudeHold.Mode>(a[1], true, out var hm)) AttitudeHold.Set(hm);
                return AttitudeHold.Status;

            case "turntest":
                // turntest DEG x|y|z (seated): turn the cockpit's target by DEG about the grid's axis, measure the turn; alone: the last result
                return a.Length > 2 ? DevTurn.Start(session, D(a[1]), a[2]) : DevTurn.Status;

            case "seats":
                return DevFlight.ListSeats(session, FrameHost.PlayerPosition);

            case "thrust":
                // thrust <x> <y> <z> <seconds>: hold a pilot command on the seated grid (x right, y up, z back)
                return DevFlight.Thrust(new Vector3((float)D(a[1]), (float)D(a[2]), (float)D(a[3])), D(a[4]));

            case "flight":
                return $"seated={FrameHost.Seated} frame={(FrameHost.PlayerFrame != null ? "#" + FrameHost.PlayerFrame.Id : "-")} {DevFlight.Status} | auto: {AutoBurn.Status} | {DevFlight.Attitude} | {GameUi.SpeedStatus} | top input: {GameUi.TopInputScreen} | {DevFlight.Info}";

            case "floaters":
                return GridGen.OnServerThread(() =>
            {
                // floaters: every floating object (server) and its distance from each planet centre
                var srv = ServerPlanetBeacon.ServerSession;
                if (srv == null) return "no server session";
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
            });

            case "freegrids":
                ServerFrames.DevFreeGrids = true;
                return "free grid census queued (see log)";

            case "gen":   // gen spawn FILE | status | clear | measure FILE | report  (generated grids: GridGen.cs)
                return GridGen.Command(session, camera, a);

            case "hinge":   // hinge list | set N force|tau|damp|limits V | hold N DEG [GAIN] | free N | stats N
                return HingeRig.Command(a);

            case "gridlaunch":   // gridlaunch <gridId> <altKm> [planet] [speed m/s] (DEV: over that planet - or the one it is at - joined group and all; no speed: a circular orbit)
            {
                string lb = null;
                if (a.Length > 3) { var lp = FindPlanet(a[3]); lb = lp != null ? SystemHost.BodyNameOf(lp) : null; if (lb == null) return "no such planet"; }
                ServerFrames.GridLaunch.Enqueue((long.Parse(a[1]), D(a[2]), lb, a.Length > 4 ? D(a[4]) : double.NaN));
                return "grid launch queued (server, next tick)";
            }

            case "gridlevel":   // gridlevel <gridId>: level it - top away from the planet, nose horizontal - its joined group too (DEV)
                ServerFrames.GridLevel.Enqueue(long.Parse(a[1]));
                return "grid level queued (server, next tick)";

            case "griddamp":   // griddamp <gridId> on|off (DEV: the grid's dampeners, as the game toggles them)
                ServerFrames.GridDamp.Enqueue((long.Parse(a[1]), On(a[2])));
                return "grid dampeners queued (server, next tick)";

            case "gridup":   // gridup <gridId> <m>: move a grid straight up (against its gravity, from the aero mod) by m
            {
                var g = GridMembers.Get((long)D(a[1]));
                if (g?.Entity == null) return "no grid";
                Type ph = null;
                ph = AeroLink.Find("AeroMod.PhysicsHack");
                var gm = ph?.GetMethod("GetGravityDirection", new[] { typeof(Keen.VRage.DCS.Accessors.DEntityContext) });
                if (gm == null) return "no AeroMod.PhysicsHack.GetGravityDirection";
                Vector3 down = default;   // (the server grid's gravity data: read there)
                string gr = GridGen.OnServerThread(() => { down = (Vector3)gm.Invoke(null, new object[] { g.Entity.Data }); return "ok"; });
                if (gr != "ok") return gr;
                if (down.LengthSquared() < 0.25f) return "no gravity here";
                var d = -(Vector3D)Vector3.Normalize(down) * D(a[2]);
                ServerFrames.GridMove.Enqueue((g.Id, d, false));
                return $"grid {g.Id} up by {D(a[2])} m (gravity {down})";
            }

            case "gridmove":   // gridmove <gridId> <dx> <dy> <dz> [group] (m: BY this offset, world axes; DEV: refuses jointed grids unless 'group' moves all joined together)
                ServerFrames.GridMove.Enqueue((long.Parse(a[1]), new Vector3D(D(a[2]), D(a[3]), D(a[4])), a.Length > 5 && a[5] == "group"));
                return "grid move queued (server, next tick)";

            case "npcrel":   // npcrel on|off (DEV: NPC grids feel relative motion, for tests)
                ServerFrames.DevNpcRelative = On(a[1]);
                return "npc relative motion " + ServerFrames.DevNpcRelative;

            case "stores":   // stores: every battery / tank on a railed grid
                return ServerPlanetBeacon.ServerSession is { } ws ? GridGen.OnServerThread(() => WarpBill.Describe(ws)) : WarpBill.Describe(session);

            case "nearcenter":   // nearcenter: every grid within a planet radius of any planet's centre (the game's watchdog)
            {
                var sb3 = new System.Text.StringBuilder();
                foreach (var kv in SystemHost.BeaconOf)
                {
                    var pdef = SystemHost.Registry?.FindDefinition(kv.Key);
                    double R = pdef?.RadiusMeters ?? 0;
                    foreach (var g in GridMembers.All())
                    {
                        if (!g.IsServer) continue;
                        double d = (GridMembers.Position(g) - kv.Value.Center).Length();
                        if (d < Math.Max(R * 0.5, 1000)) sb3.Append($"[{kv.Key}: grid {g.Id} '{g.DisplayName}' {d / 1000:F1} km from centre (R {R / 1000:F0} km)] ");
                    }
                }
                // (and the player's character: a stowed player's body has to be somewhere)
                try
                {
                    var ch = FrameHost.PlayerCharacter(session);
                    if (ch != null)
                    {
                        var cp = ch.Data.GetWorldTransform().Position;
                        foreach (var kv in SystemHost.BeaconOf)
                            sb3.Append($"[character {(cp - kv.Value.Center).Length() / 1000:F1} km from {kv.Key}'s centre] ");
                    }
                }
                catch { }
                return sb3.Length > 0 ? sb3.ToString() : "no grid within half a planet radius of any centre";
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

            case "gridsstop":
                ServerFrames.StopAllGrids = true;
                return "stopping all dynamic grids (server, next tick)";

            default:
                return ExecuteMap(session, camera, line, a, verb);
        }
    }
}
