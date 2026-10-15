using Keen.VRage.DCS.Components;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The local player's character, per session (server and client have their own), looked up at most twice a second.
/// Every lookup is an engine query over all entities of a type that allocates its own buffers: from the client's
/// frame and the server's tick it was ~0.7 MB/s of garbage. A cached character is checked alive first (no throw:
/// an exception costs ~200 ms in SE2), so a death or respawn is seen at once.
/// </summary>
public static class PlayerCache
{
    sealed class Entry { public Entity E; public long At; }
    static readonly Dictionary<Keen.VRage.Core.Game.Systems.Session, Entry> _by = new Dictionary<Keen.VRage.Core.Game.Systems.Session, Entry>();
    static readonly List<Entity> _buf = new List<Entity>();

    public static Entity Of(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (session == null) return null;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_by)
        {
            if (!_by.TryGetValue(session, out var e)) _by[session] = e = new Entry();
            if (e.E != null && now - e.At < System.Diagnostics.Stopwatch.Frequency / 2 && Alive(e.E)) return e.E;
            // the LOCAL player's own character where there is one (the client: its player controller's possession stack - the
            //  character at the bottom, a seat above it): the first alive character was any player's on a listen server
            e.E = LocalCharacter(session) ?? OwnCharacter(session);
            e.At = now;
            return e.E;
        }
    }

    /// <summary>A new world (WorldReset): the last world's sessions and characters let go (kept by session, every world's
    /// stayed in memory - 2026-10-15).</summary>
    internal static void ResetWorld() { lock (_by) { _by.Clear(); _hasLocalId = false; } }

    // The local player's identity, from the client session (its players component): what its character on the SERVER is owned by.
    static Keen.Game2.Simulation.GameSystems.Ownership.IdentityId _localId;
    static bool _hasLocalId;

    /// <summary>A session without a local player (the server's): the local player's character there - the alive character
    /// owned by the client's local identity. 2026-10-15: it was the first alive character, on a listen host with remote
    /// players any of theirs - the local player's rider velocity, suit bill and Delfos heat applied to someone else. Unknown
    /// (the client not seen yet, or no owner match): the only character if there is just one (single player), else none -
    /// never a guess. Caller holds _by.</summary>
    static Entity OwnCharacter(Keen.VRage.Core.Game.Systems.Session session)
    {
        _buf.Clear();
        Entity found = null;
        if (session.TryFillAliveCharacters(_buf))
        {
            if (_hasLocalId)
                foreach (var c in _buf)
                {
                    var own = c.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipComponent>();
                    if (own != null && own.Owner.Equals(_localId)) { found = c; break; }
                }
            if (found == null && _buf.Count == 1) found = _buf[0];
        }
        _buf.Clear();
        return found;
    }

    static Entity LocalCharacter(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            var players = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>();
            var pc = players?.LocalPlayerController;
            if (pc == null) return null;
            var id = players.LocalPlayerIdentity;
            if (id.IsValid()) { _localId = id; _hasLocalId = true; }
            foreach (var (c, _) in pc.ControlledEntities)
            {
                var ent = c?.Entity;
                if (ent != null && ent.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.CharacterComponent>() != null && Alive(ent)) return ent;
            }
        }
        catch { }
        return null;
    }

    static bool Alive(Entity e)
    {
        var d = e.Data;
        var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }
}
