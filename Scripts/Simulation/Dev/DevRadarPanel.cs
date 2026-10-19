#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections;
using Keen.VRage.Core;

namespace OrbitalMod;

/// <summary>
/// DEV (harness `radarpanel`): a radar's control panel opened the way the game opens a block's terminal
/// (TerminalControllerSessionComponent.OpenTerminal with GridScreenSelection{ControlPanel}, the radar block as the
/// interacted entity), then the radar selected in its block list (ControlPanelViewModel.BlockTerminalSelection, as a click)
/// - the step whose detail models used to kill the game (KeyNotFoundException in TranslateLocalToRemote, see
/// DetailModelPairing) - and its detail models / Transmit Power read back. Added 2026-10-19 (fix-check).
/// The client copy of a `testship` radar: no client block reported the Radar's compositions (2026-10-19 fix-check), so it is
/// the client block nearest where the test radar was spawned (ClientRadar).
/// </summary>
public static class DevRadarPanel
{
    static IDisposable _terminal;
    static object _radarVm;
    public static string Status = "not opened";

    public static string Command(Keen.VRage.Core.Game.Systems.Session session, string what, double value)
    {
        try
        {
            switch (what)
            {
                case "open": Open(session); return "opening: " + Status + "; " + Found;
                case "select": return Select(session);
                case "power":
                {
                    var m = RadarModel(session);
                    if (m == null) return "no RadarBlockDetailModel in the panel";
                    UiThread.Post(() => { try { m.Power = (float)value; } catch (Exception e) { Status = "power: " + e.Message; } });
                    return $"Transmit Power -> {value:P0} (through the panel's model)";
                }
                case "close":
                    try { _terminal?.Dispose(); } catch { }
                    _terminal = null; _radarVm = null;
                    return "closed";
                default: return Report(session);
            }
        }
        catch (Exception e) { return $"radarpanel {what} failed: {e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>The radar block's client copy (the mod's RadarComponent lives on the server copy only): a client block of
    /// the Radar's client composition (...0005; the prefab ...0004, the server's ...0003) if the client reports one, else -
    /// what works (2026-10-19, fix-finish: no client block reported a mod composition) - the client block nearest the
    /// spawned test radar (DevTestShip.RadarAt; client and server share the world's coordinates), within 15 m.</summary>
    static readonly Guid RadarClientPrefab = new Guid("0b170002-5e50-4000-8000-000000000004"), RadarClientComp = new Guid("0b170002-5e50-4000-8000-000000000005"),
                         RadarServer = new Guid("0b170002-5e50-4000-8000-000000000003");
    public static string Found = "-";
    static Entity ClientRadar(Keen.VRage.Core.Game.Systems.Session session)
    {
        int n = 0; var mine = new System.Text.StringBuilder();
        Entity near = null; double nd = 15;
        Vector3D? at = DevTestShip.RadarAt;
        foreach (var e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>())
        {
            n++;
            Guid g = Guid.Empty;
            try { g = e.GetComposition()?.Guid ?? Guid.Empty; } catch { }
            if (g == RadarClientComp || g == RadarClientPrefab || g == RadarServer) { Found = $"by composition {g}"; return e; }
            if (g.ToString().StartsWith("0b17")) mine.Append(' ').Append(g);
            if (at.HasValue)
            {
                double d;
                try { d = (e.Data.GetWorldTransform().Position - at.Value).Length(); } catch { continue; }
                if (d < nd) { nd = d; near = e; }
            }
        }
        if (near != null)
        {
            string comp = "?";
            try { comp = near.GetComposition()?.Guid.ToString() ?? "none"; } catch (Exception ce) { comp = "throws " + ce.GetType().Name; }
            Found = $"nearest client block to the test radar: {nd:F1} m, composition {comp}, of {n} client blocks";
            return near;
        }
        Status = $"no client radar among {n} client blocks (mod compositions:{mine}; test radar at {(at.HasValue ? ServerPlanetBeacon.Fmt(at.Value) : "none - testship first")})";
        return null;
    }

    static async void Open(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var ch = FrameHost.PlayerCharacter(session);
            var term = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.Interaction.TerminalControllerSessionComponent>();
            var radar = ClientRadar(session);
            if (ch == null || term == null || radar == null) { Status = $"cannot open (character {ch != null}, terminal {term != null}, client radar {radar != null}) " + Status; return; }
            var sel = new Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition.GridScreenSelection
                { Panel = Keen.Game2.Client.UI.TerminalScreen.GridScreenMode.ControlPanel };
            Status = "asked";
            _terminal = await term.OpenTerminal(sel, radar, ch);
            Status = _terminal != null ? "open" : "OpenTerminal gave nothing";
        }
        catch (Exception e) { Status = "open failed: " + e.GetType().Name + ": " + e.Message; }
    }

    static object Panel(Keen.VRage.Core.Game.Systems.Session session)
    {
        var top = GameUi.TopScreenObject(session);
        if (top == null || top.GetType().Name != "TerminalScreenViewModel") return null;
        var sub = PlanetRenderBridge.GetMember(top, "SelectedSubScreen");
        var content = sub == null ? null : PlanetRenderBridge.GetMember(sub, "Content");
        return content != null && content.GetType().Name == "ControlPanelViewModel" ? content : null;
    }

    static bool IsRadar(object entityVm)
    {
        var model = PlanetRenderBridge.GetMember(entityVm, "Model");
        if (!(PlanetRenderBridge.GetMember(model, "DetailViewTypes") is IEnumerable types)) return false;
        foreach (var t in types)
            if (PlanetRenderBridge.GetMember(t, "Type") is Type tt && tt == typeof(RadarBlockDetailModel)) return true;
        return false;
    }

    static string Select(Keen.VRage.Core.Game.Systems.Session session)
    {
        var p = Panel(session);
        if (p == null) return "no control panel on top (" + (GameUi.TopScreenObject(session)?.GetType().Name ?? "none") + ")";
        if (!(PlanetRenderBridge.GetMember(p, "Blocks") is IEnumerable blocks)) return "panel has no Blocks";
        int n = 0; object radar = null;
        foreach (var b in blocks) { n++; if (radar == null && IsRadar(b)) radar = b; }
        if (radar == null) return $"{n} blocks listed, none offers RadarBlockDetailModel";
        _radarVm = radar;
        var selection = PlanetRenderBridge.GetMember(p, "BlockTerminalSelection") as IList;
        if (selection == null) return "panel has no BlockTerminalSelection";
        UiThread.Post(() => { try { selection.Clear(); selection.Add(radar); Status = "radar selected"; } catch (Exception e) { Status = "select failed: " + e.GetType().Name + ": " + e.Message; } });
        return $"{n} blocks listed; selecting the radar";
    }

    static RadarBlockDetailModel RadarModel(Keen.VRage.Core.Game.Systems.Session session)
    {
        var p = Panel(session);
        if (p == null || !(PlanetRenderBridge.GetMember(p, "DetailModels") is IEnumerable models)) return null;
        foreach (var m in models) if (m is RadarBlockDetailModel r) return r;
        return null;
    }

    static string Report(Keen.VRage.Core.Game.Systems.Session session)
    {
        var sb = new System.Text.StringBuilder("radarpanel: " + Status + "; found " + Found + "; pairing " + DetailModelPairing.Status);
        var p = Panel(session);
        if (p == null) { sb.Append("; no control panel on top (" + (GameUi.TopScreenObject(session)?.GetType().Name ?? "none") + ")"); return sb.ToString(); }
        if (PlanetRenderBridge.GetMember(p, "DetailModels") is IEnumerable models)
        {
            sb.Append("; detail models:");
            foreach (var m in models) sb.Append(' ').Append(m?.GetType().Name);
        }
        var r = RadarModel(session);
        sb.Append(r != null ? $"; Transmit Power {r.Power:P0}" : "; no radar model");
        var members = r?.GetTypeInfo();
        if (members != null) { sb.Append("; members:"); foreach (var kv in members.MembersDeclarationOrder) sb.Append(' ').Append(kv.Key.Name); }
        return sb.ToString();
    }
}
