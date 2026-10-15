using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

// EncounterFrames (partial): the game's procedural spawns put into frames - near a frame: joined; far: a frame of
// their own on an approach.
public static partial class EncounterFrames
{
    // ───────────────────────────── procedural spawns ─────────────────────────────

    private static void ProcessNewGrids(double t)
    {
        while (_added.TryDequeue(out var a)) _waiting.Add(a);
        if (_waiting.Count == 0) return;
        double now = Wall();
        var due = _waiting.FindAll(w => now - w.at >= SpawnSettleSeconds);
        if (due.Count == 0) return;
        _waiting.RemoveAll(w => now - w.at >= SpawnSettleSeconds);
        var reg = SystemHost.Registry;
        var handled = new HashSet<long>();
        foreach (var (id, _) in due)
        {
            if (handled.Contains(id)) continue;
            var g = GridMembers.Get(id);
            if (g == null || !g.IsServer || SystemHost.Frames.FindByMember(id) != null) continue;
            Vector3D pos = GridMembers.Position(g);
            if (VoxelBerthRegistry.TryCellContaining(pos, reg, out _, out _)) continue;   // planet cells are world space

            // The frame whose berth it spawned near.
            ProximityFrame F = null; double d = FarReach;
            foreach (var f in SystemHost.Frames.Frames)
            {
                double dd = (pos - f.BerthCenter).Length();
                if (dd < d) { d = dd; F = f; }
            }
            if (F == null) continue;

            if (!IsEncounterGrid(g))
            {
                // A new player grid in a frame's space (a split-off piece, a projection): it is that frame's.
                if (d <= SiteRadius && SystemHost.Frames.AddMember(F, id)) Event($"new grid {id} '{g.DisplayName}' joins frame #{F.Id}");
                continue;
            }
            // A static grid is built on something (an asteroid base on its rock, which is voxel, not a
            // grid): it cannot move without it, so it stays where it spawned, in this frame.
            bool anchored = false;
            foreach (var o in GridMembers.All())
                if (o.IsServer && SystemHost.Frames.FindByMember(o.Id) == null && (GridMembers.Position(o) - pos).Length() <= ClusterRadius
                    && IsEncounterGrid(o) && !GridMembers.IsDynamic(o)) { anchored = true; break; }
            // Never on a planet's border: a frame orbiting low about a planet (periapsis under its keep
            // radius, where frames hand over to the planet's space) takes no encounter; the encounter goes
            // to its own frame on a safe orbit (its rock, which cannot move, is deleted).
            if (!IsSite(F) && OnPlanetBorder(F, out double safeR))
            {
                var cl = new List<OrbitalGridComponent>();
                foreach (var o in GridMembers.All())
                {
                    if (!o.IsServer || SystemHost.Frames.FindByMember(o.Id) != null) continue;
                    if ((GridMembers.Position(o) - pos).Length() > ClusterRadius || !IsEncounterGrid(o)) continue;
                    cl.Add(o); handled.Add(o.Id);
                }
                int gone = SpawnGuard.DeleteEncountersNear(pos, ClusterRadius);
                if (gone > 0)
                {
                    Event($"SPAWN on {F.ParentBodyName}'s border: '{g.DisplayName}' near frame #{F.Id} -> {gone} encounter(s) despawned (its berth is closed to them)");
                    continue;
                }
                AsteroidBridge.RequestDeleteNear(pos, ClusterRadius);
                Event($"SPAWN on {F.ParentBodyName}'s border: {cl.Count} encounter grid(s) '{g.DisplayName}' near frame #{F.Id} -> moved to a safe orbit");
                FarFrame(F, cl, t, safeR);
                continue;
            }
            if (d <= CloseRadius || anchored || (IsSite(F) && d <= SiteRadius))
            {
                // CLOSE: a similar orbit, i.e. the same frame. (A site on a planet's border takes none either.)
                if (OnPlanetBorder(F, out double sr2))
                {
                    // (the whole spawn together, as above: one grid at a time scattered it over separate orbits)
                    var cl2 = new List<OrbitalGridComponent>();
                    foreach (var o in GridMembers.All())
                    {
                        if (!o.IsServer || SystemHost.Frames.FindByMember(o.Id) != null) continue;
                        if (o != g && ((GridMembers.Position(o) - pos).Length() > ClusterRadius || !IsEncounterGrid(o))) continue;
                        cl2.Add(o); handled.Add(o.Id);
                    }
                    AsteroidBridge.RequestDeleteNear(pos, ClusterRadius);
                    Event($"SPAWN on {F.ParentBodyName}'s border: {cl2.Count} encounter grid(s) '{g.DisplayName}' near frame #{F.Id} -> moved to a safe orbit");
                    FarFrame(F, cl2, t, sr2);
                    continue;
                }
                if (SystemHost.Frames.AddMember(F, id))
                    Event($"SPAWN close: encounter grid {id} '{g.DisplayName}' {d / 1000:F1} km from frame #{F.Id} -> joins it");
                continue;
            }

            // FAR: every unframed encounter grid spawned with it goes into one new frame on its own orbit.
            var cluster = new List<OrbitalGridComponent>();
            foreach (var o in GridMembers.All())
            {
                if (!o.IsServer || SystemHost.Frames.FindByMember(o.Id) != null) continue;
                if ((GridMembers.Position(o) - pos).Length() > ClusterRadius || !IsEncounterGrid(o)) continue;
                cluster.Add(o); handled.Add(o.Id);
            }
            FarFrame(F, cluster, t);
        }
    }

    /// <summary>
    /// A frame orbiting low about a planet: periapsis under the planet's keep radius (the band where frames
    /// hand over to the planet's space and back). Out: a safe circular radius above that band.
    /// </summary>
    public static bool OnPlanetBorder(ProximityFrame f, out double safeR)
    {
        safeR = 0;
        var reg = SystemHost.Registry;
        var body = reg?.Find(f.ParentBodyName);
        var def = reg?.FindDefinition(f.ParentBodyName);
        if (body == null || body.IsRoot || def == null) return false;
        double keep = PlanetBerths.KeepRadius(def);
        safeR = keep * 1.25;
        return f.Elements.PeriapsisRadius < keep;
    }

    private static void FarFrame(ProximityFrame F, List<OrbitalGridComponent> cluster, double t, double minPe = 0)
    {
        if (cluster.Count == 0) return;
        Vector3D c = Vector3D.Zero, vAvg = Vector3D.Zero;
        foreach (var g in cluster) { c += GridMembers.Position(g); vAvg += GridMembers.Velocity(g); }
        c /= cluster.Count; vAvg /= cluster.Count;
        StateVector cur = OrbitPropagation.StateAt(F.Elements, t);
        // Its place: the spawn's bearing from the berth, but genuinely far (25-60 km, beyond the merge
        // range): it comes back only if its relative orbit brings it back (a conjunction).
        Vector3D delta = c - F.BerthCenter;                        // berth offsets are inertial
        uint h0 = (uint)(cluster[0].Id * 40503L);
        double far = FarMinKm * 1000 + (FarMaxKm - FarMinKm) * 1000 * ((h0 & 0xFFFF) / 65535.0);
        delta = (delta.LengthSquared() > 1 ? Vector3D.Normalize(delta) : Vector3D.UnitX) * Math.Max(far, delta.Length());
        // A deterministic kick (2-15 m/s, mostly in the orbit plane): a relative orbit that is eccentric
        // against the spawning frame's, so the encounter drifts off and may come round again.
        uint hsh = (uint)(cluster[0].Id * 2654435761L);
        double mag = 2 + 13 * ((hsh & 0xFFFF) / 65535.0);
        double ang = ((hsh >> 16) & 0xFFFF) / 65535.0 * 2 * Math.PI;
        Vector3D R = Vector3D.Normalize(cur.Position), N = Vector3D.Cross(cur.Position, cur.Velocity);
        N = N.LengthSquared() > 1e-12 ? Vector3D.Normalize(N) : Vector3D.UnitZ;
        Vector3D T = Vector3D.Cross(N, R);
        Vector3D kick = (R * Math.Cos(ang) + T * Math.Sin(ang) + N * 0.15) * mag;
        var el = CaptureMath.CaptureElements(new StateVector(cur.Position + delta, cur.Velocity + vAvg + kick), F.Elements.Mu, t);
        // (spawned orbits are always stable: clear of the planet's border by 5 km, and bound - checked every time, not only when
        //  a caller asked)
        if (minPe <= 0)
        {
            var pdef = SystemHost.Registry?.FindDefinition(F.ParentBodyName);
            var pnode = SystemHost.Registry?.Find(F.ParentBodyName);
            if (pdef != null && pnode != null && !pnode.IsRoot && !string.IsNullOrEmpty(pdef.ParkSubtype)) minPe = PlanetBerths.ShellRadius(pdef) + 5000;
        }
        if (minPe > 0 && !(el.Eccentricity < 1.0)) el = CaptureMath.CaptureElements(new StateVector(cur.Position + delta, cur.Velocity + vAvg), F.Elements.Mu, t);   // (no kick)
        if (minPe > 0 && (!(el.PeriapsisRadius >= minPe) || !(el.Eccentricity < 1.0)))
        {
            // Off the border: a circular orbit at the safe radius, in the frame's plane, where the spawn is.
            Vector3D up = Vector3D.Normalize(cur.Position + delta);
            Vector3D along = Vector3D.Cross(N, up);
            along = along.LengthSquared() > 1e-12 ? Vector3D.Normalize(along) : T;
            double rc = Math.Max(minPe, (cur.Position + delta).Length());   // (circular where it is - or at the safe radius if lower)
            el = CaptureMath.CaptureElements(new StateVector(up * rc, along * Math.Sqrt(F.Elements.Mu / rc)), F.Elements.Mu, t);
        }
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;
        var nf = SystemHost.Frames.CreateFrame(F.ParentBodyName, el, 0);
        if (nf == null) return;
        nf.IsEncounter = true;
        foreach (var g in cluster)
        {
            ServerFrames.MoveGrid(g, nf.BerthCenter + (GridMembers.Position(g) - c), null, -vAvg);
            SystemHost.Frames.AddMember(nf, g.Id);
        }
        Event($"SPAWN far: {cluster.Count} encounter grid(s) '{cluster[0].DisplayName}' {delta.Length() / 1000:F1} km from frame #{F.Id} " +
              $"-> own frame #{nf.Id} (kick {mag:F1} m/s, a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F4})");
    }
}
