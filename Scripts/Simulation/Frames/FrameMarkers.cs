using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// GPS MARKERS ACROSS FRAMES. A GPS marker is a world position, and a world position belongs to a
/// WINDOW: a planet cell, a frame's berth (a conjunction, or an encounter site's bubble), or plain world
/// space. When the marker's window is the one the camera is in, the world position is the truth and the
/// game draws it as usual. When it is another window, the world position means nothing here: the marker
/// is FRAME-TRANSFERRED, i.e. its true place in the solar system (the other window's model position plus
/// its offset there) is taken relative to the camera's own model position, and it is drawn at that
/// relative location from the camera, with the true distance. The game's copy is hidden meanwhile (and
/// shown again the moment the windows agree). Markers this mod hid are recorded in the save, so a load
/// never leaves one hidden by mistake.
/// </summary>
public static class FrameMarkers
{
    public static bool Enabled = true;
    public static string Status = "";

    private static readonly HashSet<GPSMarker> _hidden = new HashSet<GPSMarker>();
    private static readonly HashSet<string> _restoreKeys = new HashSet<string>();

    private struct Proxy { public Vector3D World; public string Name; public double Distance; public ColorSRGB Color; }
    private static readonly List<Proxy> _proxies = new List<Proxy>();

    /// <summary>Client tick (holds nothing; takes FramesLock briefly).</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        _proxies.Clear();
        var markers = LocalMarkers(session);
        if (markers == null) return;

        if (_restoreKeys.Count > 0)
        {
            foreach (var m in markers) if (_restoreKeys.Contains(Key(m))) m.Hidden = false;
            _restoreKeys.Clear();
        }
        if (!Enabled || !SystemHost.Built) { UnhideAll(); return; }

        bool camOk;
        Vector3D camModel; string camWin; Chart camChart;
        lock (ServerFrames.FramesLock) camOk = ModelOf(camera.Position, t, out camModel, out camWin, out camChart);

        int moved = 0;
        foreach (var m in markers)
        {
            if (m == null) continue;
            bool ours = _hidden.Contains(m);
            if (m.Hidden && !ours) continue;   // hidden by the player: leave it
            Vector3D mm; string win; bool ok;
            lock (ServerFrames.FramesLock) ok = ModelOf(m.Position, t, out mm, out win, out _);
            if (!camOk || !ok || win == camWin)
            {
                if (ours) { m.Hidden = false; _hidden.Remove(m); }
                continue;
            }
            // Another window: the marker's true place, relative to the camera's.
            Vector3D d = mm - camModel;
            Vector3D dw = camChart.FromInertial(d);
            double dist = d.Length();
            if (!IsFinite(dist) || dist < 1) continue;
            if (!ours) { m.Hidden = true; _hidden.Add(m); }
            // Draw at a comfortable depth in the true direction (projection only needs the direction).
            Vector3D at = camera.Position + dw * (Math.Min(dist, 5e4) / dist);
            _proxies.Add(new Proxy { World = at, Name = m.Name, Distance = dist, Color = m.Color });
            moved++;
        }
        // Forget markers that no longer exist.
        _hidden.RemoveWhere(h => !markers.Contains(h));
        Status = $"gps {markers.Count} marker(s), {moved} frame-transferred";

        if (_proxies.Count > 0 && !MapView.Visible && !OrbitalMap.Active) Draw(session);
    }

    private static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        object config = null;
        try
        {
            var map = session.SessionComponents.TryGet<Keen.Game2.Client.WorldObjects.ColonizationMap.ColonizationMapSessionComponent>();
            if (map != null) config = PlanetRenderBridge.GetMember(map, "_configuration");
        }
        catch { }
        if (!MapPipeline.UiBegin(session, config)) return;
        try
        {
            foreach (var p in _proxies)
                MapPipeline.HudMarker(p.World, p.Name, Dist(p.Distance), p.Color);
        }
        finally { MapPipeline.UiEnd(); }
    }

    private static string Dist(double m) =>
        m >= 1e9 ? $"{m / 1e9:F2} M km" : m >= 1e6 ? $"{m / 1e3:N0} km" : m >= 1e4 ? $"{m / 1e3:F0} km" : m >= 1e3 ? $"{m / 1e3:F1} km" : $"{m:F0} m";

    /// <summary>The local player's GPS markers: their own list, their groups, and contract HUD markers.</summary>
    private static List<GPSMarker> LocalMarkers(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var players = session.Get<ClientPlayersSessionComponent>();
            var data = session.Get<IPerPlayerData>();
            if (players == null || data == null) return null;
            var id = players.LocalPlayerIdentity;
            var list = new List<GPSMarker>();
            var gps = data.GetPerPlayerData<GPSMarkersData>(id);
            // ObservableList derives from a framework type mod code cannot reference: read the lists as
            // plain enumerables.
            if (gps != null)
            {
                Add(list, PlanetRenderBridge.GetMember(gps, "GPSMarkers"));
                if (PlanetRenderBridge.GetMember(gps, "MarkersGroups") is System.Collections.IEnumerable groups)
                    foreach (var g in groups) Add(list, PlanetRenderBridge.GetMember(g, "Markers"));
            }
            var contracts = data.GetPerPlayerData<ContractPerPlayerData>(id);
            if (contracts != null) Add(list, PlanetRenderBridge.GetMember(contracts, "HUDMarkers"));
            return list;
        }
        catch { return null; }
    }

    private static void Add(List<GPSMarker> list, object enumerable)
    {
        if (enumerable is System.Collections.IEnumerable e) foreach (var o in e) if (o is GPSMarker m) list.Add(m);
    }

    /// <summary>
    /// A world point's window and its model position (sun-centred): a planet cell (the planet plus the
    /// chart-rotated offset), or a frame's berth (the frame's orbit position plus the berth offset).
    /// Caller holds FramesLock.
    /// </summary>
    public static bool ModelOf(Vector3D world, double t, out Vector3D model, out string window, out Chart chart)
    {
        model = default; window = null; chart = default;
        var reg = SystemHost.Registry;
        if (reg == null || SystemHost.Frames == null) return false;
        if (VoxelBerthRegistry.TryCellContaining(world, reg, out string body, out Vector3D cell) && reg.Find(body) is GravityBody b)
        {
            chart = Chart.Of(body, t);
            model = b.OriginInRoot(t).Position + chart.ToInertial(world - cell);
            window = "cell:" + body;
            return IsFinite(model);
        }
        ProximityFrame best = null; double bd = ServerFrames.SlotRadius;
        foreach (var f in SystemHost.Frames.Frames)
        {
            double d = (world - f.BerthCenter).Length();
            if (d <= bd) { bd = d; best = f; }
        }
        if (best == null || !(reg.Find(best.ParentBodyName) is GravityBody parent)) return false;
        model = parent.OriginInRoot(t).Position + OrbitPropagation.StateAt(best.Elements, t).Position + (world - best.BerthCenter);
        window = "frame:" + best.Id;
        return IsFinite(model);
    }

    /// <summary>Harness: every local marker, its window, and whether it is frame-transferred.</summary>
    public static string Describe(Keen.VRage.Core.Game.Systems.Session session)
    {
        var markers = LocalMarkers(session);
        if (markers == null) return "no GPS data";
        var sb = new System.Text.StringBuilder($"{markers.Count} marker(s):");
        double t = SystemHost.Now;
        int i = 0;
        foreach (var m in markers)
        {
            string win = "-";
            lock (ServerFrames.FramesLock) if (ModelOf(m.Position, t, out _, out string w, out _)) win = w;
            sb.Append($" [{i++}] '{m.Name}' {win}{(m.Hidden ? (_hidden.Contains(m) ? " (transferred)" : " (hidden)") : "")};");
        }
        return sb.ToString();
    }

    /// <summary>Harness: a copy of marker i (same type and icon) at a world point, added to the local list.</summary>
    public static string DevCloneAt(Keen.VRage.Core.Game.Systems.Session session, int i, Vector3D world, string name)
    {
        var markers = LocalMarkers(session);
        if (markers == null || i < 0 || i >= markers.Count) return "no such marker";
        var players = session.Get<ClientPlayersSessionComponent>();
        var gps = session.Get<IPerPlayerData>()?.GetPerPlayerData<GPSMarkersData>(players.LocalPlayerIdentity);
        if (gps == null) return "no GPS data";
        var m = markers[i].DeepClone();
        m.Name = name;
        m.Position = world;
        m.Hidden = false;
        return PlanetRenderBridge.CallAdd(PlanetRenderBridge.GetMember(gps, "GPSMarkers"), m) ? $"marker '{name}' added" : "add failed";
    }

    private static void UnhideAll()
    {
        foreach (var m in _hidden) try { m.Hidden = false; } catch { }
        _hidden.Clear();
    }

    private static string Key(GPSMarker m) => (m.Identifier ?? "") + "|" + m.Name;

    /// <summary>Save: the markers this mod hid (keys).</summary>
    public static List<string> HiddenKeys()
    {
        var l = new List<string>();
        foreach (var m in _hidden) try { l.Add(Key(m)); } catch { }
        return l;
    }

    /// <summary>Load: markers hidden by this mod at save time are shown again first (then re-evaluated).</summary>
    public static void RestoreHidden(string key) => _restoreKeys.Add(key);

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static bool IsFinite(Vector3D v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
