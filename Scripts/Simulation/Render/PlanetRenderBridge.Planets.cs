using System.Collections;
using System.Reflection;
using System.Globalization;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;

#pragma warning disable
namespace OrbitalMod;

/// <summary>The real planets: terrain and atmosphere shown or hidden (the proxies stand in while they are hidden).</summary>
public static partial class PlanetRenderBridge
{
    // ─────────────────────────────────────────────────────────────────────────
    // Real planet visibility
    // ─────────────────────────────────────────────────────────────────────────

    private static int _terrainFailures;

    /// <summary>Frames to keep re-queuing hide transitions before freezing the clipmap.</summary>
    public const int HideSettleFrames = 20;

    /// <summary>
    /// Hide or show a planet's terrain, for BOTH the detailed and the low-res clipmap.
    ///
    /// Cell visibility is a queued transition (VoxelCell.SetVisible -> _batchedCellUpdates) that is
    /// only COMMITTED by VoxelClipmap.Update -> EndBatch -> RecordBatchCommit. Update returns early
    /// when clipmap._visible is false. So the stock Visible setter (and flipping _visible first)
    /// never commits the hide for cells that already exist; it only "works" for a planet whose cells
    /// were never built. Verified in game: at 88 km the atmosphere vanished and the terrain stayed.
    ///
    /// Hide therefore takes several frames: queue hides with _visible still true, re-queue for
    /// <see cref="HideSettleFrames"/> frames (catching cells streamed in meanwhile), then set
    /// _visible = false to freeze the clipmap (and have future cells created hidden). Call
    /// <see cref="TickTerrain"/> every frame. Show is immediate: _visible = true, then queue shows.
    /// Tolerates cells that are not attached yet (the stock setter throws on those).
    /// Returns false on any failure so the caller retries next frame.
    /// </summary>
    public static bool SetTerrainVisible(PlanetHandles h, bool visible)
    {
        if (h == null) return false;
        h.HideFramesLeft = visible ? 0 : HideSettleFrames;
        return ApplyTerrain(h, visible, setFlag: visible);
    }

    /// <summary>
    /// WARM-UP before showing (2026-10-10, "often I see a low quality one" - the user): the terrain was frozen while hidden,
    /// its detail still centred where the camera last was (often a berth 2,000+ km away); shown at once, it rebuilt in view,
    /// coarse first. Warm: unfreeze it (_visible = true: the clipmap updates round the camera again) while its render roots
    /// stay deactivated - call every frame until <see cref="TerrainSettled"/>, keeping the proxy up, then
    /// <see cref="SetTerrainVisible"/>(true).
    /// </summary>
    public static bool WarmTerrain(PlanetHandles h)
    {
        if (h?.Terrain == null) return false;
        h.HideFramesLeft = 0;
        try
        {
            foreach (object clipmap in h.Clipmaps) SetMember(clipmap, "_visible", true);
        }
        catch { return false; }
        return ApplyTerrain(h, false, setFlag: false);   // (roots made since last frame: dark too)
    }

    /// <summary>
    /// The terrain has caught up since the warm-up began (warmFrame 0 starts the count): every clipmap has been through one
    /// whole rebuild round the camera (VoxelClipmap._updateState left Idle and came back to it: a batch done), or had
    /// nothing to rebuild (Idle all along, for 20 frames). Waiting for "idle with nothing pending" never ended for a moving
    /// camera - an orbit re-centres the clipmap continually (it always hit the cap). Unknown (reflection failed): true.
    /// </summary>
    public static bool TerrainSettled(PlanetHandles h, int warmFrame)
    {
        if (h?.Terrain == null) return true;
        try
        {
            if (warmFrame <= 0 || h.WarmBusy == null) { h.WarmBusy = new Dictionary<object, int>(ReferenceEqualityComparer.Instance); }
            bool all = true;
            foreach (object clipmap in h.Clipmaps)
            {
                object st = GetMember(clipmap, "_updateState");
                if (st == null) return true;
                bool idle = st.ToString() == "Idle";
                h.WarmBusy.TryGetValue(clipmap, out int phase);   // 0 not seen busy, 1 busy, 2 a rebuild done
                if (!idle && phase == 0) phase = 1;
                else if (idle && phase == 1) phase = 2;
                h.WarmBusy[clipmap] = phase;
                if (phase != 2 && !(phase == 0 && idle && warmFrame >= 20)) all = false;
            }
            return all;
        }
        catch { return true; }
    }

    /// <summary>The warm-up's state, for the log: per clipmap its phase (done / busy and its state / idle all along).</summary>
    public static string WarmReport(PlanetHandles h)
    {
        if (h?.Terrain == null || h.WarmBusy == null) return "-";
        int done = 0, idle = 0; var busy = new List<string>();
        foreach (object clipmap in h.Clipmaps)
        {
            h.WarmBusy.TryGetValue(clipmap, out int phase);
            if (phase == 2) done++;
            else if (phase == 0) idle++;
            else busy.Add(GetMember(clipmap, "_updateState")?.ToString() ?? "?");
        }
        return $"{done} rebuilt, {idle} idle throughout, {busy.Count} still busy{(busy.Count > 0 ? " (" + string.Join(", ", busy) + ")" : "")}";
    }

    /// <summary>Advance a pending hide. Cheap no-op when nothing is pending.</summary>
    public static void TickTerrain(PlanetHandles h)
    {
        if (h == null || h.HideFramesLeft <= 0) return;
        h.HideFramesLeft--;
        // Last frame: queue once more AND freeze.
        ApplyTerrain(h, false, setFlag: h.HideFramesLeft == 0);
    }

    private static bool ApplyTerrain(PlanetHandles h, bool visible, bool setFlag)
    {
        if (h?.Terrain == null) return false;
        try
        {
            foreach (object clipmap in h.Clipmaps)
            {
                if (setFlag || visible) SetMember(clipmap, "_visible", visible);

                // Every cell mesh hangs off a render RootEntity from the clipmap's
                // DistributedRootEntityProvider (one root per 1 km block, created on demand under
                // lock(_rootEntities)). Deactivating a root hides everything under it immediately,
                // with no dependence on the clipmap's batch commit. Take the SAME lock Keen takes.
                object provider = GetMember(clipmap, "RootEntityProvider");
                if (!(GetMember(provider, "_rootEntities") is IDictionary roots)) continue;
                var snapshot = new List<object>();
                lock (roots)
                {
                    foreach (object root in roots.Values) snapshot.Add(root);
                }
                foreach (object root in snapshot)
                {
                    if (GetMember(root, "IsValid") is bool ok && !ok) continue;
                    RootMethod(root.GetType(), visible)?.Invoke(root, null);
                }
            }
            return true;
        }
        catch (Exception e)
        {
            if (++_terrainFailures <= 5) Log.Default?.Warning($"[ORBIT] terrain visibility failed ({_terrainFailures}): {Inner(e)}");
            return false;
        }
    }

    private static MethodInfo _rootActivate, _rootDeactivate, _rootUpdateTransform;

    private static MethodInfo RootMethod(Type rootType, bool activate)
    {
        _rootActivate ??= rootType.GetMethod("Activate", Type.EmptyTypes);
        _rootDeactivate ??= rootType.GetMethod("Deactivate", Type.EmptyTypes);
        return activate ? _rootActivate : _rootDeactivate;
    }

    public static bool SetAtmosphereVisible(PlanetHandles h, bool visible)
    {
        if (h?.EnvShowArgs == null) return false;
        try
        {
            h.EnvSetParameters.Invoke(h.Environment, visible ? h.EnvShowArgs : h.EnvHideArgs);
            return true;
        }
        catch (Exception e) { WarnOnce("atmo-set", $"SetParameters failed: {Inner(e)}"); return false; }
    }
}
