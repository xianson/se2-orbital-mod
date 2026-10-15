using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using static OrbitalMod.HudMath;

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

    private struct Proxy { public Vector3D World; public string Name; public double Distance; public ColorSRGB Color; public GPSMarker Marker; }
    private static readonly List<Proxy> _proxies = new List<Proxy>();

    /// <summary>Client tick (holds nothing; takes FramesLock briefly).</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        _proxies.Clear();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Prewarm.Tick();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        try { Contacts.Tick(session, camera, t); } catch (Exception ex) { FrameHost.Fault("Contacts", ex); }
        long a2 = GC.GetAllocatedBytesForCurrentThread();
        try { ContactsTab.Tick(session); } catch (Exception ex) { FrameHost.Fault("ContactsTab", ex); }
        long a3 = GC.GetAllocatedBytesForCurrentThread();
        AllocParts[0] += a1 - a0; AllocParts[1] += a2 - a1; AllocParts[2] += a3 - a2;
        var markers = LocalMarkers(session, _tickMarkers);
        AllocParts[3] += GC.GetAllocatedBytesForCurrentThread() - a3;
        try { TickRest(session, camera, t, markers); }
        finally { AllocParts[4] += GC.GetAllocatedBytesForCurrentThread() - a3; }
    }
    /// <summary>(diagnostics, harness aeroget OrbitalMod.FrameMarkers.AllocParts: bytes allocated since load by prewarm,
    /// contacts, contacts tab, the marker list, the rest; and of the rest, the body/contact markers' collect and the draw)</summary>
    public static long[] AllocParts = new long[7];
    public static string AllocPartsText => $"prewarm {AllocParts[0] >> 10} contacts {AllocParts[1] >> 10} tab {AllocParts[2] >> 10} markers {AllocParts[3] >> 10} rest {(AllocParts[4] - AllocParts[3]) >> 10} (collect {AllocParts[5] >> 10} draw {AllocParts[6] >> 10}) KB";

    static void TickRest(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t, List<GPSMarker> markers)
    {
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
            _proxies.Add(new Proxy { World = at, Name = m.Name, Distance = dist, Color = m.Color, Marker = m });
            moved++;
        }
        // Forget markers that no longer exist. (a loop, not RemoveWhere with a lambda: it captured the list - a closure and a
        //  delegate every frame, made on entry to this method even when it returned early)
        if (_hidden.Count > 0)
        {
            var gone = _gone; gone.Clear();
            foreach (var h in _hidden) if (!markers.Contains(h)) gone.Add(h);
            for (int i = 0; i < gone.Count; i++) _hidden.Remove(gone[i]);
            gone.Clear();
        }
        if (StatusDue(ref _statusAt)) Status = $"gps {markers.Count} marker(s), {moved} frame-transferred{(MapPipeline.GpsError.Length > 0 ? " (game draw: " + MapPipeline.GpsError + ")" : "")}";

        BodyMarkers._session = session;
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        if (camOk) BodyMarkers.Collect(camera, camModel, camChart, t); else BodyMarkers.Clear();
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        if ((_proxies.Count > 0 || BodyMarkers.Any) && !MapView.Visible) Draw(session);
        AllocParts[5] += b1 - b0; AllocParts[6] += GC.GetAllocatedBytesForCurrentThread() - b1;
    }

    /// <summary>Open a HUD draw batch in the map's font (the caller ends it with MapPipeline.UiEnd).</summary>
    public static bool BeginHud(Keen.VRage.Core.Game.Systems.Session session)
    {
        // The flight HUD is not drawn while a screen has the UI (the terminal, a dialog, a menu): it
        // showed through the terminal (a world without map data opened on a dim flight HUD).
        if (!MapView.Visible && GameUi.TopScreenNeedingInput(session) != null) return false;
        object config = null;
        try
        {
            var map = session.SessionComponents.TryGet<Keen.Game2.Client.WorldObjects.ColonizationMap.ColonizationMapSessionComponent>();
            if (map != null) config = PlanetRenderBridge.GetMember(map, "_configuration");
        }
        catch { }
        return MapPipeline.UiBegin(session, config);
    }

    private static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (!BeginHud(session)) return;
        try
        {
            BodyMarkers.Draw(session);   // (first: your own points draw over them)
            foreach (var p in _proxies)
                if (!MapPipeline.GameMarker(session, p.Marker, p.World, p.Distance))
                    MapPipeline.HudMarker(p.World, p.Name, Dist(p.Distance), p.Color);
        }
        finally { MapPipeline.UiEnd(); }
    }

    private static string Dist(double m) =>
        m >= 1e9 ? $"{m / 1e9:F2} M km" : m >= 1e6 ? $"{m / 1e3:N0} km" : m >= 1e4 ? $"{m / 1e3:F0} km" : m >= 1e3 ? $"{m / 1e3:F1} km" : $"{m:F0} m";

    /// <summary>
    /// Where the game draws a marker of its own (your GPS points, contracts' markers, and the antenna
    /// broadcasts it shows): world positions. A contact already marked by the game gets no second marker.
    /// </summary>
    public static List<Vector3D> GameMarked(Keen.VRage.Core.Game.Systems.Session session)
    {
        // (reused: called every frame by BodyMarkers - its own list, as the tick's markers have theirs)
        var l = _gameMarked; l.Clear();
        var markers = LocalMarkers(session, _gmMarkers);
        if (markers != null) foreach (var m in markers) if (m != null && !m.Hidden) l.Add(m.Position);
        foreach (var m in _hidden) if (m != null) l.Add(m.Position);   // (frame-transferred: still the game's marker)
        try
        {
            var comp = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent>();
            var ant = comp != null ? PlanetRenderBridge.GetMember(comp, "_antennas") : null;
            var active = ant != null ? PlanetRenderBridge.GetMember(ant, "ActiveMarkers") : null;
            if (active is System.Collections.IList al) { for (int i = 0; i < al.Count; i++) if (al[i] is GPSMarker gm) l.Add(gm.Position); }   // (by index: no enumerator)
            else if (active is System.Collections.IEnumerable ae) foreach (var o in ae) if (o is GPSMarker gm) l.Add(gm.Position);
        }
        catch { }
        return l;
    }

    /// <summary>The local player's GPS markers: their own list, their groups, and contract HUD markers.</summary>
    private static List<GPSMarker> LocalMarkers(Keen.VRage.Core.Game.Systems.Session session) => LocalMarkers(session, new List<GPSMarker>());

    // (the per-frame callers' own lists, reused - no list a frame: 2026-10-14, Orbital's client allocated ~0.9 MB/s and the
    //  game's 2.5 GB heap paid for it in 230-400 ms full collections)
    static readonly List<GPSMarker> _tickMarkers = new List<GPSMarker>(), _gmMarkers = new List<GPSMarker>();
    static readonly List<Vector3D> _gameMarked = new List<Vector3D>();
    static readonly List<GPSMarker> _gone = new List<GPSMarker>();
    static double _statusAt;
    /// <summary>A status line rebuilt at most once a second (its string is garbage every frame otherwise).</summary>
    internal static bool StatusDue(ref double at)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < at) return false;
        at = now + 1.0; return true;
    }

    private static List<GPSMarker> LocalMarkers(Keen.VRage.Core.Game.Systems.Session session, List<GPSMarker> list)
    {
        list.Clear();
        try
        {
            var players = session.Get<ClientPlayersSessionComponent>();
            var data = session.Get<IPerPlayerData>();
            if (players == null || data == null) return null;
            var id = players.LocalPlayerIdentity;
            // (the player's two data objects looked up twice a second, not on every call - two or three calls a frame: the
            //  game's lookup walks its observable list (an enumerator each time) and, when a type is stored under its
            //  instantiation type (GPSMarkersData's is GPSMarkersServerData), reads the type's attribute again; the
            //  marker lists themselves are still read from them every call)
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (!ReferenceEquals(session, _pdSession) || !ReferenceEquals(data, _pdData) || !id.Equals(_pdId) || now - _pdAt > 0.5 || now < _pdAt)
            {
                _pdGps = data.GetPerPlayerData<GPSMarkersData>(id);
                _pdContracts = data.GetPerPlayerData<ContractPerPlayerData>(id);
                _pdSession = session; _pdData = data; _pdId = id; _pdAt = now;
            }
            var gps = _pdGps;
            // ObservableList derives from a framework type mod code cannot reference: read the lists as
            // plain enumerables.
            if (gps != null)
            {
                Add(list, PlanetRenderBridge.GetMember(gps, "GPSMarkers"));
                var groups = PlanetRenderBridge.GetMember(gps, "MarkersGroups");
                if (groups is System.Collections.IList gl) { for (int i = 0; i < gl.Count; i++) Add(list, PlanetRenderBridge.GetMember(gl[i], "Markers")); }
                else if (groups is System.Collections.IEnumerable ge) foreach (var g in ge) Add(list, PlanetRenderBridge.GetMember(g, "Markers"));
            }
            var contracts = _pdContracts;
            if (contracts != null) Add(list, PlanetRenderBridge.GetMember(contracts, "HUDMarkers"));
            return list;
        }
        catch { return null; }
    }
    static object _pdSession, _pdData; static Keen.Game2.Simulation.GameSystems.Ownership.IdentityId _pdId; static double _pdAt;
    static GPSMarkersData _pdGps; static ContractPerPlayerData _pdContracts;

    private static void Add(List<GPSMarker> list, object enumerable)
    {
        // (the game's lists are ObservableLists - Collections, IList: read by index; walked as IEnumerable each boxed an
        //  enumerator, several a frame)
        if (enumerable is System.Collections.IList l) { for (int i = 0; i < l.Count; i++) if (l[i] is GPSMarker m) list.Add(m); }
        else if (enumerable is System.Collections.IEnumerable e) foreach (var o in e) if (o is GPSMarker m) list.Add(m);
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
            window = CellWindow(body);
            return IsFinite(model);
        }
        ProximityFrame best = null; double bd = ServerFrames.SlotRadius;
        // (the registry's own dictionary walked with its struct enumerator: through IEnumerable each call boxed one - this
        //  runs for every marker, contact and sensor, every frame)
        var frames = SystemHost.Frames.Frames;
        if (frames is Dictionary<long, ProximityFrame>.ValueCollection vc)
            foreach (var f in vc) { double d = (world - f.BerthCenter).Length(); if (d <= bd) { bd = d; best = f; } }
        else
            foreach (var f in frames) { double d = (world - f.BerthCenter).Length(); if (d <= bd) { bd = d; best = f; } }
        if (best == null || !(reg.Find(best.ParentBodyName) is GravityBody parent)) return false;
        model = parent.OriginInRoot(t).Position + OrbitPropagation.StateAt(best.Elements, t).Position + (world - best.BerthCenter);
        window = FrameWindow(best.Id);
        return IsFinite(model);
    }

    // The windows' names ("cell:<body>", "frame:<id>"), made once each: ModelOf ran for every marker and contact every frame,
    // and each call built its name anew (2026-10-14). Compared by value (==), as before.
    static readonly Dictionary<string, string> _cellWindows = new Dictionary<string, string>();
    static readonly Dictionary<long, string> _frameWindows = new Dictionary<long, string>();
    static string CellWindow(string body)
    {
        lock (_cellWindows) { if (!_cellWindows.TryGetValue(body, out var w)) _cellWindows[body] = w = "cell:" + body; return w; }
    }
    static string FrameWindow(long id)
    {
        lock (_frameWindows)
        {
            if (!_frameWindows.TryGetValue(id, out var w))
            {
                if (_frameWindows.Count > 4096) _frameWindows.Clear();   // (frames come and go: bounded)
                _frameWindows[id] = w = "frame:" + id;
            }
            return w;
        }
    }

    /// <summary>Every visible local marker with its true (sun-centred) model position, for the map.</summary>
    public static List<(string name, ColorSRGB color, Vector3D model)> MapMarkers(Keen.VRage.Core.Game.Systems.Session session, double t)
    {
        var l = new List<(string, ColorSRGB, Vector3D)>();
        var markers = LocalMarkers(session);
        if (markers == null) return l;
        foreach (var m in markers)
        {
            if (m == null || (m.Hidden && !_hidden.Contains(m))) continue;
            bool ok; Vector3D mm;
            lock (ServerFrames.FramesLock) ok = ModelOf(m.Position, t, out mm, out _, out _);
            if (ok) l.Add((m.Name, m.Color, mm));
        }
        return l;
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

    /// <summary>Every GPS point and marked place of yours (the Contacts tab's places), or null.</summary>
    public static List<GPSMarker> PlaceMarkers(Keen.VRage.Core.Game.Systems.Session session) => session == null ? null : LocalMarkers(session);

    /// <summary>The player hides (true) or shows a GPS point (the Contacts tab's eye): a hidden one is the player's, never
    /// ours to show again; a shown one may be frame-transferred again on the next tick.</summary>
    public static void SetUserHidden(GPSMarker m, bool hidden)
    {
        if (m == null) return;
        _hidden.Remove(m);
        m.Hidden = hidden;
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

}
