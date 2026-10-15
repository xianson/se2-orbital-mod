using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// ServerFrames (partial): grids onto the rails - joining a stowing player
// (attach), lone grids stowed by themselves, the DEV grid orbit, and loose pieces adopted by a nearby frame.
public static partial class ServerFrames
{
    // ───────────────────────────── attach (grids join a stowing player) ─────────────────────────────

    private static void DoAttach(AttachRequest req)
    {
        var frame = SystemHost.Frames.Get(req.FrameId);
        if (frame == null) return;
        int n = 0;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || !GridMembers.IsDynamic(g)) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D p = GridMembers.Position(g);
            if ((p - req.RefPos).Length() > AttachRadius) continue;
            if (EncounterFrames.IsNpc(g)) continue;   // NPCs keep their own world
            // only what flies with you: a ship landed on the ground below (a small moon's stow altitude is a few km) was taken
            // into orbit with a stowing walker. A grid on its conic (HighSpeed) has no physical velocity: not judged by it.
            if (!_gridHighSpeed.ContainsKey(g.Id) && (GridMembers.Velocity(g) - req.RefVel).Length() > AttachRelSpeed) continue;
            if (!SystemHost.Frames.AddMember(frame, g.Id)) continue;
            // A grid riding its conic (HighSpeed) has zero physical velocity: its true velocity is the
            // conic's. Leave HighSpeed first, or StepGridHighSpeed keeps dragging it off the berth.
            Chart chart = req.Body != null ? Chart.Of(req.Body, req.Time) : default;
            Vector3D cellC = default;
            bool inCell = req.Body != null && VoxelBerthRegistry.TryGetCell(req.Body, SystemHost.Registry, out cellC);
            Vector3D v = inCell ? chart.VelToInertial(p - cellC, GridMembers.Velocity(g)) : GridMembers.Velocity(g);
            if (_gridHighSpeed.TryGetValue(g.Id, out var hs))
            {
                var st = OrbitPropagation.StateAt(hs.el, SystemHost.Now);
                if (IsFinite(st.Velocity)) v = st.Velocity;
                _gridHighSpeed.Remove(g.Id);
            }
            // Same relative placement in the berth; velocity relative to the frame (the player's own
            // velocity went into the rails).
            // (MoveGrid: a jointed grid comes with its whole joined group, or not at all if it is locked to a base)
            if (!MoveGrid(g, req.Berth + (inCell ? chart.ToInertial(p - req.RefPos) : p - req.RefPos), v - req.RefVel, out var withA, default, inCell ? chart.ToInertialRotation() : (Quaternion?)null))
            { SystemHost.Frames.RemoveMember(g.Id); continue; }
            foreach (var o in withA) if (SystemHost.Frames.FindByMember(o.Id) == null) SystemHost.Frames.AddMember(frame, o.Id);
            n++;
        }
        if (n > 0) Event($"ATTACH {n} grid(s) within {AttachRadius / 1000:F0} km -> frame #{frame.Id}");
    }

    // ───────────────────────────── stow (grids without a player) ─────────────────────────────

    /// <summary>
    /// Total partition for grids: a grid in a planet cell that is beyond the keep, or above the shell
    /// on an arc that escapes it, belongs on the rails. Grids near the player are left to the player's
    /// own stow, which takes them along (one frame, not two). Grids within the attach radius of the
    /// stowing grid join its frame.
    /// </summary>
    static readonly List<Entity> _stowMoveSet = new List<Entity>();   // (StowLoneGrids': server thread, under FramesLock)
    private static void StowLoneGrids()
    {
        var reg = SystemHost.Registry;
        double t = SystemHost.Now;
        foreach (var g in GridMembers.All())
        {
            if (!g.IsServer || !GridMembers.IsDynamic(g)) continue;
            if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            // Grids near a player on foot go with the player's own stow. A seated player's stow does
            // not run: the ship they fly stows here (and they with it, as its child).
            if (!FrameHost.Seated && (pos - PlayerPosition).Length() <= AttachRadius) continue;
            if (EncounterFrames.IsNpc(g)) continue;
            lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;   // (going with its group this tick)
            if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
            {
                if (!OrbitalConfig.CaptureLegacySpace || !SystemHost.TryNearestCell(pos, out body, out cell)) continue;
            }
            GravityBody node = reg.Find(body);
            BodyDefinition def = reg.FindDefinition(body);
            if (node == null || def == null) continue;

            Vector3D vel = GridMembers.Velocity(g);
            if (_gridHighSpeed.TryGetValue(g.Id, out var hs))
            {
                var st = OrbitPropagation.StateAt(hs.el, t);
                if (IsFinite(st.Velocity)) vel = st.Velocity;
            }
            bool legacy = !VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out _);
            Chart chart = legacy ? default : Chart.Of(body, t);
            Vector3D relChart = pos - cell;
            Vector3D rel = chart.ToInertial(relChart);
            if (!_gridHighSpeed.ContainsKey(g.Id)) vel = chart.VelToInertial(relChart, vel);
            double d = rel.Length();
            double shell = PlanetBerths.ShellRadius(def);
            double keep = PlanetBerths.KeepRadius(def);
            if (d < shell * PlanetBerths.StowShellMargin) continue;
            // locked to a base: stays. (Asked here, last of the filters - every one above only skips and reads nothing it could
            //  change, so the order is free: a grid inside the shell - a landed ship, a base - ran this physics query and joint
            //  search ~6 times a second, with a new list each, 2026-10-15)
            if (GridMembers.IsConstrained(g) && !GridMembers.MoveSet(g, _stowMoveSet, out _)) { _stowMoveSet.Clear(); continue; }
            _stowMoveSet.Clear();
            // (STOWED AS SOON AS IT IS OUT: past the conversion radius R_c by the stow floor's 5 % (StowShellMargin - the hysteresis:
            //  back down, the release is at R_c), whatever its orbit. Inside the keep it also had to be LEAVING (FrameRails.EscapesShell:
            //  apoapsis above 1.5 R_c, or hyperbolic): a spaceplane in a low orbit - apoapsis under ~46 km at Verdure - never left the
            //  planet frame, silently (the user, 2026-10-10: "need to stow as soon as you pass the nominal boundary, with hysteresis"))

            StateVector borg = node.OriginInRoot(t);
            Vector3D cel = borg.Position + rel;
            GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
            StateVector porg = parent.OriginInRoot(t);
            var el = CaptureMath.CaptureElements(new StateVector(cel - porg.Position, borg.Velocity + vel - porg.Velocity), parent.Mu, t);
            if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) continue;
            var frame = SystemHost.Frames.CreateFrame(parent.Name, el, g.Id);
            if (frame == null) continue;
            _gridHighSpeed.Remove(g.Id);
            // (turned out of the planet's rotating chart, as attached grids and arrivals are: unturned, the ship a seated player
            //  flies left every spinning planet facing off by the chart's angle - planet and sky jumped)
            if (!MoveGrid(g, frame.BerthCenter, Vector3D.Zero, out var withS, default, legacy ? (Quaternion?)null : chart.ToInertialRotation())) { SystemHost.Frames.Dissolve(frame.Id); continue; }
            foreach (var o in withS) { _gridHighSpeed.Remove(o.Id); if (SystemHost.Frames.FindByMember(o.Id) == null) SystemHost.Frames.AddMember(frame, o.Id); }
            Event($"STOW grid {g.Id} '{g.DisplayName}' -> frame #{frame.Id} orbiting {parent.Name}: r={(cel - porg.Position).Length() / 1000:F1} km " +
                  $"|v|={vel.Length():F0} m/s a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F3} slot={frame.BerthSlotId}");
            DoAttach(new AttachRequest { FrameId = frame.Id, RefPos = pos, RefVel = vel, Berth = frame.BerthCenter, Body = legacy ? null : body, Time = t });
        }
    }

    private static void DoGridOrbit(GridOrbitRequest r)
    {
        var g = GridMembers.Get(r.GridId);
        if (g == null || !g.IsServer || !GridMembers.IsDynamic(g)) { Event($"gridorbit: grid {r.GridId} not found / not dynamic"); return; }
        var old = SystemHost.Frames.FindByMember(g.Id);
        if (old != null) SystemHost.Frames.RemoveMember(g.Id);
        var f = SystemHost.Frames.CreateFrame(r.Body, r.El, g.Id);
        if (f == null) { Event("gridorbit: no free berth"); return; }
        _gridHighSpeed.Remove(g.Id);
        if (!MoveGrid(g, f.BerthCenter, Vector3D.Zero, out var withG)) { SystemHost.Frames.Dissolve(f.Id); return; }
        // Joined to others (sub-grids on hinges, docked): they go with it, into its new frame (they were moved, never
        // framed - left in no frame, or in the old one; the reentry test saw the Hercules arrive with 1 member of 7)
        foreach (var o in withG)
        {
            if (o.Id == g.Id) continue;
            if (SystemHost.Frames.FindByMember(o.Id) != null) SystemHost.Frames.RemoveMember(o.Id);
            SystemHost.Frames.AddMember(f, o.Id);
        }
        Event($"gridorbit: grid {g.Id} '{g.DisplayName}' (+{withG.Count(o => o.Id != g.Id)} joined) -> frame #{f.Id} about {r.Body} a={r.El.SemiMajorAxis / 1000:F1} km e={r.El.Eccentricity:F3} slot={f.BerthSlotId}");
    }

    /// <summary>
    /// A new player grid in a frame's space - a piece broken off, a cockpit section after a hit, a rotor head built, a paste -
    /// joins the nearest frame within its split radius. The encounter system did this only while encounters are on (a world
    /// setting): off, a ship that broke up left its pieces, and a player seated in one, in no frame for good (no orbit, warp
    /// refused, left behind at the next arrival). Encounters on: theirs (EncounterFrames.ProcessNewGrids), not raced here.
    /// </summary>
    private static void AdoptLooseGrids()
    {
        // (with encounters on too - it returned then, and a grid let go of its frame in open space was never framed again;
        //  the encounter system's own grids are left to it, and a static grid is never adopted from 20 km out)
        if (SystemHost.Frames == null || SystemHost.Registry == null) return;
        lock (FramesLock)
            foreach (var g in GridMembers.All())
            {
                if (!g.IsServer || SystemHost.Frames.FindByMember(g.Id) != null || EncounterFrames.IsNpc(g)) continue;
                if (EncounterFrames.Enabled && EncounterFrames.IsEncounterGrid(g)) continue;
                if (!GridMembers.IsDynamic(g) || GridMembers.Tombstoned(g.Id)) continue;
                lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;
                Vector3D pos = GridMembers.Position(g);
                if (VoxelBerthRegistry.TryCellContaining(pos, SystemHost.Registry, out _, out _)) continue;   // (a planet's: its own stow)
                ProximityFrame near = null; double d = SlotRadius;
                foreach (var f in SystemHost.Frames.Frames)
                {
                    double dd = (pos - f.BerthCenter).Length();
                    if (dd < d) { d = dd; near = f; }
                }
                if (near != null && SystemHost.Frames.AddMember(near, g.Id))
                    Event($"loose grid {g.Id} '{g.DisplayName}' joins frame #{near.Id} ({d / 1000:F1} km from its centre)");
            }
    }
}
