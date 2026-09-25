using System;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// A first-class reference frame: a node with a parent and a time-dependent
    /// <see cref="Pose"/> in that parent. The frame tree is the celestial scene-graph
    /// (SystemFrame root -> BodyInertial -> BodyFixed / Perifocal / LVLH ...). Convert
    /// states/positions/directions between any two frames with <see cref="FrameConvert"/>.
    /// </summary>
    public abstract class Frame
    {
        public string Name { get; protected set; }
        public Frame Parent { get; protected set; }

        /// <summary>This frame's pose within its parent at absolute time t.</summary>
        public abstract Pose PoseAt(double t);

        public override string ToString() => Name;

        // Shared rotation helper (Rodrigues), double precision, no MatrixD API dependence.
        protected static Vector3D RotateAboutAxis(Vector3D v, Vector3D unitAxis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c
                 + Vector3D.Cross(unitAxis, v) * s
                 + unitAxis * (Vector3D.Dot(unitAxis, v) * (1.0 - c));
        }
    }

    /// <summary>The inertial root (a star system frame). Identity, no parent.</summary>
    public sealed class InertialFrame : Frame
    {
        public InertialFrame(string name) { Name = name; Parent = null; }
        public override Pose PoseAt(double t) => Pose.Identity;
    }

    /// <summary>
    /// Non-rotating frame centered on a body: axes parallel to the parent, origin riding
    /// the body's ephemeris. This is the BodyInertialFrame of the taxonomy; it mirrors a
    /// <see cref="GravityBody"/> (same ephemeris) and agrees with its Galilean conversions.
    /// </summary>
    public sealed class BodyInertialFrame : Frame
    {
        private readonly IEphemeris _parentRelative; // body state in the parent frame

        public BodyInertialFrame(string name, Frame parent, IEphemeris parentRelative)
        {
            Name = name; Parent = parent; _parentRelative = parentRelative;
        }

        /// <summary>Build the inertial frame for a body, given its parent's frame.</summary>
        public static BodyInertialFrame For(GravityBody body, Frame parentFrame)
            => new BodyInertialFrame(body.Name, parentFrame, body.ParentRelative);

        public override Pose PoseAt(double t)
        {
            var bs = _parentRelative != null ? _parentRelative.StateAt(t) : StateVector.Zero;
            var p = Pose.Identity;
            p.Origin = bs.Position;
            p.OriginVelocity = bs.Velocity;
            return p;
        }
    }

    /// <summary>
    /// Perifocal (PQW) frame: fixed rotation of a body-inertial frame so +X points at
    /// periapsis, +Z along the orbit normal. Same origin as the body. Pure element math.
    /// </summary>
    public sealed class PerifocalFrame : Frame
    {
        private readonly Pose _fixed;

        public PerifocalFrame(string name, BodyInertialFrame body, double inclination, double raan, double argPeriapsis)
        {
            Name = name; Parent = body;
            double cO = Math.Cos(raan), sO = Math.Sin(raan);
            double ci = Math.Cos(inclination), si = Math.Sin(inclination);
            double cw = Math.Cos(argPeriapsis), sw = Math.Sin(argPeriapsis);
            var p = new Vector3D(cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, sw * si);
            var q = new Vector3D(-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, cw * si);
            _fixed = Pose.Identity;
            _fixed.Ax = p;
            _fixed.Ay = q;
            _fixed.Az = Vector3D.Cross(p, q);
        }

        public PerifocalFrame(string name, BodyInertialFrame body, KeplerianElements el)
            : this(name, body, el.Inclination, el.Raan, el.ArgPeriapsis) { }

        public override Pose PoseAt(double t) => _fixed;
    }

    /// <summary>
    /// Body-fixed (rotating) frame: shares the body's origin, axes spin about a fixed axis
    /// at a constant rate. For lat/lon/alt, day-night, ground sites. (SE voxel planets do
    /// not actually spin, so this rotation is logical-only — see the architecture doc.)
    /// </summary>
    public sealed class BodyFixedFrame : Frame
    {
        private readonly Vector3D _axis;   // unit spin axis, in the body-inertial frame
        private readonly double _rate;     // rad/s
        private readonly double _phase0;   // angle at t = 0

        public BodyFixedFrame(string name, BodyInertialFrame body, Vector3D spinAxis, double spinRate, double phaseAtZero = 0.0)
        {
            Name = name; Parent = body;
            _axis = Vector3D.Normalize(spinAxis);
            _rate = spinRate;
            _phase0 = phaseAtZero;
        }

        public override Pose PoseAt(double t)
        {
            double theta = _phase0 + _rate * t;
            var p = Pose.Identity;
            p.Ax = RotateAboutAxis(Vector3D.UnitX, _axis, theta);
            p.Ay = RotateAboutAxis(Vector3D.UnitY, _axis, theta);
            p.Az = RotateAboutAxis(Vector3D.UnitZ, _axis, theta);
            p.AngularVelocity = _axis * _rate;
            return p;
        }
    }

    /// <summary>
    /// LVLH / RIC frame riding an orbiting object: origin at the object, axes
    /// X=radial (away from body), Y=in-track, Z=cross-track; rotates at the orbital rate.
    /// "Body down" = -X. This is the proximity-frame orientation and the rendezvous frame.
    /// </summary>
    public sealed class LvlhFrame : Frame
    {
        private readonly IEphemeris _orbit; // the object's state in the body-inertial frame

        public LvlhFrame(string name, BodyInertialFrame body, IEphemeris orbit)
        {
            Name = name; Parent = body; _orbit = orbit;
        }

        public override Pose PoseAt(double t)
        {
            var s = _orbit.StateAt(t);
            Vector3D radial = Vector3D.Normalize(s.Position);
            Vector3D cross = Vector3D.Normalize(Vector3D.Cross(s.Position, s.Velocity)); // orbit normal
            Vector3D inTrack = Vector3D.Cross(cross, radial);                            // completes RH

            var p = Pose.Identity;
            p.Origin = s.Position;
            p.OriginVelocity = s.Velocity;
            p.Ax = radial;
            p.Ay = inTrack;
            p.Az = cross;
            // angular velocity of the rotating LVLH frame = (r x v)/|r|^2 along the orbit normal
            double r2 = s.Position.LengthSquared();
            p.AngularVelocity = Vector3D.Cross(s.Position, s.Velocity) / r2;
            return p;
        }
    }

    /// <summary>
    /// Topocentric ENU frame at a surface point (X=east, Y=north, Z=up). Fixed within a
    /// body-fixed frame, so it co-rotates with the body. Launch/landing guidance.
    /// </summary>
    public sealed class TopocentricFrame : Frame
    {
        private readonly Pose _fixed;

        public TopocentricFrame(string name, BodyFixedFrame bodyFixed,
            double latitude, double longitude, double radius)
        {
            Name = name; Parent = bodyFixed;
            double cLat = Math.Cos(latitude), sLat = Math.Sin(latitude);
            double cLon = Math.Cos(longitude), sLon = Math.Sin(longitude);
            var up = new Vector3D(cLat * cLon, cLat * sLon, sLat);
            var east = new Vector3D(-sLon, cLon, 0.0);
            var north = Vector3D.Cross(up, east);
            _fixed = Pose.Identity;
            _fixed.Origin = up * radius;
            _fixed.Ax = east;
            _fixed.Ay = north;
            _fixed.Az = up;
        }

        public override Pose PoseAt(double t) => _fixed;
    }
}
