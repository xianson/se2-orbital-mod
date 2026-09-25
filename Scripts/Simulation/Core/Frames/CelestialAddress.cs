
namespace SEAerospace.Frames
{
    /// <summary>The kind of thing a celestial address points at (docs/celestial-addressing.md).</summary>
    public enum CelestialAddressKind
    {
        SurfaceFixed,       // (body, lat/lon/alt) in the body-fixed frame — bases, pads, ore fields
        BodyInertialPoint,  // (body, position vector) in the body's inertial frame — parking slots
        LiveFrame,          // (frame id) -> the frame's CURRENT elements — ships, stations, debris
        DeepSpaceFixed,     // (vector) in the system/root frame — rendezvous points, caches
        GridRelative        // (entity id, local offset) — dock entrances, hangar doors
    }

    /// <summary>
    /// A VIRTUAL address into the celestial realm — the stable replacement for a raw
    /// world-space GPS, which under berth packing is only a physical address into whatever
    /// is stowed at that slot right now (docs/celestial-addressing.md). Anchored to the most
    /// STABLE parent available (a body or a live frame), never to an ephemeral berth slot.
    /// The <see cref="Resolve"/> layer turns one of these + an observer + a time into an
    /// InBerth / OnSky / Occluded result.
    ///
    /// Plain serializable POCO (public fields, parameterless ctor, no game types) so it rides
    /// straight into the GPS side-table / world config. Game-free.
    /// </summary>
    public class CelestialAddress
    {
        public CelestialAddressKind Kind = CelestialAddressKind.DeepSpaceFixed;

        /// <summary>Anchoring body name (SurfaceFixed, BodyInertialPoint).</summary>
        public string BodyName = "";

        /// <summary>Surface-fixed geodetic position (SurfaceFixed). Degrees + meters.</summary>
        public double LatDeg = 0.0;
        public double LonDeg = 0.0;
        public double AltMeters = 0.0;

        /// <summary>A vector/offset, meters: body-inertial position (BodyInertialPoint), a
        /// root-frame point (DeepSpaceFixed), or a grid-local offset (GridRelative).</summary>
        public double X = 0.0;
        public double Y = 0.0;
        public double Z = 0.0;

        /// <summary>Live frame id (LiveFrame).</summary>
        public long FrameId = 0;

        /// <summary>Anchoring grid entity id (GridRelative).</summary>
        public long EntityId = 0;

        public CelestialAddress() { }

        // ---- typed factories (no inline construction-with-init at call sites; C# 6) -------

        public static CelestialAddress SurfaceFixed(string body, double latDeg, double lonDeg, double altMeters)
        {
            CelestialAddress a = new CelestialAddress();
            a.Kind = CelestialAddressKind.SurfaceFixed;
            a.BodyName = body; a.LatDeg = latDeg; a.LonDeg = lonDeg; a.AltMeters = altMeters;
            return a;
        }

        public static CelestialAddress BodyInertialPoint(string body, double x, double y, double z)
        {
            CelestialAddress a = new CelestialAddress();
            a.Kind = CelestialAddressKind.BodyInertialPoint;
            a.BodyName = body; a.X = x; a.Y = y; a.Z = z;
            return a;
        }

        public static CelestialAddress LiveFrame(long frameId)
        {
            CelestialAddress a = new CelestialAddress();
            a.Kind = CelestialAddressKind.LiveFrame;
            a.FrameId = frameId;
            return a;
        }

        public static CelestialAddress DeepSpaceFixed(double x, double y, double z)
        {
            CelestialAddress a = new CelestialAddress();
            a.Kind = CelestialAddressKind.DeepSpaceFixed;
            a.X = x; a.Y = y; a.Z = z;
            return a;
        }

        public static CelestialAddress GridRelative(long entityId, double x, double y, double z)
        {
            CelestialAddress a = new CelestialAddress();
            a.Kind = CelestialAddressKind.GridRelative;
            a.EntityId = entityId; a.X = x; a.Y = y; a.Z = z;
            return a;
        }
    }
}
