#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// QUICK LAUNCH's decisions (engine-free: Tests/QuickLaunchTests; the engine side is QuickLaunch's key and
/// ServerFrames.DoQuickLaunch). 2026-10-18, the user: "if the game is in creative, allow quick launch in cockpit as an
/// actual keybind" - the dev fly test (Ctrl+T, FlyTest) made a player feature: the server decides, from its own state,
/// whether a seated pilot's ship may be lifted, and where to.
/// </summary>
public static class QuickLaunchRules
{
    public enum Verdict { Go, Off, NotCreative, NotSeated, NotYourSeat, Gone, Static, OnRails, NotAtPlanet, AboveAir, NoRoom }

    /// <summary>m: the least gap between the ground (the reference under the ship) and the ship's own bounding radius
    /// after the lift - it is turned level in place, so a long ship needs its length clear.</summary>
    public const double MinClearance = 100.0;
    /// <summary>m: how far under the planet's space border (PlanetBerths.ShellRadius) a launch may end - above it the ship
    /// would leave for the rails.</summary>
    public const double BorderMargin = 500.0;

    /// <summary>
    /// May this request go ahead? In this order: the world allows it (QuickLaunch.Creative); the pilot is in creative
    /// (IGameMode.IsCreative: the world's creative mode, or this player's creative tools); the server sees the player
    /// seated, on the grid the client asked for (a stale or forged id moves nothing); the grid is alive and not static
    /// (a static is never teleported: HavokMoves); it is not on rails; it is in a planet's cell.
    /// </summary>
    public static Verdict Check(bool enabled, bool creative, long serverSeatGrid, long requested, bool gridAlive, bool isStatic,
                                bool onRails, bool atPlanet)
    {
        if (!enabled) return Verdict.Off;
        if (!creative) return Verdict.NotCreative;
        if (serverSeatGrid == 0) return Verdict.NotSeated;
        if (requested != serverSeatGrid) return Verdict.NotYourSeat;
        if (!gridAlive) return Verdict.Gone;
        if (isStatic) return Verdict.Static;
        if (onRails) return Verdict.OnRails;
        if (!atPlanet) return Verdict.NotAtPlanet;
        return Verdict.Go;
    }

    /// <summary>
    /// Where the ship ends, as a distance from the planet's centre (r). rNow: where it is; rGround: the ground under it
    /// (NaN: not known - no aero ground ray; then the ship's own place stands in for the ground, so it is lifted by the
    /// altitude from where it is and never into a hill nobody measured); shellR: the planet's space border (0: none).
    /// Never lower than it is now, never closer to the ground than its radius plus MinClearance, never past the border
    /// less BorderMargin (clamped there; NoRoom when that leaves no clearance; AboveAir when it is already past the border).
    /// </summary>
    public static Verdict Radius(double rNow, double rGround, double shellR, double altitude, double shipRadius, out double r)
    {
        r = rNow;
        if (!(rNow > 0) || double.IsInfinity(rNow)) return Verdict.NotAtPlanet;
        if (shellR > 0 && rNow >= shellR) return Verdict.AboveAir;
        // (a ground above the ship is a bad ray - inside a cave or a ray through the ship: use the ship's place)
        double floor = double.IsNaN(rGround) || double.IsInfinity(rGround) || rGround > rNow ? rNow : rGround;
        double clear = Math.Max(0, double.IsNaN(shipRadius) ? 0 : shipRadius) + MinClearance;
        double want = floor + Math.Max(double.IsNaN(altitude) ? 0 : altitude, clear);
        if (want < rNow) want = rNow;   // (lifted, never dropped)
        if (shellR > 0)
        {
            double top = shellR - BorderMargin;
            if (want > top) want = top;
            if (want < floor + clear) return Verdict.NoRoom;
            if (want < rNow) want = rNow;   // (between the cap and the border already: level it and send it, no lift)
        }
        r = want;
        return Verdict.Go;
    }

    /// <summary>The launch speed (m/s, along the nose): the setting when above 0, else the cruise speed (the aero mod's top
    /// speed at 3/4 throttle; NaN or under 1 m/s: fallback), at most cap (the world's speed cap; 0: none).</summary>
    public static double Speed(double setting, double cruise, double fallback, double cap)
    {
        double v = setting > 0 ? setting : (cruise > 1 && !double.IsInfinity(cruise) ? cruise : fallback);
        if (cap > 0 && v > cap) v = cap;
        return Math.Max(0, v);
    }

    /// <summary>The player's line for a refusal (null: none - Go).</summary>
    public static string Say(Verdict v) => v switch
    {
        Verdict.Go => null,
        Verdict.Off => "Quick launch is turned off in this world",
        Verdict.NotCreative => "Quick launch works in creative only",
        Verdict.NotSeated => "Quick launch: sit in a cockpit first",
        Verdict.NotYourSeat => "Quick launch: not ready yet, try again",
        Verdict.Gone => "Quick launch: the ship is gone",
        Verdict.Static => "Quick launch: a station cannot be launched",
        Verdict.OnRails => "Quick launch: not on rails (only near a planet's surface)",
        Verdict.NotAtPlanet => "Quick launch: not near a planet",
        Verdict.AboveAir => "Quick launch: already above the planet's space",
        Verdict.NoRoom => "Quick launch: no room under the planet's space for this ship",
        _ => "Quick launch: refused",
    };
}
