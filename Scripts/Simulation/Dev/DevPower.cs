using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// DEV harness (2026-11-02, the Mech Legs Hanging Cockpit's battery test): a grid's energy stores read and driven on the server.
///   power &lt;gridId&gt;                       every store of the grid (by place: x, y, z): index, place, kind (cell-backed or not), stored / capacity, flow, mode,
///                                        and a cell-backed store's slot contents (item, fill)
///   storemode &lt;gridId&gt; &lt;i&gt; auto|charge|discharge   store i's mode (the terminal's charge / discharge switch)
///   cellout &lt;gridId&gt; &lt;i&gt;                 store i's slots emptied
///   cellin &lt;gridId&gt; &lt;i&gt; [fill] [item guid]   one item into cell-backed store i's first empty slot (default: the vanilla power cell,
///                                        fill 0.3 - its default): what the slot took (0 = refused)
/// Read and written on the server thread (GridGen.OnServerThread), the stores found as WarpBill finds them.
/// </summary>
public static class DevPower
{
    public const string PowerCell = "089240e7-2ad9-4ec9-adef-cceeda83666e";   // (ItemPowerCell: the Battery 50 slot's whitelist)

    static System.Collections.Generic.List<ResourceContainerComponent> Stores(long gridId, out string why)
    {
        why = null;
        var g = GridMembers.Get(gridId);
        var s = ServerPlanetBeacon.ServerSession;
        if (g == null || !g.IsServer || g.Entity == null) { why = "no server grid " + gridId; return null; }
        if (s == null) { why = "no server session"; return null; }
        var list = new System.Collections.Generic.List<ResourceContainerComponent>();
        foreach (var e in s.GetEntitiesOfType<ResourceContainerComponent>())
        {
            if (e.GetTopLevelParent() != g.Entity) continue;
            var c = e.TryGet<ResourceContainerComponent>();
            if (c != null) list.Add(c);
        }
        var gt = g.Entity.Data.GetWorldTransform();   // (ordered by place in the grid: x, then y, then z - an index that stays put)
        list.Sort((a, b) => { var pa = At(a, gt); var pb = At(b, gt); return pa.X != pb.X ? pa.X.CompareTo(pb.X) : pa.Y != pb.Y ? pa.Y.CompareTo(pb.Y) : pa.Z.CompareTo(pb.Z); });
        return list;
    }

    /// <summary>A store's block centre in the grid's frame (m), rounded to cm.</summary>
    static Keen.VRage.Library.Mathematics.Vector3D At(ResourceContainerComponent c, WorldTransform gt)
    {
        var d = WorldTransform.TransformDirectionInv(c.Entity.Data.GetWorldTransform().Position - gt.Position, gt);
        return new Keen.VRage.Library.Mathematics.Vector3D(System.Math.Round(d.X, 2), System.Math.Round(d.Y, 2), System.Math.Round(d.Z, 2));
    }

    public static string Power(long gridId) => GridGen.OnServerThread(() =>
    {
        var list = Stores(gridId, out var why);
        if (list == null) return why;
        var gt = GridMembers.Get(gridId).Entity.Data.GetWorldTransform();
        var sb = new System.Text.StringBuilder($"power {gridId}: {list.Count} store(s)");
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            string type = "?"; try { type = c.ResourceType?.Name.ToString() ?? "?"; } catch { }
            bool cells = c is ResourceContainerInventoryComponent;
            var at = At(c, gt);
            sb.Append($" | [{i}] at ({at.X:F2},{at.Y:F2},{at.Z:F2}) {(cells ? "cells" : "plain")} {type} stored {(double)c.CurrentChargeValue:F3} / {(double)c.MaxChargeValue:F3} flow {(double)c.CurrentChargeFlowRate:F4} mode {c.Mode}");
            var slots = c.Entity.TryGet<InventorySlotsServerComponent>();
            if (slots != null)
            {
                sb.Append(" slots:");
                for (int k = 0; k < slots.Inventories.Length; k++)
                {
                    var items = slots.Inventories[k].Items;
                    if (items.Length == 0) { sb.Append($" {k}=empty"); continue; }
                    var it = items[0];
                    var meta = it.MetaData as ResourceContainerItemMetaData;
                    sb.Append($" {k}={it.Definition.Guid} x{(double)it.Amount:F0} fill {(meta != null ? ((double)meta.FillRatio).ToString("F4") : "-")}");
                }
            }
        }
        return sb.ToString();
    });

    public static string Mode(long gridId, int i, string mode) => GridGen.OnServerThread(() =>
    {
        var list = Stores(gridId, out var why);
        if (list == null) return why;
        if (i < 0 || i >= list.Count) return $"storemode: no store {i} (grid has {list.Count})";
        ContainerMode m;
        switch (mode) { case "auto": m = ContainerMode.Auto; break; case "charge": m = ContainerMode.Charge; break; case "discharge": m = ContainerMode.Discharge; break; default: return "storemode: auto|charge|discharge"; }
        list[i].Mode = m;
        return $"storemode {gridId} [{i}]: {list[i].Mode}";
    });

    /// <summary>convey &lt;gridId&gt;: each inventory of the grid (its block's definition, its own, items) and the conveyor group(s) its block is in
    /// (ConveyorSystemComponent.TryGetGroup over the block's subgraph nodes 0..7, numbered in order met): two inventories in one group are joined
    /// by conveyors, ports included.</summary>
    public static string Convey(long gridId) => GridGen.OnServerThread(() =>
    {
        var g = GridMembers.Get(gridId);
        if (g == null || !g.IsServer || g.Entity == null) return "no server grid " + gridId;
        var sys = g.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent>();
        var conv = g.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors.ConveyorSystemComponent>();
        if (sys == null || conv == null) return $"convey {gridId}: no inventory system ({sys != null}) or conveyor system ({conv != null})";
        var groups = new System.Collections.Generic.List<Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors.ConveyorGroup>();
        var gt = g.Entity.Data.GetWorldTransform();
        var sb = new System.Text.StringBuilder($"convey {gridId}: {sys.Inventories.Count} inventories");
        foreach (var inv in sys.Inventories)
        {
            var blk = inv.Entity;
            var d = WorldTransform.TransformDirectionInv(blk.Data.GetWorldTransform().Position - gt.Position, gt);
            sb.Append($" | at ({d.X:F2},{d.Y:F2},{d.Z:F2}) {blk} inv {inv.Definition.Guid.ToString().Substring(0, 8)} items {inv.Items.Length} registered {conv.IsInventoryRegistered(inv)} groups");
            for (int n = 0; n < 8; n++)
            {
                var grp = conv.TryGetGroup(blk.DEntity, n);
                if (grp == null) continue;
                int k = groups.IndexOf(grp); if (k < 0) { groups.Add(grp); k = groups.Count - 1; }
                sb.Append($" {n}:G{k}({grp.Blocks.Count} blocks)");
            }
        }
        return sb.ToString();
    });

    /// <summary>cellout &lt;gridId&gt; &lt;i&gt;: every item out of cell-backed store i's slots (gone: a test's reset).</summary>
    public static string CellOut(long gridId, int i) => GridGen.OnServerThread(() =>
    {
        var list = Stores(gridId, out var why);
        if (list == null) return why;
        if (i < 0 || i >= list.Count) return $"cellout: no store {i} (grid has {list.Count})";
        var slots = list[i].Entity.TryGet<InventorySlotsServerComponent>();
        if (slots == null) return $"cellout: store {i} has no cell slots";
        int n = 0;
        for (int k = 0; k < slots.Inventories.Length; k++) { n += slots.Inventories[k].Items.Length; slots.Inventories[k].RemoveAllItems(); }
        return $"cellout {gridId} [{i}]: {n} item stack(s) removed";
    });

    public static string CellIn(long gridId, int i, double fill, string item) => GridGen.OnServerThread(() =>
    {
        var list = Stores(gridId, out var why);
        if (list == null) return why;
        if (i < 0 || i >= list.Count) return $"cellin: no store {i} (grid has {list.Count})";
        var slots = list[i].Entity.TryGet<InventorySlotsServerComponent>();
        if (slots == null) return $"cellin: store {i} has no cell slots";
        if (!System.Guid.TryParse(item ?? PowerCell, out var id) || !DefinitionManager.Instance.TryGetDefinition(id, out ItemDefinition def) || def == null)
            return "cellin: no item definition " + (item ?? PowerCell);
        for (int k = 0; k < slots.Inventories.Length; k++)
        {
            if (slots.Inventories[k].Items.Length != 0) continue;
            var meta = new ResourceContainerItemMetaData { FillRatio = (FixedPoint)fill };
            var took = slots.Inventories[k].AddItem(new ItemStack(def, (FixedPoint)1, meta), false);
            return $"cellin {gridId} [{i}] slot {k}: {def.Guid} fill {fill:F2} -> took {(double)took:F0}";
        }
        return $"cellin: store {i}'s {slots.Inventories.Length} slot(s) are full";
    });
}
