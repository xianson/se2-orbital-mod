using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable
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

/// <summary>
/// THE GAME'S HUD PLACES (2026-10-12, the user: "the ui is still not right at low res"): our HUD pieces were placed by
/// fractions measured on the game's own at 4K (16:9) - at 640x480 (4:3) the game lays its HUD out otherwise and ours sat
/// off it. Read off its controls instead, on the UI thread, each AlignButton job (screen px, x y w h): the toggle row's
/// buttons (CockpitHUDToggles' square descendants as tall as the row; before our move) - the rightmost is our slot, their
/// spacing the move - and the SPD and ALT boxes (HUDSpeedometer / HUDAltimeter: the largest BeveledBorder in each). Null
/// when not seated or not found: the callers fall back to the 4K measurement.
/// </summary>
public static class HudGeom
{
    public static volatile float[] Slot, Spd, Alt;
    public static volatile float Pitch;
    public static string Status = "-";

    public static void Clear() { Slot = Spd = Alt = null; Pitch = 0f; Status = "not seated"; }

    /// <summary>DEV ('hudtree'): the game HUD's controls under the first one of this type name (in the cockpit HUD screen's
    /// visual root), each with its type, name, rectangle (screen px) and visibility - to see what to read. UI thread.</summary>
    public static volatile string Tree = "-";
    internal static string TreeOf(object screen, string typeName, int max, float scale)
    {
        object top = UiThread.VisualRoot(screen) ?? screen;
        object c = typeName == "root" ? top : UiThread.Descendant(top, typeName);
        if (c == null) return $"no {typeName} under the root ({UiThread.VisualRoot(screen)?.GetType().Name ?? "no root"})";
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var v in new[] { c }.Concat(UiThread.Descendants(c)))
        {
            if (n++ >= max) { sb.Append(" ..."); break; }
            var q = UiThread.RectIn(v, top);
            string nm = v.GetType().GetProperty("Name")?.GetValue(v) as string;
            string text = v.GetType().GetProperty("Text")?.GetValue(v) as string;
            sb.Append($" | {v.GetType().Name}{(string.IsNullOrEmpty(nm) ? "" : "#" + nm)}{(text != null ? $"'{text}'" : "")} {q.x * scale:F0},{q.y * scale:F0} {q.w * scale:F0}x{q.h * scale:F0}{(UiThread.IsVisible(v) ? "" : " hidden")}");
        }
        return sb.ToString();
    }

    /// <summary>UI thread. shiftUnits: our current move of the toggle row (UI units, taken back out).</summary>
    internal static void Measure(object screen, object top, float scale, object toggles, float shiftUnits)
    {
        // the toggle buttons: the BeveledBorders in its row (#Power, #ParkingBrake, #Antenna, #Dampener - hudtree, 2026-10-12)
        var xs = new List<(float x, float y, float w, float h)>();
        foreach (var v in UiThread.Descendants(toggles))
        {
            if (!v.GetType().Name.Contains("Beveled") || !UiThread.IsVisible(v)) continue;
            var q = UiThread.RectIn(v, top);
            if (q.w <= 0f || q.h <= 0f) continue;
            xs.Add(((q.x + shiftUnits) * scale, q.y * scale, q.w * scale, q.h * scale));
        }
        xs.Sort((a, b) => a.x.CompareTo(b.x));
        if (xs.Count >= 2)
        {
            var last = xs[xs.Count - 1];
            Slot = new[] { last.x, last.y, last.w, last.h };
            Pitch = last.x - xs[xs.Count - 2].x;
        }
        else if (xs.Count == 1) { var last = xs[0]; Slot = new[] { last.x, last.y, last.w, last.h }; Pitch = last.w * 1.35f; }
        else { Slot = null; Pitch = 0f; }
        // the SPD and ALT boxes
        MeasureBoxes(top, scale);
        Status = $"[TranslatePoint: {UiThread.TranslateFrom}] toggles {xs.Count} (pitch {Pitch:F0} px), SPD {(Spd != null ? $"{Spd[0]:F0},{Spd[1]:F0} {Spd[2]:F0}x{Spd[3]:F0}" : "-")}, ALT {(Alt != null ? $"{Alt[0]:F0},{Alt[1]:F0} {Alt[2]:F0}x{Alt[3]:F0}" : "-")}";
    }

    /// <summary>The SPD and ALT boxes (either may be missing: on foot in space there is no ALT).</summary>
    internal static void MeasureBoxes(object top, float scale)
    {
        Spd = Box(top, scale, "HUDSpeedometer", "PART_Tachometer");
        Alt = Box(top, scale, "HUDAltimeter", "PART_Readout");
        if (Slot == null) Status = $"SPD {(Spd != null ? $"{Spd[0]:F0},{Spd[1]:F0} {Spd[2]:F0}x{Spd[3]:F0}" : "-")}, ALT {(Alt != null ? $"{Alt[0]:F0},{Alt[1]:F0} {Alt[2]:F0}x{Alt[3]:F0}" : "-")}";
    }

    /// <summary>The readout box (BeveledBorder#part) of the game's SPD / ALT control (hudtree, 2026-10-12).</summary>
    static float[] Box(object top, float scale, string control, string part)
    {
        object c = UiThread.Descendant(top, control);
        if (c == null || !UiThread.IsVisible(c)) return null;
        object b = UiThread.Named(c, "BeveledBorder", part);
        if (b == null || !UiThread.IsVisible(b)) return null;
        var q = UiThread.RectIn(b, top);
        return q.w > 0f && q.h > 0f ? new[] { q.x * scale, q.y * scale, q.w * scale, q.h * scale } : null;
    }
}

/// <summary>
/// OUR HINTS AS THE GAME'S OWN (2026-10-12, the user: "can the hints be the same text / style? same line?"): ControlHints
/// built from the game's own types - a BoundActionHint on an empty InputActionDefinition (its text given), its keys as
/// DigitalControls (Keyboard ';', Ctrl) - and added to the active ControlHintsLayer's Hints, then the view model's
/// RestoreHintVisibility (its public refresh) lays them out: the game draws them on its line, in its look. Rebuilt when a
/// text changes or the game swaps the layer (it rebuilds its hints per input context); taken out when not seated.
/// The hint types' constructors take VRage.Input types (scripts cannot reference that assembly): all by reflection; UI
/// thread only (the hints are bound to the UI).
/// </summary>
public static class NativeHints
{
    public static string Status = "-";
    static bool _looked; static Type _defType, _digitalType, _controlType, _boundType, _hintType;
    static object _layer; static System.Collections.IList _list; static readonly List<object> _ours = new List<object>();
    static string _t1, _t2;

    static void Look()
    {
        if (_looked) return; _looked = true;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            _defType ??= a.GetType("Keen.VRage.Input.InputActionDefinition");
            _digitalType ??= a.GetType("Keen.VRage.Input.DigitalControl");
            _controlType ??= a.GetType("Keen.VRage.Input.InputControl");
            _boundType ??= a.GetType("Keen.Game2.Client.UI.Library.Controls.ControlHints.BoundActionHint");
            _hintType ??= a.GetType("Keen.Game2.Client.UI.Library.Controls.ControlHints.ControlHint");
        }
    }

    /// <summary>UI thread: our hints in the active layer, with these texts (t2 null: the first only; vm null: taken out).</summary>
    public static void Update(object vm, string t1, string t2)
    {
        Look();
        if (_defType == null || _digitalType == null || _controlType == null || _boundType == null || _hintType == null) { Status = "hints: the game's hint types not found"; return; }
        object layer = vm?.GetType().GetMethod("GetActiveLayer")?.Invoke(vm, null);
        var list = layer?.GetType().GetProperty("Hints", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(layer) as System.Collections.IList;
        bool present = list != null && _ours.Count > 0;
        foreach (var h in _ours) if (list == null || !list.Contains(h)) { present = false; break; }
        if (vm == null || list == null) { Remove(); _layer = null; _list = null; Status = vm == null ? "hints: not seated" : "hints: no active layer"; return; }
        if (present && ReferenceEquals(layer, _layer) && t1 == _t1 && t2 == _t2) return;
        Remove();
        _layer = layer; _list = list; _t1 = t1; _t2 = t2;
        _ours.Add(Make(t1, Keen.VRage.Core.Input.KeyboardInputs.OemSemicolon));
        if (t2 != null) _ours.Add(Make(t2, Keen.VRage.Core.Input.KeyboardInputs.Control, Keen.VRage.Core.Input.KeyboardInputs.OemSemicolon));
        foreach (var h in _ours) list.Add(h);
        vm.GetType().GetMethod("RestoreHintVisibility")?.Invoke(vm, null);
        Status = $"hints: {_ours.Count} in the game's line";
    }

    /// <summary>A ControlHint: the text, and a key box per input.</summary>
    static object Make(string text, params Keen.VRage.Core.Input.DigitalInput[] keys)
    {
        var def = Activator.CreateInstance(_defType);
        var action = Activator.CreateInstance(_boundType, def, text);
        var controls = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_controlType));
        foreach (var k in keys) controls.Add(Activator.CreateInstance(_digitalType, k));
        return Activator.CreateInstance(_hintType, action, controls);
    }

    static void Remove()
    {
        if (_list != null) foreach (var h in _ours) { try { _list.Remove(h); } catch { } }
        _ours.Clear(); _t1 = _t2 = null;
    }
}

/// <summary>
/// Avalonia by reflection (scripts cannot reference it): the UI thread's dispatcher, a visual's descendants, root, bounds,
/// position in another, visibility and opacity, and an Animation-priority RenderTransform held. Everything that touches a
/// control must run on the UI thread (Post).
/// </summary>
public static class UiThread
{
    static bool _looked; static object _dispatcher; static MethodInfo _post; static object[] _postDefaults;
    static MethodInfo _descendants, _translatePoint, _setValue;
    static Type _pointType, _translateType; static object _renderTransformProp, _animPriority;
    /// <summary>Where TranslatePoint was found (status).</summary>
    public static string TranslateFrom = "-";

    static void Look()
    {
        if (_looked) return; _looked = true;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name != "Avalonia.Base") continue;
            var dt = a.GetType("Avalonia.Threading.Dispatcher");
            _dispatcher = dt?.GetProperty("UIThread")?.GetValue(null);
            foreach (var m in dt?.GetMethods() ?? Array.Empty<MethodInfo>())
            {
                var ps = m.GetParameters();
                if (m.Name != "Post" || ps.Length == 0 || ps[0].ParameterType != typeof(Action)) continue;
                _post = m; _postDefaults = new object[ps.Length];
                for (int k = 1; k < ps.Length; k++)
                    _postDefaults[k] = ps[k].HasDefaultValue && ps[k].DefaultValue != null ? ps[k].DefaultValue : ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                break;
            }
            // (GetVisualDescendants is Avalonia.VisualTree.VisualExtensions'; TranslatePoint is Avalonia.VisualExtensions' -
            //  looked for in the first only, it was never found and every rectangle came out 0x0)
            foreach (var vn in new[] { "Avalonia.VisualTree.VisualExtensions", "Avalonia.VisualExtensions" })
                foreach (var m in a.GetType(vn)?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
                {
                    if (m.Name == "GetVisualDescendants" && m.GetParameters().Length == 1) _descendants ??= m;
                    if (m.Name == "TranslatePoint" && m.GetParameters().Length == 3) _translatePoint ??= m;
                }
            // (not there: any public static class of Avalonia.Base with a TranslatePoint(visual, point, relativeTo))
            if (_translatePoint == null)
                try
                {
                    foreach (var t in a.GetExportedTypes())
                    {
                        if (!t.IsAbstract || !t.IsSealed) continue;
                        var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "TranslatePoint" && x.GetParameters().Length == 3);
                        if (m != null) { _translatePoint = m; break; }
                    }
                }
                catch { }
            TranslateFrom = _translatePoint?.DeclaringType?.FullName ?? "not found";
            _pointType = a.GetType("Avalonia.Point");
            _translateType = a.GetType("Avalonia.Media.TranslateTransform");
            var visual = a.GetType("Avalonia.Visual");
            _renderTransformProp = visual?.GetField("RenderTransformProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var prio = a.GetType("Avalonia.Data.BindingPriority");
            _animPriority = prio != null ? Enum.Parse(prio, "Animation") : null;
            for (var t = visual; t != null && _setValue == null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "SetValue" && !m.IsGenericMethod && ps.Length == 3 && ps[2].ParameterType == prio && ps[1].ParameterType == typeof(object)) { _setValue = m; break; }
                }
            break;
        }
    }

    /// <summary>Run on the UI thread (false: no dispatcher).</summary>
    public static bool Post(Action a)
    {
        Look();
        if (_dispatcher == null || _post == null) return false;
        var args = (object[])_postDefaults.Clone(); args[0] = a;
        try { _post.Invoke(_dispatcher, args); return true; } catch { return false; }
    }

    public static IEnumerable<object> Descendants(object v)
    {
        Look();
        if (v == null || _descendants == null) yield break;
        System.Collections.IEnumerable all = null;
        try { all = _descendants.Invoke(null, new[] { v }) as System.Collections.IEnumerable; } catch { }
        if (all == null) yield break;
        foreach (var d in all) if (d != null) yield return d;
    }

    public static object Descendant(object v, string typeName)
    {
        foreach (var d in Descendants(v)) if (d.GetType().Name == typeName) return d;
        return null;
    }

    public static object VisualRoot(object v) { try { return v == null ? null : Prop(v.GetType(), "VisualRoot")?.GetValue(v); } catch { return null; } }
    public static bool IsVisible(object v) { try { return v == null ? false : Prop(v.GetType(), "IsEffectivelyVisible")?.GetValue(v) is bool b ? b : Prop(v.GetType(), "IsVisible")?.GetValue(v) is bool c && c; } catch { return false; } }
    public static float Opacity(object v) { try { return v == null ? 1f : Prop(v.GetType(), "Opacity")?.GetValue(v) is double d ? (float)d : 1f; } catch { return 1f; } }
    /// <summary>A descendant of this type name and control name (x:Name), or null.</summary>
    public static object Named(object v, string typeName, string name)
    {
        foreach (var d in Descendants(v))
            if (d.GetType().Name == typeName && Prop(d.GetType(), "Name")?.GetValue(d) as string == name) return d;
        return null;
    }

    /// <summary>A property by name, the most derived one (GetProperty threw AmbiguousMatchException for Bounds - declared on
    /// more than one class of the hierarchy - and every rectangle came out 0x0).</summary>
    static PropertyInfo Prop(Type t, string name)
    {
        for (var x = t; x != null; x = x.BaseType)
        {
            var p = x.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    public static (float x, float y, float w, float h) Bounds(object v)
    {
        try
        {
            var r = v == null ? null : Prop(v.GetType(), "Bounds")?.GetValue(v);
            if (r == null) return default;
            var t = r.GetType();
            return ((float)(double)Prop(t, "X").GetValue(r), (float)(double)Prop(t, "Y").GetValue(r), (float)(double)Prop(t, "Width").GetValue(r), (float)(double)Prop(t, "Height").GetValue(r));
        }
        catch { return default; }
    }

    /// <summary>v's rectangle in root's coordinates (UI units).</summary>
    public static (float x, float y, float w, float h) RectIn(object v, object root)
    {
        Look();
        var b = Bounds(v);
        try
        {
            if (_translatePoint == null || _pointType == null) return default;
            // (both corners: a Viewbox or a transform between them scales the size too)
            var r0 = _translatePoint.Invoke(null, new[] { v, Activator.CreateInstance(_pointType, 0.0, 0.0), root });
            var r1 = _translatePoint.Invoke(null, new[] { v, Activator.CreateInstance(_pointType, (double)b.w, (double)b.h), root });
            if (r0 == null || r1 == null) return default;
            var pt = r0.GetType();
            double x0 = (double)Prop(pt, "X").GetValue(r0), y0 = (double)Prop(pt, "Y").GetValue(r0);
            double x1 = (double)Prop(pt, "X").GetValue(r1), y1 = (double)Prop(pt, "Y").GetValue(r1);
            return ((float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Abs(x1 - x0), (float)Math.Abs(y1 - y0));
        }
        catch { return default; }
    }

    /// <summary>A RenderTransform translate held on v at Animation priority (above the game's own); dispose to let go.</summary>
    public static IDisposable HoldTranslate(object v, double x, double y)
    {
        Look();
        if (v == null || _setValue == null || _translateType == null || _renderTransformProp == null || _animPriority == null) return null;
        try
        {
            var tr = Activator.CreateInstance(_translateType, x, y);
            return _setValue.Invoke(v, new[] { _renderTransformProp, tr, _animPriority }) as IDisposable;
        }
        catch { return null; }
    }

    /// <summary>Breadth-first through fields and a few view properties, the first object whose type is so named.</summary>
    public static object FindByTypeName(object root, string typeName, int maxDepth, int budget)
    {
        if (root == null) return null;
        // (the walk's set and queue reused, and each type's fields listed once: GetFields copies its array on every call -
        //  for every object of a 400-object walk, twice a second, on the game thread, 2026-10-14)
        var seen = _seen; seen.Clear();
        var q = _queue; q.Clear(); q.Enqueue((root, 0));
        try
        {
            while (q.Count > 0 && budget-- > 0)
            {
                var (o, d) = q.Dequeue();
                if (o == null || o is string || !seen.Add(o)) continue;
                var t = o.GetType();
                if (t.IsPrimitive || t.IsEnum) continue;
                if (t.Name == typeName) return o;
                if (d >= maxDepth) continue;
                foreach (var f in WalkFields(t))
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v != null) q.Enqueue((v, d + 1));
                }
            }
            return null;
        }
        finally { seen.Clear(); q.Clear(); }   // (nothing of the HUD kept alive between walks)
    }
    [ThreadStatic] static HashSet<object> _seenTs; [ThreadStatic] static Queue<(object o, int d)> _queueTs;
    static HashSet<object> _seen => _seenTs ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
    static Queue<(object o, int d)> _queue => _queueTs ??= new Queue<(object o, int d)>();
    static readonly Dictionary<Type, FieldInfo[]> _walkFields = new Dictionary<Type, FieldInfo[]>();
    /// <summary>The fields the walk follows (not primitive, string, enum or pointer), the type's own first, then its bases'.</summary>
    static FieldInfo[] WalkFields(Type t)
    {
        lock (_walkFields)
        {
            if (_walkFields.TryGetValue(t, out var fs)) return fs;
            var l = new List<FieldInfo>();
            for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!(f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum || f.FieldType.IsPointer)) l.Add(f);
            _walkFields[t] = fs = l.ToArray();
            return fs;
        }
    }
}
