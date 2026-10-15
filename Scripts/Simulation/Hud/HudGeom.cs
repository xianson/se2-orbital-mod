using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

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
