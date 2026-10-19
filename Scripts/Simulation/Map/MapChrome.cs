using System;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE GAME'S MAP PANELS THAT CONTRADICT THIS MAP (2026-10-18, UI audit C6), quieted - only while this mod's map is open,
/// each held with an Animation-priority IsVisible (UiThread.HoldVisible: the game's own value back when let go):
///  - the Colonization tab's "Selected sector: Verdure Sector" box (ColonizationView.PART_Content's border) while the map is
///    about another planet - it stayed on Verdure while you looked at Kemik (CleanMap sets <see cref="StaleSector"/>);
///  - a world without colonization sectors: the empty colonization index box at the top right (MapOverlayView.PART_Colonization).
/// The terminal's map hints (the game's "Zoom in to view intersector pathways / W Forward / A Move Left ...": this map moves
/// by the mouse, its keys are its own hint line) are emptied once per terminal (RendezvousView.Inject -> ClearMapHints).
/// UI thread twice a second; reflection in docs/REFLECTION_AND_WHITELIST.md (§3.9, 2026-10-18).
/// </summary>
public static class MapChrome
{
    public static string Status = "-";
    /// <summary>The game's selected sector is about another planet than the one the map is about (CleanMap, each frame).</summary>
    public static volatile bool StaleSector;
    static double _next; static volatile bool _posted;
    static volatile bool _open, _sectorless;
    static object _shared;
    static object _terminal, _sectorBox, _indexBox;
    static IDisposable _sectorHold, _indexHold;
    static readonly Action _job = Job;

    /// <summary>Game thread, every frame (cheap): twice a second the UI thread's look; once more as the map closes.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session, bool mapOpen)
    {
        if (!mapOpen && !_open && _sectorHold == null && _indexHold == null) return;
        double now = Num.Wall();
        if ((now < _next && mapOpen == _open) || _posted) return;
        _next = now + 0.5;
        _shared ??= AvaloniaReflect.SharedUi(session);
        if (_shared == null) return;
        _open = mapOpen && CleanMap.Enabled;
        _sectorless = MapView.Sectorless;
        _posted = UiThread.Post(_job);
    }

    static void Release(ref IDisposable h) { try { h?.Dispose(); } catch { } h = null; }

    static void Job()
    {
        try
        {
            if (!_open) { Release(ref _sectorHold); Release(ref _indexHold); _terminal = _sectorBox = _indexBox = null; Status = "map closed"; return; }
            var term = AvaloniaReflect.ActiveScreen(_shared, "TerminalScreen");
            if (!ReferenceEquals(term, _terminal)) { Release(ref _sectorHold); Release(ref _indexHold); _terminal = term; _sectorBox = _indexBox = null; }
            if (term == null) { Status = "no TerminalScreen among the active screens"; return; }
            // the selected sector's box: the border round ColonizationView.PART_Content
            if (_sectorBox == null)
            {
                var cv = UiThread.Descendant(term, "ColonizationView");
                _sectorBox = UiThread.Parent(AvaloniaReflect.Field(cv, "PART_Content"));
            }
            if (StaleSector && !_sectorless) { if (_sectorHold == null && _sectorBox != null) _sectorHold = UiThread.HoldVisible(_sectorBox, false); }
            else Release(ref _sectorHold);
            // the colonization index box, empty in a world without sectors
            if (_indexBox == null) _indexBox = AvaloniaReflect.Field(UiThread.Descendant(term, "MapOverlayView"), "PART_Colonization");
            if (_sectorless) { if (_indexHold == null && _indexBox != null) _indexHold = UiThread.HoldVisible(_indexBox, false); }
            else Release(ref _indexHold);
            Status = $"sector box {(_sectorBox == null ? "not found" : _sectorHold != null ? "hidden (stale)" : "shown")}, index box {(_indexBox == null ? "not found" : _indexHold != null ? "hidden (sectorless)" : "shown")}";
        }
        catch (Exception e) { Status = "chrome: " + (e.InnerException ?? e).Message; }
        finally { _posted = false; }
    }

    /// <summary>
    /// The map tabs' bottom hints emptied (the Colonization tab's and ours - the game's W A S D and wheel hints, which do
    /// nothing on this map: its own keys are on its hint line). TerminalSubScreen.ControlHints (an init-only property, set
    /// by reflection) to an empty list, then the terminal's private UpdateControlHints re-reads the selected one. Once per
    /// terminal; false: not done (the game's hints stay).
    /// </summary>
    public static bool ClearMapHints(object term, object colonization, object ours)
    {
        try
        {
            var prop = colonization?.GetType().GetProperty("ControlHints", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || !prop.PropertyType.IsGenericType) return false;
            var empty = Array.CreateInstance(prop.PropertyType.GetGenericArguments()[0], 0);
            var set = prop.GetSetMethod(true);
            if (set == null) return false;
            set.Invoke(colonization, new object[] { empty });
            if (ours != null) set.Invoke(ours, new object[] { empty });
            MethodInfo upd = null;
            for (var t = term.GetType(); t != null && upd == null; t = t.BaseType)
                upd = t.GetMethod("UpdateControlHints", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            upd?.Invoke(term, null);
            return true;
        }
        catch { return false; }
    }
}
