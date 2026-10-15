using System.Collections.Generic;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV: the orbit card against the truth. `hudcheck`: what the HUD shows (the player's frame, the card, its speed and
/// altitude) against the same thing worked out on the server side from scratch - the server grid nearest the player
/// (seated) or the character (on foot), its frame by membership (else the frame of the nearest framed grid within 300 m),
/// its own position and velocity (divided by the warp). PASS/FAIL lines.
/// </summary>
public static class DevHudCheck
{
    public static string Check(Keen.VRage.Core.Game.Systems.Session client)
    {
        var sb = new System.Text.StringBuilder();
        int fails = 0;
        void Line(bool ok, string s) { if (!ok) fails++; sb.Append(ok ? "PASS " : "FAIL ").Append(s).Append(" || "); }
        var ch = FrameHost.PlayerCharacter(client);
        if (ch == null) return "no character";
        Vector3D pos = ch.Data.GetWorldTransform().Position;
        double n = Math.Max(1.0, SystemHost.Timescale), t = SystemHost.Now;
        // the truth, from the server's side - worked out ON the server thread (2026-10-15: the seat search below ran on the
        // harness's thread - the client's frame job - and the game asserted 'Scene.IsCurrentThreadDCS()' in GetEntitiesOfType)
        ProximityFrame f = null; Vector3D off = Vector3D.Zero, vel = Vector3D.Zero; string what = null, refused = null;
        bool seatedNow = FrameHost.Seated; long playerId = FrameHost.PlayerId;
        Vector3D chVel = !seatedNow && ch.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var crb) ? (Vector3D)crb.LinearVelocity : Vector3D.Zero;
        string ran = GridGen.OnServerThread(() =>
        {
        if (seatedNow)
        {
            // (the seat's own grid, from the server: the seat whose pilot is your server character, and that block's grid - the
            //  grid nearest you was a different sub-grid of a big ship whose cockpit sits 65 m from its origin)
            OrbitalGridComponent g = null; double bd = 100;
            try
            {
                var ss = ServerPlanetBeacon.ServerSession; var sch = ss != null ? PlayerCache.Of(ss) : null;
                if (sch != null)
                    foreach (var se in ss.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>())
                    {
                        var st = se.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>() as Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.ISeat;
                        if (st?.Pilot != sch) continue;
                        var ge = se.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;
                        foreach (var x in GridMembers.All()) if (x.IsServer && ReferenceEquals(x.Entity, ge)) { g = x; break; }
                        break;
                    }
            }
            catch { }
            if (g == null)
                foreach (var x in GridMembers.All()) { if (!x.IsServer) continue; double d = (GridMembers.Position(x) - pos).Length(); if (d < bd) { bd = d; g = x; } }
            if (g == null) { refused = "seated, no server grid within 100 m"; return "ok"; }
            lock (ServerFrames.FramesLock)
            {
                f = SystemHost.Frames?.FindByMember(g.Id);
                if (f == null)
                {
                    double fd = PlayerOrbit.FrameReach;
                    foreach (var x in GridMembers.All())
                    {
                        if (!x.IsServer || x.Id == g.Id) continue;
                        double d = (GridMembers.Position(x) - GridMembers.Position(g)).Length();
                        var xf = SystemHost.Frames?.FindByMember(x.Id);
                        if (xf != null && d < fd) { fd = d; f = xf; }
                    }
                }
            }
            what = $"seated in grid {g.Id} '{g.DisplayName}'{(f != null && f.AnchorEntityId == g.Id ? " (the anchor)" : "")}";
            if (f != null) { off = GridMembers.Position(g) - f.BerthCenter; vel = GridMembers.Velocity(g) / n; }
        }
        else
        {
            // (the truth from the server's side: your own membership, else the frame of a framed grid within reach - never the
            //  HUD's own frame, which made this check say "none is none" while you floated in no frame beside your ship)
            lock (ServerFrames.FramesLock)
            {
                f = SystemHost.Frames?.FindByMember(playerId);
                if (f == null && !VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out _, out _))
                {
                    double fd = PlayerOrbit.FrameReach;
                    foreach (var x in GridMembers.All())
                    {
                        if (!x.IsServer) continue;
                        double d = (GridMembers.Position(x) - pos).Length();
                        var xf = SystemHost.Frames?.FindByMember(x.Id);
                        if (xf != null && d < fd) { fd = d; f = xf; }
                    }
                }
            }
            what = "on foot";
            if (f != null) { off = pos - f.BerthCenter; vel = chVel / n; }
        }
        return "ok";
        });
        // (the HUD against it here, once the server has worked it out - never waited for in this frame: ServerAsks, 2026-10-15)
        return GridGen.Then(ran, Against);
        string Against(string ranR)
        {
        if (ranR != "ok") return "hudcheck: " + ranR;
        if (refused != null) return refused;
        sb.Append(what).Append(": ");
        var hudFrame = FrameHost.PlayerFrame;
        Line(hudFrame?.Id == f?.Id, $"the HUD's frame #{hudFrame?.Id.ToString() ?? "none"} is the truth's #{f?.Id.ToString() ?? "none"} ({FrameHost.SeatedWhy})");
        if (f == null) { sb.Insert(0, $"hudcheck {(fails == 0 ? "PASS" : "FAIL")} ({fails}) || "); return sb.ToString(); }
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        double radius = def?.RadiusMeters ?? 0;
        var fc = OrbitPropagation.StateAt(f.Elements, t);
        var own = new StateVector(fc.Position + off, fc.Velocity + vel);
        double tSpeed = own.Velocity.Length(), tAlt = own.Position.Length() - radius;
        var r = OrbitHud.Current;
        bool walking = OrbitHud.Walking;
        bool holding = FrameHost.HoldingStation;   // (holding station: your orbit shown, no drift plot - by design)
        Line(r != null, $"an orbit card ({(r == null ? "none" : r.Body)}{(walking ? ", hidden: walking" : "")}{(holding ? ", holding station" : "")})");
        if (r != null && !walking && !MapView.Visible) Line(OrbitHud.CardOpen, $"the card is up on screen ({OrbitHud.CardWhy})");
        if (r != null)
        {
            Line(Math.Abs(r.Speed - tSpeed) <= 1.0 + 0.002 * tSpeed, $"its speed {r.Speed:F1} m/s, the truth {tSpeed:F1} (the frame's own {fc.Velocity.Length():F1})");
            Line(Math.Abs(r.Alt - tAlt) <= 20.0, $"its altitude {r.Alt / 1000:F3} km, the truth {tAlt / 1000:F3} km");
        }
        // the plan (and the map's "You") start from your own orbit
        if (Maneuvers.Base(t, out var bb, out var bel) && bb?.Name == f.ParentBodyName)
        {
            var bs = OrbitPropagation.StateAt(bel, t);
            Line((bs.Position - own.Position).Length() <= 30 && Math.Abs(bs.Velocity.Length() - tSpeed) <= 0.1 + 0.002 * tSpeed,
                 $"the plan starts from you: {(bs.Position - own.Position).Length():F1} m, {bs.Velocity.Length() - tSpeed:+0.00;-0.00} m/s off");
        }
        else Line(false, "the plan has no orbit");
        if (FrameHost.Seated)
        {
            Line(FrameHost.SeatGrid != null, "the seat's grid found (its own: not the nearest)");
            Line(!FrameHost.Dampeners, "no suit station-keeping claimed while seated");
        }
        if (r?.Relative != null && !r.RelIsTarget)
        {
            // (the snapshot's anchor, not StaticAnchorOf: that reads the server's entities, from here the cross-scene read the
            //  game's DataGuard asserts on - the 2026-10-15 scene audit)
            double leaveAt = ServerFrames.LeaveRadius(ServerFrames.StaticAnchorView(f, out _));
            Line(Math.Abs(r.RelBoundary - leaveAt) < 1, $"the plot's boundary {r.RelBoundary / 1000:F0} km is where you leave ({leaveAt / 1000:F0} km)");
        }
        bool inAnchor = FrameHost.Seated && what.Contains("(the anchor)");
        Line(Math.Abs((FrameHost.RiderOffset - off).Length()) <= 30 + vel.Length() * 0.6, $"the rider offset ({FrameHost.RiderOffset.Length():F0} m) is the grid's ({off.Length():F0} m)");
        // (the anchor's pilot: its own place in the frame - the pin lets it sit off the centre - but no drift plot)
        if (inAnchor) Line(FrameHost.AnchorRigid, $"the anchor's pilot: no drift plot (rigid {FrameHost.AnchorRigid})");
        // what the HUD draws, against who you are and what you target
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string tgt = Maneuvers.Target;
        bool hasTgt = tgt != null && SystemHost.Registry?.Find(tgt) == null && !RendezvousView.TargetIsOwnFrame(tgt);
        if (hasTgt)
        {
            // a target only the star is common to: no plot about it
            Targets.At(tgt, SystemHost.Now, out var tpar, out _);
            var c = SEAerospace.Frames.RendezvousPlot.Common(SystemHost.Registry?.Find(f.ParentBodyName), tpar);
            if (c == null || c.IsRoot) hasTgt = false;
        }
        var want = !FrameHost.Seated ? (SEAerospace.Frames.RendezvousPlot.Plot?)null
                 : hasTgt ? SEAerospace.Frames.RendezvousPlot.Plot.Target
                 : FrameHost.RiderFrame == f.Id && !FrameHost.AtAnchor && !FrameHost.AnchorRigid ? SEAerospace.Frames.RendezvousPlot.Plot.Anchor
                 : SEAerospace.Frames.RendezvousPlot.Plot.Disc;
        // (holding station - dampeners, no relative force: no orbit display at all, by design)
        if (OrbitHud.EntryHidden) want = null;   // (coming down through the air: no orbit graph, by design)
        if (want.HasValue && holding) Line(wall - OrbitHud.LastPlotWall < 1 && OrbitHud.LastPlot == SEAerospace.Frames.RendezvousPlot.Plot.Disc, $"holding station: the HUD draws {(wall - OrbitHud.LastPlotWall < 1 ? OrbitHud.LastPlot.ToString() : "nothing")}, expected Disc (your orbit, no drift plot)");
        else if (want.HasValue) Line(wall - OrbitHud.LastPlotWall < 1 && OrbitHud.LastPlot == want.Value, $"the HUD draws {(wall - OrbitHud.LastPlotWall < 1 ? OrbitHud.LastPlot.ToString() : "nothing")}, expected {want} (target {tgt ?? "none"}, at anchor {FrameHost.AtAnchor})");
        if (hasTgt) { var tl = Maneuvers.TargetLine(t); Line(tl != null, $"the target line: {tl}"); }
        if (r?.Relative != null && hasTgt) Line(r.RelIsTarget, $"the plot is about the target ({r.AnchorName}), now along {r.RelNow.along / 1000:F2} km radial {r.RelNow.radial / 1000:F2} km");
        sb.Insert(0, $"hudcheck {(fails == 0 ? "PASS" : "FAIL")} ({fails}) || ");
        return sb.ToString();
        }
    }
}
