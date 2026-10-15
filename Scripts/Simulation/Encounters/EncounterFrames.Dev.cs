using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

// EncounterFrames (partial): DEV harness requests - a grid made a site, a target site, a far spawn.
public static partial class EncounterFrames
{
    // ───────────────────────────── harness ─────────────────────────────

    private static readonly ConcurrentQueue<(long grid, string sector)> _devSites = new ConcurrentQueue<(long, string)>();
    /// <summary>DEV: move a grid to a sector's charted centre and make it an authored site there.</summary>
    public static void RequestDevSite(long gridId, string sector) => _devSites.Enqueue((gridId, sector));

    private static readonly ConcurrentQueue<(string name, string host, double altKm, double aheadKm)> _devTargets = new ConcurrentQueue<(string, string, double, double)>();
    /// <summary>DEV: a target site of a body's own (no sector needed): a circular equatorial orbit about it at altKm, aheadKm
    /// along it from the player's frame now (its own phase when the player is not round that body).</summary>
    public static void RequestDevTarget(string name, string host, double altKm, double aheadKm) => _devTargets.Enqueue((name, host, altKm, aheadKm));

    private static void DevMakeTarget(string name, string host, double altKm, double aheadKm, double t)
    {
        var reg = SystemHost.Registry;
        var body = reg?.Find(host);
        double R = reg?.FindDefinition(host)?.RadiusMeters ?? 0;
        if (body == null || !(R > 0)) { Event($"devtarget: no body {host}"); return; }
        double r = R + altKm * 1000, n = Math.Sqrt(body.Mu / (r * r * r)), theta0 = 0;
        var pf = FrameHost.PlayerFrame;
        if (pf != null && pf.ParentBodyName == host) { var p = OrbitPropagation.StateAt(pf.Elements, t).Position; theta0 = Math.Atan2(p.Y, p.X); }
        int slot = 0;
        if (!AsteroidFrames.ReserveBerth(SystemHost.Frames.Allocator, reg, ref slot, out int sid, out Vector3D berth)) { Event("devtarget: no free berth"); return; }
        var site = new Site
        {
            Sector = name, Host = host, Label = name, Anchor = true, World = berth, OwnR = r, OwnTheta = theta0 + aheadKm * 1000 / r - n * t,
            Home = new SectorHomes.Home { Sector = name, Kind = SectorHomes.Kind.Body, Host = host },
        };
        var f = AddSite(site, t);
        if (f == null) { SystemHost.Frames.Allocator.Free(sid); Event($"devtarget: {name} has no orbit"); return; }
        Event($"devtarget: {name} round {host} at {altKm:F0} km, {aheadKm:F0} km ahead - frame #{f.Id}");
    }

    private static readonly ConcurrentQueue<long> _devFar = new ConcurrentQueue<long>();
    /// <summary>DEV: treat a grid as a far spawn from its frame (0 = the first dynamic encounter grid in a frame).</summary>
    public static void RequestDevFar(long gridId) => _devFar.Enqueue(gridId);

    private static void DevFar(long gridId, double t)
    {
        OrbitalGridComponent g = gridId != 0 ? GridMembers.Get(gridId) : null;
        if (g == null)
            foreach (var c in GridMembers.All())
                if (c.IsServer && GridMembers.IsDynamic(c) && IsEncounterGrid(c) && SystemHost.Frames.FindByMember(c.Id) != null && !GridMembers.IsConstrained(c)) { g = c; break; }
        var F = g != null ? SystemHost.Frames.FindByMember(g.Id) : null;
        if (g == null || F == null) { Event("devfar: no framed dynamic encounter grid"); return; }
        SystemHost.Frames.RemoveMember(g.Id);
        FarFrame(F, new List<OrbitalGridComponent> { g }, t);
    }

    private static void DevMakeSite(Keen.VRage.Core.Game.Systems.Session session, long gridId, string sectorName, double t)
    {
        var g = gridId != 0 ? GridMembers.Get(gridId) : null;
        if (gridId == 0)
            foreach (var c in GridMembers.All())
                if (c.IsServer && GridMembers.IsDynamic(c) && !IsEncounterGrid(c) && SystemHost.Frames.FindByMember(c.Id) == null && !GridMembers.IsConstrained(c)) { g = c; break; }
        if (g == null || !g.IsServer) { Event($"devsite: grid {gridId} not found"); return; }
        if (GridMembers.IsConstrained(g)) { Event($"devsite: grid {g.Id} is held by a constraint (moving it would break Havok)"); return; }
        gridId = g.Id;
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        if (sectors == null) { Event("devsite: no sectors"); return; }
        SectorComponent sc = null;
        foreach (var s in sectors.Sectors) if (s.Name.IndexOf(sectorName, StringComparison.OrdinalIgnoreCase) >= 0) { sc = s; break; }
        if (sc == null) { Event($"devsite: no sector '{sectorName}'"); return; }
        var old = SystemHost.Frames.FindByMember(gridId);
        if (old != null) SystemHost.Frames.RemoveMember(gridId);
        Vector3D world = sc.Area.Center;
        if (!ServerFrames.MoveGrid(g, world, Vector3D.Zero)) return;
        var f = MakeSite(sectors, GameMap.HomesBySector(sectors, SystemHost.Registry), world, g.DisplayName, t);
        if (f == null) { Event("devsite: no home for that sector"); return; }
        SystemHost.Frames.AddMember(f, gridId);
        _built = true;
    }
}
