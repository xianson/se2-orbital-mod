using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Observers;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

#pragma warning disable
namespace OrbitalMod;

/// <summary>How the per-planet frame decides between real planet and proxy.</summary>
public enum ProxyMode
{
    /// <summary>Real planet inside its frame sphere, proxy outside. The intended behaviour.</summary>
    Frame,
    /// <summary>Always hide the real planet and draw the proxy. For judging the proxy up close.</summary>
    AlwaysProxy,
    /// <summary>Never touch the real planet. The mod is inert.</summary>
    AlwaysReal,
    /// <summary>Flip between real and proxy every <see cref="OrbitalConfig.AlternateSeconds"/>. For side-by-side comparison.</summary>
    Alternate,
}

/// <summary>Tunables. Edit and reload the world.</summary>
public static class OrbitalConfig
{
    public static ProxyMode Mode = ProxyMode.Frame;

    /// <summary>Frame sphere = gravity reach × this. Enter below it.</summary>
    public static double FrameEnterFactor = 1.0;
    /// <summary>Leave the frame above gravity reach × this. Hysteresis against flicker.</summary>
    public static double FrameExitFactor = 1.1;
    /// <summary>Floor on the frame sphere, in planet radii, for planets with tiny gravity reach.</summary>
    public static double MinFrameRadii = 1.5;

    /// <summary>Clamp proxy render distance (m), keeping angular size. 0 = draw at true distance.</summary>
    public static double ProxyClampDistance = 0;

    public static double AlternateSeconds = 10;

    /// <summary>Also hide atmosphere and clouds with the terrain.</summary>
    public static bool HideAtmosphere = true;
}

/// <summary>
/// Injected into every planet prefab (see PlanetInjector). On the client, decides each render
/// frame whether this planet is shown as the real voxel planet or as a proxy globe.
/// All late-bound engine access goes through <see cref="PlanetRenderBridge"/>.
/// </summary>
public partial class PlanetFrameComponent : Component, IInSceneListener
{
    /// <summary>Observer tag the engine registers for the camera (VisualEffectsCullingConfiguration).</summary>
    private static readonly StringId CameraTag = StringId.Get("VisualEffectsObserver");

    /// <summary>Give the client render components ~10 s to appear before concluding this is a server.</summary>
    private const int MaxSetupAttempts = 600;

    [Keen.VRage.DCS.Annotations.Component]
    private readonly DiscoverablePlanetComponent _discoverable;

    [Keen.VRage.DCS.Annotations.Component]
    private readonly GravityGeneratorComponent _gravity;

    private PlanetRenderBridge.PlanetHandles _handles;
    private PlanetRenderBridge.Proxy _proxy;
    private int _setupAttempts;
    private bool _disabled;
    private bool _inFrame = true;
    private bool _realShown = true;
    private string _name = "planet";

    void IInSceneListener.OnAddedToScene()
    {
        _name = Entity?.DebugName ?? "planet";
        Log.Default?.Info($"[ORBIT] PlanetFrameComponent added: {_name}");
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (PlanetRenderBridge.Lock)
        {
            if (_handles != null && !_realShown)
            {
                PlanetRenderBridge.SetTerrainVisible(_handles, true);
                PlanetRenderBridge.SetAtmosphereVisible(_handles, true);
                _realShown = true;
            }
            PlanetRenderBridge.DisposeProxy(_proxy);
            _proxy = null;
        }
    }

    [After(typeof(RenderSubmissionBegin))]
    private class OnPlanetFrame : JobGroup;

    [OnPlanetFrame]
    [MustHave(typeof(PlanetFrameComponent))]
    private static void PlanetFrameJob(PlanetFrameComponent planet, IObservers observers)
    {
        planet.Tick(observers);
    }

    private void Tick(IObservers observers)
    {
        if (_disabled || OrbitalConfig.Mode == ProxyMode.AlwaysReal && _realShown) return;
        if (!observers.TryGetFirstTransform(CameraTag, out WorldTransform camera)) return;

        lock (PlanetRenderBridge.Lock)
        {
            if (_handles == null && !TrySetup()) return;

            Vector3D center = Entity.Data.GetWorldTransform().Position;
            double distance = (center - camera.Position).Length();

            double reach = Math.Max(_gravity.AffectDistance, _handles.Radius * OrbitalConfig.MinFrameRadii);
            bool wasInFrame = _inFrame;
            _inFrame = FrameMath.UpdateInFrame(_inFrame, distance,
                reach * OrbitalConfig.FrameEnterFactor, reach * OrbitalConfig.FrameExitFactor);
            if (_inFrame != wasInFrame)
            {
                Log.Default?.Info($"[ORBIT] {_name}: {(_inFrame ? "entered" : "left")} frame at {distance / 1000:F1} km (reach {reach / 1000:F1} km)");
            }

            bool wantReal = WantReal();

            // Never hide the real planet unless a proxy can stand in for it.
            if (!wantReal && !_handles.HasProxyModel) wantReal = true;

            if (wantReal != _realShown && PlanetRenderBridge.SetTerrainVisible(_handles, wantReal))
            {
                if (OrbitalConfig.HideAtmosphere) PlanetRenderBridge.SetAtmosphereVisible(_handles, wantReal);
                _realShown = wantReal;
                Log.Default?.Info($"[ORBIT] {_name}: real planet {(wantReal ? "SHOWN" : "HIDDEN")} at {distance / 1000:F1} km");
            }

            if (!_realShown)
            {
                FrameMath.ProjectProxy(camera.Position, center, _handles.Radius, OrbitalConfig.ProxyClampDistance,
                    out Vector3D renderCenter, out double renderRadius);

                if (_proxy == null)
                {
                    _proxy = PlanetRenderBridge.CreateProxy(_handles, renderCenter);
                    Log.Default?.Info($"[ORBIT] {_name}: proxy {(_proxy != null ? "created" : "FAILED")} " +
                                      $"(model radius {_handles.ProxyModelRadius:F3}, planet radius {_handles.Radius / 1000:F1} km)");
                    if (_proxy == null)
                    {
                        // Proxy failed: put the real planet back and stop trying.
                        PlanetRenderBridge.SetTerrainVisible(_handles, true);
                        PlanetRenderBridge.SetAtmosphereVisible(_handles, true);
                        _realShown = true;
                        _disabled = true;
                        return;
                    }
                }
                PlanetRenderBridge.UpdateProxy(_handles, _proxy, renderCenter, renderRadius);
                PlanetRenderBridge.SetProxyVisible(_proxy, true);
            }
            else
            {
                PlanetRenderBridge.SetProxyVisible(_proxy, false);
            }
        }
    }

    private bool WantReal()
    {
        switch (OrbitalConfig.Mode)
        {
            case ProxyMode.AlwaysProxy: return false;
            case ProxyMode.AlwaysReal: return true;
            case ProxyMode.Alternate:
                long period = (long)(OrbitalConfig.AlternateSeconds * 1000);
                return period <= 0 || (System.Environment.TickCount64 / period) % 2 == 0;
            default: return _inFrame;
        }
    }

    private bool TrySetup()
    {
        var env = Entity.TryGet<PlanetEnvironmentRenderComponent>();
        if (env == null)
        {
            if (++_setupAttempts >= MaxSetupAttempts)
            {
                _disabled = true;
                Log.Default?.Info($"[ORBIT] {_name}: no planet render component, frame logic off (server or headless)");
            }
            return false;
        }

        _handles = PlanetRenderBridge.Resolve(env, _discoverable, _name);
        Log.Default?.Info($"[ORBIT] {_name}: resolved terrain={_handles.Terrain != null} " +
                          $"atmosphere={_handles.EnvShowArgs != null} proxyModel={_handles.HasProxyModel} " +
                          $"radius={_handles.Radius / 1000:F1} km gravityReach={_gravity.AffectDistance / 1000:F1} km");

        if (_handles.Terrain == null)
        {
            _disabled = true;
            return false;
        }
        return true;
    }
}
