using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace OrbitalMod;

/// <summary>What the server knows about a planet that its client twin does not.</summary>
public sealed class PlanetBeacon
{
    public Vector3D Center;
    public PrefabDefinition MapVisual;
    public double GravityReach;
    public GravityLaw Gravity;
    public string Name;
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
public class ServerPlanetBeacon : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly DiscoverablePlanetComponent _discoverable;

    [Keen.VRage.DCS.Annotations.Component]
    private readonly GravityGeneratorComponent _gravity;

    private PlanetBeacon _beacon;

    void IInSceneListener.OnAddedToScene()
    {
        _beacon = new PlanetBeacon
        {
            Center = Entity.Data.GetWorldTransform().Position,
            MapVisual = PlanetRenderBridge.GetMapVisualPrefab(_discoverable),
            GravityReach = _gravity.AffectDistance,
            Name = Entity?.DebugName ?? "planet",
        };
        PlanetRenderBridge.TryGetGravityLaw(_gravity, out double g0, out double r0, out double falloff);
        _beacon.Gravity = new GravityLaw { G0 = g0, R0 = r0, Falloff = falloff, Reach = _gravity.AffectDistance };
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
