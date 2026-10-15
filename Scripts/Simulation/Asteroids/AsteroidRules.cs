#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>What AsteroidBridge read off one of the generator's bodies (engine-free: Tests/AsteroidTests).</summary>
internal struct RockFacts
{
    /// <summary>Its entity is alive (nothing else was read if not).</summary>
    public bool Alive;
    /// <summary>It has the generator's ProceduralEntityComponentBase, and that is the voxel one (ProceduralVoxelEntityComponent,
    /// an IEditableProceduralEntity): a procedural asteroid, nothing else the generator may track.</summary>
    public bool Procedural, Voxel;
    /// <summary>MarkedAsManual / IsManual: promoted (a player edited it, or it is saved with the world).</summary>
    public bool Manual;
    /// <summary>Its component's ID is the generator's key for it; the key is in the generator's skipped (excluded) ids.</summary>
    public bool IdMatches, Skipped;
    /// <summary>Its world box was read; it lies in one of our asteroid frames' volumes (AsteroidFrames.Protects).</summary>
    public bool HasBox, Ours;
    /// <summary>A grid's box within GridMargin of its box; a character within PlayerMargin of its box.</summary>
    public bool GridNear, PlayerNear;
}

internal enum RockVerdict { Delete, Off, NotServer, NoScan, Dead, NotProcedural, NotVoxel, Manual, Skipped, IdMismatch, NoBox, Ours, GridNear, PlayerNear }

/// <summary>
/// THE BRIDGE'S DELETE RULE (2026-10-15): a generator body is deleted only when every fact proves it is an untouched
/// procedural asteroid nobody is at - the switch on, the server's generator, our frames' volumes known, alive, the voxel procedural component, not
/// manual, not excluded, its id the generator's, its box known, not ours, no grid and no player near. Anything unknown
/// keeps it. (The engine's own DeleteEntity also only forgets a manual or excluded body, never deletes it - a second
/// line, not the rule.)
/// </summary>
internal static class AsteroidRules
{
    /// <summary>A grid this close to the body's box (m) keeps it: landed, locked, built on, or parked by it.</summary>
    public const double GridMargin = 100;
    /// <summary>A character this close to the body's box (m) keeps it: someone at it (mining, walking on it, arriving).</summary>
    public const double PlayerMargin = 300;

    /// <summary>enabled: the switch. server: the server's generator (a client's must not delete). framesKnown: the asteroid
    /// frames' volumes were found this pass (AsteroidFrames.ScanOk; else Ours cannot be trusted).</summary>
    public static RockVerdict Classify(bool enabled, bool server, bool framesKnown, in RockFacts f)
    {
        if (!enabled) return RockVerdict.Off;
        if (!server) return RockVerdict.NotServer;
        if (!framesKnown) return RockVerdict.NoScan;
        if (!f.Alive) return RockVerdict.Dead;
        if (!f.Procedural) return RockVerdict.NotProcedural;
        if (!f.Voxel) return RockVerdict.NotVoxel;
        if (f.Manual) return RockVerdict.Manual;
        if (f.Skipped) return RockVerdict.Skipped;
        if (!f.IdMatches) return RockVerdict.IdMismatch;
        if (!f.HasBox) return RockVerdict.NoBox;
        if (f.Ours) return RockVerdict.Ours;
        if (f.GridNear) return RockVerdict.GridNear;
        if (f.PlayerNear) return RockVerdict.PlayerNear;
        return RockVerdict.Delete;
    }

    /// <summary>A kept body that still counts as placed on purpose (an anchor that persists). Before 2026-10-15 every
    /// body was (the manual lookup never found its type): all but the deleted and the dead still are.</summary>
    public static bool Persists(RockVerdict v) => v != RockVerdict.Delete && v != RockVerdict.Dead;

    /// <summary>A keep worth a log line (once per body): one that is not the normal case (manual, ours, the switch off).</summary>
    public static bool LogKeep(RockVerdict v) => v >= RockVerdict.NotProcedural && v != RockVerdict.Manual && v != RockVerdict.Ours;

    /// <summary>The Status line's tail for one pass (counts indexed by verdict).</summary>
    public static string PassLine(bool enabled, bool server, bool framesKnown, int[] n)
    {
        if (!enabled) return "; deleting generated asteroids is OFF";
        int held = n[(int)RockVerdict.GridNear] + n[(int)RockVerdict.PlayerNear], unsure = 0;
        for (int k = (int)RockVerdict.NotProcedural; k <= (int)RockVerdict.NoBox; k++) if (k != (int)RockVerdict.Manual) unsure += n[k];
        return $"; last pass: {n[(int)RockVerdict.Delete]} deleted, {n[(int)RockVerdict.Manual]} manual, {n[(int)RockVerdict.Ours]} frames', {held} held (grid/player near), {unsure} not provably procedural"
               + (server ? "" : ", not the server's generator: none deleted") + (framesKnown ? "" : ", asteroid frames not scanned: none deleted");
    }

    /// <summary>The verdict's reason, for the log.</summary>
    public static string Why(RockVerdict v) => v switch
    {
        RockVerdict.Delete => $"untouched procedural asteroid (not manual, not excluded, not an asteroid frame's; no grid within {GridMargin:F0} m, no player within {PlayerMargin:F0} m)",
        RockVerdict.Off => "kept: World.DeleteGeneratedAsteroids is off",
        RockVerdict.NotServer => "kept: not the server's generator",
        RockVerdict.NoScan => "kept: the asteroid frames' volumes were not found this pass",
        RockVerdict.Dead => "skipped: not alive",
        RockVerdict.NotProcedural => "kept: no procedural component (not provably generated)",
        RockVerdict.NotVoxel => "kept: not a procedural voxel body",
        RockVerdict.Manual => "kept: manual (player-edited or saved with the world)",
        RockVerdict.Skipped => "kept: excluded from generation",
        RockVerdict.IdMismatch => "kept: its id is not the generator's",
        RockVerdict.NoBox => "kept: no world box (cannot tell where it is)",
        RockVerdict.Ours => "kept: an asteroid frame's rock",
        RockVerdict.GridNear => $"kept: a grid within {GridMargin:F0} m",
        RockVerdict.PlayerNear => $"kept: a player within {PlayerMargin:F0} m",
        _ => "kept",
    };

    /// <summary>The gap between two boxes (m; 0 when they touch or overlap).</summary>
    public static double Gap(BoundingBoxD a, BoundingBoxD b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        double dz = Math.Max(0, Math.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>At most Lines log lines per Window seconds; what was held back is counted, and said when the next window opens.</summary>
internal sealed class LogLimiter
{
    public readonly int Lines;
    public readonly double Window;
    double _since = double.NegativeInfinity;
    int _used, _held;

    public LogLimiter(int lines, double window) { Lines = lines; Window = window; }

    /// <summary>May a line be written now? held: lines held back in the window that just closed (to be said once), else 0.</summary>
    public bool Allow(double now, out int held)
    {
        held = 0;
        if (now - _since >= Window) { held = _held; _held = 0; _used = 0; _since = now; }
        if (_used < Lines) { _used++; return true; }
        _held++;
        return false;
    }

    public void Reset() { _since = double.NegativeInfinity; _used = _held = 0; }
}
