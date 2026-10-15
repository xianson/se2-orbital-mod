using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Library.Mathematics;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// HOLDING STATION IS NOT FREE. A member holding station in its frame (dampeners on, thrust to cancel the pull:
/// ServerFrames.StationKeeping) has the relative force skipped - it would hold with a lag, a drift, fighting it - so
/// its thrusters are charged instead what producing that force would cost: each thruster on the face that pushes
/// against the pull, at the fraction of that face's thrust the force needs (as the game's dampeners share a face),
/// times its full rate. The game's own thrusters burn their full rate as soon as they fire at all (a few newtons of
/// tidal pull would cost all of it, or nothing under its 0.1% threshold): this is proportional. In warp, x N (the game
/// seconds pass N times faster). Drawn through the block's own resource sink (hydrogen for hydrogen thrusters,
/// power for ion and atmospheric): conveyors, priorities and running dry are the game's - a starved thruster loses
/// its thrust, and with it the grid stops holding.
/// InjectPlanetComponents adds this to every server thruster prefab with a powerable block.
/// </summary>
public class StationKeepCharge : Component, IInSceneListener,
    Keen.Game2.Simulation.WorldObjects.CubeBlocks.BlockStateModifiers.IConsumedResourceModifier
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly PowerableBlockComponent _power;
    [Keen.VRage.DCS.Annotations.Component]
    private readonly ThrusterComponent _thruster;

    internal static readonly List<StationKeepCharge> All = new List<StationKeepCharge>();
    static readonly Dictionary<Entity, List<StationKeepCharge>> _byGrid = new Dictionary<Entity, List<StationKeepCharge>>();
    static double _byGridAt = -1;
    // THE THRUSTERS BY GRID, KEPT (a profile, 2026-10-15: rebuilt every tick - every thruster of the world copied into an array,
    // its top parent looked up, a new list per grid - this was 1.7 MB/s, the server's largest garbage, and most of ChargeGrid's
    // 13 ms/s). Now built again only when a thruster comes or goes (_allVersion), when a holding grid's own list is found
    // stale (a thruster of it on another grid now: checked on every charge), and at most ByGridSeconds apart (a thruster
    // moved ONTO a holding grid without leaving the scene is charged within that). Its lists are pooled; server thread only
    // (ChargeGrid runs in the frame step, under FramesLock).
    public static double ByGridSeconds = 0.25;
    static int _allVersion, _byGridVersion = -1;   // (_allVersion: under lock (All))
    static readonly List<List<StationKeepCharge>> _listPool = new List<List<StationKeepCharge>>();
    static readonly List<StationKeepCharge> _rebuildBuf = new List<StationKeepCharge>();
    private float _rate;
    private double _stamp;
    private double _full = -1;
    private static FieldInfo _defField;

    /// <summary>Its full rate (the block's units per second) when it fires: the definition's ResourcesRequiredToThrust.</summary>
    double FullRate
    {
        get
        {
            if (_full >= 0) return _full;
            _full = 0;
            try
            {
                _defField ??= typeof(ThrusterComponent).GetField("_definition", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_defField?.GetValue(_thruster) is ThrusterDefinition d && d.ResourcesRequiredToThrust.HasValue) _full = (double)d.ResourcesRequiredToThrust.Value;
            }
            catch { }
            return _full;
        }
    }

    /// <summary>What it asks now (units per second), for the harness.</summary>
    public float Rate => _rate;

    void Set(float rate, double now)
    {
        _stamp = now;
        if (rate == _rate) return;
        // (only a real change: every change re-balances the grid's resource network)
        if (rate > 0 && _rate > 0 && System.Math.Abs(rate - _rate) < 0.01f * System.Math.Max(rate, _rate)) return;
        _rate = rate;
        try { _power?.UpdateConsumedResource(); } catch { }
    }

    Keen.VRage.Library.Mathematics.FixedPoint Keen.Game2.Simulation.WorldObjects.CubeBlocks.BlockStateModifiers.IConsumedResourceModifier.RecomputeConsumedResource()
        => (Keen.VRage.Library.Mathematics.FixedPoint)_rate;
    void Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.IResourceNode.GetResources(
        Keen.VRage.Library.Memory.BufferReference<Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.ResourceNodeData> resources) { }

    void IInSceneListener.OnAddedToScene() { lock (All) { All.Add(this); _allVersion++; } }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) { All.Remove(this); _allVersion++; } }


    /// <summary>The last charge, for the harness: text made when read (2026-10-15: a new string per holding grid per tick before).
    /// (Its three parts written apart: a read racing a charge may mix two charges' numbers - diagnostics only.)</summary>
    public static string Status => _stState == 0 ? "-" : _stState == 1 ? "holding: no thrusters" : $"holding: {_stN} thruster(s) on the grid asked {_stTotal:G3}/s in all";
    static volatile int _stState; static int _stN; static double _stTotal;

    /// <summary>The thrusters by grid built again (ChargeGrid's: server thread). Its old lists back to the pool.</summary>
    static void RebuildByGrid(double now, Keen.VRage.DCS.Scenes.Scene scene)
    {
        foreach (var l in _byGrid.Values) { l.Clear(); _listPool.Add(l); }
        _byGrid.Clear();
        var every = _rebuildBuf;
        lock (All) { every.Clear(); every.AddRange(All); _byGridVersion = _allVersion; }
        foreach (var c in every)
        {
            // (only the server scene's thrusters: All holds the client scene's copies too, and reading theirs from the server
            //  thread trips the game's DataGuard ("Concurrent access (read) - IsEntityAlive"; the in-game integration run,
            //  2026-10-15) - their parents were read across threads before the alive guard, unnoticed)
            if (c.Entity == null || !ReferenceEquals(c.Entity.Data.Scene, scene)) continue;
            if (!Alive(c.Entity)) continue;   // (a thruster deleted before its listener ran: its parent read threw, ~200 ms - 2026-10-15)
            var top = c.Entity.GetTopLevelParent();
            if (top == null) continue;
            if (!_byGrid.TryGetValue(top, out var l))
            {
                if (_listPool.Count > 0) { l = _listPool[_listPool.Count - 1]; _listPool.RemoveAt(_listPool.Count - 1); }
                else l = new List<StationKeepCharge>();
                _byGrid[top] = l;
            }
            l.Add(c);
        }
        every.Clear();
        _byGridAt = now;
    }

    /// <summary>
    /// A grid holding station this tick: needLocal is the force (N, the grid's axes) its thrusters would have to make
    /// against the true relative pull, faceMax the grid's thrust per face (MaxThrustData), warp the clock's rate.
    /// </summary>
    public static void ChargeGrid(Entity grid, Vector3D needLocal, Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData faceMax, double warp)
    {
        if (grid == null) return;
        double now = Wall(), total = 0; int n = 0;
        // (the thrusters by grid: kept - see _byGrid - built again when thrusters came or went, or ByGridSeconds on)
        int ver; lock (All) ver = _allVersion;
        if (ver != _byGridVersion || now - _byGridAt > ByGridSeconds) RebuildByGrid(now, grid.Data.Scene);
        _byGrid.TryGetValue(grid, out var mine);
        // (every thruster of this grid's list still on it: one moved to another grid since - a split - and the lists are built anew.
        //  A dead one counts as moved, asked before its parent is read - that threw, ~200 ms: the lists are built without it.
        //  The charge loop below runs on what this checked, its try the backstop - 2026-10-15)
        if (mine != null)
            foreach (var c in mine) { var top = Alive(c.Entity) ? c.Entity.GetTopLevelParent() : null; if (top == null || !top.Equals(grid)) { RebuildByGrid(now, grid.Data.Scene); _byGrid.TryGetValue(grid, out mine); break; } }
        if (mine == null) { _stState = 1; return; }
        var all = mine;
        // (the air it is in (no water in orbit): a thruster that makes no thrust here - an atmospheric one in vacuum - is not charged)
        float air = 0f, water = 0f;
        try { if (grid.Data.TryGet<Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData>(out var ad)) air = ad.Density; } catch { }
        Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration classes = null;
        try { classes = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.GetConfiguration<Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration>(); } catch { }
        foreach (var c in all)
        {
            try
            {
                var e = c.Entity;
                if (e == null) continue;
                float rate = 0f;
                if (e.Data.TryGet<ThrustData>(out var td) && td.MaxThrustPower > 0)
                {
                    Vector3D dir = (Vector3D)Base6Directions.GetVector(td.Direction);   // (the way it pushes the grid)
                    double f = Vector3D.Dot(needLocal, dir);
                    var reg = faceMax.Regular;
                    double face = reg.GetDirectionValue(td.Direction);
                    float eff = 1f;
                    try { if (classes != null && classes.ThrustClasses.TryGetValue(td.ThrustClass, out var cd)) eff = Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement.GridMovementCollectorComponent.GetThrustEfficiency(in cd, air, water); } catch { }
                    // (every thruster on that face makes the same fraction of its own thrust, as the game's dampeners: its fuel
                    //  at that fraction - one that pushes nothing here asks nothing)
                    if (f > 0 && face > 0 && eff > 0f) rate = (float)(c.FullRate * System.Math.Min(1.0, f / face) * System.Math.Max(1.0, warp));
                }
                c.Set(rate, now);
                total += rate; n++;
            }
            catch { }
        }
        _stN = n; _stTotal = total; _stState = 2;
    }

    /// <summary>Once a server tick: a thruster whose grid was not charged lately (it stopped holding, left its frame) asks nothing.</summary>
    [ThreadStatic] static List<StationKeepCharge> _sweep;
    public static void Sweep()
    {
        double now = Wall();
        // (into a list of this thread's, kept: an array of every thruster per call was 0.35 MB/s - a profile, 2026-10-14)
        var all = _sweep ??= new List<StationKeepCharge>();
        lock (All) { all.Clear(); all.AddRange(All); }
        foreach (var c in all) if (c._rate != 0f && now - c._stamp > 0.5) { c._rate = 0f; try { c._power?.UpdateConsumedResource(); } catch { } }
        all.Clear();
    }

    /// <summary>DEV (harness): what the thrusters of a grid ask now.</summary>
    public static string Describe(Entity grid)
    {
        double total = 0; int n = 0, on = 0; string applied = "";
        StationKeepCharge[] all; lock (All) all = All.ToArray();
        foreach (var c in all)
        {
            if (c.Entity == null || c.Entity.GetTopLevelParent() != grid) continue;
            n++;
            if (c._rate > 0)
            {
                on++; total += c._rate;
                // (what the block asks of the grid's network now - the game's own figure, ours included)
                if (applied.Length == 0) try { applied = $", a charged block applies {PlanetRenderBridge.GetMember(c._power, "_appliedConsumption")} (ours {c._rate:G3})"; } catch { }
            }
        }
        return $"{n} thruster(s), {on} charged, {total:G4}/s{applied}";
    }
}
