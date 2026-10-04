using System.Globalization;
using System.Text;
using Keen.VRage.DCS.Components;
using Keen.Game2.Simulation.GameSystems;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.Persistence;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The saved orbital state, carried by every server planet (<see cref="ServerPlanetBeacon"/>) in the
/// world save. Planets always exist, so a frame survives even when it holds only the player.
///
/// The builder is the ENGINE's EntityNameSessionComponentObjectBuilder (a string -> Entity map):
/// a mod cannot define its own builder, because the serializer generator always emits a generic
/// (DynamicObject) serializer that needs System.Linq.Expressions, which mod scripts do not get.
/// Keys used:
///  - "state:&lt;text&gt;"  -> the planet itself: the frame registry, the clock and the HighSpeed
///                          conics, as text (the value is only there because values must exist).
///  - "member:&lt;savedId&gt;" -> that grid's entity. Entity references are the one identity SE2 keeps
///                          across a save; runtime member ids are not stable and are remapped on load.
///  - "player:&lt;savedId&gt;" -> the planet: the local player's saved id (single player: whoever loads
///                          the world is that player).
/// </summary>
/// <summary>Capture / encode / decode / apply of the saved orbital state.</summary>
public static class SavedState
{
    public const string Magic = "ORBITAL-STATE 1";
    /// <summary>Slot id saved for a site's berth you were in: no lattice slot, keep the saved centre.</summary>
    const int LatentBerth = -2;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────────────────────── save ─────────────────────────────

    /// <summary>Fill a builder from the live state (server thread, at save time).</summary>
    public static void Capture(EntityNameSessionComponentObjectBuilder ob, Entity self)
    {
        ob.NamedEntities = new Dictionary<string, Entity>();
        if (self == null || !SystemHost.Built || SystemHost.Frames == null) return;
        lock (ServerFrames.FramesLock)
        {
            var snap = FramePersistence.Capture(SystemHost.Frames);
            snap.ClockEpochSeconds = SystemHost.Now;
            snap.ClockTimescale = 1.0;
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append("clock ").Append(D(SystemHost.Now)).Append('\n');
            sb.Append("next ").Append(snap.NextFrameId).Append('\n');
            foreach (var fs in snap.Frames)
            {
                // Sites are rebuilt from the world; not a site you (or your grids) are in: rebuilt, it need not
                // come back in the same berth, and a ring rock's comes back only near your orbit. Saved as a
                // plain frame on the site's orbit, you load where you were, and the site joins you (a merge).
                bool keptSite = false;
                if (EncounterFrames.IsTransient(fs.Id))
                {
                    keptSite = true;
                    var live = SystemHost.Frames.Get(fs.Id);
                    if (live == null || !EncounterFrames.HasNonNpc(live)) continue;
                    Log.Default?.Info($"[ORBIT-FRAME] save: site frame #{fs.Id} kept as a plain frame (you or your grids are in it)");
                }
                sb.Append("frame ").Append(fs.Id).Append(' ').Append(Esc(fs.ParentBodyName))
                  .Append(' ').Append(D(fs.SemiMajorAxis)).Append(' ').Append(D(fs.Eccentricity))
                  .Append(' ').Append(D(fs.Inclination)).Append(' ').Append(D(fs.Raan))
                  .Append(' ').Append(D(fs.ArgPeriapsis)).Append(' ').Append(D(fs.TrueAnomaly))
                  .Append(' ').Append(D(fs.Mu)).Append(' ').Append(D(fs.Epoch))
                  .Append(' ').Append(D(fs.VirtualVelocityX)).Append(' ').Append(D(fs.VirtualVelocityY)).Append(' ').Append(D(fs.VirtualVelocityZ))
                  .Append(' ').Append(D(fs.PendingDrainDvX)).Append(' ').Append(D(fs.PendingDrainDvY)).Append(' ').Append(D(fs.PendingDrainDvZ))
                  .Append(' ').Append(fs.AnchorEntityId).Append(' ').Append(keptSite ? LatentBerth : fs.BerthSlotId)
                  .Append(' ').Append(D(fs.BerthCenterX)).Append(' ').Append(D(fs.BerthCenterY)).Append(' ').Append(D(fs.BerthCenterZ))
                  .Append(' ').Append(fs.IsEncounter && !keptSite ? 1 : 0)   // (a kept site: a plain frame, so the rebuilt site merges with it)
                  .Append(" members");
                foreach (long m in fs.MemberIds) sb.Append(' ').Append(m);
                sb.Append('\n');
                foreach (long m in fs.MemberIds) Remember(ob, m);
            }
            if (FrameHost.TryGetHighSpeed(out string pb, out KeplerianElements pel))
            {
                sb.Append("hs ").Append(FrameHost.PlayerId).Append(' ').Append(Esc(pb)).Append(' ').Append(El(pel)).Append('\n');
                Remember(ob, FrameHost.PlayerId);
            }
            foreach (var (id, body, el) in ServerFrames.GridHighSpeedEntries())
            {
                sb.Append("hs ").Append(id).Append(' ').Append(Esc(body)).Append(' ').Append(El(el)).Append('\n');
                Remember(ob, id);
            }
            foreach (var nl in Maneuvers.SaveLines()) sb.Append(nl).Append((char)10);
            foreach (var k in FrameMarkers.HiddenKeys()) sb.Append("gpshid ").Append(Esc(k)).Append((char)10);
            // What you have seen (Contacts): rocks by label, grids by id (mapped on load as members are).
            foreach (var k in Contacts.RockKeys()) sb.Append("knownrock ").Append(Esc(k)).Append((char)10);
            foreach (long id in Contacts.GridKeys()) { sb.Append("knowngrid ").Append(id).Append((char)10); Remember(ob, id); }
            foreach (var k in Contacts.TrackedKeys())
            {
                if (k.StartsWith("r:")) sb.Append("trackedrock ").Append(Esc(k.Substring(2))).Append((char)10);
                else if (k.StartsWith("g:") && long.TryParse(k.Substring(2), NumberStyles.Integer, Inv, out long tg)) { sb.Append("trackedgrid ").Append(tg).Append((char)10); Remember(ob, tg); }
            }
            // Radar settings (not at full power), by where the block is (a save keeps grids where they were; a
            // named link to a block, not a grid, comes back detached).
            lock (SensorBlocks.Radars)
                foreach (var r in SensorBlocks.Radars)
                {
                    if (r.Entity == null || !(r.Power < 0.999f)) continue;
                    Vector3D at;
                    try { at = r.Entity.Data.GetWorldTransform().Position; } catch { continue; }
                    sb.Append("radarpower ").Append(D(at.X)).Append(' ').Append(D(at.Y)).Append(' ').Append(D(at.Z))
                      .Append(' ').Append(r.Power.ToString("R", Inv)).Append((char)10);
                }
            ob.NamedEntities["state:" + sb] = self;
            Log.Default?.Info($"[ORBIT-FRAME] saved state captured: {snap.Frames.Count} frame(s), {sb.Length} chars, {ob.NamedEntities.Count} keys");
            if (OrbitalConfig.DevHarness)
                foreach (var line in sb.ToString().Split((char)10))
                    if (line.StartsWith("frame ")) Log.Default?.Info("[ORBIT-FRAME] saved: " + line);
            if (FrameHost.PlayerId != 0) ob.NamedEntities["player:" + FrameHost.PlayerId.ToString(Inv)] = self;
        }
    }

    private static void Remember(EntityNameSessionComponentObjectBuilder ob, long id)
    {
        if (!GridMembers.IsGridId(id)) return;   // the player is the "player:" key
        var g = GridMembers.Get(id);
        if (g?.Entity != null) ob.NamedEntities["member:" + id.ToString(Inv)] = g.Entity;
    }

    // ───────────────────────────── load ─────────────────────────────

    private sealed class Pending { public string State; public Dictionary<long, Entity> Entities = new Dictionary<long, Entity>(); public long PlayerMemberId; }
    /// <summary>Radar settings from the save (where the block was, its power), set once a radar is there.</summary>
    private static readonly List<(Vector3D at, float power, double since)> _radarPower = new List<(Vector3D, float, double)>();
    private static Pending _pending;
    private static double _pendingSince = -1;
    public static string LastRestore = "none";

    static double Wall() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Server tick: the saved radar settings, onto their blocks once those have their components.</summary>
    public static void ApplyRadarSettings()
    {
        lock (_radarPower)
        {
            if (_radarPower.Count == 0) return;
            for (int i = _radarPower.Count - 1; i >= 0; i--)
            {
                var (at, pw, since) = _radarPower[i];
                RadarComponent best = null; double bd = 1.0;   // (the block within a metre of where it was)
                lock (SensorBlocks.Radars)
                    foreach (var r in SensorBlocks.Radars)
                    {
                        double d;
                        try { d = (r.Entity.Data.GetWorldTransform().Position - at).Length(); } catch { continue; }
                        if (d < bd) { bd = d; best = r; }
                    }
                if (best == null)
                {
                    if (Wall() - since > 60) { _radarPower.RemoveAt(i); Log.Default?.Info("[ORBIT-FRAME] radar setting dropped: no radar where it was"); }
                    continue;
                }
                best.Power = pw;
                _radarPower.RemoveAt(i);
                Log.Default?.Info($"[ORBIT-FRAME] radar setting restored: transmit power {pw:P0}");
            }
        }
    }

    /// <summary>No saved state waiting to be applied (frame ids are safe to hand out).</summary>
    public static bool Idle => _pending == null;

    /// <summary>Called from the planets' [Init] on load (both planets carry the same state; first wins).</summary>
    public static void OnLoaded(EntityNameSessionComponentObjectBuilder b)
    {
        if (b?.NamedEntities == null || b.NamedEntities.Count == 0 || _pending != null) return;
        var ob = new Pending();
        foreach (var kv in b.NamedEntities)
        {
            if (kv.Key.StartsWith("state:")) ob.State = kv.Key.Substring(6);
            else if (kv.Key.StartsWith("player:")) long.TryParse(kv.Key.Substring(7), NumberStyles.Integer, Inv, out ob.PlayerMemberId);
            else if (kv.Key.StartsWith("member:") && long.TryParse(kv.Key.Substring(7), NumberStyles.Integer, Inv, out long id)) ob.Entities[id] = kv.Value;

        }
        if (string.IsNullOrEmpty(ob.State)) return;
        if (!ob.State.StartsWith(Magic)) { Log.Default?.Info("[ORBIT-FRAME] saved state: unknown format, ignored"); return; }
        _pending = ob;
        Log.Default?.Info($"[ORBIT-FRAME] saved state found: {ob.State.Split('\n').Length - 1} lines, {ob.Entities?.Count ?? 0} grid entities");
    }

    /// <summary>
    /// Apply the pending state once the system is built, the player is known and every saved grid
    /// has been registered (caller holds FramesLock). Grids still missing after 20 s are dropped.
    /// </summary>
    public static void TryApply(long playerId)
    {
        var ob = _pending;
        if (ob == null || !SystemHost.Built || SystemHost.Frames == null || playerId == 0) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_pendingSince < 0) _pendingSince = now;

        // Remap saved ids -> live ids.
        var map = new Dictionary<long, long>();
        if (ob.PlayerMemberId != 0) map[ob.PlayerMemberId] = playerId;
        int missing = 0;
        foreach (var kv in ob.Entities ?? new Dictionary<long, Entity>())
        {
            long live = 0;
            try { var c = kv.Value?.TryGet<OrbitalGridComponent>(); if (c != null) live = c.Id; } catch { }
            if (live != 0) map[kv.Key] = live; else missing++;
        }
        if (missing > 0 && now - _pendingSince < 20) return;
        _pending = null;

        long Map(long id) => map.TryGetValue(id, out long l) ? l : 0;
        int frames = 0, members = 0, hs = 0;
        var lines = ob.State.Split('\n');
        foreach (var raw in lines)
        {
            var p = raw.Trim().Split(' ');
            if (p.Length < 2) continue;
            try
            {
                switch (p[0])
                {
                    case "clock":
                        SystemHost.RestoreClock(P(p[1]));
                        break;
                    case "next":
                        SystemHost.Frames.SetNextFrameId(long.Parse(p[1], Inv));
                        break;
                    case "frame":
                    {
                        var el = new KeplerianElements
                        {
                            SemiMajorAxis = P(p[3]), Eccentricity = P(p[4]), Inclination = P(p[5]), Raan = P(p[6]),
                            ArgPeriapsis = P(p[7]), TrueAnomaly = P(p[8]), Mu = P(p[9]), Epoch = P(p[10]),
                        };
                        if (!FramePersistence.IsValidElements(el, out string why)) { Log.Default?.Info($"[ORBIT-FRAME] restore: frame {p[1]} invalid ({why})"); break; }
                        // (a body this system does not have - renamed, a shifted _N suffix - left the frame on rails for good:
                        //  dropped, its grids stay where they are; a saved mu from another gravity multiplier propagated
                        //  with the old mu while reparenting used the new: the elements re-derived with the body's)
                        var fbody = SystemHost.Registry?.Find(Unesc(p[2]));
                        if (SystemHost.Registry != null && fbody == null) { Log.Default?.Info($"[ORBIT-FRAME] restore: frame {p[1]} about an unknown body '{Unesc(p[2])}': dropped"); break; }
                        if (fbody != null && fbody.Mu > 0 && Math.Abs(el.Mu - fbody.Mu) > 1e-9 * fbody.Mu)
                        {
                            var rel = OrbitalMath.ToElements(OrbitalMath.ToState(el), fbody.Mu, el.Epoch);
                            if (!FramePersistence.IsValidElements(rel, out string why2)) { Log.Default?.Info($"[ORBIT-FRAME] restore: frame {p[1]} invalid with {fbody.Name}'s mu ({why2})"); break; }
                            Log.Default?.Info($"[ORBIT-FRAME] restore: frame {p[1]} saved with mu {el.Mu:G6}, {fbody.Name}'s is {fbody.Mu:G6}: re-derived");
                            el = rel;
                        }
                        var vv = new Vector3D(P(p[11]), P(p[12]), P(p[13]));
                        var pd = new Vector3D(P(p[14]), P(p[15]), P(p[16]));
                        if (!GridMembers.Finite(vv)) vv = Vector3D.Zero;
                        if (!GridMembers.Finite(pd)) pd = Vector3D.Zero;
                        long anchor = Map(long.Parse(p[17], Inv));
                        int slot = int.Parse(p[18], Inv);
                        var center = new Vector3D(P(p[19]), P(p[20]), P(p[21]));
                        bool enc = p[22] == "1";
                        var ids = new List<long>();
                        for (int i = 24; i < p.Length; i++) { long m = Map(long.Parse(p[i], Inv)); if (m != 0) ids.Add(m); }
                        if (ids.Count == 0) break;
                        if (anchor == 0 || !ids.Contains(anchor)) anchor = ids[0];
                        var f = SystemHost.Frames.RestoreFrame(long.Parse(p[1], Inv), Unesc(p[2]), el, vv, pd, anchor, ids, slot, center, enc);
                        // A site's berth (saved because you were in it) has no lattice slot: it lives where the
                        // rock was, and so do its members' saved positions. Not a fresh slot elsewhere.
                        if (f != null && slot == LatentBerth && GridMembers.Finite(center) && center.LengthSquared() > 1)
                        {
                            if (f.BerthSlotId >= 0) SystemHost.Frames.Allocator.Free(f.BerthSlotId);
                            f.BerthSlotId = -1; f.BerthCenter = center;
                        }
                        if (f != null) Log.Default?.Info($"[ORBIT-FRAME] restored frame #{f.Id}: slot {f.BerthSlotId} berth {ServerPlanetBeacon.Fmt(f.BerthCenter)} (saved {ServerPlanetBeacon.Fmt(center)}), {ids.Count} member(s)");
                        if (f != null) { frames++; members += ids.Count; }
                        break;
                    }
                    case "node":
                    {
                        // (NaN in a node made its burn report NaN m/s left and never complete; a bad target orbit was used
                        //  as is: restored Dirty, re-planned)
                        double nt = P(p[1]), np = P(p[2]), nn = P(p[3]), nr = P(p[4]);
                        if (!(IsFin(nt) && IsFin(np) && IsFin(nn) && IsFin(nr))) { Log.Default?.Info("[ORBIT-FRAME] restore: node not finite: dropped"); break; }
                        if (p.Length >= 14)
                        {
                            string tb = Unesc(p[5]); var ta = ParseEl(p, 6);
                            bool okT = FramePersistence.IsValidElements(ta, out _) && (SystemHost.Registry == null || SystemHost.Registry.Find(tb) != null);
                            if (okT) Maneuvers.Restore(nt, np, nn, nr, tb, ta); else Maneuvers.Restore(nt, np, nn, nr);
                        }
                        else Maneuvers.Restore(nt, np, nn, nr);
                        break;
                    }
                    case "gpshid":
                        FrameMarkers.RestoreHidden(Unesc(p[1]));
                        break;
                    case "radarpower":
                        lock (_radarPower) _radarPower.Add((new Vector3D(P(p[1]), P(p[2]), P(p[3])), (float)P(p[4]), Wall()));
                        break;
                    case "trackedrock":
                        Contacts.RestoreTracked("r:" + Unesc(p[1]));
                        break;
                    case "trackedgrid":
                    {
                        long tid = Map(long.Parse(p[1], Inv));
                        if (tid != 0) Contacts.RestoreTracked("g:" + tid);
                        break;
                    }
                    case "knownrock":
                        Contacts.RestoreRock(Unesc(p[1]));
                        break;
                    case "knowngrid":
                    {
                        long kid = Map(long.Parse(p[1], Inv));
                        if (kid != 0) Contacts.RestoreGrid(kid);
                        break;
                    }
                    case "hs":
                    {
                        long id = Map(long.Parse(p[1], Inv));
                        if (id == 0) break;
                        var el = ParseEl(p, 3);
                        if (!FramePersistence.IsValidElements(el, out string hwhy) || SystemHost.Registry != null && SystemHost.Registry.Find(Unesc(p[2])) == null)
                        { Log.Default?.Info($"[ORBIT-FRAME] restore: HighSpeed state for {id} about '{Unesc(p[2])}' invalid ({hwhy ?? "unknown body"}): dropped"); break; }
                        if (id == playerId) FrameHost.RestoreHighSpeed(Unesc(p[2]), el);
                        else ServerFrames.RestoreGridHighSpeed(id, Unesc(p[2]), el);
                        hs++;
                        break;
                    }
                }
            }
            catch (Exception e) { Log.Default?.Info($"[ORBIT-FRAME] restore: bad line '{raw}': {e.Message}"); }
        }
        LastRestore = $"{frames} frame(s), {members} member(s), {hs} HighSpeed, {missing} grid(s) missing";
        Log.Default?.Info($"[ORBIT-FRAME] RESTORE from save: {LastRestore}, clock t={SystemHost.Now:F1}");
        FrameHost.LastEvent = $"{DateTime.Now:HH:mm:ss} RESTORE {LastRestore}";
    }

    // ───────────────────────────── text helpers ─────────────────────────────

    private static string D(double v) => v.ToString("R", Inv);
    private static double P(string s) => double.Parse(s, NumberStyles.Float, Inv);
    private static bool IsFin(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace("%", "%25").Replace(" ", "%20").Replace("\n", "%0A");
    private static string Unesc(string s) => s == "-" ? "" : s.Replace("%0A", "\n").Replace("%20", " ").Replace("%25", "%");

    private static string El(KeplerianElements el) =>
        $"{D(el.SemiMajorAxis)} {D(el.Eccentricity)} {D(el.Inclination)} {D(el.Raan)} {D(el.ArgPeriapsis)} {D(el.TrueAnomaly)} {D(el.Mu)} {D(el.Epoch)}";

    private static KeplerianElements ParseEl(string[] p, int i) => new KeplerianElements
    {
        SemiMajorAxis = P(p[i]), Eccentricity = P(p[i + 1]), Inclination = P(p[i + 2]), Raan = P(p[i + 3]),
        ArgPeriapsis = P(p[i + 4]), TrueAnomaly = P(p[i + 5]), Mu = P(p[i + 6]), Epoch = P(p[i + 7]),
    };
}
