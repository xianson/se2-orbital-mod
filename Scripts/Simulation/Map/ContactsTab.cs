using System.Reflection;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Library.Utils;
using SEAerospace;
using SEAerospace.Orbital;
using static OrbitalMod.Num;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE CONTACTS TAB: the game's GPS tab, its own screen (search, sort by name or distance, groups that fold, the eye),
/// run over a list of the mod's - never saved, never sent - instead of your GPS points: everything you can target.
///  - Target (a "Clear target" row while one is set) · Your ships · Contacts (ships and stations you have seen) · Planets
///    and moons · Sectors · Rocks (the rocks you have seen, nearest first) · Places (your GPS points and the game's marked
///    places, where they truly are across frames) · Hidden. Each group's header carries its count ("Contacts (4)").
///  - A contact's row says what it is and how it is known: "≈ Red Ship  [large ship · radar · tracking 40% · seen 2 h
///    ago]" ("≈": not tracked yet - position only). Its range is the distance column; its bearing and relative speed are
///    in the target panel (selecting the row targets it).
///  - Each row's distance is the true one (its position is set where the thing lies from you, across frames).
///  - Selecting a row targets it. Its eye pins it to the HUD (BodyMarkers marks the target, the pinned and what is
///    near); a place's eye is its GPS point's own. A rename sticks (saved with the world).
///  - "Add from current position" still adds a real GPS point: it shows up under Places.
///  - Delete (the Del key on a selected row): a ship, a body or a sector goes to the Hidden group (Del there brings it
///    back); a place is its GPS point, deleted for real. Rename: R (the hint the screen shows) or F2. A place's rename
///    renames its GPS point.
///  - The game's own words for the screen ("GPS", "GPS Markers", "Add / Rename / Delete GPS Marker") are the
///    Contacts tab's (<see cref="Relabel"/>: its string table, as MapView.BlankText edits it).
/// How: the terminal's Map/GPS sub screen gets a second GPSViewModel (its public constructor) over a local
/// GPSMarkersData (SetContent), and the map tab's own reference is pointed at it (its fast travel reads it). The game's
/// list only takes markers of its player-made type: ours carry it. What the screen does to the real data with our
/// markers (a group made of them) is undone (<see cref="Sanitize"/>).
/// </summary>
public static class ContactsTab
{
    public static bool Enabled = true;
    public static string Status = "-";
    public static string TabHeader = "Contacts";

    static object _term, _vm, _mapTab;
    static GPSMarkersData _local;
    static object _type;                  // GPSMarkerTypeDefinition: the game's player-made type
    static ResourceHandle<Keen.VRage.Core.Render.TextureAsset> _icon;
    static Keen.VRage.Core.Game.Systems.Session _session;

    sealed class Row { public string Key, Group, Shown; public GPSMarker M; public GPSMarker Real; }
    static readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
    static readonly Dictionary<string, Group> _groups = new Dictionary<string, Group>();
    static readonly HashSet<GPSMarker> _ours = new HashSet<GPSMarker>();

    /// <summary>Guards every CHANGE to <see cref="Pinned"/>, <see cref="Names"/> and <see cref="HiddenKeys"/>, and the save's copy
    /// of them (SavedState: the server thread, under FramesLock - so nothing but the sets' own calls is made under it).
    /// 2026-10-15: the client changed them while the save enumerated them, and the save threw.</summary>
    public static readonly object Sync = new object();
    /// <summary>Pinned to the HUD (target keys), saved.</summary>
    public static readonly HashSet<string> Pinned = new HashSet<string>();
    /// <summary>Your names for things (target key -> name), saved.</summary>
    public static readonly Dictionary<string, string> Names = new Dictionary<string, string>();
    /// <summary>The places (your GPS points), by key: their world position now.</summary>
    static readonly Dictionary<string, GPSMarker> _places = new Dictionary<string, GPSMarker>();

    const string GTarget = "Target", GYours = "Your ships", GContacts = "Contacts", GBodies = "Planets and moons", GSectors = "Sectors", GRocks = "Rocks", GPlaces = "Places", GHidden = "Hidden";
    static readonly string[] GroupOrder = { GTarget, GYours, GContacts, GBodies, GSectors, GRocks, GPlaces, GHidden };
    /// <summary>The "Clear target" row's key (selecting it clears the target).</summary>
    const string ClearKey = "\u0001clear";
    /// <summary>The Rocks group lists the nearest this many (its header counts them all).</summary>
    public static int MaxRockRows = 40;
    /// <summary>Put away (Del): listed under Hidden only, saved.</summary>
    public static readonly HashSet<string> HiddenKeys = new HashSet<string>();

    // ───────────────────────────── the game's words ─────────────────────────────

    /// <summary>The game's texts for its GPS screen, as the Contacts tab's (key -> text).</summary>
    static readonly (string key, string text)[] Words =
    {
        ("TerminalScreenHeaderMapGPSMarkers", "Contacts"),   // the tab
        ("MapScreenGPSMarkers", "Contacts"),                 // the panel's title
        ("TerminalScreenHeaderGPSMarkers", "Contacts"),
        ("MapScreenSearchMarkers", "Search contacts"),
        ("AddMarker", "Add GPS point here"),                 // (a real GPS point: it shows under Places)
        ("MapScreenAddMarker", "Add GPS point here"),
        ("RenameMarker", "Rename contact"),                  // (R or F2: RenameKey)
        ("DeleteMarker", "Hide contact"),                    // (Del hides it under Hidden - a GPS point is deleted: DeleteKey)
        ("MapScreenDeleteMarker", "Hide contact"),
        ("MapScreenEditMarker", "Edit contact"),
    };
    static bool _relabelled;

    /// <summary>Once a world runs (before any terminal is built: the tab row copies its words when it is made): the
    /// words set in the game's string tables, both the base culture's and the player's.</summary>
    public static void Relabel()
    {
        if (_relabelled || !Enabled) return;
        _relabelled = true;
        int n = 0;
        foreach (var w in Words) if (SetText(w.key, w.text)) n++;
        if (OrbitalLog.Setup) Log.Default?.Info($"[ORBIT] contacts tab: {n}/{Words.Length} of the GPS screen's texts relabelled");
    }

    static bool SetText(string key, string text)
    {
        try
        {
            var tm = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Localization.TextManager>.Instance;
            var src = typeof(Keen.VRage.Library.Localization.TextManager).GetField("_textSource", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tm);
            if (src == null) return false;
            var id = Keen.VRage.Library.Utils.StringId.Get(key);
            bool any = false;
            foreach (string table in new[] { "_baseLanguageStrings", "_localizedStrings" })
            {
                if (!(src.GetType().GetField(table, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(src) is Dictionary<Keen.VRage.Library.Utils.StringId, Keen.VRage.Library.Localization.ContentText> d)) continue;
                if (!d.TryGetValue(id, out var ct)) continue;
                ct.Text = text;
                d[id] = ct;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static object Prop(object o, string name)
    {
        try { return o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); } catch { return null; }
    }
    static object Field(object o, string name)
    {
        for (var t = o?.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(o);
        }
        return null;
    }
    static bool SetField(object o, string name, object v)
    {
        for (var t = o?.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(o, v); return true; }
        }
        return false;
    }
    static void Raise(object o, string prop)
    {
        for (var t = o?.GetType(); t != null; t = t.BaseType)
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.Name == "OnPropertyChanged" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) { m.Invoke(o, new object[] { prop }); return; }
    }

    // ───────────────────────────── installing ─────────────────────────────

    /// <summary>Every map frame, with the terminal on top: once per terminal, our screen into its GPS tab.</summary>
    public static void Tab(Keen.VRage.Core.Game.Systems.Session session)
    {
        _session = session;
        if (!Enabled) return;
        object term = null;
        try { term = GameUi.TopScreenObject(session); } catch { }
        if (term == null || term.GetType().Name != "TerminalScreenViewModel") return;
        if (ReferenceEquals(term, _term)) { if (_vm != null) { OnSelection(); DeleteKey(term); RenameKey(term); } return; }
        _term = term;
        try { Install(term); }
        catch (Exception e) { Status = "not installed: " + PlanetRenderBridge.Inner(e); PlanetRenderBridge.WarnOnce("contacts-tab", "contacts tab: " + Status); }
    }

    static void Install(object term)
    {
        if (!(Prop(term, "SubScreens") is System.Collections.IList subs)) { Status = "no sub screens"; return; }
        object gpsSub = null;
        foreach (var sub in subs) if (Prop(Prop(sub, "Id"), "Value") as string == "Map/GPS") gpsSub = sub;
        if (gpsSub == null) { Status = "no GPS tab"; return; }
        // the map tab (made on first use) and its own GPS screen
        object mapTab = Prop(term, "MapTab");
        if (mapTab == null)
        {
            var mk = term.GetType().GetMethod("GetOrCreateMapTab", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            mapTab = mk?.Invoke(term, null);
        }
        object orig = Prop(mapTab, "GPSViewModel");
        if (orig == null) { Status = "no map tab / GPS screen"; return; }
        if (_local == null) { _local = new GPSMarkersData(); if (!GuardGrouping(_local)) { _local = null; Status = "not installed: could not guard the group button"; return; } }
        _type = NotEditable(Prop(Field(orig, "_markersConfiguration"), "PlayerMadeMarkerType"));
        if (_type == null) { Status = "no player-made marker type"; return; }
        var iconObj = Prop(Field(orig, "_markersConfiguration"), "PlayerCreatedMarkerIcon");
        if (iconObj != null) _icon = (ResourceHandle<Keen.VRage.Core.Render.TextureAsset>)iconObj;
        // a second screen over our list, made as the map tab makes its own
        Type vmType = orig.GetType();
        ConstructorInfo ctor = null;
        foreach (var c in vmType.GetConstructors()) if (c.GetParameters().Length == 8) ctor = c;
        if (ctor == null) { Status = "no GPS screen constructor"; return; }
        object vm = ctor.Invoke(new object[]
        {
            mapTab, _local, Field(orig, "_discoveriesPlayerData"), Field(orig, "_player"), Field(orig, "_markersRenderComponent"),
            Prop(mapTab, "ColonizationMapComponent"), Field(orig, "_fastTravelClientSession"), Field(orig, "_factory"),
        });
        // owned by the map tab (disposed with it), as its own is
        MethodInfo attach = null;
        for (var t = mapTab.GetType(); t != null && attach == null; t = t.BaseType)
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (m.Name == "Attach" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1) { attach = m; break; }
        if (attach != null) vm = attach.MakeGenericMethod(vmType).Invoke(mapTab, new[] { vm });
        SetField(mapTab, "<GPSViewModel>k__BackingField", vm);
        Raise(mapTab, "GPSViewModel");
        gpsSub.GetType().GetMethod("SetContent")?.Invoke(gpsSub, new[] { vm });
        // its name on the tab row
        if (SetField(gpsSub, "<LocalizedHeader>k__BackingField", TabHeader)) { Raise(gpsSub, "LocalizedHeader"); Raise(term, "SubScreens"); }   // (the row is rebuilt from the list)
        _vm = vm; _mapTab = mapTab;
        Status = "installed";
        Refresh(force: true);
    }

    /// <summary>
    /// THE CONTACTS ARE NOT EDITABLE (2026-10-11: the game crashed). The screen's Edit and Remove buttons show for an
    /// editable marker type, and Remove calls the SERVER's GPSMarkersData.RemoveMarker with the selected marker - ours live
    /// only in this client's list, the server could not pair it, and the in-process replication's failure is fatal
    /// (NoMatchingLocalPairingException in RemoveMarker's network story). So our markers get a copy of the player-made type
    /// with Editable off: no Edit, no Remove (the screen's CanEditMarker). Del still hides a contact / deletes a place
    /// (DeleteKey: ours, against the right lists). The copy: MemberwiseClone, Editable's private setter.
    /// </summary>
    static object NotEditable(object def)
    {
        if (def == null) return null;
        try
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(def, null);
            var pe = copy.GetType().GetProperty("Editable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            pe?.GetSetMethod(true)?.Invoke(copy, new object[] { false });
            if (pe != null && pe.GetValue(copy) is bool b && !b) return copy;
        }
        catch { }
        // (no copy: no contacts tab at all rather than a crash a click away)
        return null;
    }

    /// <summary>
    /// THE GROUP BUTTON (2026-10-11, the same crash's other door): the screen's ToggleGroup sends the selected markers to
    /// the SERVER's GPSMarkersData.SetMarkerGroup - ours, which it cannot pair: fatal. It takes only the selected markers
    /// its list Contains; so our list's inner list answers Contains with no (Collection&lt;T&gt;.Contains asks it) while
    /// adding, removing (IndexOf), counting and listing work as ever - the button finds nothing to group and does nothing.
    /// False: the inner list could not be swapped (no tab rather than a crash a click away).
    /// </summary>
    static bool GuardGrouping(GPSMarkersData data)
    {
        try
        {
            // (by its non-generic faces: mod code may not name ObservableCollection's assembly)
            object list = PlanetRenderBridge.GetMember(data, "GPSMarkers");
            if (!(list is System.Collections.IList il)) return false;
            System.Reflection.FieldInfo f = null;
            for (var t = list.GetType(); t != null && f == null; t = t.BaseType)
                f = t.GetField("items", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f == null) return false;
            var inner = new NoContains();
            foreach (var m in il) inner.Add((GPSMarker)m);
            f.SetValue(list, inner);
            return il.Count == inner.Count && ReferenceEquals(f.GetValue(list), inner);
        }
        catch { return false; }
    }

    /// <summary>A list that says it contains nothing (GuardGrouping); everything else as a List.</summary>
    sealed class NoContains : IList<GPSMarker>
    {
        readonly List<GPSMarker> _l = new();
        public GPSMarker this[int i] { get => _l[i]; set => _l[i] = value; }
        public int Count => _l.Count;
        public bool IsReadOnly => false;
        public void Add(GPSMarker item) => _l.Add(item);
        public void Clear() => _l.Clear();
        public bool Contains(GPSMarker item) => false;
        public void CopyTo(GPSMarker[] array, int arrayIndex) => _l.CopyTo(array, arrayIndex);
        public IEnumerator<GPSMarker> GetEnumerator() => _l.GetEnumerator();
        public int IndexOf(GPSMarker item) => _l.IndexOf(item);
        public void Insert(int index, GPSMarker item) => _l.Insert(index, item);
        public bool Remove(GPSMarker item) => _l.Remove(item);
        public void RemoveAt(int index) => _l.RemoveAt(index);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _l.GetEnumerator();
    }

    static object _lastSel;
    static string _lastSearch;

    /// <summary>
    /// Del on a selected row while the tab is open (the screen's own delete only asks the server about a marker it does not
    /// have): a ship, a body or a sector is put away under Hidden, or brought back from there; a place's GPS point is
    /// deleted. Not while the search box is being typed in (its text changed this frame).
    /// </summary>
    static void DeleteKey(object term)
    {
        bool del = MapInput.KeyPressed(KeyboardInputs.Delete) | MapInput.DevKeys.Remove("contactdel");
        string search = Prop(_vm, "SearchString") as string;
        bool typing = search != _lastSearch;
        _lastSearch = search;
        if (!del || typing) return;
        object selSub = Prop(term, "SelectedSubScreen");
        if (Prop(Prop(selSub, "Id"), "Value") as string != "Map/GPS") return;
        if (!(Prop(Prop(_vm, "SelectedMarker"), "GPSMarker") is GPSMarker m)) return;
        foreach (var r in _rows.Values)
        {
            if (!ReferenceEquals(r.M, m)) continue;
            if (r.Key == ClearKey) return;
            if (r.Key.StartsWith(PlacePrefix))
            {
                if (_places.TryGetValue(r.Key, out var real))
                {
                    try { _remove ??= typeof(GPSMarkersData).GetMethod("RemoveMarker", BindingFlags.Public | BindingFlags.Static); _remove?.Invoke(null, new object[] { _session, real }); } catch { }
                    Messages.Say(SEAerospace.Flight.MessageKinds.Contacts, "GPS point deleted: " + real.Name);
                }
            }
            else
            {
                bool back; lock (Sync) { back = HiddenKeys.Remove(r.Key); if (!back) HiddenKeys.Add(r.Key); }
                if (back) Messages.Say(SEAerospace.Flight.MessageKinds.Contacts, "Contact back: " + Targets.Name(r.Key));
                else Messages.Say(SEAerospace.Flight.MessageKinds.Contacts, "Contact hidden (under Hidden; Del there brings it back): " + Targets.Name(r.Key));
            }
            Refresh(force: true);
            return;
        }
    }

    /// <summary>
    /// F2 on a selected row (2026-10-11: the contacts are not editable any more - their Edit was the crash's door): the
    /// game's text dialog, "Rename contact", the name in it; OK sets it through the row (its Name: the list shows it at
    /// once) and Refresh keeps it under Names, saved with the world. A place - a real GPS point - is renamed on its own
    /// marker. Not while the search box is being typed in.
    /// </summary>
    static void RenameKey(object term)
    {
        bool f2 = MapInput.KeyPressed(KeyboardInputs.F2) | MapInput.DevKeys.Remove("contactrename");
        // R too (2026-10-19: the screen's own hint says "R - Rename contact"): taken a moment later, and only if the search
        // box did not change meanwhile (an R typed into it is a letter, not a rename)
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (MapInput.KeyPressed(KeyboardInputs.R)) { _rAt = wall; _rSearch = Prop(_vm, "SearchString") as string; }
        if (!double.IsNaN(_rAt) && wall - _rAt >= 0.2)
        {
            bool still = (Prop(_vm, "SearchString") as string) == _rSearch;
            _rAt = double.NaN;
            if (still) f2 = true;
        }
        if (!f2) return;
        object selSub = Prop(term, "SelectedSubScreen");
        if (Prop(Prop(selSub, "Id"), "Value") as string != "Map/GPS") return;
        object row = Prop(_vm, "SelectedMarker");
        if (!(Prop(row, "GPSMarker") is GPSMarker m)) return;
        foreach (var r in _rows.Values) if (ReferenceEquals(r.M, m) && r.Key == ClearKey) return;
        string current = Strip(m.Name);
        GameUi.TextDialog(_session, "Rename contact", current, name =>
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return;
            try { row.GetType().GetProperty("Name")?.SetValue(row, name); } catch { m.Name = name; }
            Refresh(force: true);
        });
    }

    static double _rAt = double.NaN; static string _rSearch;

    /// <summary>A row selected (one): it is the target (polled every map frame: the selection's own event is a framework
    /// type mod code cannot name).</summary>
    static void OnSelection()
    {
        try
        {
            object sel = Prop(_vm, "SelectedMarker");
            if (ReferenceEquals(sel, _lastSel)) return;
            _lastSel = sel;
            if (!(Prop(sel, "GPSMarker") is GPSMarker m)) return;
            foreach (var r in _rows.Values)
                if (ReferenceEquals(r.M, m))
                {
                    if (r.Key == ClearKey) { if (Maneuvers.Target != null) CleanMap.SetTarget(null); Refresh(force: true); return; }
                    if (Maneuvers.Target != r.Key) CleanMap.SetTarget(r.Key);
                    return;
                }
        }
        catch { }
    }

    // ───────────────────────────── the list ─────────────────────────────

    static double _next;

    /// <summary>Client tick: the rows kept up to date (twice a second) while our screen exists.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        _session = session;
        if (!Enabled) return;
        Relabel();
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _next) return;
        _next = now + 0.5;
        // the places are kept even with the tab closed (they are targets)
        CollectPlaces(session);
        if (_vm != null) Refresh(force: false);
    }

    static void CollectPlaces(Keen.VRage.Core.Game.Systems.Session session)
    {
        _places.Clear();
        var real = FrameMarkers.PlaceMarkers(session);
        if (real == null) return;
        foreach (var m in real)
        {
            if (m == null || _ours.Contains(m)) continue;
            string key = PlacePrefix + (m.Identifier ?? "") + "|" + m.Name;
            if (!_places.ContainsKey(key)) _places[key] = m;
        }
    }

    public const string PlacePrefix = "place:";

    /// <summary>A place target's world position now (its GPS point's), false when it is gone.</summary>
    public static bool PlaceWorld(string key, out Vector3D world)
    {
        world = default;
        if (!_places.TryGetValue(key, out var m)) return false;
        world = m.Position;
        return true;
    }
    public static string PlaceName(string key) => _places.TryGetValue(key, out var m) ? m.Name : key.Substring(PlacePrefix.Length);

    static void Refresh(bool force)
    {
        if (_local == null || _session == null) return;
        var reg = SystemHost.Registry;
        if (reg == null) return;
        double t = SystemHost.Now;
        // where you are: the player's place (the model's), to set each row's position at its true offset from you
        var player = PlayerCache.Of(_session);
        // (no player - dead, respawning: PlayerCache answers null - returned before the read, whose null reference threw,
        //  ~200 ms, every refresh until you respawned - 2026-10-15)
        if (!Alive(player)) return;
        Vector3D pw; try { pw = player.Data.GetWorldTransform().Position; } catch { return; }
        Vector3D pm; Chart pc; bool ok;
        lock (ServerFrames.FramesLock) ok = FrameMarkers.ModelOf(pw, t, out pm, out _, out pc);
        if (!ok) return;

        foreach (var gname in GroupOrder) GroupOf(gname);   // (the groups in their order, first time)
        var want = new List<(string key, string group, string name)>();
        if (Maneuvers.Target != null) want.Add((ClearKey, GTarget, "Clear target: " + Targets.Name(Maneuvers.Target)));
        foreach (var e in Targets.ShipsCached()) want.Add((e.Key, e.Yours ? GYours : GContacts, (e.Tracked ? "" : "≈ ") + e.Name));
        foreach (var b in reg.Bodies) if (!b.IsRoot && SystemHost.BeaconOf.ContainsKey(b.Name)) want.Add((b.Name, GBodies, SystemHost.DisplayName(b.Name)));
        var secs = new HashSet<string>();
        foreach (var st in EncounterFrames.Sites) if (st.Anchor && st.Sector != null && secs.Add(st.Sector)) want.Add((st.Sector, GSectors, st.Sector));
        int rocksKnown = RockRows(want, pm, t);
        foreach (var kv in _places) want.Add((kv.Key, GPlaces, kv.Value.Name));
        // (a ship's saved keys follow the ship: after a load its frame's anchor may be another of its grids - the name, the pin
        //  and the hidden mark move to the key its row has now)
        Rekey(want);
        for (int i = 0; i < want.Count; i++) if (HiddenKeys.Contains(want[i].key)) want[i] = (want[i].key, GHidden, want[i].name);

        var seen = new HashSet<string>();
        bool regroup = false;   // (the screen reads a group's markers when it makes the group: told again on a change)
        foreach (var w in want)
        {
            seen.Add(w.key);
            if (!_rows.TryGetValue(w.key, out var r))
            {
                r = new Row { Key = w.key, Group = w.group };
                r.M = new GPSMarker((GPSMarkerTypeDefinition)_type, w.name, "", pw, _icon, ColorSRGB.White, false, true);
                _ours.Add(r.M);
                _rows[w.key] = r;
                PlanetRenderBridge.CallAdd(PlanetRenderBridge.GetMember(GroupOf(w.group), "Markers"), r.M);
                PlanetRenderBridge.CallAdd(PlanetRenderBridge.GetMember(_local, "GPSMarkers"), r.M);
                r.M.Hidden = w.key.StartsWith(PlacePrefix) ? _places[w.key].Hidden : !Pinned.Contains(w.key);
                regroup = true;
            }
            else if (r.Group != w.group)
            {
                PlanetRenderBridge.CallRemove(PlanetRenderBridge.GetMember(GroupOf(r.Group), "Markers"), r.M);
                PlanetRenderBridge.CallAdd(PlanetRenderBridge.GetMember(GroupOf(w.group), "Markers"), r.M);
                r.Group = w.group;
                regroup = true;
            }
            // a rename in the screen: kept (the name we set last is in Shown)
            if (r.Shown != null && r.M.Name != r.Shown && !string.IsNullOrWhiteSpace(r.M.Name))
            {
                if (w.key.StartsWith(PlacePrefix)) { _places[w.key].Name = Strip(r.M.Name); r.Shown = r.M.Name; continue; }   // (its key follows its name: a new row next time)
                string nm = Strip(r.M.Name);
                lock (Sync) Names[w.key] = nm;
            }
            string name = Names.TryGetValue(w.key, out var nn) ? (w.name.StartsWith("≈ ") ? "≈ " + nn : nn) : w.name;
            if (w.key == Maneuvers.Target) name = "◆ " + name;
            name += Suffix(w.key);
            if (r.M.Name != name) r.M.Name = name;
            r.Shown = name;
            // the eye: a place's is its GPS point's; anything else's pins it to the HUD
            if (w.key.StartsWith(PlacePrefix)) { var real = _places[w.key]; if (r.M.Hidden != real.Hidden) FrameMarkers.SetUserHidden(real, r.M.Hidden); }
            else if (w.key != ClearKey && r.M.Hidden == Pinned.Contains(w.key)) lock (Sync) { if (r.M.Hidden) Pinned.Remove(w.key); else Pinned.Add(w.key); }
            // where it is, from you, at its true distance (the Clear row: where the target is)
            if (Targets.RootAt(w.key == ClearKey ? Maneuvers.Target : w.key, t, out var root))
            {
                Vector3D p = pw + pc.FromInertial(root.Position - pm);
                if (IsFinite(p)) r.M.Position = p;
            }
        }
        foreach (var k in new List<string>(_rows.Keys))
        {
            if (seen.Contains(k)) continue;
            var r = _rows[k];
            PlanetRenderBridge.CallRemove(PlanetRenderBridge.GetMember(GroupOf(r.Group), "Markers"), r.M);
            PlanetRenderBridge.CallRemove(PlanetRenderBridge.GetMember(_local, "GPSMarkers"), r.M);
            _ours.Remove(r.M);
            _rows.Remove(k);
            regroup = true;
        }
        Counts(want, rocksKnown);
        if (regroup || force)
        {
            // (SE2 4.0: RefreshGroups is gone - RequestRefreshTree, which coalesces and runs RefreshTree on the UI thread)
            try { (_vm?.GetType().GetMethod("RequestRefreshTree", BindingFlags.Instance | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null)
                   ?? _vm?.GetType().GetMethod("RefreshGroups", BindingFlags.Instance | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null))?.Invoke(_vm, null); }
            catch (Exception e) { PlanetRenderBridge.WarnOnce("contacts-regroup", "contacts tab regroup: " + PlanetRenderBridge.Inner(e)); }
        }
        Sanitize();
        Status = $"installed: {_rows.Count} row(s), {Pinned.Count} pinned, {_places.Count} place(s)";
    }

    static void Rekey(List<(string key, string group, string name)> want)
    {
        var rowKeys = new HashSet<string>();
        foreach (var w in want) rowKeys.Add(w.key);
        string Home(string k)
        {
            if (rowKeys.Contains(k) || !Targets.GridOf(k, out long id)) return null;
            foreach (var w in want) if (Targets.GridOf(w.key, out _) && Targets.SameShip(w.key, id)) return w.key;
            return null;
        }
        // (copied and changed under Sync; Home - the targets - outside it)
        List<string> pins, hides, named;
        lock (Sync) { pins = new List<string>(Pinned); hides = new List<string>(HiddenKeys); named = new List<string>(Names.Keys); }
        foreach (var k in pins) { var h = Home(k); if (h != null) lock (Sync) { Pinned.Remove(k); Pinned.Add(h); } }
        foreach (var k in hides) { var h = Home(k); if (h != null) lock (Sync) { HiddenKeys.Remove(k); HiddenKeys.Add(h); } }
        foreach (var k in named) { var h = Home(k); if (h != null) lock (Sync) { if (Names.TryGetValue(k, out var v)) { Names.Remove(k); Names[h] = v; } } }
        if (Maneuvers.Target != null) { var h = Home(Maneuvers.Target); if (h != null) Maneuvers.Target = h; }
    }

    /// <summary>A row's name back to the bare name: no target mark, no "≈", no state suffix.</summary>
    static string Strip(string s)
    {
        if (s.StartsWith("◆ ")) s = s.Substring(2);
        if (s.StartsWith("≈ ")) s = s.Substring(2);
        int k = s.LastIndexOf("  [", StringComparison.Ordinal);
        if (k > 0 && s.EndsWith("]")) s = s.Substring(0, k);
        return s;
    }

    /// <summary>A contact's state after its name: "  [large ship · radar · tracking 40% · seen 2 h ago]"; "" for the rest.
    /// (Kept short and slow-changing: the range is the distance column, the bearing and relative speed the target panel's.)</summary>
    static string Suffix(string key)
    {
        if (key == ClearKey || !Contacts.InfoOf(key, out var i) || !i.Known) return "";
        string st = Contacts.StateText(i);
        string by = SEAerospace.Sensing.Signature.Name(i.Via);
        string kind = i.Rock ? "rock" : Contacts.KindText(i);
        return $"  [{kind}{(by != "-" ? "  ·  " + by : "")}{(st.Length > 0 ? "  ·  " + st : "")}]";
    }

    /// <summary>The Rocks group: the rocks you have seen (ring rocks and belt asteroids), nearest first, MaxRockRows of them.
    /// Returns how many are known in all (its header's count).</summary>
    static int RockRows(List<(string key, string group, string name)> want, Vector3D you, double t)
    {
        var rocks = _rockScratch; rocks.Clear();
        int known = 0;
        foreach (var b in RingRocks.Belts())
        {
            var o = b.Body.OriginInRoot(t).Position;
            for (int i = 0; i < b.R.Length; i++)
            {
                string label = Targets.RingRockLabel(b, i);
                if (!Contacts.KnownRock(label)) continue;
                known++;
                rocks.Add((label, (o + RingRocks.At(b, i, t) - you).LengthSquared()));
            }
        }
        var reg = SystemHost.Registry;
        foreach (var (belt, label, home, cluster) in AsteroidFrames.All())
        {
            if (!Contacts.KnownRock(label)) continue;
            known++;
            rocks.Add((label, (SectorHomes.Where(home, reg, t) - you).LengthSquared()));
        }
        rocks.Sort((x, y) => x.d2.CompareTo(y.d2));
        for (int i = 0; i < rocks.Count && i < MaxRockRows; i++)
            want.Add((rocks[i].label, GRocks, (Contacts.TrackedRock(rocks[i].label) ? "" : "≈ ") + rocks[i].label));
        rocks.Clear();
        return known;
    }
    static readonly List<(string label, double d2)> _rockScratch = new List<(string, double)>();

    /// <summary>Each group's header with its count ("Contacts (4)"; the Rocks' all known: "Rocks (93, nearest 40)").</summary>
    static void Counts(List<(string key, string group, string name)> want, int rocksKnown)
    {
        var n = _counts; n.Clear();
        foreach (var w in want) { n.TryGetValue(w.group, out int c); n[w.group] = c + 1; }
        foreach (var gname in GroupOrder)
        {
            n.TryGetValue(gname, out int c);
            string head = gname == GTarget ? gname
                        : gname == GRocks && rocksKnown > c ? $"{gname} ({rocksKnown}, nearest {c})"
                        : $"{gname} ({c})";
            var g = GroupOf(gname);
            if (g.Name != head) g.Name = head;   // (the screen's group row follows its Name: GPSMarkerGroupViewModel)
        }
        n.Clear();
    }
    static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

    static Group GroupOf(string name)
    {
        if (_groups.TryGetValue(name, out var g)) return g;
        g = new Group(name);
        _groups[name] = g;
        // in the fixed order
        var list = PlanetRenderBridge.GetMember(_local, "MarkersGroups");
        PlanetRenderBridge.CallAdd(list, g);
        return g;
    }

    /// <summary>
    /// The real GPS data never keeps one of ours: the screen's "group" (and its like) sends the selected markers to the
    /// server's list; any of ours found there is taken out again the same way.
    /// </summary>
    static MethodInfo _remove;
    static void Sanitize()
    {
        var real = FrameMarkers.PlaceMarkers(_session);
        if (real == null) return;
        foreach (var m in real)
        {
            if (m == null) continue;
            // (one of OUR objects in the real list: never sent to the server's remove - it cannot pair it, and that is the
            //  fatal crash of 2026-10-11; only the server's own copies, found by name and place below, are removed)
            if (_ours.Contains(m)) { PlanetRenderBridge.WarnOnce("contacts-own-in-real", "contacts: one of ours is in the real GPS list - left alone (the server cannot remove it)"); continue; }
            bool ours = false;
            // (a copy sent to the server, in multiplayer: by its name and place - never a place's row: those mirror real points)
            if (!ours) foreach (var r in _rows.Values) if (!r.Key.StartsWith(PlacePrefix) && r.M.Name == m.Name && (r.M.Position - m.Position).LengthSquared() < 1) { ours = true; break; }
            // (GPSMarkersData.RemoveMarker: a network story - its type is one mod code cannot name, so called by reflection)
            if (ours) { try { _remove ??= typeof(GPSMarkersData).GetMethod("RemoveMarker", BindingFlags.Public | BindingFlags.Static); _remove?.Invoke(null, new object[] { _session, m }); } catch { } }
        }
    }

    /// <summary>The map closed (its terminal goes): the next terminal gets ours anew.</summary>
    public static void Reset() { _term = null; _vm = null; _mapTab = null; }

    /// <summary>A new world: nothing kept from the last one.</summary>
    public static void ResetWorld()
    {
        Reset();
        _rows.Clear(); _groups.Clear(); _ours.Clear(); _places.Clear(); _local = null;
        lock (Sync) { Pinned.Clear(); Names.Clear(); HiddenKeys.Clear(); }
    }

    /// <summary>DEV: open the tab (as a click on it), type a search, pin a row (as its eye), select a row.</summary>
    public static string DevCommand(string[] a)
    {
        string sub = a.Length > 1 ? a[1] : "";
        switch (sub)
        {
            case "open":
                if (_term == null) return "no terminal";
                if (Prop(_term, "SubScreens") is System.Collections.IList subs)
                    foreach (var s in subs) if (Prop(Prop(s, "Id"), "Value") as string == "Map/GPS") { _term.GetType().GetProperty("SelectedSubScreen")?.SetValue(_term, s); return "contacts tab selected"; }
                return "no GPS sub screen";
            case "search":
                _vm?.GetType().GetProperty("SearchString")?.SetValue(_vm, a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "");
                return "search '" + (Prop(_vm, "SearchString") ?? "-") + "' -> " + (Prop(_vm, "TreeItems") is System.Collections.ICollection ti ? ti.Count : -1) + " top-level item(s)";
            case "pin":
            case "unpin":
            {
                string k = a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "";   // (a place's key may hold spaces)
                if (!_rows.TryGetValue(k, out var r)) return "no such row";
                r.M.Hidden = sub == "unpin";
                Refresh(force: true);
                return k.StartsWith(PlacePrefix) ? $"{k} its GPS point hidden={(_places.TryGetValue(k, out var pm) ? pm.Hidden.ToString() : "?")}" : $"{k} pinned={Pinned.Contains(k)}";
            }
            case "rename":   // as the screen's rename does: the marker's name set
                if (a.Length < 4 || !_rows.TryGetValue(a[2], out var rr)) return "no such row";
                rr.M.Name = string.Join(" ", a, 3, a.Length - 3);
                Refresh(force: true);
                return $"{a[2]} renamed: names {(Names.TryGetValue(a[2], out var nm) ? nm : "-")}";
            case "select":
                if (a.Length < 3 || !_rows.TryGetValue(a[2], out var rs)) return "no such row";
                // (SE2 4.0: _markerViewModels is a GPSMarker -> view model dictionary, was a list)
                var mvs = Field(_vm, "_markerViewModels");
                foreach (var mv in mvs is System.Collections.IDictionary md ? md.Values : (System.Collections.IEnumerable)mvs)
                    if (ReferenceEquals(Prop(mv, "GPSMarker"), rs.M)) { _vm.GetType().GetProperty("SelectedMarker")?.SetValue(_vm, mv); OnSelection(); return "selected " + a[2] + ", target " + (Maneuvers.Target ?? "none"); }
                return "row not in the screen's list";
        }
        return Describe();
    }

    /// <summary>DEV: the tab's state and rows.</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder(Status);
        string hdr = null;
        if (Prop(_term, "SubScreens") is System.Collections.IList subs)
            foreach (var s in subs) if (Prop(Prop(s, "Id"), "Value") as string == "Map/GPS") hdr = Prop(s, "LocalizedHeader") as string;
        sb.Append($" | tab '{hdr ?? "-"}' | hidden {HiddenKeys.Count}");
        foreach (var r in _rows.Values) sb.Append($" [{r.Group}: '{r.M.Name}' {(r.M.Hidden ? "" : "pinned ")}{r.Key}]");
        return sb.ToString();
    }

}
