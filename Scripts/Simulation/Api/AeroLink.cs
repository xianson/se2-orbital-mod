#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The Aerodynamics Mod's types, by name - ONE rule for every lookup into it. The assembly is the LAST loaded one that has
/// <c>AeroMod.AeroApi</c> (a hot-reloaded aero mod loads after its stale copy, which stays loaded), looked for again
/// whenever the loaded assemblies change (checked at most every 5 s). <see cref="Generation"/> counts the times that
/// assembly changed: a site that binds aero's members keeps the generation it bound at and binds again when it moves.
///
/// (2026-10-15: EntryHost bound the FIRST copy and NoteMoved the last; GridGen, the fly test and DevReentry resolved once
///  for good - after an aero hot reload the entry glow went into the stale copy's sources and looked hooked, and a lookup
///  made before aero loaded stayed empty for the process.)
/// </summary>
internal static class AeroLink
{
    static readonly object _lock = new object();
    static System.Reflection.Assembly _asm;
    static int _asmCount = -1, _gen;
    static long _checkAt;

    /// <summary>Bumped each time the aero assembly changes (found, reloaded, gone). Cheap: a timestamp compare between checks.</summary>
    public static int Generation { get { Refresh(); return System.Threading.Volatile.Read(ref _gen); } }

    /// <summary>A type of the aero mod by full name ("AeroMod.AeroApi"), from the current aero assembly; null: not loaded.</summary>
    public static Type Find(string fullName)
    {
        Refresh();
        var a = System.Threading.Volatile.Read(ref _asm);
        if (a == null) return null;
        try { return a.GetType(fullName); } catch { return null; }
    }

    static void Refresh()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < System.Threading.Volatile.Read(ref _checkAt)) return;
        lock (_lock)
        {
            if (now < _checkAt) return;
            _checkAt = now + 5 * System.Diagnostics.Stopwatch.Frequency;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            if (asms.Length == _asmCount) return;
            _asmCount = asms.Length;
            System.Reflection.Assembly found = null;
            foreach (var a in asms)
            {
                Type t;
                try { t = a.GetType("AeroMod.AeroApi"); } catch { continue; }
                if (t != null) found = a;   // (the last one: the newest load)
            }
            if (ReferenceEquals(found, _asm)) return;
            System.Threading.Volatile.Write(ref _asm, found);
            System.Threading.Interlocked.Increment(ref _gen);
        }
    }
}
