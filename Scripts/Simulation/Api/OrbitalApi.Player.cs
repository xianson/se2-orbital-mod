using SEAerospace.Api;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// (API version 2) THE LOCAL PLAYER'S FLIGHT PLAN: their orbit as the planner sees it, the maneuver nodes, the next burn, the
/// attitude hold and the target - what the map and the HUD show, for a mod that wants to show, check or fly it. These are the
/// client's (they answer where the player's client runs; SE2 2.4: the same process as the server). Threads: any; members that
/// read the frames take the frame lock (marked), the rest take only the node list's own small lock or none.
/// </summary>
public static partial class OrbitalApi
{
    // ── the orbit ──

    /// <summary>(v2) The local player's orbit now, as the planner flies from it: their frame's (their own in it while riding a
    /// frame something else anchors), or the local one in a planet's cell - with the eight numbers of TryGetFrameOrbit. False:
    /// none (on the ground, or a hop that never climbs clear of it). Takes the frame lock.</summary>
    public static bool TryGetPlayerOrbit(out string body, out double semiMajorAxis, out double eccentricity, out double inclination,
        out double raan, out double argPeriapsis, out double trueAnomaly, out double epoch, out double mu)
    {
        body = null; semiMajorAxis = eccentricity = inclination = raan = argPeriapsis = trueAnomaly = epoch = mu = 0;
        if (!SystemHost.Built || !Maneuvers.Base(SystemHost.Now, out var b, out var el) || b == null || !OrbitQueries.Usable(el)) return false;
        body = b.Name;
        semiMajorAxis = el.SemiMajorAxis; eccentricity = el.Eccentricity; inclination = el.Inclination; raan = el.Raan;
        argPeriapsis = el.ArgPeriapsis; trueAnomaly = el.TrueAnomaly; epoch = el.Epoch; mu = el.Mu;
        return true;
    }

    // ── maneuver nodes ──

    /// <summary>(v2) How many maneuver nodes the player has planned (flown and deleted ones gone).</summary>
    public static int GetManeuverNodeCount() { lock (Maneuvers.Nodes) return Maneuvers.Nodes.Count; }

    /// <summary>(v2) The index-th node in TIME order (0: the earliest): its universe time (s), its delta-v as the player set it -
    /// prograde, normal, radial out (m/s, in the orbit's axes at the node) - and whether it flies itself (auto-burn). False: no
    /// such index.</summary>
    public static bool TryGetManeuverNode(int index, out double time, out double prograde, out double normal, out double radial, out bool auto)
    {
        time = prograde = normal = radial = 0; auto = false;
        lock (Maneuvers.Nodes)
        {
            var l = Maneuvers.Nodes;
            for (int i = 0; i < l.Count; i++)
            {
                var n = l[i];
                int rank = 0;   // (time order without a sorted copy: a handful of nodes)
                for (int j = 0; j < l.Count; j++) if (l[j].T < n.T || l[j].T == n.T && j < i) rank++;
                if (rank != index) continue;
                time = n.T; prograde = n.Pro; normal = n.Nor; radial = n.Rad; auto = n.Auto;
                return true;
            }
        }
        return false;
    }

    /// <summary>(v2) Plan a burn: at a universe time (s, in the future), prograde / normal / radial-out delta-v (m/s, the orbit's
    /// axes at that point of the planned trajectory), flown by itself when auto. It chains after earlier nodes as one placed on
    /// the map does, the HUD counts it down, and warp stops ahead of it. False: a time not in the future, or a value not finite.</summary>
    public static bool AddManeuverNode(double time, double prograde, double normal, double radial, bool auto)
    {
        if (!OrbitQueries.Finite(time) || !OrbitQueries.Finite(prograde) || !OrbitQueries.Finite(normal) || !OrbitQueries.Finite(radial)) return false;
        if (time <= SystemHost.Now) return false;
        Maneuvers.Restore(time, prograde, normal, radial, auto: auto);
        return true;
    }

    /// <summary>(v2) Delete every maneuver node. Returns how many there were.</summary>
    public static int ClearManeuverNodes()
    {
        int n;
        lock (Maneuvers.Nodes) n = Maneuvers.Nodes.Count;
        Maneuvers.ClearAll();
        return n;
    }

    /// <summary>(v2) The next burn as the HUD flies it (its last tick): when it starts (universe time, s: half the burn before its
    /// node), the delta-v still to do (m/s: the planned velocity less the current one), its direction (world axes, unit), whether
    /// that node flies itself, and whether the auto-burn is flying it now. False: no burn ahead.</summary>
    public static bool TryGetNextBurn(out double startTime, out double deltaVLeft, out Vector3D directionWorld, out bool auto, out bool autoFlying)
    {
        startTime = deltaVLeft = 0; directionWorld = default; auto = false; autoFlying = AutoBurn.Flying;
        if (Maneuvers.BurnLine == null) return false;
        double left = Maneuvers.BurnLeft;
        if (!(left > 0)) return false;
        startTime = SystemHost.Now + Maneuvers.BurnIn; deltaVLeft = left; directionWorld = Maneuvers.BurnDirWorld;
        double t = SystemHost.Now; Maneuvers.Node next = null;
        // (the earliest node whose burn is not over - the auto-burn's own pick)
        lock (Maneuvers.Nodes) foreach (var n in Maneuvers.Nodes) if (!Maneuvers.BurnOver(n, t) && (next == null || n.T < next.T)) next = n;
        auto = next != null && next.Auto;
        return true;
    }

    // ── attitude hold ──

    /// <summary>(v2) The attitude hold now: 0 off, 1 prograde, 2 retrograde, 3 normal, 4 anti-normal, 5 radial out, 6 radial in,
    /// 7 level (wings level, nose on the horizon).</summary>
    public static int GetAttitudeHold() => (int)AttitudeHold.Current;

    /// <summary>(v2) Hold a mode (numbers as GetAttitudeHold; 0 lets go), applied on the next client tick as the cockpit's hold
    /// keys are. The pilot's own steering, leaving the seat or losing the orbit lets go again. False: not a mode.</summary>
    public static bool SetAttitudeHold(int mode)
    {
        if (mode < 0 || mode > (int)AttitudeHold.Mode.Level) return false;
        if (mode == (int)AttitudeHold.Current) return true;
        HoldActions.Request((AttitudeHold.Mode)mode);   // (a toggle on the client tick: the same mode again would let go - not asked twice here)
        return true;
    }

    // ── targets ──

    /// <summary>(v2) The player's target: a TARGET KEY ("" none). Keys: a body's name ("Verdure"); "grid:" and a grid's member id
    /// (GetMemberId, invariant digits: "grid:1000000042"); a site's sector name (TryGetSite's sector; a ring rock's too, after its site is gone).</summary>
    public static string GetTarget() => Maneuvers.Target ?? "";

    /// <summary>(v2) Set the player's target (a target key; null or "": none). The map marks its closest approach on the plan.
    /// False: no such target now.</summary>
    public static bool SetTarget(string key)
    {
        if (string.IsNullOrEmpty(key)) { Maneuvers.Target = null; return true; }
        if (!Targets.At(key, SystemHost.Now, out _, out _)) return false;
        Maneuvers.Target = key;
        return true;
    }

    /// <summary>(v2) A target's state at a universe time (NaN: now): the body it is about and its state relative to it (m, m/s,
    /// inertial). A ship on rails: its frame's orbit plus its place in the berth; in a planet's space, the arc its motion gives.
    /// Takes the frame lock.</summary>
    public static bool TryGetTargetState(string key, double time, out string body, out Vector3D position, out Vector3D velocity)
    {
        body = null; position = velocity = default;
        if (key == null || !SystemHost.Built) return false;
        if (!Targets.At(key, double.IsNaN(time) ? SystemHost.Now : time, out var parent, out var rel) || parent == null) return false;
        body = parent.Name; position = rel.Position; velocity = rel.Velocity;
        return true;
    }

    /// <summary>(v2) The closest approach of two targets (keys) over the universe times [t0, t1] (s): when, how close (m) and the
    /// relative speed then (m/s) - the deepest pass in the window, each on its present orbit (two-body: no burn, no sphere change).
    /// False: unknown target, not about the same body now, or no pass found. Takes the frame lock; allocates (a planning call:
    /// not every frame).</summary>
    public static bool TryGetClosestApproach(string keyA, string keyB, double t0, double t1, out double time, out double distance, out double relativeSpeed)
    {
        time = distance = relativeSpeed = 0;
        if (keyA == null || keyB == null || !SystemHost.Built || !(t1 > t0) || !OrbitQueries.Finite(t0) || !OrbitQueries.Finite(t1)) return false;
        double now = SystemHost.Now;
        if (!Targets.At(keyA, now, out var pa, out var ra) || !Targets.At(keyB, now, out var pb, out var rb) || pa == null || !ReferenceEquals(pa, pb)) return false;
        if (!OrbitQueries.FromState(ra.Position, ra.Velocity, pa.Mu, now, out var ea) || !OrbitQueries.FromState(rb.Position, rb.Velocity, pa.Mu, now, out var eb)) return false;
        var ev = ClosestApproach.Find(ea, eb, t0, t1 - t0, 256);
        if (!ev.Found) return false;
        time = ev.Time; distance = ev.MissDistance; relativeSpeed = ev.RelativeSpeed;
        return true;
    }
}
