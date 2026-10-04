using System;
using System.Collections.Generic;

namespace SEAerospace.Orbital
{
    /// <summary>
    /// A node in the SOI tree — a gravitating body (sun / planet / moon) with one mu,
    /// a sphere-of-influence radius, a parent, and an ephemeris giving its motion in the
    /// parent frame. This is the celestial-realm BodyInertialFrame hierarchy made
    /// concrete; reparenting across a patch is the classical v_rel = v - v_body change,
    /// done by <see cref="ConvertStateTo"/>.
    ///
    /// SoiRadius is the classical Laplace patched-conic boundary
    /// (<see cref="Gravity.LaplaceSoiRadius"/>), a CHOSEN dominance-switch radius — NOT a
    /// physical zero-gravity edge. Our gravity has no cutoff, so both wells are nonzero
    /// on each side; the SOI is just where we switch which body is primary.
    ///
    /// All frames are inertial and non-rotating; conversions are Galilean (translate +
    /// add relative velocity), which is exactly right whether the body is static
    /// (vanilla SE) or moving on rails (RSS-style).
    /// </summary>
    public sealed class GravityBody
    {
        public string Name;
        public double Mu;                    // gravitational parameter, m^3/s^2
        public double SoiRadius;             // sphere of influence; +inf for the root
        public GravityBody Parent;           // null = root
        public IEphemeris ParentRelative;    // this body's state in Parent's frame; null for root
        public readonly List<GravityBody> Children = new List<GravityBody>();

        // --- logical spin (the "planet day") -----------------------------------------
        // Sidereal rotation about SpinAxis. Purely a READOUT/orientation quantity (a
        // body-fixed frame for surface coords, day/night, and proxy texture rotation); it
        // is decoupled from the gravity/orbit math, which is all axisymmetric and never
        // sees the spin. 0 period = no rotation (tidally frozen / unspecified).
        public double RotationPeriodSeconds;     // sidereal day, seconds; 0 = none
        public Vector3D SpinAxis = Vector3D.UnitZ; // north pole direction (the +Z orbital axis by default)

        public bool IsRoot => Parent == null;

        // --- per-(body,t) root-state memo --------------------------------------------
        // OriginInRoot(t) walks the parent chain and re-evaluates every ancestor's
        // trig-heavy ephemeris on each call. Within a single rendered frame the SAME
        // body at the SAME snapshot Time is resolved dozens of times (every renderer +
        // GPS + sun + proxy + snapshot). Cache this body's OWN root-frame state, keyed
        // by an exact-equality stamp on t: a repeat call at the identical t returns the
        // cached value; any different t recomputes via the unchanged chain walk and
        // restamps. The cached value is produced by the same StateInRoot(Zero, t) loop,
        // so it is BIT-IDENTICAL to the un-memoized result — this is pure caching, not a
        // re-association of the floating-point sum.
        //
        // THREADING: SE Update* and Draw both run on the main thread (never concurrent),
        // so this single-field memo needs no locking. OriginInRoot MUST be called on the
        // main thread (all current callers already are).
        private double _rootStamp = double.NaN;   // t this memo is valid for; NaN == empty (NaN != NaN forces a miss)
        private StateVector _rootState;            // this body's OriginInRoot(t) at _rootStamp
        private int _memoThread;                   // the thread the memo belongs to (0: none yet)

        /// <summary>This body's state in its parent's frame at time t (zero for the root).</summary>
        public StateVector StateInParentAt(double t)
            => (Parent == null || ParentRelative == null) ? StateVector.Zero : ParentRelative.StateAt(t);

        /// <summary>Lift a state from THIS body's frame up into the parent's frame.</summary>
        public StateVector ToParent(StateVector s, double t) => StateInParentAt(t) + s;

        /// <summary>
        /// Lift a state from this body's frame all the way to the root frame.
        /// The offset==Zero base of this walk (this body's own origin in root) is the
        /// expensive, repeatedly-resolved quantity and is memoized via
        /// <see cref="OriginInRoot"/>. For a nonzero offset the original chain walk is
        /// kept verbatim so the floating-point summation order (which threads the offset
        /// into the innermost add at each level) is preserved BIT-FOR-BIT; re-associating
        /// it as OriginInRoot(t)+s would shift low bits. Galilean shift.
        /// </summary>
        public StateVector StateInRoot(StateVector s, double t)
        {
            // Hot path: lifting this body's own origin (offset zero) — fully memoized.
            // Exact component test (not VRageMath's operator ==) so only a genuine zero
            // offset, such as the StateVector.Zero that OriginInRoot/ConvertStateTo pass,
            // is routed to the memo; any nonzero offset falls through to the exact loop.
            if (s.Position.X == 0.0 && s.Position.Y == 0.0 && s.Position.Z == 0.0 &&
                s.Velocity.X == 0.0 && s.Velocity.Y == 0.0 && s.Velocity.Z == 0.0)
                return OriginInRoot(t);

            // General offset: bit-identical to the pre-memo loop (no re-association).
            var body = this;
            while (body.Parent != null) { s = body.ToParent(s, t); body = body.Parent; }
            return s;
        }

        /// <summary>
        /// This body's own origin/state expressed in the root frame at time t.
        /// Memoized per-(body,t): see the _rootStamp/_rootState fields. Must be called on
        /// the main thread (no locking by design).
        /// </summary>
        public StateVector OriginInRoot(double t)
        {
            // SE2 runs the server scene on its own thread (the SE1 single-thread note above no longer holds): the memo
            // belongs to the first thread that uses it (the client: the map samples it hundreds of times a frame); any
            // other thread walks the chain directly - a torn read of the 48-byte state put a planet thousands of km off.
            int tid = System.Environment.CurrentManagedThreadId;
            if (_memoThread == 0) System.Threading.Interlocked.CompareExchange(ref _memoThread, tid, 0);
            if (tid != _memoThread)
            {
                StateVector w = StateVector.Zero;
                var wb = this;
                while (wb.Parent != null) { w = wb.ToParent(w, t); wb = wb.Parent; }
                return w;
            }
            // Exact-equality hit: same body, same snapshot Time within the frame.
            if (t == _rootStamp) return _rootState;

            // Miss: recompute via the original chain walk (bit-identical to pre-memo).
            StateVector s = StateVector.Zero;
            var body = this;
            while (body.Parent != null) { s = body.ToParent(s, t); body = body.Parent; }

            _rootState = s;
            _rootStamp = t;   // restamp last so a hit only sees a fully-written value
            return s;
        }

        /// <summary>
        /// Convert a state expressed in THIS body's frame into <paramref name="target"/>'s
        /// frame at time t (same tree). Via the common root, Galilean — this is the
        /// frame-velocity patch generalized to any pair of bodies.
        /// </summary>
        public StateVector ConvertStateTo(GravityBody target, StateVector s, double t)
            => StateInRoot(s, t) - target.OriginInRoot(t);

        /// <summary>
        /// Rotation phase about <see cref="SpinAxis"/> at time t, radians in [0, 2pi).
        /// Zero if the body has no specified spin. Epoch phase is 0 at t = 0 (the system
        /// epoch); add a body-fixed prime-meridian offset downstream if needed.
        /// </summary>
        public double RotationAngleAt(double t)
        {
            if (RotationPeriodSeconds <= 0.0) return 0.0;
            double frac = t / RotationPeriodSeconds;
            frac -= Math.Floor(frac);
            return frac * (2.0 * Math.PI);
        }

        /// <summary>
        /// Body-fixed orientation at time t: the rotation that carries the body's reference
        /// (epoch) frame to its orientation now, about the (normalized) spin axis. Useful
        /// for placing surface coordinates and rotating a proxy's texture. Identity if no spin.
        /// </summary>
        public MatrixD RotationAt(double t)
        {
            if (RotationPeriodSeconds <= 0.0) return MatrixD.Identity;
            Vector3D axis = SpinAxis;
            if (axis.LengthSquared() < 1e-12) axis = Vector3D.UnitZ;
            else axis.Normalize();
            return MatrixD.CreateFromAxisAngle(axis, RotationAngleAt(t));
        }

        /// <summary>
        /// The DEEPEST body in the subtree under this node (inclusive) whose SOI contains
        /// <paramref name="rootPos"/> — a position in the ROOT frame — at time t: the start
        /// body for a patched-conic propagation from a celestial position. Walk down: descend
        /// into the (first) child whose SoiRadius sphere contains the point, repeat until no
        /// child claims it. Child SOIs under one parent are disjoint for any sane system
        /// (Laplace SOI &lt;&lt; orbit spacing — same assumption as <c>SoiReparent</c>), so
        /// first-hit is unambiguous. Called on the tree ROOT (infinite SOI) this is total:
        /// a point outside every planet SOI returns the root star itself.
        /// </summary>
        public GravityBody DeepestSoiContaining(Vector3D rootPos, double t)
        {
            GravityBody body = this;
            bool descended = true;
            while (descended)
            {
                descended = false;
                for (int i = 0; i < body.Children.Count; i++)
                {
                    GravityBody child = body.Children[i];
                    double soi = child.SoiRadius;
                    if (soi <= 0.0 || double.IsInfinity(soi)) continue;
                    if ((rootPos - child.OriginInRoot(t).Position).LengthSquared() < soi * soi)
                    {
                        body = child;
                        descended = true;
                        break;
                    }
                }
            }
            return body;
        }

        /// <summary>Add a child body and wire up the parent link.</summary>
        public GravityBody AddChild(string name, double mu, double soiRadius, IEphemeris parentRelative)
        {
            var c = new GravityBody
            {
                Name = name,
                Mu = mu,
                SoiRadius = soiRadius,
                Parent = this,
                ParentRelative = parentRelative,
            };
            Children.Add(c);
            return c;
        }

        public static GravityBody Root(string name, double mu, double soiRadius = double.PositiveInfinity)
            => new GravityBody { Name = name, Mu = mu, SoiRadius = soiRadius, Parent = null, ParentRelative = null };

        public override string ToString() => $"{Name}(mu={Mu:E2}, soi={SoiRadius:E2})";
    }
}
