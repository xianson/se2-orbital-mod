using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

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
