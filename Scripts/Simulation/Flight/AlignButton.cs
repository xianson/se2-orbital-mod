using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE ALIGN BUTTON (2026-10-12, the user: "add it as a button after damps, slide everything left 1 on the right toggles,
/// the button will highlight if active and show the icon of what we're aligning to as we cycle through the modes; a tool
/// tip next to the ones above the toolbar"). Seated, the cockpit HUD's toggle row (power, lights, broadcast, dampeners -
/// the game's CockpitHUDToggles, right-aligned at the bottom right) is moved one slot left and the attitude hold
/// (AttitudeHold) drawn in the slot it left, in the toggles' own look: a chamfered square, dark with a light blue-grey
/// frame and icon; lit, orange with a black icon (measured on the game's at 4K: 92 px squares on a 124 px pitch, 46 px from
/// the right edge, their tops 1654 px down). Its icon: the SELECTED mode (prograde ... radial in, level); lit while held.
/// The hints: the game's own (NativeHints - two ControlHints added to the active hint layer, so the game draws them on its
/// line, in its look): "[;] Turn Align On (mode)" / "Turn Align Off" and "[Ctrl] [;] Align <the next mode>".
/// The game's UI is Avalonia, which scripts cannot reference: found, measured and moved by reflection on its UI thread
/// (UiThread.Post) every HalfSecond; the move is an Animation-priority RenderTransform, let go when not seated.
/// </summary>
public static class AlignButton
{
    public static bool On = true;
    public static string Status = "-";
    // (1080p units; x u = H / 1080)
    const float Size = 46f, Pitch = 62f, RightMargin = 23f, TopFromBottom = 253f, Chamfer = 4f;
    static readonly ColorSRGB Frame = new ColorSRGB(143f / 255f, 167f / 255f, 172f / 255f, 1f);
    static readonly ColorSRGB Dark = new ColorSRGB(0.07f, 0.07f, 0.06f, 0.78f);
    static readonly ColorSRGB Orange = new ColorSRGB(224f / 255f, 138f / 255f, 13f / 255f, 1f);
    static readonly ColorSRGB Black = new ColorSRGB(0f, 0f, 0f, 1f);
    static readonly ColorSRGB White = new ColorSRGB(1f, 1f, 1f, 1f), Shadow = new ColorSRGB(0f, 0f, 0f, 0.85f);

    // (written by the UI thread's job, read by the draw)
    static volatile bool _shifted;
    static double _nextPost; static volatile bool _posted;
    static IDisposable _shiftHold; static object _heldToggles; static float _shiftUnits;

    /// <summary>The slot's rectangle (screen px) the button takes: the rightmost toggle's, before the row moved - the game's
    /// own (HudGeom, read off its controls), else the 4K measurement scaled (the user, 2026-10-12: at 640x480, 4:3, ours sat
    /// off the row - the game lays its HUD out differently at other sizes and shapes).</summary>
    public static bool Slot(out Vector2 min, out Vector2 max)
    {
        var g = HudGeom.Slot;
        if (g != null) { min = new Vector2(g[0], g[1]); max = new Vector2(g[0] + g[2], g[1] + g[3]); return _shifted; }
        var scr = MapPipeline.ScreenSize; float u = MapPipeline.UiScale(scr.Y);
        max = new Vector2(scr.X - RightMargin * u, scr.Y - TopFromBottom * u + Size * u);
        min = max - new Vector2(Size * u, Size * u);
        return _shifted;
    }
    /// <summary>The whole toggle row's top-right (screen px) - the warp bar sits above it.</summary>
    public static Vector2 RowTopRight
    {
        get
        {
            var g = HudGeom.Slot;
            if (g != null) return new Vector2(g[0] + g[2], g[1]);
            var scr = MapPipeline.ScreenSize; float u = MapPipeline.UiScale(scr.Y); return new Vector2(scr.X - RightMargin * u, scr.Y - TopFromBottom * u);
        }
    }

    public static void Draw(Keen.VRage.Core.Game.Systems.Session session)
    {
        bool want = On && FrameHost.Seated && !MapView.Visible;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now >= _nextPost && !_posted)
        {
            _nextPost = now + 0.5;
            var cockpitHud = want ? FindHudScreen(session) : null;
            // (on foot: the character HUD's own SPD and ALT, for the orbit disc and the build boxes - HudGeom)
            var footHud = !want && On && !FrameHost.Seated && !MapView.Visible ? FindCharacterScreen(session) : null;
            var hintsVm = want ? HintsViewModel(session) : null;
            float scrW = MapPipeline.ScreenSize.X, u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y);
            // (the hints' texts, here: AttitudeHold is the game thread's)
            bool on = AttitudeHold.Current != AttitudeHold.Mode.Off;
            string t1 = on ? "Turn Align Off" : $"Turn Align On ({AttitudeHold.Label(AttitudeHold.Selected)})";
            var next = AttitudeHold.Next();
            string t2 = next != AttitudeHold.Selected ? "Align " + AttitudeHold.Label(next) : null;
            _posted = UiThread.Post(() =>
            {
                try { if (footHud != null) FootJob(footHud, scrW); else UiJob(cockpitHud, scrW, u); } catch (Exception e) { Status = "ui: " + (e.InnerException ?? e).Message; }
                try { NativeHints.Update(hintsVm, t1, t2); } catch (Exception e) { NativeHints.Status = "hints: " + (e.InnerException ?? e).Message; }
                finally { _posted = false; }
            });
        }
        if (!want || !_shifted) return;
        if (!FrameMarkers.BeginHud(session)) return;
        try { DrawButton(); }
        finally { MapPipeline.UiEnd(); }
    }

    static readonly List<Vector2> _pts = new List<Vector2>(8);
    static void DrawButton()
    {
        Slot(out var a, out var b);
        float u = MapPipeline.UiScale(MapPipeline.ScreenSize.Y), c = Chamfer * u;
        var pts = _pts; pts.Clear();   // (one list, refilled: a new one every frame was most of this draw's own garbage)
        pts.Add(new(a.X + c, a.Y)); pts.Add(new(b.X - c, a.Y)); pts.Add(new(b.X, a.Y + c)); pts.Add(new(b.X, b.Y - c));
        pts.Add(new(b.X - c, b.Y)); pts.Add(new(a.X + c, b.Y)); pts.Add(new(a.X, b.Y - c)); pts.Add(new(a.X, a.Y + c));
        bool lit = AttitudeHold.Current != AttitudeHold.Mode.Off;
        MapPipeline.ScreenFill(pts, lit ? Orange : Dark);
        if (!lit) for (int k = 0; k < pts.Count; k++) MapPipeline.ScreenLine(pts[k], pts[(k + 1) % pts.Count], Frame, 1f * u);
        var ic = lit ? Black : Frame;
        var mid = (a + b) * 0.5f; float half = (b.X - a.X) * 0.32f;
        string icon = AttitudeHold.Selected switch
        {
            AttitudeHold.Mode.Prograde => "prograde", AttitudeHold.Mode.Retrograde => "retrograde", AttitudeHold.Mode.Normal => "normal",
            AttitudeHold.Mode.AntiNormal => "antinormal", AttitudeHold.Mode.RadialOut => "radialout", AttitudeHold.Mode.RadialIn => "radialin", _ => null,
        };
        if (icon != null && MapIcons.Has(icon)) MapPipeline.ScreenIcon(icon, mid, half, ic);
        else Horizon(mid, half, ic, u);
    }

    /// <summary>The level glyph: the horizon line, a small aircraft on it (wings, a centre dot, a fin).</summary>
    static void Horizon(Vector2 m, float h, ColorSRGB c, float u)
    {
        float w = 2.2f * u;
        MapPipeline.ScreenLine(m + new Vector2(-h, 0.35f * h), m + new Vector2(h, 0.35f * h), c, w);
        MapPipeline.ScreenLine(m + new Vector2(-0.8f * h, -0.1f * h), m + new Vector2(-0.2f * h, -0.1f * h), c, w);
        MapPipeline.ScreenLine(m + new Vector2(0.2f * h, -0.1f * h), m + new Vector2(0.8f * h, -0.1f * h), c, w);
        MapPipeline.ScreenLine(m + new Vector2(-0.2f * h, -0.1f * h), m + new Vector2(0f, 0.12f * h), c, w);
        MapPipeline.ScreenLine(m + new Vector2(0f, 0.12f * h), m + new Vector2(0.2f * h, -0.1f * h), c, w);
        MapPipeline.ScreenLine(m + new Vector2(0f, -0.1f * h), m + new Vector2(0f, -0.45f * h), c, w);
    }

    // ── the game's HUD (reflection; UI thread) ──

    /// <summary>The open cockpit HUD screen: from the cockpit's CockpitHUDComponent's handle (_hud), breadth-first.</summary>
    static object FindHudScreen(Keen.VRage.Core.Game.Systems.Session session)
    {
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CockpitHUDComponent>())
        {
            var root = PlanetRenderBridge.GetMember(e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CockpitHUDComponent>(), "_hud");
            var s = UiThread.FindByTypeName(root, "CockpitHUDScreen", 5, 400);
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>The game's hint view model (ControlHintsSessionComponent.ViewModel, by reflection: its type's base is in
    /// VRage.UI, which scripts cannot reference).</summary>
    static object HintsViewModel(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var c = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.ControlHintsSessionComponent>();
            return c?.GetType().GetProperty("ViewModel")?.GetValue(c);
        }
        catch { return null; }
    }

    /// <summary>DEV: the next job dumps the tree under this type name (HudGeom.Tree).</summary>
    public static volatile string TreeWanted; public static int TreeMax = 60;

    /// <summary>On foot's HUD screen (CharacterHUDComponent's handle, _hud).</summary>
    static object FindCharacterScreen(Keen.VRage.Core.Game.Systems.Session session)
    {
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CharacterHUDComponent>())
        {
            var root = PlanetRenderBridge.GetMember(e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CharacterHUDComponent>(), "_hud");
            var s = UiThread.FindByTypeName(root, "CharacterHUDScreen", 5, 400);
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>UI thread, on foot: no toggle row of ours (the cockpit's let go), the SPD and ALT boxes read.</summary>
    static void FootJob(object screen, float scrW)
    {
        Release();
        object top = UiThread.VisualRoot(screen) ?? screen;
        var rb = UiThread.Bounds(top);
        float scale = rb.w > 1f ? scrW / rb.w : 1f;
        HudGeom.Slot = null; HudGeom.Pitch = 0f;
        HudGeom.MeasureBoxes(top, scale);
        Status = "on foot; " + HudGeom.Status;
    }

    static void UiJob(object screen, float scrW, float u)
    {
        if (TreeWanted != null && screen != null)
        {
            var rt = UiThread.Bounds(UiThread.VisualRoot(screen) ?? screen);
            HudGeom.Tree = HudGeom.TreeOf(screen, TreeWanted, TreeMax, rt.w > 1f ? scrW / rt.w : 1f);
            TreeWanted = null;
        }
        object toggles = screen != null ? UiThread.Descendant(screen, "CockpitHUDToggles") : null;
        if (toggles == null)
        {
            Release();
            HudGeom.Clear();
            Status = screen == null ? "not seated (no cockpit HUD)" : "no CockpitHUDToggles in the cockpit HUD";
            return;
        }
        object top = UiThread.VisualRoot(screen) ?? screen;
        var rb = UiThread.Bounds(top);
        float scale = rb.w > 1f ? scrW / rb.w : 1f;   // (screen px per UI unit)
        // the game's own places: its toggle buttons (before our move), its SPD and ALT boxes (HudGeom)
        HudGeom.Measure(screen, top, scale, toggles, _shiftHold != null && ReferenceEquals(toggles, _heldToggles) ? _shiftUnits : 0f);
        // the row one slot left - by the game's own pitch (an Animation-priority transform: its own value back when let go)
        if (!ReferenceEquals(toggles, _heldToggles) || _shiftHold == null)
        {
            Release();
            _shiftUnits = HudGeom.Pitch > 0f ? HudGeom.Pitch / scale : Pitch * u / scale;
            _shiftHold = UiThread.HoldTranslate(toggles, -_shiftUnits, 0);
            _heldToggles = _shiftHold != null ? toggles : null;
        }
        _shifted = _shiftHold != null;
        Status = $"toggles moved {(_shifted ? "yes" : "no")}, scale {scale:F2}; {HudGeom.Status}; {NativeHints.Status}";
    }

    static void Release()
    {
        try { _shiftHold?.Dispose(); } catch { }
        _shiftHold = null; _heldToggles = null; _shifted = false;
    }
}
