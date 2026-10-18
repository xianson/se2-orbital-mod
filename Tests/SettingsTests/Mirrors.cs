// Stand-ins for what OrbitalSettings*.cs reaches in the mod: the fields its settings get and set (same names, same types),
// the engine types its store names, and the log. [Mirror] fields carry NO starting value here: Program.FillMirrors reads
// each one's initialiser from the mod's source (Scripts/Simulation) before the registry is first touched, and fails the run
// when one is missing, has another type, or became a const - so the registry's defaults here are the game's.
using System.Reflection;

namespace OrbitalTests.Settings
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class MirrorAttribute : Attribute { }
}

namespace Keen.Game2.Simulation.GameSystems
{
    public class EntityNameSessionComponentObjectBuilder { public Dictionary<string, OrbitalMod.Entity> NamedEntities; }
}

namespace SEAerospace.Entry
{
    [OrbitalTests.Settings.Mirror] public static class Reentry { public static double DragDensity; }
}

namespace OrbitalMod
{
    using OrbitalTests.Settings;

    public class Entity { }
    public class Log
    {
        public static Log Default;
        public readonly List<string> Lines = new List<string>();
        public void Info(string s) => Lines.Add(s);
    }

    public enum ProxyMode { Frame, AlwaysProxy, AlwaysReal, Alternate }   // (its names in order: checked against the source)

    [Mirror] public static class OrbitalConfig
    {
        public static double PlanetDaySeconds, FictitiousMinSpeed, FictitiousMinAltitude, FrameEnterFactor, FrameExitFactor,
                             MinFrameRadii, ProxyClampDistance, AlternateSeconds;
        public static bool CaptureLegacySpace, HideAtmosphere, HideRealPlanets, ShowOrbit;
        public static ProxyMode Mode;
    }
    [Mirror] public static class EntryHost { public static bool Enabled; public static double WarpLead; }
    [Mirror] public static class AsteroidFrames { public static bool Enabled; }
    [Mirror] public static class AsteroidBridge { public static bool DeleteGenerated; }
    [Mirror] public static class EncounterFrames { public static bool Enabled, SectorAnchors; }
    [Mirror] public static class RingRocks { public static bool Enabled; public static int PerRing; }
    [Mirror] public static class WarpControl
    {
        public static readonly double[] Levels = { 1, 5, 10, 50, 100, 1000, 10000 };   // (checked against the source too)
        public static int MaxLevel;
    }
    [Mirror] public static class WarpBill { public static bool Enabled; }
    [Mirror] public static class Maneuvers { public static double WarpLead; public static int MaxPatches; }
    [Mirror] public static class ServerFrames { internal static double WarpMemberSpeed; }
    [Mirror] public static class PlanetRings { public static bool ProxyRings, MapRings; }
    [Mirror] public static class PlanetRenderBridge { public static bool ProxyAtmospheres; }
    [Mirror] public static class PlanetMesh
    {
        public static int Quads; public static bool NormalMaps; public static float TangentSign;
        public static int Rebuilds;   // (not mirrored: the stand-in's own count of SetNormals)
        public static void SetNormals(bool on, float tangentSign) { NormalMaps = on; TangentSign = tangentSign; Rebuilds++; }
    }
    [Mirror] public static class PlanetFrameComponent { public static bool WarmBeforeShow; }
    [Mirror] public static class CrossingFade { public static bool Enabled; }
    [Mirror] public static class OrbitDisplay { public static float LineThickness; }
    [Mirror] public static class BodyMarkers { public static bool Enabled, ShowOrigin; }
    [Mirror] public static class FrameMarkers { public static bool Enabled; }
    [Mirror] public static class AlignButton { public static bool On; }
    [Mirror] public static class SpdIcon { public static bool On; }
    [Mirror] public static class BuildHints { public static bool On; }
    [Mirror] public static class Contacts { public static bool Enabled; public static int Version; }
    [Mirror] public static class CleanMap { public static bool Enabled, ManeuverEditor; }
    [Mirror] public static class MapPipeline { public static float TextScale; }
    [Mirror] public static class GameMap { public static double ZoomOutFactor; public static bool HideGameSectors; }
    [Mirror] public static class MapCamera { public static bool Enabled; }
    [Mirror] public static class ContactsTab { public static bool Enabled; }
    [Mirror] public static class MapView { public static double LineWidth; }
    [Mirror] public static class AttitudeHold { public static double TurnDegPerSec; }
    [Mirror] public static class AutoBurn { public static double AlignLead; }
}
