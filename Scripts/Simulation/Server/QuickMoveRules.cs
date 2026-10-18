#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// THE SPACE BORDER CLAMP of OrbitalApi.TryQuickMove (engine-free: Tests/QuickMoveTests; the engine side is
/// ServerFrames.QuickMove). Creative quick launch (2026-10-18) is the aero mod's now - the user: "ctrl+T should be in aero
/// creative"; this mod's 1.0.1 key went there. Aero decides whether and where a seated pilot's ship goes (its
/// QuickLaunchRules); this mod moves it (its safe move path, MoveGrid) and keeps it in the planet's own physics: never
/// past the space border (PlanetBerths.ShellRadius) less BorderMargin, where it would leave for the rails. Aero knows no
/// border, so it says how far down the clamp may still put the ship with its ground clearance kept (maxDrop).
/// </summary>
public static class QuickMoveRules
{
    public enum Verdict { Go, AboveAir, NoRoom, NotAtPlanet }

    /// <summary>m: how far under the planet's space border (PlanetBerths.ShellRadius) a quick move may end - above it the
    /// ship would leave for the rails.</summary>
    public const double BorderMargin = 500.0;

    /// <summary>
    /// Where the asked place ends under the border, as a distance from the planet's centre (r). rNow: where the ship is;
    /// rAsked: where aero asked for; shellR: the border (0: none - no clamp); maxDrop: how far down from rAsked the clamp may
    /// still put it (aero's clearance room). Already past the border: AboveAir. Asked past the border's cap: clamped to the
    /// cap - or, between the cap and the border already, kept where it is (never lowered) - and NoRoom when that is more
    /// than maxDrop under the asked place. Otherwise the asked place, as it is.
    /// </summary>
    public static Verdict UnderBorder(double rNow, double rAsked, double shellR, double maxDrop, out double r)
    {
        r = rAsked;
        if (!(rNow > 0) || double.IsInfinity(rNow) || !(rAsked > 0) || double.IsInfinity(rAsked)) return Verdict.NotAtPlanet;
        if (shellR > 0 && rNow >= shellR) return Verdict.AboveAir;
        if (!(shellR > 0)) return Verdict.Go;
        double top = shellR - BorderMargin;
        if (rAsked <= top) return Verdict.Go;
        double to = Math.Max(top, rNow);   // (between the cap and the border already: level it and send it, no lift)
        double drop = rAsked - to;
        if (drop > Math.Max(0, double.IsNaN(maxDrop) ? 0 : maxDrop) + 1e-6) return Verdict.NoRoom;
        r = Math.Min(rAsked, to);
        return Verdict.Go;
    }

    /// <summary>The refusal's reason, as the aero mod says it after "Quick launch: " (null: Go).</summary>
    public static string Reason(Verdict v) => v switch
    {
        Verdict.Go => null,
        Verdict.AboveAir => "already above the planet's space",
        Verdict.NoRoom => "no room under the planet's space for this ship",
        Verdict.NotAtPlanet => "not in a planet's space",
        _ => "refused",
    };
}
