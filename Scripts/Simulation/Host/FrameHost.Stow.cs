using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable
namespace OrbitalMod;

// FrameHost (partial): the player onto the rails - the stow out of a planet's cell, free flight in a spinning cell, and
// the harness's orbit commands.
public static partial class FrameHost
{
    // ───────────────────────────── stow (planet cell -> conjunction) ─────────────────────────────

    private static void TryStow(Keen.VRage.Core.Game.Systems.Session session, Entity ch, long id, Vector3D pos, Vector3D vel, double t)
    {
        var reg = SystemHost.Registry;
        if (_pendingOrbit.HasValue)
        {
            var (pb, pel) = _pendingOrbit.Value;
            _pendingOrbit = null;
            ForceStow = false;
            var pf = SystemHost.Frames.CreateFrame(pb, pel, id);
            if (pf == null) return;
            _wasInKeep = false;
            StartTeleport(session, pf.BerthCenter, Vector3D.Zero, t);
            // Unconstrained grids within the attach radius come along (constrained ones never move: Havok).
            ServerFrames.Attach.Enqueue(new ServerFrames.AttachRequest { FrameId = pf.Id, RefPos = pos, RefVel = vel, Berth = pf.BerthCenter });
            Event($"STOW (orbit command) -> frame #{pf.Id} about {pb}: a={pel.SemiMajorAxis / 1000:F1} km e={pel.Eccentricity:F3}");
            return;
        }
        bool legacy = false;
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell))
        {
            _wasInKeep = false;
            if (!ForceStow && !OrbitalConfig.CaptureLegacySpace) return; // legacy space: captured only when enabled (or forced)
            if (!SystemHost.TryNearestCell(pos, out body, out cell)) { ForceStow = false; return; }
            legacy = true;
        }
        GravityBody node = body != null ? reg.Find(body) : null;
        BodyDefinition def = body != null ? reg.FindDefinition(body) : null;
        if (node == null) { ForceStow = false; return; }

        StateVector borg = node.OriginInRoot(t);
        Chart chart = legacy ? default : Chart.Of(body, t);
        Vector3D relChart = pos - cell;
        double chartSpeed = vel.Length();   // speed in the planet's world (before the inertial conversion)
        Vector3D rel = chart.ToInertial(relChart);
        vel = chart.VelToInertial(relChart, vel);
        Vector3D cel = borg.Position + rel;
        Vector3D celVel = borg.Velocity + vel;
        double d = rel.Length();
        double shell = PlanetBerths.ShellRadius(def);
        double keep = PlanetBerths.KeepRadius(def);
        Debug += $" cell={body} d={d / 1000:F1}km shell={shell / 1000:F1} keep={keep / 1000:F1}";

        KeplerianElements elBody = CaptureMath.CaptureElements(new StateVector(rel, vel), node.Mu, t);
        if (d < keep)
        {
            _wasInKeep = true;
            // Faster than the world allows, well above the ground: onto rails (the rails carry the speed).
            bool tooFast = def != null && chartSpeed > SpeedCap * 1.02 && d - def.RadiusMeters > SurfaceGuard * 1.5;
            if (!ForceStow && !tooFast)
            {
                if (d < shell * PlanetBerths.StowShellMargin) return;                       // voxel frame owns low flight
                // (STOWED AS SOON AS IT IS OUT: past the conversion radius R_c by the stow floor's 5 % (StowShellMargin - the hysteresis:
                //  back down, the release is at R_c), whatever its orbit. Inside the keep it also had to be LEAVING (FrameRails.EscapesShell:
                //  apoapsis above 1.5 R_c, or hyperbolic): a spaceplane in a low orbit - apoapsis under ~46 km at Verdure - never left the
                //  planet frame, silently (the user, 2026-10-10: "need to stow as soon as you pass the nominal boundary, with hysteresis"))
            }
        }
        else if (!_wasInKeep && !ForceStow && !legacy) return; // v1 gate: only departures from a planet are captured

        GravityBody parent = reg.Root.DeepestSoiContaining(cel, t) ?? node;
        StateVector porg = parent.OriginInRoot(t);
        var el = CaptureMath.CaptureElements(new StateVector(cel - porg.Position, celVel - porg.Velocity), parent.Mu, t);
        if (!IsFinite(el.SemiMajorAxis) || !IsFinite(el.MeanMotion)) return;

        var frame = SystemHost.Frames.CreateFrame(parent.Name, el, id);
        if (frame == null) return;
        ForceStow = false;
        _wasInKeep = false;
        ServerFrames.Attach.Enqueue(new ServerFrames.AttachRequest { FrameId = frame.Id, RefPos = pos, RefVel = vel, Berth = frame.BerthCenter, Body = legacy ? null : body, Time = t });
        StartTeleport(session, frame.BerthCenter, Vector3D.Zero, t, legacy ? (Quaternion?)null : chart.ToInertialRotation());   // (out of the spinning chart: facing turned with it)
        // (the ships the server turns with the chart: their pilots' targets turn with them - ServerFrames.TargetTurns)
        Event($"STOW -> frame #{frame.Id} orbiting {parent.Name}: r={(cel - porg.Position).Length() / 1000:F1} km " +
              $"|v|={vel.Length():F1} m/s a={el.SemiMajorAxis / 1000:F1} km e={el.Eccentricity:F3} slot={frame.BerthSlotId} berth={ServerPlanetBeacon.Fmt(frame.BerthCenter)}");
    }

    /// <summary>Free flight in a spinning planet's cell (the rotating chart): Coriolis + centrifugal.</summary>
    private static Vector3D ApplyFictitious(Entity ch, Vector3D pos, Vector3D vel, double dt)
    {
        if (dt <= 0) return vel;
        var reg = SystemHost.Registry;
        if (!VoxelBerthRegistry.TryCellContaining(pos, reg, out string body, out Vector3D cell)) return vel;
        Chart c = Chart.Of(body, SystemHost.Now);
        if (!c.Spin) return vel;
        Vector3D r = pos - cell;
        double alt = r.Length() - (reg.FindDefinition(body)?.RadiusMeters ?? 0);
        if (vel.Length() < OrbitalConfig.FictitiousMinSpeed && alt < OrbitalConfig.FictitiousMinAltitude) return vel;
        Vector3D nv = vel + c.Fictitious(r, vel) * dt;
        if (!IsFinite(nv)) return vel;
        SetVelocity(ch, nv);
        return nv;
    }

    /// <summary>
    /// Harness: put the player on a given orbit around a planet, starting at APOAPSIS (SE1's /orbit
    /// command). Framed: replaces the frame's elements. Not framed: stows with those elements.
    /// </summary>
    public static string SetOrbit(string body, double apoAltKm, double periAltKm, double incDeg, double phaseDeg = 0)
    {
        if (!OrbitElements(body, apoAltKm, periAltKm, incDeg, phaseDeg, out var el)) return "no such body / degenerate orbit";
        return SetOrbitTo(body, el, $"Ap {apoAltKm} km Pe {periAltKm} km i {incDeg}°");
    }

    /// <summary>Harness: your frame onto exactly these elements (a shared frame or a site is left first).</summary>
    public static string SetOrbitTo(string body, KeplerianElements el, string what)
    {
        // In a site (its orbit is its ephemeris) or a frame shared with others (a station, a ship, a rock you
        // rendezvoused with): you leave it and stow onto the orbit alone; setting its elements moved them all.
        var shared = PlayerFrame;
        if (shared != null && (EncounterFrames.IsSite(shared) || shared.IsEncounter || shared.Members.Count > 1))
        {
            lock (ServerFrames.FramesLock) SystemHost.Frames.RemoveMember(_playerId);
            Event($"ORBIT: left frame #{shared.Id} (shared or a site) to take the orbit alone");
            _pendingOrbit = (body, el);
            ForceStow = true;
            return "orbit queued (left the shared frame; stows next tick)";
        }
        if (PlayerFrame != null)
        {
            PlayerFrame.ParentBodyName = body;
            PlayerFrame.Elements = el;
            PlayerFrame.PendingDrainDv = Vector3D.Zero;
            Event($"ORBIT set on frame #{PlayerFrame.Id}: {body} {what}");
            return "orbit set";
        }
        _pendingOrbit = (body, el);
        ForceStow = true;
        return "orbit queued (stows next tick)";
    }

    private static (string body, KeplerianElements el)? _pendingOrbit;

    /// <summary>Harness: stow onto exactly this orbit on the next tick.</summary>
    public static void SetPendingOrbit(string body, KeplerianElements el) => _pendingOrbit = (body, el);

    /// <summary>Elements for an Ap/Pe/inclination orbit, starting at apoapsis advanced by phaseDeg of mean anomaly.</summary>
    public static bool OrbitElements(string body, double apoAltKm, double periAltKm, double incDeg, double phaseDeg,
                                     out KeplerianElements el)
    {
        el = default;
        var reg = SystemHost.Registry;
        var node = reg?.Find(body);
        var def = reg?.FindDefinition(body);
        if (node == null || def == null) return false;
        double R = def.RadiusMeters;
        double ra = R + Math.Max(apoAltKm, periAltKm) * 1000, rp = R + Math.Min(apoAltKm, periAltKm) * 1000;
        double a = 0.5 * (ra + rp);
        double va = Math.Sqrt(node.Mu * (2.0 / ra - 1.0 / a));
        double inc = incDeg * Math.PI / 180.0;
        double t = SystemHost.Now;
        var e0 = OrbitalMath.ToElements(new StateVector(new Vector3D(ra, 0, 0), new Vector3D(0, Math.Cos(inc), Math.Sin(inc)) * va), node.Mu, t);
        if (!IsFinite(e0.SemiMajorAxis)) return false;
        if (phaseDeg != 0 && IsFinite(e0.Period))
        {
            var st = OrbitPropagation.StateAt(e0, t + phaseDeg / 360.0 * e0.Period);
            e0 = OrbitalMath.ToElements(st, node.Mu, t);
            if (!IsFinite(e0.SemiMajorAxis)) return false;
        }
        el = e0;
        return true;
    }
}
