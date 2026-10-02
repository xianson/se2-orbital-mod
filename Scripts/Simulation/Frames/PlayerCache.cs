using Keen.Game2.Simulation.Utils;
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
            _buf.Clear();
            e.E = session.TryFillAliveCharacters(_buf) && _buf.Count > 0 ? _buf[0] : null;
            e.At = now;
            _buf.Clear();
            return e.E;
        }
    }

    static bool Alive(Entity e)
    {
        var d = e.Data;
        var scene = d.Scene;
        return scene != null && scene.IsEntityAlive(d.Entity);
    }
}
