
namespace SEAerospace.Orbital
{
    /// <summary>
    /// A rigid transform of a child frame within its parent, INCLUDING the rates needed
    /// to transform velocities correctly through rotating frames. The child's axes are
    /// stored as an orthonormal triple expressed in parent coordinates (avoids any
    /// matrix row/column ambiguity).
    ///
    ///   parent_r = Origin + (l.X*Ax + l.Y*Ay + l.Z*Az)
    ///   parent_v = OriginVelocity + R(l_v) + AngularVelocity x R(l_r)
    ///
    /// where R(.) is the rotation child->parent. The velocity terms are what carry
    /// Coriolis/centrifugal effects when you view a state from a rotating frame.
    /// </summary>
    public struct Pose
    {
        public Vector3D Origin;           // child origin, in parent coords
        public Vector3D OriginVelocity;   // d/dt Origin, in parent coords
        public Vector3D AngularVelocity;  // of the child frame, in parent coords (rad/s)
        public Vector3D Ax, Ay, Az;       // child axes expressed in parent coords (orthonormal)

        public static Pose Identity => new Pose
        {
            Origin = Vector3D.Zero,
            OriginVelocity = Vector3D.Zero,
            AngularVelocity = Vector3D.Zero,
            Ax = Vector3D.UnitX,
            Ay = Vector3D.UnitY,
            Az = Vector3D.UnitZ,
        };

        /// <summary>Rotate a child-frame direction into the parent frame.</summary>
        public Vector3D DirToParent(Vector3D v) => v.X * Ax + v.Y * Ay + v.Z * Az;

        /// <summary>Rotate a parent-frame direction into the child frame (axes orthonormal).</summary>
        public Vector3D DirToLocal(Vector3D v)
            => new Vector3D(Vector3D.Dot(v, Ax), Vector3D.Dot(v, Ay), Vector3D.Dot(v, Az));

        /// <summary>Lift a full state from the child frame up into the parent frame.</summary>
        public StateVector ToParent(StateVector s)
        {
            Vector3D rr = DirToParent(s.Position);
            Vector3D pos = Origin + rr;
            Vector3D vel = OriginVelocity + DirToParent(s.Velocity) + Vector3D.Cross(AngularVelocity, rr);
            return new StateVector(pos, vel);
        }

        /// <summary>Drop a full state from the parent frame down into the child frame.</summary>
        public StateVector ToLocal(StateVector s)
        {
            Vector3D dr = s.Position - Origin;
            Vector3D dv = s.Velocity - OriginVelocity - Vector3D.Cross(AngularVelocity, dr);
            return new StateVector(DirToLocal(dr), DirToLocal(dv));
        }
    }
}
