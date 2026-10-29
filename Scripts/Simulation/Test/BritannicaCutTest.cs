using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Definitions;

namespace OrbitalMod;

/// <summary>
/// TEST (Britannica undercut, 2026-10-25): attaches the Britannica island cut to Kemik once - a Subtractive composite
/// shape (BritannicaIslandCut.vx2, voxelized by the content pipeline from a mesh of the void under one small hill: its
/// underside, a pit below, a chasm ring), a child of the planet at the cut's centre. The planet's HierarchyComponent saves its children, so later loads find it already there.
/// Does nothing unless the test assets are in the mod (Assets/Procedural/Planets/BritannicaIslandCut: the prefab's definition).
/// Undo: take the assets and this file out; a cut already in a save is removed by Detach (or a fresh world).
/// </summary>
public static class BritannicaCutTest
{
    /// <summary>The cut's server prefab and composition (staged ids: %LOCALAPPDATA%\OrbitalPlanets\Britannica\BritannicaIslandCut_ids.json).</summary>
    static readonly Guid ServerPrefab = new Guid("4a57632e-4061-43e4-8e0b-edd12fcd537c");
    static readonly Guid ServerComposition = new Guid("66cbcdf0-a6e8-4705-ab11-91c64d974a7f");
    /// <summary>The client half (2026-10-26): CompositePlanetShapeComponent is ISkipReplicate - a shape added on the server
    /// never reaches the client's planet, which is the one drawn (the server-only cut attached and the terrain stayed whole).
    /// Vanilla's scatter adds both sides; so does this.</summary>
    static readonly Guid ClientPrefab = new Guid("94637f60-ffe7-4259-b393-0c79d432af3e");
    static readonly Guid ClientComposition = new Guid("72bc06ea-57d9-4261-85ea-b7f9f3d64852");
    /// <summary>The cut's centre in Kemik's own frame (m from its centre; %LOCALAPPDATA%\OrbitalPlanets\Britannica\island_cut_info.json
    /// P_local): the composite's storage box is centred there (CompositePlanetShapeComponent: planet centre + child position).</summary>
    static readonly Vector3 CutCentre = new Vector3(-14633.387f, -59998.352f, 14369.723f);

    /// <summary>(2026-10-26: the first, planet-sized provider cut hung the server in PlanetCompositeDetailProvider.ProcessCheckpoint;
    /// this one is island-sized, under 1 km a side (the voxelizer failed on a 1.7 km box), and runs only in the Britannica test launch - OurWorld.)</summary>
    public static bool Enabled = false;   // (OFF: superseded by the procedural islands, BritannicaIslandsTest - the cut stays as the reference)
    public static string Status = "-";
    static Keen.VRage.Core.Game.Systems.Session _session;
    static bool _done;

    /// <summary>Server thread, every tick (ServerFrames.TickBody): once per session it attaches the cut, or finds it there.</summary>
    public static void Tick(Keen.VRage.Core.Game.Systems.Session session)
    {
        BritannicaIslandsTest.Tick(session, server: true);   // (the procedural islands: Test/BritannicaIslandsTest.cs)
        if (!Enabled || session == null) return;
        if (!ReferenceEquals(session, _session)) { _session = session; _done = false; }
        if (_done) return;
        _done = true;
        try
        {
            if (!OurWorld(out string why)) { Status = "off: " + why; Say(); return; }
            if (!DefinitionManager.Instance.TryGetDefinition(ServerPrefab, out PrefabDefinition prefab) || prefab == null)
            { Status = "off: no BritannicaIslandCut prefab loaded"; return; }
            foreach (var e in session.GetEntitiesOfType<ServerPlanetBeacon>())
            {
                if (!IsKemik(e)) continue;
                var h = e.TryGet<HierarchyComponent>();
                if (h == null) { Status = "Kemik has no HierarchyComponent"; Say(); return; }
                if (h.Children != null)
                    foreach (var ch in h.Children)
                        if (ch.GetComposition().Guid == ServerComposition) { Status = "cut already on Kemik (from the save)"; Say(); return; }
                var ob = prefab.Get();
                ob.GetChildTransform() = new RelativeTransform(CutCentre, Quaternion.Identity);
                var child = h.AddChild(ob);
                Status = child != null ? "cut attached to Kemik" : "AddChild returned null";
                Say();
                // (where it really is: the child's offset from Kemik's centre in WORLD axes - what `cam at ... Kemik` takes -
                //  and Kemik's orientation; 2026-10-26: camera offsets built from the cube convention missed the island)
                if (child != null)
                {
                    var pw = e.Data.GetWorldTransform(); var cw = child.Data.GetWorldTransform();
                    Vector3D off = cw.Position - pw.Position; var q = pw.Orientation;
                    Log.Default.WriteLine($"[BRITANNICA-CUT] cut offset (world axes) {off.X:F0} {off.Y:F0} {off.Z:F0} m, |{off.Length():F0}|; Kemik orientation {q.X:F4} {q.Y:F4} {q.Z:F4} {q.W:F4}");
                }
                return;
            }
            Status = "Kemik not found (no planet named Kemik)"; Say();
        }
        catch (Exception ex) { Status = "attach failed: " + ex.GetType().Name + ": " + ex.Message; Say(); }
    }

    public static string ClientStatus = "-";
    static Keen.VRage.Core.Game.Systems.Session _clientSession;
    static bool _clientDone;

    /// <summary>Client frame (OrbitalClientHost.Frame): once per session, the client's Kemik gets the cut's client prefab at
    /// the same child position as the server's.</summary>
    public static void ClientTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        BritannicaIslandsTest.Tick(session, server: false);
        if (!Enabled || session == null) return;
        if (!ReferenceEquals(session, _clientSession)) { _clientSession = session; _clientDone = false; }
        if (_clientDone) return;
        _clientDone = true;
        try
        {
            if (!OurWorld(out string why)) { ClientStatus = "off: " + why; return; }
            if (!DefinitionManager.Instance.TryGetDefinition(ClientPrefab, out PrefabDefinition prefab) || prefab == null)
            { ClientStatus = "off: no client prefab loaded"; SayClient(); return; }
            foreach (var e in session.GetEntitiesOfType<PlanetFrameComponent>())
            {
                if (!IsKemik(e)) continue;
                var h = e.TryGet<HierarchyComponent>();
                if (h == null) { ClientStatus = "client Kemik has no HierarchyComponent"; SayClient(); return; }
                if (h.Children != null)
                    foreach (var ch in h.Children)
                        if (ch.GetComposition().Guid == ClientComposition) { ClientStatus = "client cut already on Kemik"; SayClient(); return; }
                var ob = prefab.Get();
                ob.GetChildTransform() = new RelativeTransform(CutCentre, Quaternion.Identity);
                ClientStatus = h.AddChild(ob) != null ? "client cut attached to Kemik" : "client AddChild returned null";
                SayClient(); return;
            }
            // (the client's planets may not be in the scene yet on the first frames: try again later)
            _clientDone = false; ClientStatus = "client Kemik not found yet";
        }
        catch (Exception ex) { ClientStatus = "client attach failed: " + ex.GetType().Name + ": " + ex.Message; SayClient(); }
    }

    static void SayClient() => Log.Default.WriteLine("[BRITANNICA-CUT] " + ClientStatus);

    /// <summary>Removes the cut from Kemik (a save made with it keeps it otherwise).</summary>
    public static string Detach(Keen.VRage.Core.Game.Systems.Session session)
    {
        foreach (var e in session.GetEntitiesOfType<ServerPlanetBeacon>())
        {
            if (!IsKemik(e)) continue;
            var h = e.TryGet<HierarchyComponent>();
            if (h?.Children == null) return "no children";
            foreach (var ch in h.Children.ToList())
                if (ch.GetComposition().Guid == ServerComposition) { h.RemoveChild(ch); return "cut removed"; }
            return "no cut on Kemik";
        }
        return "Kemik not found";
    }

    /// <summary>Only in the Britannica test world (2026-10-26: the cut, run in every world loading the Orbital Mod, hung
    /// another session's Mech Legs world): the dev harness on, and the shared game owner file
    /// (%LOCALAPPDATA%\OrbitalModBuild\game.owner, "session|world|epoch", written by tools/harness/game_owner.sh at launch)
    /// naming the britannica session and Orbital Test Campaign 2, written within the last 15 min.</summary>
    internal static bool OurWorld(out string why)
    {
        why = "";
        if (!OrbitalConfig.DevHarness) { why = "no dev harness"; return false; }
        try
        {
            string f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitalModBuild", "game.owner");
            if (!System.IO.File.Exists(f)) { why = "no game.owner"; return false; }
            var p = System.IO.File.ReadAllText(f).Trim().Split('|');
            if (p.Length < 3 || p[0] != "britannica" || !p[1].Contains("Orbital Test Campaign 2")) { why = "not the Britannica test launch (" + string.Join(" / ", p) + ")"; return false; }
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(p[2]);
            if (age > 900) { why = $"game.owner is {age / 60} min old"; return false; }
            return true;
        }
        catch (Exception e) { why = "game.owner unreadable: " + e.Message; return false; }
    }

    /// <summary>Kemik by its entity name, as the planet beacons name planets (2026-10-26: the Orbital Mod moves the planets -
    /// a centre taken from one session's status was 850 km off in the next).</summary>
    internal static bool IsKemik(Keen.VRage.DCS.Components.Entity e) => e.DebugName != null && e.DebugName.Contains("Kemik", StringComparison.OrdinalIgnoreCase);

    static void Say() => Log.Default.WriteLine("[BRITANNICA-CUT] " + Status);
}
