using System;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE GAME'S HUD NOTIFICATIONS (2026-10-18, UI audit C1: "the game's 'Press P to Park/Lock/Connect' box sits on the orbit
/// card - over speed, BURN, ENTRY, target lines - even over the map"). That box is a HUD notification (HUDNotificationsScreen
/// -> HUDNotificationCollectionView: its ItemsControl, centred 370 design units left of the screen's middle, from 100 units
/// below it, growing down - decompiled 2.4). Read here on the UI thread every 0.25 s: its stack's screen rectangle
/// (<see cref="Rect"/>, null when empty or hidden) - the orbit card moves its lines clear of it (OrbitHud) - and while the
/// mod's map is open the whole screen is held invisible (an Animation-priority IsVisible: the game's own value back when
/// the map closes). Reflection: SharedUIComponent._activeScreenHandles (AvaloniaReflect.ActiveScreen), Avalonia's
/// visual tree (UiThread); docs/REFLECTION_AND_WHITELIST.md.
/// </summary>
public static class GameNotes
{
    /// <summary>The notification stack on screen (px: x, y, w, h), or null (none shown, not found).</summary>
    public static volatile float[] Rect;
    public static string Status = "-";
    static double _next; static volatile bool _posted;
    static object _shared;
    static object _screen; static IDisposable _hide;
    static readonly Action _job = Job;
    static volatile bool _wantHidden; static float _scrW;

    /// <summary>Every frame (game thread, cheap): four times a second, the UI thread's look.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        double now = Num.Wall();
        if (now < _next || _posted) return;
        _next = now + 0.25;
        _shared ??= AvaloniaReflect.SharedUi(session);
        if (_shared == null) { Status = "no SharedUIComponent"; return; }
        _wantHidden = MapView.Visible && CleanMap.Enabled;
        _scrW = MapPipeline.ScreenSize.X;
        _posted = UiThread.Post(_job);
    }

    /// <summary>A new session: nothing of the last one kept.</summary>
    public static void Reset() { UiThread.Post(() => { Release(); _screen = null; }); _shared = null; Rect = null; }

    static void Release() { try { _hide?.Dispose(); } catch { } _hide = null; }

    static void Job()
    {
        try
        {
            var screen = AvaloniaReflect.ActiveScreen(_shared, "HUDNotificationsScreen");
            if (!ReferenceEquals(screen, _screen)) { Release(); _screen = screen; }
            if (screen == null) { Rect = null; Status = "no HUDNotificationsScreen"; return; }
            if (_wantHidden)
            {
                _hide ??= UiThread.HoldVisible(screen, false);
                Rect = null;
                Status = _hide != null ? "hidden over the map" : "over the map, could not hide";
                return;
            }
            Release();
            var items = UiThread.Descendant(screen, "ItemsControl");
            if (items == null || !UiThread.IsVisible(items)) { Rect = null; Status = "no items"; return; }
            var top = UiThread.VisualRoot(items) ?? screen;
            var rb = UiThread.Bounds(top);
            float scale = rb.w > 1f ? _scrW / rb.w : 1f;
            var q = UiThread.RectIn(items, top);
            if (q.w <= 1f || q.h <= 2f) { Rect = null; Status = "empty"; return; }
            Rect = new[] { q.x * scale, q.y * scale, q.w * scale, q.h * scale };
            Status = $"notes {Rect[0]:F0},{Rect[1]:F0} {Rect[2]:F0}x{Rect[3]:F0}";
        }
        catch (Exception e) { Rect = null; Status = "notes: " + (e.InnerException ?? e).Message; }
        finally { _posted = false; }
    }
}
