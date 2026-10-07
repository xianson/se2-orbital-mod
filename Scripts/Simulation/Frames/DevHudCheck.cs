using System;
using System.Collections.Generic;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using Keen.VRage.Core;

#pragma warning disable
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
        // the truth, from the server's side
        ProximityFrame f = null; Vector3D off = Vector3D.Zero, vel = Vector3D.Zero; string what;
        if (FrameHost.Seated)
        {
            OrbitalGridComponent g = null; double bd = 100;
            foreach (var x in GridMembers.All()) { if (!x.IsServer) continue; double d = (GridMembers.Position(x) - pos).Length(); if (d < bd) { bd = d; g = x; } }
            if (g == null) return "seated, no server grid within 100 m";
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
                f = SystemHost.Frames?.FindByMember(FrameHost.PlayerId);
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
            if (f != null) { off = pos - f.BerthCenter; vel = (ch.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? (Vector3D)rb.LinearVelocity : Vector3D.Zero) / n; }
        }
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
        bool holding = FrameHost.HoldingStation;   // (holding station: no relative force, no orbit shown - by design)
        if (holding) Line(r == null, $"holding station: no orbit shown ({(r == null ? "none" : "shown: " + r.Body)})");
        else Line(r != null, $"an orbit card ({(r == null ? "none" : r.Body)}{(walking ? ", hidden: walking" : "")})");
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
            double leaveAt = ServerFrames.LeaveRadius(ServerFrames.StaticAnchorOf(f, out _));
            Line(Math.Abs(r.RelBoundary - leaveAt) < 1, $"the plot's boundary {r.RelBoundary / 1000:F0} km is where you leave ({leaveAt / 1000:F0} km)");
        }
        bool inAnchor = FrameHost.Seated && what.Contains("(the anchor)");
        if (!inAnchor) Line(Math.Abs((FrameHost.RiderOffset - off).Length()) <= 30 + vel.Length() * 0.6, $"the rider offset ({FrameHost.RiderOffset.Length():F0} m) is the grid's ({off.Length():F0} m)");
        else Line(FrameHost.RiderFrame == -1, $"the anchor's pilot rides nothing (rider frame {FrameHost.RiderFrame})");
        // what the HUD draws, against who you are and what you target
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        string tgt = Maneuvers.Target;
        bool hasTgt = tgt != null && SystemHost.Registry?.Find(tgt) == null && !RendezvousView.TargetIsOwnFrame(tgt);
        if (hasTgt)
        {
            // a target only the star is common to: no plot about it
            string host = null;
            foreach (var st in EncounterFrames.Sites) if (st.Sector == tgt) { host = st.Host; break; }
            var c = SEAerospace.Frames.RendezvousPlot.Common(SystemHost.Registry?.Find(f.ParentBodyName), host != null ? SystemHost.Registry?.Find(host) : null);
            if (c == null || c.IsRoot) hasTgt = false;
        }
        var want = !FrameHost.Seated ? (SEAerospace.Frames.RendezvousPlot.Plot?)null
                 : hasTgt ? SEAerospace.Frames.RendezvousPlot.Plot.Target
                 : FrameHost.RiderFrame == f.Id && !FrameHost.AtAnchor ? SEAerospace.Frames.RendezvousPlot.Plot.Anchor
                 : SEAerospace.Frames.RendezvousPlot.Plot.Disc;
        // (holding station - dampeners, no relative force: no orbit display at all, by design)
        if (want.HasValue && holding) Line(!(wall - OrbitHud.LastPlotWall < 1), $"holding station: the HUD draws {(wall - OrbitHud.LastPlotWall < 1 ? OrbitHud.LastPlot.ToString() : "nothing")}, expected nothing");
        else if (want.HasValue) Line(wall - OrbitHud.LastPlotWall < 1 && OrbitHud.LastPlot == want.Value, $"the HUD draws {(wall - OrbitHud.LastPlotWall < 1 ? OrbitHud.LastPlot.ToString() : "nothing")}, expected {want} (target {tgt ?? "none"}, at anchor {FrameHost.AtAnchor})");
        if (hasTgt) { var tl = Maneuvers.TargetLine(t); Line(tl != null, $"the target line: {tl}"); }
        if (r?.Relative != null && hasTgt) Line(r.RelIsTarget, $"the plot is about the target ({r.AnchorName}), now along {r.RelNow.along / 1000:F2} km radial {r.RelNow.radial / 1000:F2} km");
        sb.Insert(0, $"hudcheck {(fails == 0 ? "PASS" : "FAIL")} ({fails}) || ");
        return sb.ToString();
    }
}
