using Keen.Game2.Client.WorldObjects.ColonizationMap;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Render;
using SEAerospace;
using SEAerospace.Frames;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// KSP-style orbits inside SE2's own strategic map (the colonization map).
///
/// The colonization map is a small diorama built at the player's position (map units are metres
/// at the diorama; the camera sits 0.2 to 1 unit away). Everything here draws with a Map-type
/// mesh builder, which the renderer shows only while the map is open, on top of the map's own
/// sectors, planet globes and markers. Two layers, chosen by how far the map camera is zoomed out:
///  - LOCAL (zoomed in): around the map's planet globes, the player's orbit and every frame's
///    orbit, at the globe's own scale (inclination kept: the map flattens positions, not these).
///  - SYSTEM (zoomed out): the star system laid flat, KSP style: the star, each planet's
///    heliocentric orbit and live position, SOI rings, and frames on heliocentric orbits.
/// </summary>
public static class MapView
{
    public enum ViewMode { Auto, Local, System }
    public static ViewMode Mode = ViewMode.Auto;
    /// <summary>Zoom fraction (0 = closest, 1 = farthest) above which Auto shows the system layer.</summary>
    public static double SystemZoomFraction = 0.7;
    public static string Status = "map closed";
    public static bool DiagCross;
    /// <summary>The map globe model's own radius (1.03 units): globes render at radius x this.</summary>
    public const double GlobeModelRadius = 1.03;
    /// <summary>Last map-space position of each body's globe (for the spectator camera).</summary>
    public static readonly Dictionary<string, Vector3D> GlobePos = new Dictionary<string, Vector3D>();
    public static bool Visible { get; private set; }

    private static MeshBuilder _builder;
    private static bool _drew;


    private static IDisposable _terminal;

    /// <summary>Open the strategic map the way the map key does (the terminal's Map tab).</summary>
    public static async void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            if (ch == null || term == null) { Status = "cannot open map (no character/terminal)"; return; }
            var sel = new Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition.MapScreenSelection
                { Panel = Keen.Game2.Client.UI.TerminalScreen.MapScreenMode.Colonization };
            _terminal = await term.OpenTerminal(sel, ch, ch);
        }
        catch (Exception e) { Status = "open map failed: " + e.Message; }
    }

    /// <summary>Harness: the terminal's Build tab (the block catalogue), to see the blocks there.</summary>
    public static async void OpenBuild(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            if (ch == null || term == null) { Status = "cannot open build (no character/terminal)"; return; }
            var sel = new Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition.BuildScreenSelection
                { Panel = Keen.Game2.Client.UI.TerminalScreen.BuildScreenMode.Blocks };
            _terminal = await term.OpenTerminal(sel, ch, ch);
        }
        catch (Exception e) { Status = "open build failed: " + e.Message; }
    }

    public static void Close()
    {
        try { _terminal?.Dispose(); } catch { }
        _terminal = null;
    }

    public static ColonizationMapSessionComponent Map(Keen.VRage.Core.Game.Systems.Session session)
    {
        try { return session?.SessionComponents.TryGet<ColonizationMapSessionComponent>(); } catch { return null; }
    }

    // ── a world without colonization sectors (a scenario: Concordia) ──
    // The game builds its colonization map only where there are sectors (TerminalScreenViewModel / MapTabViewModel
    // .ColonizationMapAvailable: Sectors.Count > 0): elsewhere its Map tab is the GPS list alone, and the map scene - the
    // diorama this map is drawn in - is never shown. So while the terminal is on its Map tab in such a world, the scene is
    // shown here (ColonizationMapSessionComponent.ToggleMap: no sector needed) and hidden again when it leaves.
    static System.Reflection.FieldInfo _openVm;
    static System.Reflection.PropertyInfo _mapTabSel;
    static bool _sceneOurs;
    /// <summary>Whether this world has no colonization sectors (the map scene is ours to show).</summary>
    public static bool Sectorless { get; private set; }

    static void SectorlessScene(Keen.VRage.Core.Game.Systems.Session session, ColonizationMapSessionComponent map)
    {
        if (map == null) return;
        // (a new session: the last one's failures and pending toggle do not count)
        if (!ReferenceEquals(session, _sceneSession)) { _sceneSession = session; _sceneFailures = 0; _togglePending = false; _hidVisuals = false; _noticeBlanked = false; _blankTries = 0; _backdropHold = null; _backdropRect = null; _backdropWarned = false; }
        // (a posted toggle that never ran must not block the next for good)
        if (_togglePending && System.Diagnostics.Stopwatch.GetTimestamp() - _toggleAt > 3 * System.Diagnostics.Stopwatch.Frequency) _togglePending = false;
        SectorsSessionComponent sec = null;
        try { sec = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        Sectorless = sec == null || sec.Sectors.Count == 0;
        // (the game's "Map Data Unavailable" over the map, blanked - retried once a second for a minute: its text tables can
        //  come later than the first 20 frames after load, and it showed)
        long nowBlank = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Sectorless && !_noticeBlanked && _blankTries < 60 && nowBlank - _blankAt > System.Diagnostics.Stopwatch.Frequency)
        {
            _blankAt = nowBlank; _blankTries++; _noticeBlanked = BlankText("MapDataUnavailable");
            if (_noticeBlanked && OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] sectorless map: 'Map Data Unavailable' blanked (try {_blankTries})");
        }
        bool want = false;
        if (Sectorless)
        {
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            _openVm ??= typeof(Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent).GetField("_openVM", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // (the view model's type lives in an assembly scripts cannot reference: its IsMapTabSelected by reflection)
            object vm = term != null ? _openVm?.GetValue(term) : null;
            if (vm != null)
            {
                _mapTabSel ??= vm.GetType().GetProperty("IsMapTabSelected");
                want = _mapTabSel?.GetValue(vm) is bool b && b;
            }
        }
        // (each opening blanks the notice again - once, as the map opens: the game reloads its text tables, it showed again)
        if (want && !_wantedLast && _noticeBlanked) { _noticeBlanked = false; _blankTries = 0; _blankAt = 0; }
        _wantedLast = want;
        if (_togglePending) return;
        if (want && !map.IsVisible && _sceneFailures < 3) Toggle(map, true);   // (three failures: given up this session)
        else if (!want && _sceneOurs) { if (map.IsVisible) Toggle(map, false); else _sceneOurs = false; }
        // (the placeholder sector ring hidden once per opening)
        if (want && map.IsVisible && _sceneOurs) { if (!_hidVisuals) { HideSectorVisuals(map); _hidVisuals = true; } }
        else _hidVisuals = false;
        // (the terminal's dark backdrop: kept hidden while the map is ours - re-checked each second, on the UI thread: a
        //  hold applied once from the toggle was lost when its lookup failed, and the map stayed dimmed for good)
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ours = want && map.IsVisible && _sceneOurs;
        if ((ours != (_backdropHold != null) || (ours && _noticeHold == null)) && nowTicks - _backdropTry > System.Diagnostics.Stopwatch.Frequency && _uiPost != null)
        {
            _backdropTry = nowTicks;
            PostBackdrop(ours);
        }
    }
    // (its own method: a lambda here captured 'ours', and the closure for it was made on every call of SectorlessScene)
    static void PostBackdrop(bool ours)
    {
        var args = (object[])_uiPostDefaults.Clone();
        args[0] = (Action)(() => Backdrop(ours));
        try { _uiPost.Invoke(_uiDispatcher, args); } catch { }
    }

    static volatile bool _togglePending;
    static long _backdropTry;
    static long _toggleAt; static object _sceneSession; static bool _hidVisuals; static int _blankTries;
    static object _uiDispatcher; static System.Reflection.MethodInfo _uiPost; static object[] _uiPostDefaults; static bool _uiLooked;

    /// <summary>ToggleMap switches the camera (an entity is closed): never from inside a scene job - that asserted and
    /// crashed the game (twice: the engine's SynchronizationContext.Post runs its callback at once, in the job). Posted
    /// to the UI's dispatcher (Avalonia's Dispatcher.UIThread - where the game's own map tab runs it; scripts cannot
    /// reference Avalonia, so by reflection), which runs it outside the scene's jobs. No dispatcher: not shown.</summary>
    static void Toggle(ColonizationMapSessionComponent map, bool show)
    {
        if (!_uiLooked)
        {
            _uiLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Avalonia.Base") continue;
                var t = a.GetType("Avalonia.Threading.Dispatcher");
                _uiDispatcher = t?.GetProperty("UIThread")?.GetValue(null);
                foreach (var m in t?.GetMethods() ?? Array.Empty<System.Reflection.MethodInfo>())
                {
                    var ps = m.GetParameters();
                    if (m.Name != "Post" || ps.Length == 0 || ps[0].ParameterType != typeof(Action)) continue;
                    _uiPost = m;
                    _uiPostDefaults = new object[ps.Length];
                    for (int k = 1; k < ps.Length; k++)
                        _uiPostDefaults[k] = ps[k].HasDefaultValue && ps[k].DefaultValue != null ? ps[k].DefaultValue
                            : ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                    break;
                }
                break;
            }
        }
        if (_uiDispatcher == null || _uiPost == null) { Status = "sectorless world: no UI dispatcher to show the map scene from"; return; }
        _togglePending = true; _toggleAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var args = (object[])_uiPostDefaults.Clone();
        args[0] = (Action)(() =>
        {
            try
            {
                if (map.IsVisible != show)
                {
                    // (no sectors: its sector selection would index an empty list - off before showing)
                    if (show) { map.ToggleSectorSelection(false); EnsureDiscoveries(map); }
                    if (show) map.ShowMap(); else map.HideMap();   // (4.0 dropped ToggleMap, which was exactly this)
                }
                _sceneOurs = show;
                Backdrop(show);
            }
            catch (Exception e)
            {
                Status = "map scene toggle failed: " + e.Message;
                Log.Default?.Warning("[ORBIT] sectorless map scene: " + e);
                // (ShowMap throws after switching the camera and before marking itself visible: the camera would stay in
                //  map mode with the game thinking the map closed. Mark it visible and hide it - its own restore path)
                if (show) Recover(map);
                _sceneOurs = false;
                Backdrop(false);
            }
            finally { _togglePending = false; }
        });
        try { _uiPost.Invoke(_uiDispatcher, args); }
        catch (Exception e) { _togglePending = false; _uiPost = null; Status = "map scene post failed: " + e.Message; }
    }

    // ── the terminal's backdrop ──
    // The terminal draws a full-screen dark rectangle (TerminalScreen.PART_ItemDropArea, the UI background opacity) and
    // hides it only on the Map tab WITH a colonization map (IsVisible = !IsMapTabSelected || !ColonizationMapAvailable).
    // A sectorless world has none, so it stayed over this map and greyed it all out. Hidden while the map is ours at
    // Avalonia's Animation priority - above the game's binding, which stays and takes over again when the handle is
    // disposed (map closed, tab left). VRage.UI and Avalonia by reflection (scripts cannot reference them); UI thread.
    static IDisposable _backdropHold, _noticeHold; static object _backdropHeldRect;
    static System.Reflection.MethodInfo _descendants;

    static void HideNotice()
    {
        var notice = FindNamed(_backdropTerminal, "NoMapDataNotice");
        if (notice != null && _setValue != null) _noticeHold = _setValue.Invoke(notice, new[] { _isVisibleProp, (object)false, _animPriority }) as IDisposable;
        if (_noticeHold != null && OrbitalLog.Setup) Log.Default?.Info("[ORBIT] sectorless map: the 'Map Data Unavailable' notice hidden");
    }

    /// <summary>A control named so among a visual's descendants (Avalonia's VisualExtensions.GetVisualDescendants, by
    /// reflection), or null.</summary>
    static object FindNamed(object root, string name)
    {
        if (root == null) return null;
        try
        {
            if (_descendants == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Avalonia.Base") continue;
                    var ve = a.GetType("Avalonia.VisualTree.VisualExtensions");
                    foreach (var m in ve?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) ?? Array.Empty<System.Reflection.MethodInfo>())
                        if (m.Name == "GetVisualDescendants" && m.GetParameters().Length == 1) { _descendants = m; break; }
                    break;
                }
            if (!(_descendants?.Invoke(null, new[] { root }) is System.Collections.IEnumerable all)) return null;
            foreach (var v in all)
                if (v != null && v.GetType().GetProperty("Name")?.GetValue(v) as string == name) return v;
        }
        catch { }
        return null;
    }
    static object _backdropRect, _isVisibleProp, _animPriority; static System.Reflection.MethodInfo _setValue;
    static bool _backdropWarned;
    static void Backdrop(bool hide)
    {
        try
        {
            if (!hide) { _backdropHold?.Dispose(); _backdropHold = null; _noticeHold?.Dispose(); _noticeHold = null; return; }
            var rect = FindBackdrop();
            if (rect == null) return;
            if (_backdropHold != null && ReferenceEquals(rect, _backdropHeldRect)) { if (_noticeHold == null) HideNotice(); return; }   // (held already: the notice, once it exists)
            _backdropHold?.Dispose(); _backdropHold = null;
            if (_setValue == null)
            {
                Type visual = null, prio = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Avalonia.Base") { visual = a.GetType("Avalonia.Visual"); prio = a.GetType("Avalonia.Data.BindingPriority"); break; }
                _isVisibleProp = visual?.GetField("IsVisibleProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                _animPriority = prio != null ? Enum.Parse(prio, "Animation") : null;
                for (var t = rect.GetType(); t != null && _setValue == null; t = t.BaseType)
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "SetValue" && !m.IsGenericMethod && ps.Length == 3 && ps[2].ParameterType == prio && ps[1].ParameterType == typeof(object)) { _setValue = m; break; }
                    }
                if (_isVisibleProp == null || _animPriority == null || _setValue == null) { WarnBackdrop("Avalonia's SetValue/IsVisibleProperty/BindingPriority not found"); return; }
            }
            _backdropHold = _setValue.Invoke(rect, new[] { _isVisibleProp, (object)false, _animPriority }) as IDisposable;
            _backdropHeldRect = rect;
            // the overlay's "Map Data Unavailable" (MapOverlayView's NoMapDataNotice, shown when there is no colonization map):
            // hidden the same way - blanking its text came too late on the first opening (the label had its text already)
            _noticeHold?.Dispose(); _noticeHold = null;
            HideNotice();
            if (OrbitalLog.Setup) Log.Default?.Info("[ORBIT] sectorless map: the terminal's backdrop hidden" + (_backdropHold == null ? " (no handle back: it stays hidden until the next terminal)" : ""));
        }
        catch (Exception e) { WarnBackdrop((e.InnerException ?? e).Message); }
    }

    /// <summary>The live terminal's backdrop rectangle: UIEngineComponent -> ScreenManager -> its cached / loaded screens ->
    /// the TerminalScreen -> PART_ItemDropArea. Null (warned once) when the path is not there.</summary>
    static object FindBackdrop()
    {
        var session = _sceneSession as Keen.VRage.Core.Game.Systems.Session;
        if (session == null) return null;
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        // The UI engine is not a session component (asking the session for it found nothing: "no ScreenManager"): the game's
        // own SharedUIComponent holds it (_ui), as the terminal's opener reaches it (SessionInGameUISessionComponent).
        var shared = GameUi.SharedUi(session);
        var engine = shared?.GetType().GetField("_ui", F)?.GetValue(shared);
        var sm = engine?.GetType().GetProperty("ScreenManager")?.GetValue(engine);
        if (sm == null) { WarnBackdrop(shared == null ? "no SharedUIComponent" : engine == null ? "SharedUIComponent has no _ui" : "no ScreenManager"); return null; }
        object terminal = null;
        // the terminal is a cached screen (the game reopens it: ScreenManager.TryGetCachedScreen<TerminalScreen>())
        _termType ??= FindTypeNamed("Keen.Game2.Client.UI.TerminalScreen.TerminalScreen");
        if (_termType != null)
            foreach (var m in sm.GetType().GetMethods())
                if (m.Name == "TryGetCachedScreen" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                { try { terminal = m.MakeGenericMethod(_termType).Invoke(sm, null); } catch { } break; }
        // open, it is out of the cache: the screen manager's own collections (its screens, their handles), searched by type -
        // its source is not in the decompile, so no field names are assumed
        var seen = new System.Text.StringBuilder();
        if (terminal == null) terminal = FindTerminal(sm, 3, seen, new HashSet<object>(ReferenceEqualityComparer.Instance));
        if (terminal == null) { WarnBackdrop("no TerminalScreen in the screen manager (searched: " + seen + ")"); return null; }
        if (ReferenceEquals(terminal, _backdropTerminal) && _backdropRect != null) return _backdropRect;   // (the same terminal: its rectangle)
        _backdropTerminal = terminal;
        _backdropRect = terminal.GetType().GetField("PART_ItemDropArea", F | System.Reflection.BindingFlags.Public)?.GetValue(terminal);
        if (_backdropRect == null) WarnBackdrop("TerminalScreen has no PART_ItemDropArea");
        return _backdropRect;
    }
    static object _backdropTerminal; static Type _termType;

    /// <summary>A TerminalScreen reachable from o through fields and collections, depth levels deep (null: none).</summary>
    static object FindTerminal(object o, int depth, System.Text.StringBuilder seen, HashSet<object> visited)
    {
        if (o == null || depth < 0 || o is string || o.GetType().IsPrimitive || !visited.Add(o)) return null;
        if (o.GetType().Name == "TerminalScreen") return o;
        if (o is System.Collections.IDictionary d) { foreach (var v in d.Values) { var r = FindTerminal(v, depth - 1, seen, visited); if (r != null) return r; } return null; }
        if (o is System.Collections.IEnumerable e) { int n = 0; foreach (var v in e) { if (n++ > 256) break; var r = FindTerminal(v, depth - 1, seen, visited); if (r != null) return r; } return null; }
        if (depth == 0) return null;
        for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum) continue;
                if (depth == 3 && seen.Length < 400) seen.Append(f.Name).Append(' ');
                object v; try { v = f.GetValue(o); } catch { continue; }
                var r = FindTerminal(v, depth - 1, seen, visited);
                if (r != null) return r;
            }
        return null;
    }
    static Type FindTypeNamed(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(name); if (t != null) return t; }
        return null;
    }

    static void WarnBackdrop(string why)
    {
        if (_backdropWarned) return;
        _backdropWarned = true;
        Log.Default?.Warning("[ORBIT] sectorless map: the terminal's backdrop not hidden (the map stays dimmed): " + why);
    }

    static System.Reflection.FieldInfo _discField;
    /// <summary>What ShowMap needs that only the colonization tab sets up: the map entity, and the player's discovery record
    /// (an empty one when missing: the type's public constructor).</summary>
    static void EnsureDiscoveries(ColonizationMapSessionComponent map)
    {
        _ = map.MapEntity;   // (the map's diorama entity is spawned on first use - by the colonization tab, which a sectorless
                             //  world never builds; ShowMap's discovered planets hang off it: null, the NullReferenceException)
        _discField ??= typeof(ColonizationMapSessionComponent).GetField("_discoveriesPlayerData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (_discField != null && _discField.GetValue(map) == null)
            _discField.SetValue(map, new Keen.Game2.Simulation.GameSystems.Discoveries.DiscoveriesPlayerData());
    }

    static bool _noticeBlanked, _wantedLast; static long _blankAt;
    /// <summary>The game's GPS page says "Map Data Unavailable" across the map in a world without sectors - where this map
    /// now is. Its localized text (LocalizationPackage's string tables, by reflection, as the aero mod's Mach readout
    /// does) blanked; true once done.</summary>
    static bool BlankText(string key)
    {
        try
        {
            var tm = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Localization.TextManager>.Instance;
            var src = typeof(Keen.VRage.Library.Localization.TextManager).GetField("_textSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tm);
            if (src == null) return false;
            var id = Keen.VRage.Library.Utils.StringId.Get(key);
            bool any = false;
            foreach (string table in new[] { "_baseLanguageStrings", "_localizedStrings" })
            {
                if (!(src.GetType().GetField(table, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(src) is Dictionary<Keen.VRage.Library.Utils.StringId, Keen.VRage.Library.Localization.ContentText> d)) continue;
                if (!d.TryGetValue(id, out var ct)) continue;
                ct.Text = " ";
                d[id] = ct;
                any = true;
            }
            return any;
        }
        catch { return true; }   // (not again: it is cosmetic)
    }

    static bool _sectorVisualsLogged;
    /// <summary>A sectorless world's map entity still draws a placeholder sector ring (the wedges): every render component
    /// on it or its children whose type names a sector, hidden while the scene is ours (closing restores the game's state
    /// through RestoreGame / HideMap). The first time, what is there goes to the log.</summary>
    static void HideSectorVisuals(ColonizationMapSessionComponent map)
    {
        var e = map.MapEntity;
        if (e == null) return;
        var sb = _sectorVisualsLogged ? null : new System.Text.StringBuilder();
        void Visit(Entity x, int depth)
        {
            foreach (var c in x.Components)
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                sb?.Append(new string(' ', depth * 2)).Append(n).Append((char)10);
                if (n.IndexOf("Sector", StringComparison.Ordinal) >= 0) PlanetRenderBridge.SetRenderComponentVisible(c, false);
            }
            var kids = x.TryGet<HierarchyComponent>()?.Children;
            if (kids != null && depth < 3) foreach (var k in kids) if (k != null) Visit(k, depth + 1);
        }
        Visit(e, 0);
        if (sb != null) { _sectorVisualsLogged = true; if (OrbitalLog.Setup) Log.Default?.Info("[ORBIT] sectorless map entity: " + (char)10 + sb); }
    }

    static int _sceneFailures;
    static void Recover(ColonizationMapSessionComponent map)
    {
        _sceneFailures++;
        try
        {
            var vis = typeof(ColonizationMapSessionComponent).GetProperty("IsVisible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            vis?.GetSetMethod(true)?.Invoke(map, new object[] { true });
            map.HideMap();
        }
        catch (Exception e) { Log.Default?.Warning("[ORBIT] sectorless map scene: restore failed: " + e.Message); }
    }

    /// <summary>The client session (MapLayout reads the game's UI design scale through it).</summary>
    public static Keen.VRage.Core.Game.Systems.Session SessionForLayout;

    // (the guarded parts read the session and the map from here, set just before: lambdas capturing them were a closure and
    //  a delegate every frame - the closure made on entry, map open or not, 2026-10-14)
    static Keen.VRage.Core.Game.Systems.Session _gSession; static ColonizationMapSessionComponent _gMap;
    static readonly Action _sectorless = () => SectorlessScene(_gSession, _gMap);
    static readonly Action _mapCamera = () =>
    {
        var map = _gMap; var session = _gSession;
        bool hm = GameMap.TryMouse(map, out var mm); MapCamera.Tick(session, map, hm, mm); if (hm) DebugPanel.Tick(session, mm);
    };

    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, WorldTransform camera, double t)
    {
        SessionForLayout = session;
        var map = Map(session);
        _gSession = session; _gMap = map;
        FrameHost.Guard("MapView.Sectorless", _sectorless);
        Visible = map != null && map.IsVisible;
        MapChrome.Tick(session, map != null && map.IsVisible && SystemHost.Built);   // (the game's contradicting map panels - UI audit C6)
        if (map == null || !map.IsVisible || !SystemHost.Built) { RendezvousView.Reset(); ContactsTab.Reset(); MapCamera.Release(session); CleanMap.ResetView(); if (map != null) GameMap.RestoreGame(map); MapGlobes.HideAll(); Clear(); Status = map == null ? "no map component" : "map closed"; return; }
        SectorsSessionComponent sectors = null;
        try { sectors = session.SessionComponents.TryGet<SectorsSessionComponent>(); } catch { }
        if (sectors == null) { Status = "no sectors component"; return; }   // (a sectorless world still has the component: its list is empty)

        FrameHost.Guard("MapCamera", _mapCamera);

        _builder ??= CreateBuilder(session);
        if (_builder == null) { Status = "no mesh builder"; return; }

        Vector3D mapPos = map.MapEntity.Data.GetWorldTransform().Position;
        Quaternion orient = map.Orientation;
        // Vertices relative to the map: the diorama is ~1 m across, hundreds of km from the origin,
        // where float positions only resolve centimetres (orbit rings collapsed; seen in game).
        _builder.SetPrimitiveOffset(mapPos);
        _builder.UpdateEntityTransform(new WorldTransform(mapPos));   // the batch sits at the offset
        Vector3D origin = sectors.MapWorldPosition;
        double scale = sectors.MapWorldScale;
        double planetScale = PlanetScale(map);
        double dist = (camera.Position - mapPos).Length();
        double span = Math.Max(1e-6, map.MaxDistance - map.MinDistance);
        double zoom = Math.Max(0, Math.Min(1, (dist - map.MinDistance) / span));
        bool system = Mode == ViewMode.System || (Mode == ViewMode.Auto && zoom >= SystemZoomFraction);

        var reg = SystemHost.Registry;
        bool unified;
        lock (ServerFrames.FramesLock) unified = GameMap.Draw(session, _builder, map, sectors, camera.Position, mapPos, orient, t);
        if (unified) { Status = GameMap.Status; }
        if (DiagCross)
        {
            // DEV: a bright cross at the map entity, 0.5 map units each way, plus a sphere.
            var red = new ColorSRGB(1f, 0.1f, 0.1f);
            _builder.AddLine(mapPos - Rot(orient, new Vector3D(0.5, 0, 0)), mapPos + Rot(orient, new Vector3D(0.5, 0, 0)), red, true);
            _builder.AddLine(mapPos - Rot(orient, new Vector3D(0, 0, 0.5)), mapPos + Rot(orient, new Vector3D(0, 0, 0.5)), red, false);
            _builder.AddLine(mapPos, mapPos + Rot(orient, new Vector3D(0, 0.5, 0)), red, (float)0.01, false);
            _builder.AddSphere(new WorldTransform(mapPos, Quaternion.Identity), 0.05, red, red, false);
        }
        _builder.Commit();
        _drew = true;
        if (!unified) Status = $"{(system ? "SYSTEM" : "LOCAL")} zoom={zoom:F2} dist={dist:F3} scale={scale:E2} planetScale={planetScale:F1}";
    }

    /// <summary>Line width in world metres, scaled by the distance to the camera (a saved setting; the map's own lines
    /// are drawn by CleanMap now - the old LOCAL / SYSTEM layers that used it were removed 2026-10-11).</summary>
    public static double LineWidth = 0.003;

    private static Vector3D Rot(Quaternion q, Vector3D v) => (QuaternionD)q * v;

    /// <summary>The map's globe size multiplier (ColonizationMapConfiguration.PlanetScale; private config).</summary>
    private static double PlanetScale(ColonizationMapSessionComponent map)
    {
        try
        {
            var cfg = PlanetRenderBridge.GetMember(map, "_configuration");
            var v = cfg != null ? PlanetRenderBridge.GetMember(cfg, "PlanetScale") : null;
            if (v is float f && f > 0) return f;
            if (v is double d && d > 0) return d;
        }
        catch { }
        return 1.0;
    }

    private static MeshBuilder CreateBuilder(Keen.VRage.Core.Game.Systems.Session session)
    {
        try { return session.Get<IDebugDraw>().MeshBuilderFactory.CreateMeshBuilder(MeshBuilder.Type.Map, 0L, 4096, "OrbitalModMap"); }
        catch { return null; }
    }

    public static void Clear()
    {
        if (_builder != null && _drew) { _builder.Commit(); _drew = false; }
    }

}
