using System;
using System.Collections.Generic;
using SEAerospace.Orbital;
using SEAerospace.SystemDef;

namespace SEAerospace.Frames
{
    /// <summary>Which realm a resolved target lands in, for the observer.</summary>
    public enum ResolveKind
    {
        InBerth,   // same berth/frame as the observer: a real world-space position
        OnSky,     // elsewhere: a direction in the observer's LVLH sky + a celestial distance
        Occluded   // line of sight blocked by a body between observer and target
    }

    /// <summary>The three-way result of a <see cref="Resolve"/> call (a small POCO a HUD /
    /// proxy feed / comms graph turns into a marker, a sky dot, or "no line of sight").</summary>
    public struct ResolveResult
    {
        public ResolveKind Kind;

        /// <summary>InBerth: the target's real world-space position.</summary>
        public Vector3D WorldPos;

        /// <summary>OnSky: unit direction to the target in the observer's LVLH frame
        /// (X=radial/up-from-body, Y=in-track, Z=cross-track) — markers sweep the sky as
        /// the observer orbits, which is correct.</summary>
        public Vector3D Direction;

        /// <summary>OnSky: true celestial distance to the target, meters.</summary>
        public double CelestialDistance;

        /// <summary>Occluded: the name of the body blocking the line of sight.</summary>
        public string OccludingBody;

        public static ResolveResult InBerth(Vector3D worldPos)
        {
            ResolveResult r = new ResolveResult();
            r.Kind = ResolveKind.InBerth;
            r.WorldPos = worldPos;
            return r;
        }

        public static ResolveResult OnSky(Vector3D direction, double distance)
        {
            ResolveResult r = new ResolveResult();
            r.Kind = ResolveKind.OnSky;
            r.Direction = direction;
            r.CelestialDistance = distance;
            return r;
        }

        public static ResolveResult Occluded(string body)
        {
            ResolveResult r = new ResolveResult();
            r.Kind = ResolveKind.Occluded;
            r.OccludingBody = body;
            return r;
        }
    }

    /// <summary>A body that can block a line of sight, in the common (parent) frame.</summary>
    public struct ResolveBody
    {
        public string Name;
        public Vector3D Position;   // in the common frame
        public double Radius;       // occluding radius (m)

        public ResolveBody(string name, Vector3D position, double radius)
        {
            Name = name;
            Position = position;
            Radius = radius;
        }
    }

    /// <summary>
    /// The load-bearing door between realms (docs/celestial-addressing.md): given the
    /// observer's frame state and a target — both expressed in a COMMON (parent/celestial)
    /// frame — decide InBerth / OnSky / Occluded. This is the pure geometric core, game-free
    /// and offline-testable; the game-side wrapper gathers the positions (resolving a
    /// <see cref="CelestialAddress"/> against the system + frame registries) and calls it.
    ///
    /// RSS's <c>ConvertRealPosToCurrentProxy</c> / <c>ConvertPlanetPosToReal</c> were the
    /// ad-hoc version of exactly this call; here it is total and typed.
    /// </summary>
    public static class Resolve
    {
        private const double Coincident = 1.0; // m: target essentially on top of the observer

        /// <summary>
        /// Project a target into the observer's realm.
        /// <paramref name="observerPos"/>/<paramref name="observerVel"/> define the observer's
        /// celestial state in the common frame (its LVLH basis is derived from them).
        /// <paramref name="observerFrameId"/> and <paramref name="targetFrameId"/> decide the
        /// InBerth case (same frame → same berth). <paramref name="observerAnchorWorldPos"/> is
        /// the observer berth's world origin (the anchor), used to place an InBerth target.
        /// <paramref name="occluders"/> are bodies in the common frame; null/empty = no occlusion.
        /// </summary>
        public static ResolveResult Project(
            Vector3D observerPos, Vector3D observerVel, long observerFrameId, Vector3D observerAnchorWorldPos,
            Vector3D targetPos, long targetFrameId,
            IList<ResolveBody> occluders)
        {
            // Same-parent convenience: the observer orbits the common frame's origin, so its
            // LVLH basis comes straight from its own position/velocity.
            Vector3D radial = SafeNormalize(observerPos, Vector3D.UnitX);
            Vector3D cross = SafeNormalize(Vector3D.Cross(observerPos, observerVel), Vector3D.UnitZ);
            Vector3D along = Vector3D.Cross(cross, radial);
            return ProjectWithBasis(observerPos, radial, along, cross, observerFrameId,
                observerAnchorWorldPos, targetPos, targetFrameId, occluders);
        }

        /// <summary>
        /// Project with an EXPLICIT LVLH basis and reference position. Used for cross-SOI
        /// routing: everything is expressed in the ROOT frame (so positions/occluders share
        /// one frame), but the observer's LVLH basis is the one about ITS OWN parent (frames
        /// are non-rotating/inertial, so the basis vectors are identical in parent and root).
        /// </summary>
        public static ResolveResult ProjectWithBasis(
            Vector3D observerRefPos, Vector3D radial, Vector3D along, Vector3D cross,
            long observerFrameId, Vector3D observerAnchorWorldPos,
            Vector3D targetPos, long targetFrameId, IList<ResolveBody> occluders)
        {
            Vector3D relCel = targetPos - observerRefPos;
            double dist = relCel.Length();

            // Same frame (or coincident): the target is in the observer's berth — a real world
            // position. The berth is a 1:1 inertial window, so the celestial offset IS the
            // berth-local offset from the anchor.
            if ((targetFrameId != 0 && targetFrameId == observerFrameId) || dist < Coincident)
                return ResolveResult.InBerth(observerAnchorWorldPos + relCel);

            // Line-of-sight: a body whose sphere straddles the observer→target segment occludes.
            string occluder = FirstOccluder(observerRefPos, targetPos, dist, occluders);
            if (occluder != null)
                return ResolveResult.Occluded(occluder);

            // OnSky: express the celestial direction in the observer's LVLH basis so the marker
            // sits where the player would look, and sweeps the sky as they orbit.
            Vector3D dirLvlh = new Vector3D(
                Vector3D.Dot(relCel, radial),
                Vector3D.Dot(relCel, along),
                Vector3D.Dot(relCel, cross));
            dirLvlh = SafeNormalize(dirLvlh, Vector3D.UnitX);
            return ResolveResult.OnSky(dirLvlh, dist);
        }

        // The first body (if any) whose sphere blocks the observer→target segment. A body
        // whose closest point to the segment lies BEHIND either endpoint doesn't occlude.
        private static string FirstOccluder(Vector3D a, Vector3D b, double segLen, IList<ResolveBody> bodies)
        {
            if (bodies == null || segLen <= 0.0) return null;
            Vector3D dir = (b - a) / segLen;
            for (int i = 0; i < bodies.Count; i++)
            {
                ResolveBody body = bodies[i];
                if (body.Radius <= 0.0) continue;
                Vector3D toBody = body.Position - a;
                double proj = Vector3D.Dot(toBody, dir);
                if (proj <= 0.0 || proj >= segLen) continue;     // closest point is past an endpoint
                double perpSq = toBody.LengthSquared() - proj * proj;
                if (perpSq < body.Radius * body.Radius)
                    return body.Name;
            }
            return null;
        }

        private static Vector3D SafeNormalize(Vector3D v, Vector3D fallback)
        {
            return v.LengthSquared() > 1e-18 ? Vector3D.Normalize(v) : fallback;
        }

        /// <summary>
        /// Resolve a <see cref="CelestialAddress"/> to a position in a given parent body's
        /// inertial frame, for the cases that need only the FRAME registry + parent context
        /// (LiveFrame sharing the parent, BodyInertialPoint on the parent, DeepSpaceFixed). The
        /// game-side wrapper handles surface-fixed (needs the body spin/radius), cross-SOI tree
        /// routing, and grid-relative (needs the live grid world pose). Returns false when the
        /// address can't be placed in THIS parent frame from registry data alone.
        /// </summary>
        public static bool TryResolveInParent(CelestialAddress addr, string parentBodyName,
            FrameRegistry frames, double t, out Vector3D parentPos, out long frameId)
        {
            parentPos = Vector3D.Zero;
            frameId = 0;
            if (addr == null) return false;

            switch (addr.Kind)
            {
                case CelestialAddressKind.LiveFrame:
                    if (frames == null) return false;
                    ProximityFrame f = frames.Get(addr.FrameId);
                    if (f == null || f.ParentBodyName != parentBodyName) return false; // different SOI: tree routing (TODO)
                    parentPos = OrbitPropagation.StateAt(f.Elements, t).Position;
                    frameId = f.Id;
                    return true;

                case CelestialAddressKind.BodyInertialPoint:
                    // Supported here only when the anchoring body IS the parent (origin-relative).
                    if (addr.BodyName != parentBodyName) return false;
                    parentPos = new Vector3D(addr.X, addr.Y, addr.Z);
                    return true;

                case CelestialAddressKind.DeepSpaceFixed:
                    // A point given directly in this (root/parent) frame.
                    parentPos = new Vector3D(addr.X, addr.Y, addr.Z);
                    return true;

                default:
                    return false; // SurfaceFixed / GridRelative need the game-side wrapper
            }
        }

        // ---- tree-based resolution (closes the FrameManager↔SystemRegistry parent seam) ----

        /// <summary>
        /// Resolve a <see cref="CelestialAddress"/> to a position in the ROOT (system) frame
        /// using the live <see cref="GravityBody"/> SOI tree — the unified path that makes
        /// CROSS-SOI and SURFACE-FIXED resolution work (frames must be parented to tree nodes).
        ///  - LiveFrame: the frame's orbit lifted from its parent node to root.
        ///  - BodyInertialPoint: an offset in the body node's frame, lifted to root.
        ///  - SurfaceFixed: a geodetic point, rotated by the body's spin (`RotationAt`) using the
        ///    authored radius from the <see cref="BodyDefinition"/>, then placed at the node origin.
        ///  - DeepSpaceFixed: a point already in root.
        ///  - GridRelative: needs the live grid pose → game-side only (returns false).
        /// Returns false when the address can't be placed (unknown body/frame, GridRelative).
        /// </summary>
        public static bool ResolveCelestial(CelestialAddress addr, FrameRegistry frames, SystemRegistry sys,
            double t, out Vector3D rootPos, out long frameId)
        {
            rootPos = Vector3D.Zero;
            frameId = 0;
            if (addr == null || sys == null) return false;

            switch (addr.Kind)
            {
                case CelestialAddressKind.LiveFrame:
                {
                    if (frames == null) return false;
                    ProximityFrame f = frames.Get(addr.FrameId);
                    if (f == null) return false;
                    GravityBody node = sys.Find(f.ParentBodyName);
                    if (node == null) return false;
                    StateVector inParent = OrbitPropagation.StateAt(f.Elements, t);
                    rootPos = node.StateInRoot(inParent, t).Position;
                    frameId = f.Id;
                    return true;
                }
                case CelestialAddressKind.BodyInertialPoint:
                {
                    GravityBody node = sys.Find(addr.BodyName);
                    if (node == null) return false;
                    StateVector local = new StateVector(new Vector3D(addr.X, addr.Y, addr.Z), Vector3D.Zero);
                    rootPos = node.StateInRoot(local, t).Position;
                    return true;
                }
                case CelestialAddressKind.SurfaceFixed:
                {
                    GravityBody node = sys.Find(addr.BodyName);
                    BodyDefinition def = sys.FindDefinition(addr.BodyName);
                    if (node == null || def == null) return false;
                    double r = def.RadiusMeters + addr.AltMeters;
                    Vector3D surf = SurfaceVector(addr.LatDeg, addr.LonDeg, r);   // body-fixed at epoch
                    Vector3D inertial = Vector3D.Transform(surf, node.RotationAt(t)); // spun to time t
                    rootPos = node.OriginInRoot(t).Position + inertial;
                    return true;
                }
                case CelestialAddressKind.DeepSpaceFixed:
                    rootPos = new Vector3D(addr.X, addr.Y, addr.Z);
                    return true;
                default:
                    return false; // GridRelative
            }
        }

        /// <summary>Every tree body as an occluder, positioned in the ROOT frame at time t with
        /// its authored radius — the line-of-sight set for cross-SOI projection.</summary>
        public static List<ResolveBody> TreeOccluders(SystemRegistry sys, double t)
        {
            List<ResolveBody> list = new List<ResolveBody>();
            if (sys == null) return list;
            IReadOnlyList<GravityBody> bodies = sys.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                GravityBody b = bodies[i];
                BodyDefinition def = sys.FindDefinition(b.Name);
                double r = def != null ? def.RadiusMeters : 0.0;
                if (r > 0.0)
                    list.Add(new ResolveBody(b.Name, b.OriginInRoot(t).Position, r));
            }
            return list;
        }

        // Geodetic (lat/lon on a sphere of radius r) -> body-fixed Cartesian, +Z = north pole.
        private static Vector3D SurfaceVector(double latDeg, double lonDeg, double r)
        {
            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            double cl = Math.Cos(lat);
            return new Vector3D(r * cl * Math.Cos(lon), r * cl * Math.Sin(lon), r * Math.Sin(lat));
        }
    }
}
