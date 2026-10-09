using System;
using System.Collections.Generic;
using SEAerospace.Frames;

/// <summary>The frames' membership rules (MembershipRules) on a fake world: joined ships, NPCs, grids that vanish and come
/// back under the same id, frames that take grids from each other within a tick.</summary>
static class MembershipCases
{
    sealed class FakeWorld : IFrameWorld
    {
        public readonly Dictionary<long, (double mass, bool dyn, bool npc)> Grids = new Dictionary<long, (double, bool, bool)>();
        public readonly HashSet<long> Gone = new HashSet<long>(), Returning = new HashSet<long>();
        public readonly Dictionary<long, long> Frame = new Dictionary<long, long>();
        public readonly List<HashSet<long>> Ships = new List<HashSet<long>>();
        public void Add(long id, double mass, long frame, bool dyn = true, bool npc = false) { Grids[id] = (mass, dyn, npc); if (frame >= 0) Frame[id] = frame; }
        public void Ship(params long[] ids) => Ships.Add(new HashSet<long>(ids));
        public bool IsLive(long id) => Grids.ContainsKey(id) && !Gone.Contains(id);
        public bool MayReturn(long id) => Returning.Contains(id);
        public bool IsDynamic(long id) => Grids.TryGetValue(id, out var g) && g.dyn;
        public bool IsNpc(long id) => Grids.TryGetValue(id, out var g) && g.npc;
        public double Mass(long id) => Grids.TryGetValue(id, out var g) ? g.mass : 0;
        public IReadOnlyCollection<long> Joined(long id)
        {
            foreach (var s in Ships) if (s.Contains(id)) { var l = new List<long>(); foreach (long o in s) if (IsLive(o)) l.Add(o); return l; }
            return new[] { id };
        }
        public long FrameOf(long id) => Frame.TryGetValue(id, out long f) ? f : -1;
    }

    public static void Run(Action<string, bool, string> ok3)
    {
        void ok(string n, bool c, string d = "") => ok3(n, c, d);
        const long player = 1, F = 92, G = 37;
        // The Hercules: a 125 t hull, a 238 kg sub-grid ('Ramp Lights') and five more on hinges - all one ship.
        FakeWorld Hercules(long anchorFrame)
        {
            var w = new FakeWorld();
            w.Add(29, 238, anchorFrame); w.Add(30, 125620, anchorFrame); w.Add(31, 4000, anchorFrame);
            for (long i = 32; i <= 35; i++) w.Add(i, 300, anchorFrame);
            w.Ship(29, 30, 31, 32, 33, 34, 35);
            return w;
        }
        var all = new long[] { 29, 30, 31, 32, 33, 34, 35 };

        // 1. a light sub-grid anchoring the ship: the hull takes over
        {
            var w = Hercules(F);
            var c = MembershipRules.ElectAnchor(w, F, 29, all, player, false, out long a);
            ok("membership: a ship anchored by a light sub-grid -> its heaviest grid", c == MembershipRules.AnchorChoice.Grid && a == 30, $"{c} {a}");
        }
        // 2. the hull anchors: kept (no handover between near-twins either)
        {
            var w = Hercules(F);
            ok("membership: the heaviest grid anchoring: kept", MembershipRules.ElectAnchor(w, F, 30, all, player, false, out long a) == MembershipRules.AnchorChoice.Keep && a == 30);
            var t = new FakeWorld(); t.Add(1001, 1000, F); t.Add(1002, 1030, F); t.Ship(1001, 1002);
            ok("membership: twins within 5 %: no handover", MembershipRules.ElectAnchor(t, F, 1001, new long[] { 1001, 1002 }, player, false, out _) == MembershipRules.AnchorChoice.Keep);
        }
        // 3. the anchor respawning (an area unload): wait - never the player meanwhile
        {
            var w = Hercules(F);
            foreach (long i in all) { w.Gone.Add(i); w.Returning.Add(i); }
            var c = MembershipRules.ElectAnchor(w, F, 30, all, player, true, out long a);
            ok("membership: the anchor respawning: wait (not the player)", c == MembershipRules.AnchorChoice.Wait && a == 30, $"{c} {a}");
        }
        // 4. the anchor gone for good: the heaviest live member; none: the player; none and no player: nothing
        {
            var w = Hercules(F); w.Gone.Add(30);
            var c = MembershipRules.ElectAnchor(w, F, 30, all, player, true, out long a);
            ok("membership: the anchor gone for good: the heaviest live member", c == MembershipRules.AnchorChoice.Grid && a == 31, $"{c} {a}");
            var e = new FakeWorld(); e.Add(50, 1000, F); e.Gone.Add(50);
            ok("membership: no grid left, the player a member: the player", MembershipRules.ElectAnchor(e, F, 50, new long[] { 50, player }, player, true, out long p) == MembershipRules.AnchorChoice.Player && p == player);
            ok("membership: no grid, no player: none", MembershipRules.ElectAnchor(e, F, 50, new long[] { 50 }, player, false, out _) == MembershipRules.AnchorChoice.None);
        }
        // 5. NPC grids never anchor; a static grid is no grid anchor either
        {
            var w = new FakeWorld(); w.Add(60, 900000, F, npc: true); w.Add(61, 1000, F); w.Add(62, 5000000, F, dyn: false);
            ok("membership: the heaviest is an NPC's or static: the player's ship anchors", MembershipRules.ElectAnchor(w, F, -1, new long[] { 60, 61, 62 }, player, false, out long a) == MembershipRules.AnchorChoice.Grid && a == 61, $"{a}");
        }
        // 6. a grid of the ship that is in ANOTHER frame never anchors this one
        {
            var w = Hercules(F); w.Frame[30] = G;
            ok("membership: a heavier joined grid in another frame: no handover", MembershipRules.ElectAnchor(w, F, 29, all, player, false, out long a) != MembershipRules.AnchorChoice.Grid || a != 30);
        }
        // 7. a split carrying the joined ship: the old frame's grids and the unframed ones go; a third frame's stay
        {
            var w = Hercules(G); w.Frame.Remove(34); w.Frame[35] = 77;
            var take = MembershipRules.SplitTakes(w, G, 29, all);
            ok("membership: a split takes the ship's grids of the old frame", take.Contains(30) && take.Contains(31) && take.Contains(32) && take.Contains(33));
            ok("membership: ... and its grids in no frame (no ship left on no rails)", take.Contains(34));
            ok("membership: ... not a grid of a third frame, nor the split grid itself", !take.Contains(35) && !take.Contains(29));
        }
        // 8. the bug of 2026-10-07: after a split took the ship to frame #92, the old frame's step must not judge its grids
        {
            var w = Hercules(G);
            foreach (long i in all) w.Frame[i] = F;   // (the split re-homed them this tick)
            bool any = false; foreach (long i in all) if (MembershipRules.StillJudged(w, G, i)) any = true;
            ok("membership: grids taken by a split this tick are not judged by the old frame", !any);
            ok("membership: ... the new frame's step judges them", MembershipRules.StillJudged(w, F, 30));
            var carry = MembershipRules.ArrivalCarries(w, G, all);
            ok("membership: an arrival of the old frame carries none of them", carry.Count == 0);
        }
        // 9. the prune: never a grid that may come back; a gone one only past the grace
        {
            var w = Hercules(F); w.Gone.Add(32); w.Returning.Add(32); w.Gone.Add(33);
            ok("membership: a respawning grid is never pruned", !MembershipRules.Prune(w, 32, 600, 10));
            ok("membership: a gone grid pruned past the grace only", !MembershipRules.Prune(w, 33, 5, 10) && MembershipRules.Prune(w, 33, 11, 10));
            ok("membership: a live grid never pruned", !MembershipRules.Prune(w, 30, 600, 10));
        }
    }
}
