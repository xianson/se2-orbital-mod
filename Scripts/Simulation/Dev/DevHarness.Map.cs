using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// DevHarness (partial): the harness commands for the map, its UI and its picks, targets and contacts, the proxies' look, sites, rocks, maneuver nodes.
// (Execute's switch, by theme: a verb not here falls through to the next group - DevHarness.cs has the order.)
public static partial class DevHarness
{
    private static string ExecuteMap(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line, string[] a, string verb)
    {
        switch (verb)
        {
            case "map":
            {
                if (On(a[1])) MapView.Open(session); else MapView.Close();
                return On(a[1]) ? "opening map (terminal Map tab)" : "closing map";
            }

            case "legacy":
                OrbitalConfig.CaptureLegacySpace = On(a[1]);
                return $"captureLegacySpace={OrbitalConfig.CaptureLegacySpace}";

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

            case "noderound":   // noderound: the nodes written as a save writes them, cleared, read back as a load reads them
            {
                string Sig() { var l = new List<string>(); lock (Maneuvers.Nodes) foreach (var n in Maneuvers.Nodes) l.Add($"{n.T:F3}/{n.Pro:F3}/{n.Nor:F3}/{n.Rad:F3}/{(n.Auto ? "auto" : "manual")}/{(n.TBody != null && !n.Dirty ? "target" : "-")}"); l.Sort(); return string.Join(" ", l); }
                string before = Sig();
                var lines = Maneuvers.SaveLines();
                lock (Maneuvers.Nodes) Maneuvers.Nodes.Clear();
                Maneuvers.Selected = null;
                foreach (var ln in lines) SavedState.RestoreNode(ln.Split(' '));
                string after = Sig();
                return $"noderound {(before == after ? "SAME" : "DIFFERENT")} | before {before} | after {after} | {lines.Count} line(s)";
            }

            case "sites":   // sites: every sector site (sector@host#frame), ';'-separated | sites homes: every sector's home (sector@host:kind)
            {
                var l = new List<string>();
                if (a.Length > 1 && a[1] == "homes")
                {
                    var sectors = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Colonization.SectorsSessionComponent>();
                    if (sectors == null) return "no sectors";
                    foreach (var kv in GameMap.HomesBySector(sectors, SystemHost.Registry)) l.Add($"{kv.Key}@{kv.Value.Host}:{kv.Value.Kind}");
                    return "homes " + l.Count + ": " + string.Join(";", l);
                }
                foreach (var st in EncounterFrames.Sites) l.Add($"{st.Sector}@{st.Host}#{st.FrameId}");
                return "sites " + l.Count + ": " + string.Join(";", l);
            }

            case "encounters":
            {
                // (its census reads the server grids' places and kinds: on the server thread, once - 2026-10-15 scene audit)
                return GridGen.Then(GridGen.OnServerThread(EncounterFrames.Describe), ed =>
                {
                    Log.Default?.Info("[ORBIT-DEV] " + ed);
                    return ed.Replace((char)10, '|');
                });
            }

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

            case "devtarget":   // devtarget <name> <body> <altKm> <aheadKm>: a target site on its own circular orbit (no sector needed)
                EncounterFrames.RequestDevTarget(a[1], a[2], D(a[3]), D(a[4]));
                return "devtarget queued";

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

            case "sectorvis":   // sectorvis on|off: the map's sector renderer (our mesh) shown or not
            {
                var mp = MapView.Map(session);
                if (mp == null) return "no map";
                PlanetRenderBridge.SetRenderComponentVisible(PlanetRenderBridge.GetMember(mp, "SectorsRenderer"), On(a[1]));
                return "sector renderer visible=" + On(a[1]);
            }

            case "hiresglobe":   // hiresglobe on|off | status | face <i> <tex> <flipU> <flipV> <rot> | normals on|off [tangentSign] | wind ccw|cw
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
                    case "normals":   // hiresglobe normals on|off [tangentSign 1|-1]
                        PlanetMesh.SetNormals(On(a[2]), a.Length > 3 ? (float)D(a[3]) : 1f);
                        return $"hi-res globe normal maps={PlanetMesh.NormalMaps} tangent sign={PlanetMesh.TangentSign}";
                    case "wind":
                        PlanetMesh.SetWinding(!a[2].Equals("cw", StringComparison.OrdinalIgnoreCase));
                        return "hi-res globe front faces " + (PlanetMesh.CcwOutside ? "counter-clockwise" : "clockwise") + " from outside";
                    default:
                        return "hiresglobe on|off|status|face|normals|wind";
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

            case "meshrebuild":
                CleanMap.MeshRebuildSeconds = D(a[1]);
                return $"sector mesh rebuild every {CleanMap.MeshRebuildSeconds} s";

            case "debugpanel":
                DebugPanel.DevOpen = true;
                return "debug panel toggled";

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
                RingRocks.Recheck();
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

            case "selrock":   // selrock <rock label> | selrock off (as a click on its map marker)
                CleanMap.SelectedRock = a[1] == "off" ? null : string.Join(" ", a, 1, a.Length - 1);
                return "selected rock " + (CleanMap.SelectedRock ?? "none");

            case "targets":   // targets: the target, its line, the map's target list, the ship pins on screen, what is under the cursor
                return CleanMap.DevTargets();

            case "contactstab":   // contactstab [open | search <text> | pin <key> | unpin <key> | select <key>]: the Contacts tab
                return ContactsTab.DevCommand(a);

            case "targetpanel":   // targetpanel lists|panel|more: the map's right column with a target
                return CleanMap.DevPanel(a.Length > 1 ? a[1] : "");

            case "atmosphz":   // atmosphz on|off: the proxy atmosphere spherized as the planet's
                PlanetRenderBridge.AtmoSpherize = On(a[1]); PlanetRenderBridge.ResetAtmospheres();
                return "proxy atmosphere spherized: " + PlanetRenderBridge.AtmoSpherize;

            case "atmohills":   // atmohills on|off: the proxy atmosphere's inner sphere (radius with the hills) on the globe
                PlanetRenderBridge.AtmoOnHills = On(a[1]); PlanetRenderBridge.ResetAtmospheres();
                return "proxy atmosphere on the hills radius: " + PlanetRenderBridge.AtmoOnHills;

            case "targetrow":   // targetrow <i>: click the map's target list row i (0 = first) on the next frame
                CleanMap.DevRowClick = (int)D(a[1]);
                return "target list row " + a[1] + " click queued";

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

            case "contacts":   // contacts [reveal|forget|on|off|sensor telescope|radar|off|age <s>|info <target key>]
            {
                // (reveal and the count read each server grid's owner: on the server thread; age / info are the client's state)
                string cs = a.Length > 1 ? a[1] : "";
                return cs == "forget" || cs == "sensor" || cs == "on" || cs == "off" || cs == "age" || cs == "info" ? Contacts.Command(a) : GridGen.OnServerThread(() => Contacts.Command(a));
            }

            case "bodymarkers":   // bodymarkers on|off: planet and moon markers in flight
                BodyMarkers.Enabled = On(a[1]);
                return "body markers " + BodyMarkers.Enabled + " | " + BodyMarkers.Status;

            case "curvecull":   // curvecull on|off: skip map curve pieces wholly off the view
                CleanMap.CullCurves = On(a[1]);
                return "curve cull " + CleanMap.CullCurves;

            case "shotui":
                PlanetRenderBridge.ShotWithoutUi = !On(a[1]);
                return $"screenshots with UI={On(a[1])}";

            default:
                return ExecuteSystems(session, camera, line, a, verb);
        }
    }
}
