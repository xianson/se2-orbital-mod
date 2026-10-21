#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections;
using System.Reflection;
using Keen.VRage.Core;
using Keen.Game2.Simulation.WorldObjects.Movement;

namespace OrbitalMod;

/// <summary>
/// DEV (harness, 2026-10-19 in-game check of the aero mod's Autopilot block and flight assists). Everything by reflection on
/// the aero mod's types (this mod never references it):
///  - `appanel open|select [n]|press METHOD|set MEMBER VALUE|report|close`: the Autopilot block's REAL control panel (an enum VALUE by name) - opened
///    as the game opens a block's terminal (TerminalControllerSessionComponent.OpenTerminal, ControlPanel), the n-th block
///    offering AeroAutopilotBlockDetailModel selected (as a click), its buttons pressed (the [Button] method the panel's
///    button calls: HoverHold, HeightHold, AutopilotOff, ToggleHoverHold, ToggleHeightHold) and its fields set (HeightM ...)
///    through the detail model on the UI thread; report: every detail model's members and the autopilot's Status / HeightM.
///  - `appanel seat` / `appanel actions` / `appanel actionsclose` (2026-10-20): the TOOLBAR's action list for the Autopilot
///    block, as a player assigns one - the seat (the block with a toolbar, ToolbarBlockDetailModel) selected in the control
///    panel, then the panel's toolbar editor (ControlPanelViewModel.Toolbar) opened on its first slot for the Autopilot block
///    (ControlPanelToolbarViewModel.EditTile: the game's BlockActionDetailsScreen); `actions` again lists its action names
///    (ActionItemViewModel.Name); `actionsclose` cancels it (CancelCommand - no slot is changed).
///  - `apkeys MX MY MZ SECONDS [PITCH YAW ROLL deg/s]`: a seated pilot's input on the seat grid's client copy, every frame for
///    SECONDS: ControlData.Movement (the movement keys) and, with a rate, ControlData.Rotation plus the reticle
///    (TargetControlData) turned at that rate about the grid's own axes - what a mouse / the roll keys write.
///  - `apstate ID`: AeroApi.TryGetAutopilot and the flight state (height over the ground, speeds, vertical / horizontal
///    velocity, load factor, stall) of a server grid.
///  - `apblock on|off [n]`: the n-th (or every) server Autopilot block switched on / off (PowerableBlockComponent.Enabled,
///    the game's own on/off property).
///  - `aerosetting NAME [VALUE]`: AeroApi.TryGetSetting / SetSetting (what the settings screen calls).
///  - `vmfind TEXT [maxNodes]`: paths in the top screen's view model whose text contains TEXT (the build menu's catalogue).
/// </summary>
public static class DevAutopilot
{
    static IDisposable _terminal;
    public static string Status = "-";
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public;
    static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

    // ── appanel ──
    public static string Panel(Keen.VRage.Core.Game.Systems.Session session, string[] a)
    {
        string what = a.Length > 1 ? a[1] : "report";
        try
        {
            switch (what)
            {
                case "open": Open(session); return "opening: " + Status;
                case "select": return Select(session, a.Length > 2 ? int.Parse(a[2], CI) : 0);
                case "seat": return SelectOffering(session, "ToolbarBlockDetailModel");
                case "actions": return Actions(session);
                case "actionsclose":
                {
                    var top = GameUi.TopScreenObject(session);
                    if (top == null || top.GetType().Name != "BlockActionDetailsScreenViewModel") return "no action list on top (" + (top?.GetType().Name ?? "none") + ")";
                    var cmd = PlanetRenderBridge.GetMember(top, "CancelCommand");   // (an ICommand: its Execute by reflection)
                    var exec = cmd?.GetType().GetMethod("Execute", new[] { typeof(object) });
                    if (exec == null) return "no CancelCommand";
                    UiThread.Post(() => { try { exec.Invoke(cmd, new object[] { null }); Status = "action list closed"; } catch (Exception e) { Status = "close: " + (e.InnerException ?? e).Message; } });
                    return "closing the action list";
                }
                case "press":
                {
                    var m = ApModel(session); if (m == null) return "no AeroAutopilotBlockDetailModel in the panel (open, select first)";
                    var mi = m.GetType().GetMethod(a[2], Inst, null, Type.EmptyTypes, null); if (mi == null) return "no method " + a[2];
                    UiThread.Post(() => { try { mi.Invoke(m, null); Status = "pressed " + a[2]; } catch (Exception e) { Status = "press: " + (e.InnerException ?? e).Message; } });
                    return "pressing " + a[2];
                }
                case "set":
                {
                    object m = a.Length > 4 ? Model(session, a[4]) : ApModel(session);
                    if (m == null) return "no such detail model in the panel";
                    var pi = m.GetType().GetProperty(a[2], Inst); if (pi == null || !pi.CanWrite) return "no writable " + a[2] + " on " + m.GetType().Name;
                    object v = pi.PropertyType == typeof(bool) ? (object)(a[3] == "1" || a[3].Equals("true", StringComparison.OrdinalIgnoreCase) || a[3] == "on")
                        : pi.PropertyType == typeof(float) ? float.Parse(a[3], CI) : pi.PropertyType == typeof(double) ? double.Parse(a[3], CI)
                        : pi.PropertyType == typeof(int) ? (object)int.Parse(a[3], CI) : pi.PropertyType.IsEnum ? Enum.Parse(pi.PropertyType, a[3], true) : a[3];
                    UiThread.Post(() => { try { pi.SetValue(m, v); Status = $"set {m.GetType().Name}.{a[2]} = {v}"; } catch (Exception e) { Status = "set: " + (e.InnerException ?? e).Message; } });
                    return $"setting {m.GetType().Name}.{a[2]} = {v}";
                }
                case "close":
                    try { _terminal?.Dispose(); } catch { }
                    _terminal = null; return "closed";
                default: return Report(session);
            }
        }
        catch (Exception e) { return $"appanel {what} failed: {e.GetType().Name}: {e.Message}"; }
    }

    static async void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            // (the interacted entity: the seat when seated, else the client block nearest the character)
            Entity block = FrameHost.Seated ? FrameHost.SeatBlock : null;
            if (block == null && ch != null)
            {
                double bd = 40; Vector3D me = ch.Data.GetWorldTransform().Position;
                foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>())
                {
                    double d; try { d = (e.Data.GetWorldTransform().Position - me).Length(); } catch { continue; }
                    if (d < bd) { bd = d; block = e; }
                }
            }
            if (ch == null || term == null || block == null) { Status = $"cannot open (character {ch != null}, terminal {term != null}, block {block != null})"; return; }
            var sel = new Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition.GridScreenSelection
                { Panel = Keen.Game2.Client.UI.TerminalScreen.GridScreenMode.ControlPanel };
            Status = "asked";
            _terminal = await term.OpenTerminal(sel, block, ch);
            Status = _terminal != null ? "open" : "OpenTerminal gave nothing";
        }
        catch (Exception e) { Status = "open failed: " + e.GetType().Name + ": " + e.Message; }
    }

    static object ControlPanel(Keen.VRage.Core.Game.Systems.Session session)
    {
        var top = GameUi.TopScreenObject(session);
        if (top == null || top.GetType().Name != "TerminalScreenViewModel") return null;
        var sub = PlanetRenderBridge.GetMember(top, "SelectedSubScreen");
        var content = sub == null ? null : PlanetRenderBridge.GetMember(sub, "Content");
        return content != null && content.GetType().Name == "ControlPanelViewModel" ? content : null;
    }

    static bool Offers(object entityVm, string modelType, bool orDerived = false)
    {
        var model = PlanetRenderBridge.GetMember(entityVm, "Model");
        if (!(PlanetRenderBridge.GetMember(model, "DetailViewTypes") is IEnumerable types)) return false;
        foreach (var t in types)
            if (PlanetRenderBridge.GetMember(t, "Type") is Type tt)
                for (var b = tt; b != null; b = orDerived ? b.BaseType : null) if (b.Name == modelType) return true;
        return false;
    }

    static string Name(object vm)
    {
        foreach (var n in new[] { "Name", "DisplayName", "CustomName", "Title" })
            if (PlanetRenderBridge.GetMember(vm, n) is object o && !(o is IEnumerable && !(o is string))) return o.ToString();
        var model = PlanetRenderBridge.GetMember(vm, "Model");
        foreach (var n in new[] { "Name", "DisplayName", "CustomName" })
            if (PlanetRenderBridge.GetMember(model, n) is object o) return o.ToString();
        return vm.GetType().Name;
    }

    static string Select(Keen.VRage.Core.Game.Systems.Session session, int which)
    {
        var p = ControlPanel(session);
        if (p == null) return "no control panel on top (" + (GameUi.TopScreenObject(session)?.GetType().Name ?? "none") + ")";
        var blocksOf = PlanetRenderBridge.GetMember(p, "Blocks") ?? PlanetRenderBridge.GetMember(PlanetRenderBridge.GetMember(p, "Tree"), "Blocks");
        if (!(blocksOf is IEnumerable blocks)) return "panel has no Blocks (nor Tree.Blocks)";
        int n = 0, k = 0; object pick = null; var names = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            n++;
            if (!Offers(b, "AeroAutopilotBlockDetailModel")) continue;
            names.Append(" [").Append(k).Append("] ").Append(Name(b));
            if (k++ == which) pick = b;
        }
        if (pick == null) return $"{n} blocks listed, {k} autopilot(s):{names}; no #{which}";
        var selection = PlanetRenderBridge.GetMember(p, "BlockTerminalSelection") as IList;
        if (selection == null) return "panel has no BlockTerminalSelection";
        UiThread.Post(() => { try { selection.Clear(); selection.Add(pick); Status = $"autopilot #{which} selected"; } catch (Exception e) { Status = "select failed: " + e.GetType().Name + ": " + e.Message; } });
        return $"{n} blocks listed, {k} autopilot(s):{names}; selecting #{which}";
    }

    /// <summary>The first block in the control panel offering a detail model of that type, selected (as a click).</summary>
    static string SelectOffering(Keen.VRage.Core.Game.Systems.Session session, string modelType)
    {
        var p = ControlPanel(session);
        if (p == null) return "no control panel on top (" + (GameUi.TopScreenObject(session)?.GetType().Name ?? "none") + ")";
        var blocksOf = PlanetRenderBridge.GetMember(p, "Blocks") ?? PlanetRenderBridge.GetMember(PlanetRenderBridge.GetMember(p, "Tree"), "Blocks");
        if (!(blocksOf is IEnumerable blocks)) return "panel has no Blocks";
        object pick = null;
        // (the server's model is a subclass - ToolbarBlockDetailModelServer: its base type matched too; else a seat / cockpit by name)
        foreach (var b in blocks) if (Offers(b, modelType, true)) { pick = b; break; }
        if (pick == null && modelType.StartsWith("Toolbar"))
            foreach (var b in blocks) { string n = Name(b); if (n.Contains("Seat") || n.Contains("Cockpit")) { pick = b; break; } }
        if (pick == null) return "no block offering " + modelType;
        var selection = PlanetRenderBridge.GetMember(p, "BlockTerminalSelection") as IList;
        if (selection == null) return "panel has no BlockTerminalSelection";
        UiThread.Post(() => { try { selection.Clear(); selection.Add(pick); Status = $"{Name(pick)} selected"; } catch (Exception e) { Status = "select failed: " + e.Message; } });
        return "selecting " + Name(pick);
    }

    /// <summary>appanel actions: the action list open - its names; else the toolbar editor opened on slot 1 for the Autopilot block
    /// (the seat selected first: `appanel seat`).</summary>
    static string Actions(Keen.VRage.Core.Game.Systems.Session session)
    {
        var top = GameUi.TopScreenObject(session);
        if (top != null && top.GetType().Name == "BlockActionDetailsScreenViewModel")
        {
            if (!(PlanetRenderBridge.GetMember(top, "Actions") is IEnumerable acts)) return "action list: no Actions";
            var names = new System.Collections.Generic.List<string>();
            foreach (var x in acts) names.Add(PlanetRenderBridge.GetMember(x, "Name")?.ToString() ?? "(null)");
            int blank = names.FindAll(n => string.IsNullOrWhiteSpace(n) || n == "(null)").Count;
            return $"action list: {names.Count} actions, {blank} blank: " + string.Join(" | ", names);
        }
        var p = ControlPanel(session);
        if (p == null) return "no control panel on top (" + (top?.GetType().Name ?? "none") + ")";
        var tb = PlanetRenderBridge.GetMember(p, "Toolbar");
        var tvm = tb == null ? null : PlanetRenderBridge.GetMember(tb, "ToolbarViewModel");
        if (tvm == null) return "no toolbar in the panel (select the seat first: appanel seat)";
        if (!(PlanetRenderBridge.GetMember(tvm, "ToolbarTileViewModels") is IList tiles) || tiles.Count == 0) return "the toolbar has no slots";
        object ap = null;
        if (PlanetRenderBridge.GetMember(p, "Blocks") is IEnumerable blocks) foreach (var b in blocks) if (Offers(b, "AeroAutopilotBlockDetailModel")) { ap = b; break; }
        if (ap == null) return "no Autopilot block in the panel";
        System.Reflection.MethodInfo edit = null;
        foreach (var m in tb.GetType().GetMethods(Inst))
        {
            var ps = m.GetParameters();
            if (m.Name == "EditTile" && ps.Length == 2 && ps[0].ParameterType.IsInstanceOfType(ap) && ps[1].ParameterType.IsInstanceOfType(tiles[0])) { edit = m; break; }
        }
        if (edit == null) return "no EditTile(block, tile) on " + tb.GetType().Name;
        var tile = tiles[0];
        UiThread.Post(() => { try { edit.Invoke(tb, new[] { ap, tile }); Status = "action list asked"; } catch (Exception e) { Status = "edit: " + (e.InnerException ?? e).Message; } });
        return $"opening the action list of {Name(ap)} on slot 1";
    }

    static object Model(Keen.VRage.Core.Game.Systems.Session session, string typeName)
    {
        var p = ControlPanel(session);
        if (p == null || !(PlanetRenderBridge.GetMember(p, "DetailModels") is IEnumerable models)) return null;
        foreach (var m in models) if (m != null && m.GetType().Name == typeName) return m;
        return null;
    }
    static object ApModel(Keen.VRage.Core.Game.Systems.Session session) => Model(session, "AeroAutopilotBlockDetailModel");

    static string Report(Keen.VRage.Core.Game.Systems.Session session)
    {
        var sb = new System.Text.StringBuilder("appanel: " + Status);
        var p = ControlPanel(session);
        if (p == null) { sb.Append("; no control panel on top (" + (GameUi.TopScreenObject(session)?.GetType().Name ?? "none") + ")"); return sb.ToString(); }
        if (PlanetRenderBridge.GetMember(p, "DetailModels") is IEnumerable models)
            foreach (var m in models)
            {
                if (m == null) continue;
                sb.Append("; ").Append(m.GetType().Name).Append(':');
                foreach (var pi in m.GetType().GetProperties(Inst))
                {
                    if (pi.GetIndexParameters().Length > 0) continue;
                    var t = pi.PropertyType;
                    if (!(t.IsPrimitive || t == typeof(string) || t.IsEnum)) continue;
                    object v; try { v = pi.GetValue(m); } catch { v = "?"; }
                    sb.Append(' ').Append(pi.Name).Append('=').Append(v is float f ? f.ToString("0.##", CI) : v);
                }
            }
        return sb.ToString();
    }

    // ── apkeys: the pilot's input on the seat grid's client copy ──
    static Vector3 _move, _rateDeg; static double _until; static bool _keysOn;
    public static string Keys(string[] a)
    {
        if (!FrameHost.Seated || FrameHost.SeatGrid == null) return "not seated";
        _move = new Vector3(F(a[1]), F(a[2]), F(a[3]));
        _rateDeg = a.Length > 7 ? new Vector3(F(a[5]), F(a[6]), F(a[7])) : Vector3.Zero;
        _until = Now() + double.Parse(a[4], CI); _keysOn = true;
        return $"pilot keys {_move} turn {_rateDeg} deg/s for {a[4]} s";
    }
    static float F(string s) => float.Parse(s, CI);
    static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    static double _last;

    public static void ClientTick()
    {
        if (!_keysOn) return;
        var g = FrameHost.SeatGrid;
        double now = Now(), dt = _last > 0 ? Math.Min(0.1, now - _last) : 1 / 60.0; _last = now;
        bool on = now < _until && FrameHost.Seated && g != null;
        if (g == null) { _keysOn = false; return; }
        try
        {
            g.Data.TryGet<ControlData>(out var cd);
            cd.Movement = on ? _move : Vector3.Zero;
            cd.Rotation = on ? _rateDeg * 0.01f : Vector3.Zero;
            g.Data.Set(cd);
            if (on && _rateDeg.LengthSquared() > 0 && g.Data.TryGet<TargetControlData>(out var tc) && tc.TargetOrientation.IsValidAndRotationIsNormalized())
            {
                Vector3 r = _rateDeg * (float)(dt * Math.PI / 180);
                float ang = r.Length();
                tc.TargetOrientation = Quaternion.Normalize(tc.TargetOrientation * Quaternion.CreateFromAxisAngle(r / ang, ang));
                g.Data.Set(tc);
            }
            Status = on ? "keys on" : "keys released";
        }
        catch (Exception e) { Status = "keys: " + e.Message; }
        if (!on) { _keysOn = false; _last = 0; }
    }


    /// <summary>apdiag: the seat grid's client copy as the aero mod's autopilot reads the pilot (ControlData, the reticle, its
    /// record's Keys / Seated / Steer and their age).</summary>
    public static string Diag()
    {
        var g = FrameHost.SeatGrid;
        if (g == null) return "not seated";
        var sb = new System.Text.StringBuilder();
        if (g.Data.TryGet<ControlData>(out var cd)) sb.Append($"ControlData move {cd.Movement} rot {cd.Rotation}"); else sb.Append("no ControlData");
        sb.Append(g.Data.Has<TargetControlData>() ? " | reticle yes" : " | reticle NO");
        var t = AeroLink.Find("AeroMod.AeroGridComponent");
        if (t == null) return sb.Append(" | no aero").ToString();
        object comp = null;
        foreach (var m in typeof(Entity).GetMethods(Inst))
            if (m.Name == "TryGet" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
            { try { comp = m.MakeGenericMethod(t).Invoke(g, new[] { Activator.CreateInstance(m.GetParameters()[0].ParameterType) }); } catch { } break; }
        if (comp == null) return sb.Append(" | no aero component on the client copy").ToString();
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object F(string n) { try { var v = t.GetField(n, any)?.GetValue(comp); return v is ICollection c ? (object)("n=" + c.Count) : v ?? "null"; } catch (Exception e) { return e.GetType().Name; } }
        sb.Append($" | client aero: quiet {F("_quietFrames")} thrusters {F("_thrusterCache")} gyros {F("_gyroCache")} ApFlying {F("ApFlying")} init {F("_initialized")} readout '{F("ApReadoutClient")}' protText '{F("PilotProtText")}'");
        var rec = t.GetField("_apRec", any)?.GetValue(comp);
        var ap = rec?.GetType().GetField("Ap", any)?.GetValue(rec);
        if (ap == null)
        {
            // (why not: every published record with an autopilot against this client copy - FindRecord's tests)
            sb.Append(" | no autopilot record matched");
            var tt = AeroLink.Find("AeroMod.ThrustTorque");
            var dict = tt?.GetField("_published", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
            var gwt = g.Data.GetWorldTransform();
            bool cBox = g.Data.TryGet<Keen.VRage.Core.Game.Data.BoundingBoxData>(out var cb);
            sb.Append($" | client pos {gwt.Position} box {(cBox ? cb.BoundingBox.Min + ".." + cb.BoundingBox.Max : "none")}");
            if (dict != null)
            {
                object[] vals; lock (dict) { vals = new object[dict.Count]; dict.Values.CopyTo(vals, 0); }
                int n = 0;
                foreach (var rv in vals)
                {
                    var rt = rv.GetType();
                    if (rt.GetField("Ap", any)?.GetValue(rv) == null) continue;
                    n++;
                    var pos = (Vector3D)rt.GetField("Pos").GetValue(rv); var q = (Quaternion)rt.GetField("Orient").GetValue(rv);
                    long st = (long)rt.GetField("Stamp").GetValue(rv);
                    double ag = (System.Diagnostics.Stopwatch.GetTimestamp() - st) / (double)System.Diagnostics.Stopwatch.Frequency;
                    float dot = Math.Abs(Quaternion.Dot(q, gwt.Orientation));
                    sb.Append($" | rec{n}: age {ag:F2}s dist {(pos - gwt.Position).Length():F1} m angle {2 * Math.Acos(Math.Min(1.0, dot)) * 180 / Math.PI:F1} deg box {rt.GetField("BoxMin").GetValue(rv)}..{rt.GetField("BoxMax").GetValue(rv)} hasBox {rt.GetField("HasBox").GetValue(rv)} gone {rt.GetField("Gone").GetValue(rv)}");
                }
                sb.Append($" | {n} record(s) with an autopilot of {vals.Length}");
            }
            return sb.ToString();
        }
        var at = ap.GetType();
        long stamp = (long)at.GetField("PilotStamp").GetValue(ap);
        double age = (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        sb.Append($" | record Seated {at.GetField("Seated").GetValue(ap)} Keys {at.GetField("Keys").GetValue(ap)} Steer {at.GetField("Steer").GetValue(ap)} age {age:F2}s Flying {at.GetField("Flying").GetValue(ap)}");
        return sb.ToString();
    }


    /// <summary>apreticle: the seat grid's reticle (TargetControlData) set to where the cockpit points now - after a teleport that
    /// turned the ship (flytest), the reticle still holds the old attitude and the ship flies back to it.</summary>
    public static string Reticle()
    {
        var g = FrameHost.SeatGrid; var seat = FrameHost.SeatBlock;
        if (g == null || seat == null) return "not seated";
        if (!g.Data.TryGet<TargetControlData>(out var tc)) return "no reticle (TargetControlData) on the seat grid";
        var q = seat.Data.GetWorldTransform().Orientation;
        var d = Quaternion.Normalize(Quaternion.Inverse(tc.TargetOrientation) * q);
        double ang = 2 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W))) * 180 / Math.PI;
        tc.TargetOrientation = q; g.Data.Set(tc);
        return $"reticle set to the cockpit's attitude (it was {ang:F1} deg off)";
    }

    // ── apstate / apblock / aerosetting ──
    static Type Api => AeroLink.Find("AeroMod.AeroApi");
    static readonly string[] Want = { "GroundHeight", "Airspeed", "GroundSpeed", "LoadFactor", "StallState", "StallMarginDeg", "StallGAvailable", "AngularVelocityX", "AngularVelocityY", "AngularVelocityZ", "Lift" };

    public static string State(long id)
    {
        var gi = GridMembers.Get(id);
        if (gi?.Entity == null) return "no grid " + id;
        var api = Api; if (api == null) return "no aero mod";
        var sb = new System.Text.StringBuilder($"'{gi.DisplayName}'");
        var tg = api.GetMethod("TryGetAutopilot");
        if (tg != null)
        {
            var args = new object[] { gi.Entity, 0, 0, 0, "" };
            bool ok = (bool)tg.Invoke(null, args);
            sb.Append(ok ? $" ap goals {args[1]} prot {args[2]} reason {args[3]} readout '{args[4]}'" : " ap none");
        }
        var names = (string[])api.GetMethod("GetFlightStateNames")?.Invoke(null, null);
        var fs = new float[names?.Length ?? 0];
        int n = names == null ? 0 : (int)api.GetMethod("GetFlightState").Invoke(null, new object[] { gi.Entity, fs });
        if (n > 0)
            foreach (var w in Want) { int i = Array.IndexOf(names, w); if (i >= 0 && i < n) sb.Append(' ').Append(w).Append(' ').Append(fs[i].ToString("0.###", CI)); }
        else sb.Append(" (flight state: none yet)");
        Vector3D v = GridMembers.Velocity(gi);
        int ui = names == null ? -1 : Array.IndexOf(names, "UpX");
        if (n > 0 && ui >= 0)
        {
            var up = new Vector3D(fs[ui], fs[ui + 1], fs[ui + 2]);
            if (up.LengthSquared() > 1e-6)
            {
                up = Vector3D.Normalize(up); double vz = Vector3D.Dot(v, up); sb.Append($" vUp {vz:F2} vHoriz {(v - up * vz).Length():F2}");
                // (the height along the planet's up: the distance from the nearest planet's centre)
                Vector3D gp = GridMembers.Position(gi); double best = double.MaxValue;
                foreach (var pb in PlanetBeacons.All()) { double d = (pb.Center - gp).Length(); if (d < best) best = d; }
                if (best < double.MaxValue) sb.Append($" r {best:F1}");
            }
        }
        sb.Append($" |v| {v.Length():F2}");
        try { var we = gi.Entity.Data.Get<Keen.VRage.Physics.Data.RigidBodyData>(); sb.Append($" w {((Vector3D)we.AngularVelocity).Length():F3}"); } catch { }
        return sb.ToString();
    }

    public static string Block(string onOff, int which)
    {
        var t = AeroLink.Find("AeroMod.AeroAutopilotComponent"); if (t == null) return "no AeroAutopilotComponent";
        var all = t.GetField("All", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as IList;
        if (all == null) return "no AeroAutopilotComponent.All";
        bool on = onOff == "on";
        var sb = new System.Text.StringBuilder();
        object[] items; lock (all) { items = new object[all.Count]; all.CopyTo(items, 0); }
        for (int i = 0; i < items.Length; i++)
        {
            if (which >= 0 && i != which) continue;
            var e = t.GetProperty("Entity", Inst)?.GetValue(items[i]) as Entity;
            var pb = e?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockComponent>();
            if (pb == null) { sb.Append($"#{i} no power component; "); continue; }
            pb.Enabled = on;
            sb.Append($"#{i} Enabled={pb.Enabled}; ");
        }
        return items.Length + " autopilot block(s): " + sb;
    }

    public static string Setting(string[] a)
    {
        var api = Api; if (api == null) return "no aero mod";
        if (a.Length > 2)
        {
            var args = new object[] { a[1], double.Parse(a[2], CI), null };
            bool ok = (bool)api.GetMethod("SetSetting").Invoke(null, args);
            if (!ok) return $"{a[1]}: refused ({args[2]})";
        }
        var g = new object[] { a[1], 0.0 };
        return (bool)api.GetMethod("TryGetSetting").Invoke(null, g) ? $"{a[1]} = {((double)g[1]).ToString("0.###", CI)}" : "no setting " + a[1];
    }

    // ── vmfind: text in the top screen's view model ──
    public static string Find(Keen.VRage.Core.Game.Systems.Session session, string text, int max)
    {
        var top = GameUi.TopScreenObject(session);
        if (top == null) return "no top screen";
        var seen = new System.Collections.Generic.HashSet<object>(ReferenceComparer.Instance);
        var q = new System.Collections.Generic.Queue<(object o, string path, int depth)>();
        q.Enqueue((top, top.GetType().Name, 0));
        var hits = new System.Text.StringBuilder(); int nodes = 0, found = 0;
        while (q.Count > 0 && nodes < max && found < 40)
        {
            var (o, path, depth) = q.Dequeue();
            if (o == null || !seen.Add(o)) continue;
            nodes++;
            var t = o.GetType();
            if (o is IEnumerable en && !(o is string))
            {
                int i = 0;
                try { foreach (var x in en) { if (i >= 400) break; Visit(x, $"{path}[{i++}]", depth + 1); } } catch { }
                continue;
            }
            if (depth > 14) continue;
            foreach (var pi in t.GetProperties(Inst))
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                var pt = pi.PropertyType;
                if (pt.IsPrimitive || pt.IsEnum || pt == typeof(Type) || pt.Namespace?.StartsWith("Avalonia") == true) continue;
                object v; try { v = pi.GetValue(o); } catch { continue; }
                Visit(v, path + "." + pi.Name, depth + 1);
            }
        }
        void Visit(object v, string p, int d)
        {
            if (v == null) return;
            if (v is string s) { if (s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { found++; hits.Append(" | ").Append(p).Append("='").Append(s.Length > 60 ? s.Substring(0, 60) : s).Append('\''); } return; }
            var vt = v.GetType();
            if (vt.IsPrimitive || vt.IsEnum || v is Delegate || v is Type || v is System.Reflection.MemberInfo) return;
            if (vt.IsValueType && vt.Namespace != null && !vt.Namespace.StartsWith("Keen")) return;
            q.Enqueue((v, p, d));
        }
        return $"vmfind '{text}': {found} hit(s) in {nodes} nodes{hits}";
    }

    sealed class ReferenceComparer : System.Collections.Generic.IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }
}
