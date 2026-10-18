using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// DevHarness (partial): the harness commands for the mod's systems and settings, the aero mod's switches, sensors, cost / memory / frame-rate probes.
// (Execute's switch, by theme: a verb not here falls through to the next group - DevHarness.cs has the order.)
public static partial class DevHarness
{
    private static string ExecuteSystems(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, string line, string[] a, string verb)
    {
        switch (verb)
        {
            case "sensornopower":   // sensornopower on|off: sensors count without power (harness test blocks)
                SensorBlocks.DevIgnorePower = On(a[1]);
                return "sensors without power: " + SensorBlocks.DevIgnorePower;

            case "radarpower":   // radarpower <0..1>: every radar's transmit power (as the terminal slider)
            {
                // (the radars are the server's blocks: their draw is asked again there)
                return GridGen.OnServerThread(() =>
                {
                    int n = 0;
                    lock (SensorBlocks.Radars) foreach (var r in SensorBlocks.Radars) { r.Power = (float)D(a[1]); n++; }
                    return $"{n} radar(s) at {D(a[1]):P0}";
                });
            }

            case "sensorblocks":   // sensorblocks: the sensor blocks' definitions, unlock and count
                return GridGen.OnServerThread(() => SensorBlocks.Describe(session));   // (the server's blocks' power and draw read there)

            case "buildmenu":   // buildmenu on|off: the terminal's Build tab (block catalogue)
                if (On(a[1])) MapView.OpenBuild(session); else MapView.Close();
                return On(a[1]) ? "opening the build menu" : "closing";

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

            case "setting":   // setting <name> [value]: a mod setting read, or set (DEV: OrbitalSettings, as the settings file would)
            {
                if (a.Length < 2) return "setting <name> [value]";
                if (a.Length < 3) return OrbitalSettings.TryGet(a[1], out double cur) ? $"{a[1]} = {cur}" : $"no setting {a[1]}";
                return OrbitalSettings.Set(a[1], D(a[2]), out string why) ? $"{a[1]} = {D(a[2])}" : $"not set: {why}";
            }

            case "settingsui":   // settingsui open|select|flip <setting>|close|report: the Options screen's Mods tab (Ui/SettingsTab.cs) -
                                 // open the screen (SharedUIComponent.OpenOptionsScreen), select the tab, change a row's control as a
                                 // player would (the tab's pump applies it), close what it opened; report (ask twice: the first asks the
                                 // UI thread): tab found, each section's rows counted / declared, ours vs the registry
                return SettingsTab.Dev(a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "report");

            case "hints":   // hints <gridId> | off | overlay <0|1|2> | status: the build hints on a grid without holding a block
                if (a.Length > 1 && a[1] == "off") { BuildHints.DevGridId = 0; return "build hints: building only"; }
                if (a.Length > 2 && a[1] == "overlay") { BuildHints.Overlay = (int)D(a[2]); return $"overlay {BuildHints.Overlay}"; }
                if (a.Length > 1 && a[1] != "status") BuildHints.DevGridId = (long)D(a[1]);
                return $"build hints: grid {BuildHints.DevGridId}, overlay {BuildHints.Overlay} | {BuildHints.Status}";

            case "aeroflight":   // aeroflight on|off [name]: the aero mod's scripted flight test (the grid with the most wings, or the one named)
                return PlanetRenderBridge.SetForeignString("AeroMod.AeroGridComponent+AeroFlightTest", "Only", a.Length > 2 ? a[2] : null) + "; "
                     + PlanetRenderBridge.SetForeignFlag("AeroMod.AeroGridComponent+AeroFlightTest", "Enabled", On(a[1]));

            case "fxcull":   // fxcull [on|off]: far grids' looping block effects hidden (EffectCull) - its status, or switched
                if (a.Length > 1) EffectCull.On = On(a[1]);
                return EffectCull.Run(session, camera.Position);

            case "mem":   // mem: managed memory (MB) and GC counts - for leak hunts
                return $"managed {GC.GetTotalMemory(false) / 1048576} MB, gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";

            case "particles":   // particles: every particle effect the game is playing, grouped (tag | effect | looped | state), also logged [ORBIT-PARTICLES]
                return ParticleCensus.Report(session, true, camera.Position);

            case "hudtree":   // hudtree <TypeName|root> [max]: queue a dump of the game HUD's controls under that one; "hudtree" alone: the last dump
                if (a.Length > 1) { AlignButton.TreeMax = a.Length > 2 ? (int)D(a[2]) : 60; AlignButton.TreeWanted = a[1]; return "queued (next UI job, ~0.5 s)"; }
                return HudGeom.Tree;

            case "alignbtn":   // alignbtn [on|off]: the attitude hold's toggle after the cockpit's dampeners - its status
                if (a.Length > 1) AlignButton.On = On(a[1]);
                return $"align button {(AlignButton.On ? "on" : "off")}: {AlignButton.Status} | hold {AttitudeHold.Status}";

            case "aeroaware":   // aeroaware on|off: the aero mod's aero-aware attitude law (gyros and thrusters told the air's torque; off: the game's gyros)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.ThrustTorque", "AeroAware", On(a[1]));

            case "pitchlog":   // pitchlog on|off: the aero mod's [AERO-PITCH] line (a piloted grid's rates, the air's torque, the gyros' and thrusters', 10 a second)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroGridComponent", "PitchLog", On(a[1]));

            case "aerocost":   // aerocost on|off: the aero mod's [AERO-COST] line (where its time goes, once a second)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroCost", "Log", On(a[1]));

            case "aero":   // aero on|off: the whole aero mod's simulation (A/B tests)
                return PlanetRenderBridge.SetForeignFlag("AeroMod.AeroSwitch", "Enabled", On(a[1]));

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

            case "clock":
                // clock +<hours> | clock set <hours>
                if (a[1].Equals("set", StringComparison.OrdinalIgnoreCase)) SystemHost.DevAdvanceClock(D(a[2]) * 3600 - SystemHost.Now);
                else SystemHost.DevAdvanceClock(D(a[1]) * 3600);
                return $"universe clock t={SystemHost.Now / 3600:F1} h";

            case "inv":   // inv [reset]: the invariants' violations (Invariants) - every suite ends requiring none
                if (a.Length > 1 && a[1] == "off") { Invariants.Enabled = false; return "invariants off"; }
                if (a.Length > 1 && a[1] == "on") { Invariants.Enabled = true; return "invariants on"; }
                return Invariants.Describe(a.Length > 1 && a[1] == "reset");

            case "fxcheck":   // fxcheck [reset]: the aero mod's effect consistency since the last reset (every drawn frame judged)
            {
                var t = AeroLink.Find("AeroMod.AeroApi"); var m = t?.GetMethod("GetFxConsistency", new[] { typeof(bool) });
                if (m != null) return (string)m.Invoke(null, new object[] { a.Length > 1 && a[1] == "reset" });
                return "fxcheck: the aero mod has no GetFxConsistency";
            }

            case "hitchms":   // hitchms <ms>: log every server tick gap over this (default 150)
                if (a.Length > 1) TickRate.HitchLogMs = D(a[1]);
                return $"hitch log over {TickRate.HitchLogMs:F0} ms";

            case "warm":   // warm [on|off|clear]: the warm berths (no engine stall when a frame takes a berth)
                if (a.Length > 1 && a[1] == "clear") return GridGen.OnServerThread(WarmBerths.Clear);   // (closes the server's placeholders)
                if (a.Length > 1) WarmBerths.Enabled = On(a[1]);
                return WarmBerths.Status;

            case "havok":   // havok [on|off]: the Havok cluster audit ([ORBIT-HAVOK]) and the static-move guard's counts
                if (a.Length > 1) HavokAudit.Enabled = On(a[1]);
                return $"{HavokAudit.Status}; static moves refused {ServerFrames.StaticMovesRefused}, parked {ServerFrames.StaticParks}, park failed {ServerFrames.StaticParkFailed}";

            case "encon":   // encon on|off: the encounter system (sites, its adoption of new grids)
                EncounterFrames.Enabled = On(a[1]);
                return "encounters " + (EncounterFrames.Enabled ? "on" : "off");

            case "crossfade":   // crossfade on|off: the fade across an arrival into a planet's frame; alone: its state
                if (a.Length > 1) CrossingFade.Enabled = On(a[1]);
                return $"crossing fade {(CrossingFade.Enabled ? "on" : "off")}: {CrossingFade.Status}";

            case "hudcheck":   // hudcheck: the orbit card against the truth (DevHudCheck)
                return DevHudCheck.Check(session);

            case "fps":   // fps [frames]: the client's frame rate over the last frames (default 300): average, 1% low, worst
            {
                int n = Math.Min(_frameCount, a.Length > 1 ? Math.Max(10, (int)D(a[1])) : 300);
                if (n < 10) return "fps: not enough frames yet";
                var ms = new double[n];
                for (int i = 0; i < n; i++) ms[i] = _frameMs[((_frameIdx - 1 - i) % _frameMs.Length + _frameMs.Length) % _frameMs.Length];
                Array.Sort(ms);
                double sum = 0; foreach (var m in ms) sum += m;
                double avg = sum / n, p99 = ms[Math.Min(n - 1, (int)(n * 0.99))];
                return $"fps {1000.0 / avg:F1} avg ({avg:F2} ms), 1% low {1000.0 / p99:F1} ({p99:F1} ms), worst {ms[n - 1]:F1} ms over {n} frames";
            }

            case "perf":   // GPU / render / main thread ms (average/max since the last stats log)
                return PlanetRenderBridge.FrameStats(session) + $" | mapcam {MapCamera.Enabled}: {MapCamera.Status}";

            case "warpbill":   // warpbill on|off: warp ages the railed grids' batteries and tanks
                WarpBill.Enabled = On(a[1]);
                return "warp bill " + WarpBill.Enabled + " | " + WarpBill.Status;

            case "allocs":   // allocs on|off|show: bytes allocated per guarded client system (FrameHost.Guard)
                if (a.Length > 1 && a[1] != "show") { FrameHost.AllocWatch = On(a[1]); FrameHost.AllocReport(); return "alloc watch " + FrameHost.AllocWatch; }   // (a fresh window)
                return FrameHost.AllocReport();

            case "statusfast":   // statusfast on|off: the status file at every poll even with no commands (perf scripts)
                StatusAlways = On(a[1]);
                return "status every poll: " + StatusAlways;

            case "aeroset":   // aeroset Type.Member value (AeroMod. prefix implied): a static of the aero mod
            case "aeroget":   // aeroget Type.Member
            {
                if (a.Length < 2) return "usage: aeroset Type.Member value | aeroget Type.Member";
                int dot = a[1].LastIndexOf('.');
                if (dot < 0) return "need Type.Member";
                string type = a[1].Substring(0, dot); if (!type.Contains('.')) type = "AeroMod." + type;
                return PlanetRenderBridge.ForeignValue(type, a[1].Substring(dot + 1), a[0] == "aeroset" && a.Length > 2 ? a[2] : null);
            }

            default:
                return "unknown command";
        }
    }
}
