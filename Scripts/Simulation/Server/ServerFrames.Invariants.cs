using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Orbital;
using SEAerospace.Rendezvous;
using SEAerospace.SystemDef;
using static OrbitalMod.Num;

#pragma warning disable
namespace OrbitalMod;

// ServerFrames (partial): the server's invariants check (the rules: Invariants.cs).
public static partial class ServerFrames
{
    static readonly Dictionary<long, double> _unframedSince = new Dictionary<long, double>();
    static readonly HashSet<long> _everFramed = new HashSet<long>();
    // (CheckInvariants' own, kept: a dictionary, a set and lists per check before - 26 KB/s, 2026-10-15. Server thread, under
    //  FramesLock)
    static readonly Dictionary<Keen.VRage.DCS.Components.Entity, OrbitalGridComponent> _invByEntity = new Dictionary<Keen.VRage.DCS.Components.Entity, OrbitalGridComponent>();
    static readonly HashSet<long> _invSeen = new HashSet<long>();
    static readonly List<long> _invGone = new List<long>();
    /// <summary>The server's invariants (Invariants), once a second, under FramesLock, after the frame step.</summary>
    internal static void CheckInvariants()
    {
        if (!Invariants.Enabled || SystemHost.Frames == null || SystemHost.Registry == null) return;
        try
        {
            double now = Wall(), N = Math.Max(1.0, SystemHost.Timescale);
            var byEntity = _invByEntity; byEntity.Clear();
            var all = GridMembers.All();
            foreach (var g in all) if (g.IsServer && g.Entity != null) byEntity[g.Entity] = g;
            foreach (var f in SystemHost.Frames.Frames) foreach (long m in f.Members) if (GridMembers.IsGridId(m)) _everFramed.Add(m);
            foreach (var f in SystemHost.Frames.Frames)
            {
                // anchor-alive
                if (GridMembers.IsGridId(f.AnchorEntityId) && GridMembers.Get(f.AnchorEntityId) == null && !GridMembers.Tombstoned(f.AnchorEntityId))
                    Invariants.Report("anchor-alive", "f" + f.Id, $"frame #{f.Id}: its anchor {f.AnchorEntityId} is no live grid ({f.Members.Count} member(s))");
                bool isStatic = StaticAnchorOf(f, out Vector3D sAt);
                double leave = LeaveRadius(isStatic) + 5000;
                foreach (long m in f.Members)
                {
                    if (!GridMembers.IsGridId(m) || !(GridMembers.Get(m) is OrbitalGridComponent g) || !g.IsServer || g.Entity == null) continue;
                    if (f.IsEncounter || EncounterFrames.IsNpc(g) || !GridMembers.IsDynamic(g)) continue;
                    Vector3D p = GridMembers.Position(g);
                    // past-edge (beyond the leave radius with 5 km to spare: a split is due within a tick or two)
                    double away = isStatic ? (p - sAt).Length() : (p - f.BerthCenter).Length();
                    if (away > leave && away < 60000) Invariants.Report("past-edge", "g" + m, $"grid {m} '{g.DisplayName}' {away / 1000:F1} km from frame #{f.Id}'s {(isStatic ? "static anchor" : "centre")}, unsplit");
                    // ship-together
                    // (the joint search kept for a second per grid - JoinedSet, the frame step's own: it ran a search of its own for
                    //  every jointed member every second before, beside the step's)
                    if (GridMembers.IsConstrained(g))
                    {
                        var set = JoinedSet(g, now);
                        if (set != null)
                            foreach (var e in set)
                                if (byEntity.TryGetValue(e, out var o) && o != g && GridMembers.IsDynamic(o))
                                {
                                    var of = SystemHost.Frames.FindByMember(o.Id);
                                    if (of != f) Invariants.Report("ship-together", "g" + m, $"grid {m} '{g.DisplayName}' in frame #{f.Id}, joined grid {o.Id} '{o.DisplayName}' in {(of == null ? "no frame" : "frame #" + of.Id)}");
                                }
                    }
                    // warp-state
                    if (N <= 1.0 && _trueRel.ContainsKey(m) && !_settle.ContainsKey(m))
                        Invariants.Report("warp-state", "g" + m, $"grid {m} '{g.DisplayName}' still steered at x1 (its warp state left over)");
                }
            }
            // on-rails: a player ship (dynamic, not an encounter's or NPC) in open space, in no frame for over 10 s
            var seen = _invSeen; seen.Clear();
            foreach (var g in all)
            {
                if (!g.IsServer || g.Entity == null || !GridMembers.IsDynamic(g) || EncounterFrames.IsNpc(g) || EncounterFrames.IsEncounterGrid(g)) continue;
                if (SystemHost.Frames.FindByMember(g.Id) != null) continue;
                if (!_everFramed.Contains(g.Id)) continue;   // (a ship let go of its frame: one never framed - the world's own, encounters off - is not this rule's)
                lock (GridMembers.Pending) if (GridMembers.Pending.ContainsKey(g.Id)) continue;
                Vector3D p = GridMembers.Position(g);
                if (VoxelBerthRegistry.TryCellContaining(p, SystemHost.Registry, out _, out _)) continue;   // (a planet's space: no frame there)
                seen.Add(g.Id);
                if (!_unframedSince.TryGetValue(g.Id, out double since)) { _unframedSince[g.Id] = now; continue; }
                if (now - since > 10) Invariants.Report("on-rails", "g" + g.Id, $"grid {g.Id} '{g.DisplayName}' in open space in no frame for {now - since:F0} s (no orbit: warp leaves it behind)");
            }
            var gone = _invGone; gone.Clear();
            foreach (long id in _unframedSince.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (long id in gone) _unframedSince.Remove(id);
            gone.Clear(); seen.Clear(); byEntity.Clear();
        }
        catch (Exception ex) { Invariants.Report("checker", "x", "the check itself failed: " + ex.Message); }
    }
}
