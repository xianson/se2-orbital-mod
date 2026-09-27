using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Physics.Data;

#pragma warning disable
namespace OrbitalMod;

/// <summary>What the server knows about a planet that its client twin does not.</summary>
public sealed class PlanetBeacon
{
    public Vector3D Center;
    public PrefabDefinition MapVisual;
    public double GravityReach;
    public GravityLaw Gravity;
    /// <summary>The planet's law as loaded, for restoring.</summary>
    public GravityLaw OriginalGravity;
    public string Name;

    /// <summary>A gravity change requested from any thread; applied on the server thread by the beacon job.</summary>
    public volatile GravityRequest PendingGravity;
}

public sealed class GravityRequest
{
    public bool Restore;
    public float Falloff;
    public float Reach;
}

/// <summary>
/// Process-wide registry of server planets, so a client planet can find its server twin by
/// position. Works in single player and on a listen host, where both scenes share a process.
/// A pure dedicated-server client never sees these; it needs a replicated or definition-level
/// pairing instead (see README, open item).
/// </summary>
public static class PlanetBeacons
{
    private static readonly List<PlanetBeacon> _beacons = new List<PlanetBeacon>();

    public static void Add(PlanetBeacon beacon)
    {
        lock (_beacons) _beacons.Add(beacon);
    }

    public static void Remove(PlanetBeacon beacon)
    {
        lock (_beacons) _beacons.Remove(beacon);
    }

    /// <summary>Nearest beacon within <paramref name="tolerance"/> metres of <paramref name="center"/>.</summary>
    public static PlanetBeacon Find(Vector3D center, double tolerance)
    {
        lock (_beacons)
        {
            PlanetBeacon best = null;
            double bestSq = tolerance * tolerance;
            foreach (var b in _beacons)
            {
                double d = (b.Center - center).LengthSquared();
                if (d <= bestSq) { bestSq = d; best = b; }
            }
            return best;
        }
    }

    /// <summary>Snapshot copy, safe to iterate.</summary>
    public static List<PlanetBeacon> All()
    {
        lock (_beacons) return new List<PlanetBeacon>(_beacons);
    }

    public static int Count
    {
        get { lock (_beacons) return _beacons.Count; }
    }
}

/// <summary>
/// Injected into server planet compositions. Publishes the planet's map globe prefab and
/// gravity reach for the client-side <see cref="PlanetFrameComponent"/>.
/// </summary>
public partial class ServerPlanetBeacon : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly DiscoverablePlanetComponent _discoverable;

    [Keen.VRage.DCS.Annotations.Component]
    private readonly GravityGeneratorComponent _gravity;

    private PlanetBeacon _beacon;

    /// <summary>The server scene's session (set by the first server planet).</summary>
    public static Keen.VRage.Core.Game.Systems.Session ServerSession;

    /// <summary>Load: the saved orbital state (frames, clock, HighSpeed conics). See <see cref="SavedState"/>.</summary>
    [Keen.VRage.DCS.Annotations.Init]
    private void InitState(Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder ob) => SavedState.OnLoaded(ob);

    /// <summary>Save: every planet writes the same global state (the first one read on load wins).</summary>
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeState(Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder ob) => SavedState.Capture(ob, Entity);

    void IInSceneListener.OnAddedToScene()
    {
        ServerSession ??= Entity.GetSession();
        _beacon = new PlanetBeacon
        {
            Center = Entity.Data.GetWorldTransform().Position,
            MapVisual = PlanetRenderBridge.GetMapVisualPrefab(_discoverable),
            GravityReach = _gravity.AffectDistance,
            Name = Entity?.DebugName ?? "planet",
        };
        PlanetRenderBridge.TryGetGravityLaw(_gravity, out double g0, out double r0, out double falloff);
        _beacon.Gravity = new GravityLaw { G0 = g0, R0 = r0, Falloff = falloff, Reach = _gravity.AffectDistance };
        _beacon.OriginalGravity = _beacon.Gravity;
        PlanetBeacons.Add(_beacon);
        Log.Default?.Info($"[ORBIT] beacon {_beacon.Name}: center={Fmt(_beacon.Center)} " +
                          $"gravity g0={g0:F2} m/s² r0={r0 / 1000:F1} km falloff={falloff:F2} reach={_beacon.GravityReach / 1000:F1} km " +
                          $"mapVisual={_beacon.MapVisual?.DebugName ?? "none"}");
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        if (_beacon != null) PlanetBeacons.Remove(_beacon);
        _beacon = null;
    }

    internal static string Fmt(Vector3D v) => $"({v.X / 1000:F1}, {v.Y / 1000:F1}, {v.Z / 1000:F1}) km";
}

public partial class ServerPlanetBeacon
{
    [After(typeof(RenderSubmissionBegin))]
    private class OnBeaconTick : JobGroup;

    /// <summary>Applies queued gravity changes on the thread that owns this (server) entity.</summary>
    [OnBeaconTick]
    [MustHave(typeof(ServerPlanetBeacon))]
    private static void BeaconJob(ServerPlanetBeacon beacon)
    {
        try { ServerFrames.Tick(beacon.Entity.GetSession()); }
        catch (Exception ex) { FrameHost.Fault("ServerFrames", ex); }
        try { ServerGravityMultiplier = beacon.Entity.GetSession().Get<Keen.VRage.Physics.IPhysics>().GravityMultiplier; } catch { }
        ApplyPlayerRequest(beacon);
        var b = beacon._beacon;
        var req = b?.PendingGravity;
        if (req == null) return;
        b.PendingGravity = null;

        float falloff = req.Restore ? (float)b.OriginalGravity.Falloff : req.Falloff;
        float reach = req.Restore ? (float)b.OriginalGravity.Reach : req.Reach;
        bool ok = PlanetRenderBridge.SetGravityLaw(beacon._gravity, falloff, reach);

        PlanetRenderBridge.TryGetGravityLaw(beacon._gravity, out double g0, out double r0, out double f);
        b.Gravity = new GravityLaw { G0 = g0, R0 = r0, Falloff = f, Reach = beacon._gravity.AffectDistance };
        b.GravityReach = b.Gravity.Reach;
        Log.Default?.Info($"[ORBIT] {b.Name}: gravity {(ok ? "set" : "FAILED")} -> falloff={f:F2} r0={r0 / 1000:F1} km reach={b.Gravity.Reach / 1000:F1} km");
    }
}

/// <summary>DEV: a change to the player's character, applied server-side by a beacon job.</summary>
public sealed class PlayerRequest
{
    public bool? Dampeners;
    public Vector3D? Velocity;
    /// <summary>HighSpeed step: set the character's world position (orientation kept).</summary>
    public Vector3D? Position;
}

public partial class ServerPlanetBeacon
{
    /// <summary>Queued from the (client-side) harness; taken by whichever beacon job runs first.</summary>
    public static PlayerRequest PendingPlayer;

    /// <summary>Server physics scene GravityMultiplier (world setting; the aero mod forces 1).</summary>
    public static float ServerGravityMultiplier = float.NaN;

    private static void ApplyPlayerRequest(ServerPlanetBeacon beacon)
    {
        var req = System.Threading.Interlocked.Exchange(ref PendingPlayer, null);
        if (req == null) return;
        ApplyToCharacter(beacon.Entity.GetSession(), req, "server");
    }

    /// <summary>Apply a player request to the first alive character of <paramref name="session"/>.</summary>
    public static string ApplyToCharacter(Keen.VRage.Core.Game.Systems.Session session, PlayerRequest req, string side)
    {
        var chars = new List<Entity>();
        if (session == null || !session.TryFillAliveCharacters(chars) || chars.Count == 0)
        {
            Log.Default?.Warning($"[ORBIT-DEV] no alive character ({side})");
            return "no character";
        }
        var ctx = chars[0].Data;
        // Toggle is a structural change (DampeningData added/removed), visible next frame.
        if (req.Dampeners.HasValue && ctx.Has<DampeningData>() != req.Dampeners.Value)
            ctx.ToggleDampeners(clearRelativeDampeners: true);
        if (req.Velocity.HasValue)
            ctx.Set(new RigidBodyData { LinearVelocity = (Vector3)req.Velocity.Value });
        if (req.Position.HasValue)
        {
            var wt = ctx.GetWorldTransform();
            ctx.Set(new WorldTransform(req.Position.Value, wt.Orientation));
        }
        string s = $"{side}: had dampeners={ctx.Has<DampeningData>()} vel={(req.Velocity.HasValue ? req.Velocity.Value.Length().ToString("F1") : "unchanged")}";
        Log.Default?.Info("[ORBIT-DEV] player " + s);
        return s;
    }
}
