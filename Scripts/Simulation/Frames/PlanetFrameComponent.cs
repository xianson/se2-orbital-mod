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
    /// <summary>
    /// Planet day (sidereal), seconds: -1 = the world's own sun period (keeps SE2's day length; no
    /// spin when the world's sun does not rotate), 0 = planets do not spin, &gt; 0 = that period.
    /// </summary>
    public static double PlanetDaySeconds = -1;
    /// <summary>Fictitious forces in a spinning cell apply above this speed or altitude only (the
    /// surface is at rest in the chart; slow ground traffic sees corrections far below friction).</summary>
    public static double FictitiousMinSpeed = 20.0, FictitiousMinAltitude = 5000.0;

    /// <summary>
    /// TOTAL PARTITION for legacy space (the world outside every planet cell, e.g. the spawn area):
    /// capture its dynamic grids and the player into conjunction frames, treating that space as a
    /// window around the nearest planet (celestial = planet + world offset). OFF by default: objects
    /// there are nearly at rest relative to the planet, so their captured orbits are radial falls and
    /// the whole area would drop into the planet within the hour. A world designed for orbits (things
    /// placed with orbital velocity) wants it on.
    /// </summary>
    public static bool CaptureLegacySpace = false;

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

    /// <summary>Master switch for hiding real planets. Off = proxies can still be drawn (debug) but real planets are never touched.</summary>
    public static bool HideRealPlanets = true;

    /// <summary>
    /// DEBUG: park every proxy globe in front of the camera, side by side, at
    /// <see cref="DebugDistance"/> and <see cref="DebugAngularDiameterDeg"/>, regardless of frame.
    /// For eyeballing the proxy's look without flying to a planet. Leave false.
    /// </summary>
    public static bool DebugProxyInFront = false;

    /// <summary>Draw the predicted orbit around the dominant planet, with an element readout.</summary>
    public static bool ShowOrbit = true;

    /// <summary>
    /// DEV ONLY: poll %TEMP%\OrbitalMod\cmd.txt for teleport/view commands (see DevHarness), and the
    /// F8 debug panel. On only where the developer's harness has put %TEMP%\OrbitalMod\dev.flag (the
    /// test launcher does): a player's install never runs harness commands.
    /// </summary>
    public static bool DevHarness => _dev ??= DevFlagPresent();
    private static bool? _dev;
    private static bool DevFlagPresent()
    {
        try { return System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrbitalMod", "dev.flag")); }
        catch { return false; }
    }
    public static double DebugDistance = 300;
    public static double DebugAngularDiameterDeg = 12;
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

    /// <summary>Frames to wait for render components and the server beacon (~20 s) before giving up.</summary>
    private const int MaxSetupAttempts = 1200;

    /// <summary>How far a server beacon may sit from this client planet and still be its twin.</summary>
    private const double BeaconTolerance = 1000.0;

    /// <summary>Frame sphere in planet radii when no server beacon supplies the gravity reach.</summary>
    private const double FallbackReachRadii = 3.0;

    private PlanetRenderBridge.PlanetHandles _handles;
    private double _gravityReach;
    private GravityLaw _law;
    private PlanetBeacon _beacon;
    private string _planetName = "planet";
    private PlanetRenderBridge.Proxy _proxy;
    private int _setupAttempts;
    private bool _disabled;
    private bool _inFrame = true;
    private bool _realShown = true;
    private string _name = "planet";
    private int _debugSlot = -1;
    private static int _nextDebugSlot;

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
            try { PlanetRings.Release(Entity.Data.GetWorldTransform().Position); }
            catch (Exception e) { Log.Default?.Warning("[ORBIT] ring release failed: " + e.Message); }
        }
    }

    // What this frame did with the proxy, for the planet's rings (PlanetRings.Sync at the end of the tick).
    private bool _ringProxyOn;
    private Vector3D _ringProxyAt;
    private double _ringK;

    private void RingProxy(Vector3D at, double renderRadius, double realRadius)
    {
        _ringProxyOn = realRadius > 0;
        _ringProxyAt = at;
        _ringK = realRadius > 0 ? renderRadius / realRadius : 0;
    }

    private void SyncRings()
    {
        if (_handles == null) return;
        try { PlanetRings.Sync(Entity.Data.GetWorldTransform().Position, _realShown, _ringProxyOn, _ringProxyAt, _ringK); }
        catch (Exception e) { FrameHost.Fault("PlanetRings", e); }
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
        // The whole job body guarded: an exception here would escape into the engine's job (fatal).
        try { TickBody(observers); }
        catch (Exception ex) { FrameHost.Fault("PlanetFrame job", ex); }
    }

    private void TickBody(IObservers observers)
    {
        if (_disabled) return;
        if (!observers.TryGetFirstTransform(CameraTag, out WorldTransform camera)) return;

        lock (PlanetRenderBridge.Lock)
        {
            var session = Entity.GetSession();
            DevHarness.Poll(session, camera); // rate-limited; whichever planet ticks first runs it
            if (SpecCam.Current.HasValue) camera = SpecCam.Current.Value; // DEV spectator: build proxies for its viewpoint
            double mult = 1;
            try { mult = session.Get<Keen.VRage.Physics.IPhysics>().GravityMultiplier; } catch { }
            try { GameMap.Safety(session); } catch { }
            try { FrameHost.Tick(session, camera, mult); } // once per frame (clock-deduped)
            catch (Exception ex) { FrameHost.Fault("FrameHost", ex); }

            if (_handles == null && !TrySetup()) return;

            Vector3D center = Entity.Data.GetWorldTransform().Position;
            double distance = (center - camera.Position).Length();

            if (_beacon != null && _beacon.GravityReach > 0) { _law = _beacon.Gravity; _gravityReach = _beacon.GravityReach; }
            try { _law.Multiplier = session.Get<Keen.VRage.Physics.IPhysics>().GravityMultiplier; } catch { }
            PlanetRenderBridge.TickTerrain(_handles);
            if (FrameHost.PlayerFrame == null && !MapView.Visible) OrbitDisplay.Consider(this, session, camera, center, _handles.Radius, _law, _planetName);

            // FRAMES MODE (the SE-Aerospace model): the observer is in a planet cell or a conjunction.
            _ringProxyOn = false;
            if (TickFramesMode(camera, distance)) { SyncRings(); return; }

            if (OrbitalConfig.Mode == ProxyMode.AlwaysReal && _realShown && !OrbitalConfig.DebugProxyInFront) { SyncRings(); return; }

            double reach = Math.Max(_gravityReach,_handles.Radius * OrbitalConfig.MinFrameRadii);
            bool wasInFrame = _inFrame;
            _inFrame = FrameMath.UpdateInFrame(_inFrame, distance,
                reach * OrbitalConfig.FrameEnterFactor, reach * OrbitalConfig.FrameExitFactor);
            if (_inFrame != wasInFrame)
            {
                Log.Default?.Info($"[ORBIT] {_name}: {(_inFrame ? "entered" : "left")} frame at {distance / 1000:F1} km (reach {reach / 1000:F1} km)");
            }

            bool wantReal = WantReal() || !OrbitalConfig.HideRealPlanets;

            // Never hide the real planet unless a proxy can stand in for it.
            if (!wantReal && !_handles.HasProxyModel) wantReal = true;

            if (wantReal != _realShown && PlanetRenderBridge.SetTerrainVisible(_handles, wantReal))
            {
                if (OrbitalConfig.HideAtmosphere) PlanetRenderBridge.SetAtmosphereVisible(_handles, wantReal);
                _realShown = wantReal;
                Log.Default?.Info($"[ORBIT] {_name}: real planet {(wantReal ? "SHOWN" : "HIDDEN")} at {distance / 1000:F1} km");
            }

            if (!_realShown || OrbitalConfig.DebugProxyInFront)
            {
                FrameMath.ProjectProxy(camera.Position, center, _handles.SurfaceRadius > 0 ? _handles.SurfaceRadius : _handles.Radius, OrbitalConfig.ProxyClampDistance,
                    out Vector3D renderCenter, out double renderRadius);

                if (OrbitalConfig.DebugProxyInFront)
                {
                    if (_debugSlot < 0) _debugSlot = _nextDebugSlot++;
                    Vector3D forward = (Vector3D)Vector3.Transform(Vector3.Forward, camera.Orientation);
                    Vector3D right = (Vector3D)Vector3.Transform(Vector3.Right, camera.Orientation);
                    renderRadius = OrbitalConfig.DebugDistance * Math.Tan(OrbitalConfig.DebugAngularDiameterDeg * Math.PI / 360.0);
                    double spacing = renderRadius * 2.4;
                    renderCenter = camera.Position + forward * OrbitalConfig.DebugDistance
                                   + right * ((_debugSlot + 0.6) * spacing);
                }

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
                if (!OrbitalConfig.DebugProxyInFront) RingProxy(renderCenter, renderRadius, _handles.SurfaceRadius > 0 ? _handles.SurfaceRadius : _handles.Radius);
            }
            else
            {
                PlanetRenderBridge.SetProxyVisible(_proxy, false);
            }
            SyncRings();
        }
    }

    private bool _inKeep;

    /// <summary>
    /// Rendering under the frames model. Returns false when there is no published observer frame
    /// (legacy space: the literal world, handled by the older gravity-reach logic).
    ///  - Observer in THIS planet's cell: real voxel within the keep envelope (latched: demand below
    ///    keep, drop above keep × 1.02, as SE1's materializer); beyond it, a proxy at the true place.
    ///  - Observer in a conjunction or another planet's cell: real voxel hidden, proxy at the
    ///    frame-relative celestial offset (PlanetBerths.ProjectProxy: true direction, true angular
    ///    size, render distance clamped at 2000 km).
    /// </summary>
    private bool TickFramesMode(WorldTransform camera, double distance)
    {
        if (!SystemHost.Built || !FrameHost.Observer.HasValue) return false;
        string body = _beacon != null ? SystemHost.BodyNameOf(_beacon) : null;
        var reg = SystemHost.Registry;
        var node = body != null ? reg?.Find(body) : null;
        var def = body != null ? reg?.FindDefinition(body) : null;
        if (node == null || def == null) return false;

        var obs = FrameHost.Observer.Value;
        double t = SystemHost.Now;
        bool own = FrameHost.ObserverPlanet == body;
        double keep = SEAerospace.PlanetBerths.KeepRadius(def);
        double drop = SEAerospace.PlanetBerths.KeepDropRadius(def);
        _inKeep = own && (_inKeep ? distance < drop : distance < keep);

        bool wantReal = _inKeep;
        if (OrbitalConfig.Mode == ProxyMode.AlwaysReal) wantReal = true;
        else if (OrbitalConfig.Mode == ProxyMode.AlwaysProxy) wantReal = false;
        if (!OrbitalConfig.HideRealPlanets || !_handles.HasProxyModel) wantReal = true;

        if (wantReal != _realShown && PlanetRenderBridge.SetTerrainVisible(_handles, wantReal))
        {
            if (OrbitalConfig.HideAtmosphere) PlanetRenderBridge.SetAtmosphereVisible(_handles, wantReal);
            _realShown = wantReal;
            Log.Default?.Info($"[ORBIT] {body}: real planet {(wantReal ? "SHOWN" : "HIDDEN")} " +
                              $"(frames: observer in {(FrameHost.ObserverPlanet ?? "conjunction")}, {distance / 1000:F1} km)");
        }

        if (_realShown) { PlanetRenderBridge.SetProxyVisible(_proxy, false); return true; }

        double radius = _handles.SurfaceRadius > 0 ? _handles.SurfaceRadius : _handles.Radius;
        var pp = SEAerospace.PlanetBerths.ProjectProxy(camera.Position, node.OriginInRoot(t).Position, obs, radius, t, drop);
        if (pp.RenderRadius <= 0) { PlanetRenderBridge.SetProxyVisible(_proxy, false); return true; }

        if (_proxy == null)
        {
            _proxy = PlanetRenderBridge.CreateProxy(_handles, pp.RenderPos);
            Log.Default?.Info($"[ORBIT] {body}: frames proxy {(_proxy != null ? "created" : "FAILED")}");
            if (_proxy == null) return true;
        }
        PlanetRenderBridge.UpdateProxy(_handles, _proxy, pp.RenderPos, pp.RenderRadius);
        PlanetRenderBridge.SetProxyVisible(_proxy, true);
        RingProxy(pp.RenderPos, pp.RenderRadius, radius);
        _lastProxyTrueDistance = pp.TrueDistance;
        return true;
    }

    /// <summary>Last frames-mode proxy distance (m), for the harness status.</summary>
    internal double _lastProxyTrueDistance;

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
        _setupAttempts++;
        var env = Entity.TryGet<PlanetEnvironmentRenderComponent>();
        Vector3D center = Entity.Data.GetWorldTransform().Position;
        PlanetBeacon beacon = PlanetBeacons.Find(center, BeaconTolerance);

        // Wait for both the render side and the server twin; give up on the beacon eventually.
        bool lastChance = _setupAttempts >= MaxSetupAttempts;
        if (env == null || (beacon == null && !lastChance))
        {
            if (lastChance)
            {
                _disabled = true;
                Log.Default?.Info($"[ORBIT] {_name}: no planet render component, frame logic off");
            }
            return false;
        }

        _handles = PlanetRenderBridge.Resolve(env, beacon?.MapVisual, _name);
        _gravityReach = beacon != null && beacon.GravityReach > 0
            ? beacon.GravityReach
            : _handles.Radius * FallbackReachRadii;
        _law = beacon?.Gravity ?? default;
        _beacon = beacon;
        _planetName = beacon != null ? DevHarness.PlanetName(beacon) : _name;
        MapGlobes.Register(_handles, center);

        Log.Default?.Info($"[ORBIT] {_name}: resolved after {_setupAttempts} frames center={ServerPlanetBeacon.Fmt(center)} " +
                          $"beacon={(beacon != null ? beacon.Name : "NONE")} (of {PlanetBeacons.Count}) " +
                          $"terrain={_handles.Terrain != null} atmosphere={_handles.EnvShowArgs != null} " +
                          $"proxyModel={_handles.HasProxyModel} modelRadius={_handles.ProxyModelRadius:F3} " +
                          $"radius={_handles.Radius / 1000:F1} km reach={_gravityReach / 1000:F1} km");

        if (_handles.Terrain == null)
        {
            _disabled = true;
            return false;
        }
        return true;
    }
}
