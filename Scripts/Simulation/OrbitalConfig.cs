#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>How the per-planet frame decides between real planet and proxy.</summary>
public enum ProxyMode
{
    /// <summary>Real planet inside its frame sphere, proxy outside. The intended behaviour.</summary>
    Frame,
    /// <summary>Always hide the real planet and draw the proxy. For judging the proxy up close.</summary>
    AlwaysProxy,
    /// <summary>Never touch the real planet. The mod is inert.</summary>
    AlwaysReal,
    /// <summary>Flip between real and proxy every <see cref="OrbitalConfig.AlternateSeconds"/>. For side-by-side comparison.</summary>
    Alternate,
}

/// <summary>Tunables. Edit and reload the world.</summary>
public static class OrbitalConfig
{
    /// <summary>
    /// Planet day (sidereal), seconds: -1 = the world's own sun period (keeps SE2's day length; no
    /// spin when the world's sun does not rotate), 0 = planets do not spin, &gt; 0 = that period.
    /// </summary>
    public static double PlanetDaySeconds = -1;
    /// <summary>Fictitious forces in a spinning cell apply above this speed or altitude only (the
    /// surface is at rest in the chart; slow ground traffic sees corrections far below friction).</summary>
    public static double FictitiousMinSpeed = 20.0, FictitiousMinAltitude = 5000.0;

    /// <summary>
    /// TOTAL PARTITION for legacy space (the world outside every planet cell, e.g. the spawn area):
    /// capture its dynamic grids and the player into conjunction frames, treating that space as a
    /// window around the nearest planet (celestial = planet + world offset). OFF by default: objects
    /// there are nearly at rest relative to the planet, so their captured orbits are radial falls and
    /// the whole area would drop into the planet within the hour. A world designed for orbits (things
    /// placed with orbital velocity) wants it on.
    /// </summary>
    public static bool CaptureLegacySpace = false;

    public static ProxyMode Mode = ProxyMode.Frame;

    /// <summary>Frame sphere = gravity reach × this. Enter below it.</summary>
    public static double FrameEnterFactor = 1.0;
    /// <summary>Leave the frame above gravity reach × this. Hysteresis against flicker.</summary>
    public static double FrameExitFactor = 1.1;
    /// <summary>Floor on the frame sphere, in planet radii, for planets with tiny gravity reach.</summary>
    public static double MinFrameRadii = 1.5;

    /// <summary>Clamp proxy render distance (m), keeping angular size. 0 = draw at true distance.</summary>
    public static double ProxyClampDistance = 0;

    public static double AlternateSeconds = 10;

    /// <summary>Also hide atmosphere and clouds with the terrain.</summary>
    public static bool HideAtmosphere = true;

    /// <summary>Master switch for hiding real planets. Off = proxies can still be drawn (debug) but real planets are never touched.</summary>
    public static bool HideRealPlanets = true;

    /// <summary>
    /// DEBUG: park every proxy globe in front of the camera, side by side, at
    /// <see cref="DebugDistance"/> and <see cref="DebugAngularDiameterDeg"/>, regardless of frame.
    /// For eyeballing the proxy's look without flying to a planet. Leave false.
    /// </summary>
    public static bool DebugProxyInFront = false;

    /// <summary>Draw the predicted orbit around the dominant planet, with an element readout.</summary>
    public static bool ShowOrbit = true;

    /// <summary>
    /// DEV ONLY: poll %TEMP%\OrbitalMod\cmd.txt for teleport/view commands (see DevHarness), and the
    /// F8 debug panel. On only where the developer's harness has put %TEMP%\OrbitalMod\dev.flag (the
    /// test launcher does): a player's install never runs harness commands.
    /// </summary>
    public static bool DevHarness => _dev ??= DevFlagPresent();
    private static bool? _dev;
    private static bool DevFlagPresent()
    {
        try { return System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrbitalMod", "dev.flag")); }
        catch { return false; }
    }
    public static double DebugDistance = 300;
    public static double DebugAngularDiameterDeg = 12;
}
