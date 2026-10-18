using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Observers;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

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
        if (OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] PlanetFrameComponent added: {_name}");
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
    private Quaternion? _ringTurn;

    /// <summary>
    /// The proxy's turn: the planet's surface axes (its cell's world axes, body-fixed) through its spin to the celestial
    /// axes (its Chart), then into the observer's window (its own chart, when it rides a spinning cell). Identity when
    /// you are in the planet's own cell (the two cancel) - where the proxy was always right; from anywhere else the
    /// planet's spin and the windows' axes were ignored (its surface and rings stood turned).
    /// </summary>
    static Quaternion ProxyTurn(string body, SEAerospace.ObserverFrame obs, double t)
    {
        var chart = Chart.Of(body, t);
        Vector3D R(Vector3D v) => SEAerospace.PlanetBerths.SpinFromCelestial(obs, chart.ToInertial(v));
        Vector3D x = R(Vector3D.UnitX), y = R(Vector3D.UnitY), z = R(Vector3D.UnitZ);
        // (a planet without spin data, or a NaN: no turn - a NaN transform is never drawn, the proxy vanished)
        if (!GridMembers.Finite(x) || !GridMembers.Finite(y) || !GridMembers.Finite(z) || Math.Abs(x.Length() - 1) > 1e-3 || Math.Abs(z.Length() - 1) > 1e-3) return Quaternion.Identity;
        // rows are the images of the basis (VRage's row-vector matrices: Transform(UnitX) is the first row)
        var m = Matrix.Identity;
        m.M11 = (float)x.X; m.M12 = (float)x.Y; m.M13 = (float)x.Z;
        m.M21 = (float)y.X; m.M22 = (float)y.Y; m.M23 = (float)y.Z;
        m.M31 = (float)z.X; m.M32 = (float)z.Y; m.M33 = (float)z.Z;
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        if (float.IsNaN(q.X) || float.IsNaN(q.W)) return Quaternion.Identity;
        // (checked once: the quaternion must turn the axes as the charts do)
        if (!_turnChecked)
        {
            _turnChecked = true;
            Vector3 qx = Vector3.Transform(Vector3.UnitX, q), qz = Vector3.Transform(Vector3.UnitZ, q);
            double ex = (new Vector3D(qx.X, qx.Y, qx.Z) - x).Length(), ez = (new Vector3D(qz.X, qz.Y, qz.Z) - z).Length();
            if (OrbitalLog.Setup || ex > 1e-3 || ez > 1e-3) Log.Default?.Info($"[ORBIT] proxy turn check ({body}): axes off by {ex:E1} / {ez:E1}{(ex > 1e-3 || ez > 1e-3 ? " - WRONG CONVENTION" : "")}");
        }
        return q;
    }
    static bool _turnChecked;

    private void RingProxy(Vector3D at, double renderRadius, double realRadius)
    {
        _ringProxyOn = realRadius > 0;
        _ringProxyAt = at;
        _ringK = realRadius > 0 ? renderRadius / realRadius : 0;
    }

    private void SyncRings()
    {
        if (_handles == null) return;
        try { PlanetRings.Sync(Entity.Data.GetWorldTransform().Position, _realShown, _ringProxyOn, _ringProxyAt, _ringK, _ringTurn); }
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
            // (the physics service looked up once a frame per planet, not twice; its multiplier read where it was)
            Keen.VRage.Physics.IPhysics phys = null;
            try { phys = session.Get<Keen.VRage.Physics.IPhysics>(); mult = phys.GravityMultiplier; } catch { }
            try { GameMap.Safety(session); } catch { }
            try { GameMap.HideMapObjectsInFlight(session); } catch { }
            long c0 = ModCost.Start();
            try { FrameHost.Tick(session, camera, mult); } // once per frame (clock-deduped)
            catch (Exception ex) { FrameHost.Fault("FrameHost", ex); }
            ModCost.Client.Stop(c0);

            if (_handles == null && !TrySetup()) return;

            Vector3D center = Entity.Data.GetWorldTransform().Position;
            double distance = (center - camera.Position).Length();

            if (_beacon != null && _beacon.GravityReach > 0) { _law = _beacon.Gravity; _gravityReach = _beacon.GravityReach; }
            try { if (phys != null) _law.Multiplier = phys.GravityMultiplier; } catch { }
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
                if (OrbitalLog.Frames) Log.Default?.Info($"[ORBIT] {_name}: {(_inFrame ? "entered" : "left")} frame at {distance / 1000:F1} km (reach {reach / 1000:F1} km)");
            }

            bool wantReal = WantReal() || !OrbitalConfig.HideRealPlanets;

            // Never hide the real planet unless a proxy can stand in for it.
            if (!wantReal && !_handles.HasProxyModel) wantReal = true;

            if (wantReal != _realShown && PlanetRenderBridge.SetTerrainVisible(_handles, wantReal))
            {
                if (OrbitalConfig.HideAtmosphere) PlanetRenderBridge.SetAtmosphereVisible(_handles, wantReal);
                _realShown = wantReal;
                if (OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] {_name}: real planet {(wantReal ? "SHOWN" : "HIDDEN")} at {distance / 1000:F1} km");
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
                    if (OrbitalLog.Setup || _proxy == null) Log.Default?.Info($"[ORBIT] {_name}: proxy {(_proxy != null ? "created" : "FAILED")} " +
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
                NoteProxy(_beacon != null ? SystemHost.BodyNameOf(_beacon) : _name, renderCenter - camera.Position, renderRadius, _handles.SurfaceRadius > 0 ? _handles.SurfaceRadius : _handles.Radius, "legacy");
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

    /// <summary>Warm the real terrain before showing it (PlanetRenderBridge.WarmTerrain): at least WarmMinFrames, then
    /// three settled frames in a row, at most WarmMaxSeconds.</summary>
    public static bool WarmBeforeShow = true;
    public static int WarmMinFrames = 10;
    public static double WarmMaxSeconds = 8.0;   // (the proxy stands in meanwhile: a late real planet beats a coarse one)
    private int _warmFrames, _settledFrames; private long _warmStart;

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

        double radius = _handles.SurfaceRadius > 0 ? _handles.SurfaceRadius : _handles.Radius;
        var pp = SEAerospace.PlanetBerths.ProjectProxy(camera.Position, node.OriginInRoot(t).Position, obs, radius, t, drop);

        // Showing the real planet: warm its terrain first (rebuilt round the camera while dark), the proxy staying up -
        // only where a proxy is there to stand in, and no longer than WarmMaxSeconds
        bool show = wantReal;
        if (wantReal && !_realShown && WarmBeforeShow && _proxy != null && pp.RenderRadius > 0)
        {
            if (_warmFrames == 0) _warmStart = System.Diagnostics.Stopwatch.GetTimestamp();
            PlanetRenderBridge.WarmTerrain(_handles);
            _warmFrames++;
            _settledFrames = PlanetRenderBridge.TerrainSettled(_handles, _warmFrames - 1) ? _settledFrames + 1 : 0;
            double warmS = (System.Diagnostics.Stopwatch.GetTimestamp() - _warmStart) / (double)System.Diagnostics.Stopwatch.Frequency;
            show = (_warmFrames >= WarmMinFrames && _settledFrames >= 3) || warmS >= WarmMaxSeconds;
            if (show && OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] {body}: terrain warmed in {warmS:F2} s ({_warmFrames} frames{(warmS >= WarmMaxSeconds ? ", capped" : "")}; clipmaps: {PlanetRenderBridge.WarmReport(_handles)})");
        }
        else if (!wantReal && _warmFrames > 0 && !_realShown)
            PlanetRenderBridge.SetTerrainVisible(_handles, false);   // (warming called off: dark and frozen again)
        if (!wantReal || _realShown) { _warmFrames = 0; _settledFrames = 0; }

        if (show != _realShown && PlanetRenderBridge.SetTerrainVisible(_handles, show))
        {
            if (OrbitalConfig.HideAtmosphere) PlanetRenderBridge.SetAtmosphereVisible(_handles, show);
            _realShown = show;
            _warmFrames = 0; _settledFrames = 0;
            if (OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] {body}: real planet {(show ? "SHOWN" : "HIDDEN")} " +
                              $"(frames: observer in {(FrameHost.ObserverPlanet ?? "conjunction")}, {distance / 1000:F1} km)");
        }

        if (_realShown) { PlanetRenderBridge.SetProxyVisible(_proxy, false); return true; }

        if (pp.RenderRadius <= 0) { PlanetRenderBridge.SetProxyVisible(_proxy, false); return true; }

        if (_proxy == null)
        {
            _proxy = PlanetRenderBridge.CreateProxy(_handles, pp.RenderPos);
            if (OrbitalLog.Setup || _proxy == null) Log.Default?.Info($"[ORBIT] {body}: frames proxy {(_proxy != null ? "created" : "FAILED")}");
            if (_proxy == null) return true;
        }
        _ringTurn = ProxyTurn(body, obs, t);
        PlanetRenderBridge.UpdateProxy(_handles, _proxy, pp.RenderPos, pp.RenderRadius, _ringTurn);
        PlanetRenderBridge.SetProxyVisible(_proxy, true);
        NoteProxy(body, pp.RenderPos - camera.Position, pp.RenderRadius, radius, "frames");
        RingProxy(pp.RenderPos, pp.RenderRadius, radius);
        _lastProxyTrueDistance = pp.TrueDistance;
        return true;
    }

    /// <summary>DEV (DevReentry): per body, where its proxy was last drawn from the camera (world), its drawn radius, the
    /// body radius it stands for, which path drew it, when (Stopwatch).</summary>
    internal sealed class DrawnProxy { public Vector3D Dir; public double Radius, BodyRadius; public string Path; public long Wall; }
    internal static readonly Dictionary<string, DrawnProxy> Drawn = new Dictionary<string, DrawnProxy>();
    static void NoteProxy(string body, Vector3D dir, double radius, double bodyRadius, string path)
    {
        if (body == null) return;
        lock (Drawn)
        {
            if (!Drawn.TryGetValue(body, out var d)) Drawn[body] = d = new DrawnProxy();
            d.Dir = dir; d.Radius = radius; d.BodyRadius = bodyRadius; d.Path = path; d.Wall = System.Diagnostics.Stopwatch.GetTimestamp();
        }
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

        if (OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] {_name}: resolved after {_setupAttempts} frames center={ServerPlanetBeacon.Fmt(center)} " +
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
