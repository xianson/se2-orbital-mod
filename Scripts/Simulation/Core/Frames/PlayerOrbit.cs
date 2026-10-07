using System;
using System.Collections.Generic;
using SEAerospace.Orbital;

namespace SEAerospace.Frames
{
    /// <summary>
    /// WHOSE ORBIT THE PLAYER SEES (game-free; tested offline: Tests/PlayerOrbitTests). The orbit card, the plot and the
    /// speed are the player's own orbit: the frame's virtual orbit plus where the player is in the frame's berth and how
    /// they move in it. This decides, from a snapshot, which frame that is and the player's offset and velocity in it -
    /// without state carried between calls (a seated player once read the offset and velocity left from their last walk:
    /// the wrong speed, or no card, in a pasted-in grid that was not the frame's anchor).
    ///  - On foot: the frame the character is a member of; their own position and velocity.
    ///  - Seated: the grid they sit in - its frame if it is a member, else the frame of the nearest member grid within
    ///    <see cref="FrameReach"/> of it (a grid just pasted in, not yet framed, rides the frame it sits in); that grid's
    ///    position and velocity. The anchor too: an anchor that drifted from its berth's centre has its own offset.
    ///  - Under warp N the berth's relative motion runs N times faster (ServerFrames scales it): velocities divided by N.
    /// Positions are world (window) positions in the frame's berth; velocities world velocities.
    /// </summary>
    public static class PlayerOrbit
    {
        /// <summary>How far from a framed grid a seated player's unframed grid still counts as in that frame (m).</summary>
        public const double FrameReach = 300.0;
        /// <summary>Within this of the anchor (m) the player is at it: no rendezvous plot about it.</summary>
        public const double AnchorReach = 100.0;

        public struct GridState
        {
            public long Id; public Vector3D Position, Velocity;
            /// <summary>Its world bounding box, when known: distances are to its hull, not its origin (a big ship's origin is
            /// hundreds of metres from where you stand on it).</summary>
            public Vector3D BoxMin, BoxMax; public bool HasBox;
            public GridState(long id, Vector3D p, Vector3D v) { Id = id; Position = p; Velocity = v; BoxMin = BoxMax = default; HasBox = false; }
            public GridState(long id, Vector3D p, Vector3D v, Vector3D min, Vector3D max) { Id = id; Position = p; Velocity = v; BoxMin = min; BoxMax = max; HasBox = true; }

            /// <summary>From a point to this grid: to its box (0 inside it), else to its origin.</summary>
            public double DistanceTo(Vector3D p)
            {
                if (!HasBox) return (Position - p).Length();
                double dx = Math.Max(0, Math.Max(BoxMin.X - p.X, p.X - BoxMax.X));
                double dy = Math.Max(0, Math.Max(BoxMin.Y - p.Y, p.Y - BoxMax.Y));
                double dz = Math.Max(0, Math.Max(BoxMin.Z - p.Z, p.Z - BoxMax.Z));
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        public struct Result
        {
            /// <summary>The frame whose orbit is the player's (null: none - no card).</summary>
            public ProximityFrame Frame;
            /// <summary>The player's offset from the berth's centre and velocity in the frame (true, not warped).</summary>
            public Vector3D Offset, Velocity;
            /// <summary>The grid the player sits in (0 on foot); it is the frame's anchor; the player is at the anchor.</summary>
            public long SeatGrid; public bool InAnchor, AtAnchor;
            public string Why;
        }

        /// <summary>On foot: the character's frame (by membership; null: none), position and velocity.</summary>
        public static Result OnFoot(ProximityFrame frame, Vector3D pos, Vector3D vel, double timescale, Vector3D? anchorPos = null)
        {
            var r = new Result { Why = frame == null ? "not in a frame" : "on foot" };
            if (frame == null) return r;
            r.Frame = frame;
            r.Offset = Finite(pos) ? pos - frame.BerthCenter : Vector3D.Zero;
            r.Velocity = Finite(vel) ? vel / Math.Max(1.0, timescale) : Vector3D.Zero;
            r.AtAnchor = anchorPos.HasValue && (pos - anchorPos.Value).Length() <= AnchorReach;
            return r;
        }

        /// <summary>Seated in grid `seat` (its state; Id from the server's grid ids). grids: every grid's snapshot (positions;
        /// velocities unused); frameOf: a grid id's frame (null: none).</summary>
        public static Result Seated(GridState seat, IReadOnlyList<GridState> grids, Func<long, ProximityFrame> frameOf, double timescale)
        {
            var r = new Result { SeatGrid = seat.Id };
            if (!Finite(seat.Position)) { r.Why = "the seat's grid has no position"; return r; }
            ProximityFrame f = seat.Id != 0 ? frameOf(seat.Id) : null;
            if (f == null)
            {
                // not framed (just pasted, or left out): the frame of the nearest framed grid near it
                double bd = FrameReach;
                if (grids != null)
                    foreach (var g in grids)
                    {
                        if (g.Id == seat.Id || !Finite(g.Position)) continue;
                        double d = (g.Position - seat.Position).Length();
                        if (d >= bd) continue;
                        var gf = frameOf(g.Id);
                        if (gf != null) { bd = d; f = gf; }
                    }
                r.Why = f != null ? "seated in an unframed grid near a framed one" : "seated, no frame near";
            }
            else r.Why = "seated in a framed grid";
            if (f == null) return r;
            r.Frame = f;
            r.InAnchor = f.AnchorEntityId == seat.Id;
            r.Offset = seat.Position - f.BerthCenter;
            r.Velocity = Finite(seat.Velocity) ? seat.Velocity / Math.Max(1.0, timescale) : Vector3D.Zero;
            if (r.InAnchor) r.AtAnchor = true;
            else if (grids != null)
                foreach (var g in grids)
                    if (g.Id == f.AnchorEntityId && Finite(g.Position)) { r.AtAnchor = (g.Position - seat.Position).Length() <= AnchorReach; break; }
            return r;
        }

        /// <summary>
        /// On foot in no frame, out in the rails' space (not a planet's): the frame to join - the frame of the nearest framed
        /// grid within <see cref="FrameReach"/> (a ship you just stood up in: it was stowed with you seated in it, and a
        /// seated character is no grid - it was never made a member, and walked off it into no frame: no orbit, and left
        /// behind when the frame moved). Null: none near.
        /// </summary>
        public static ProximityFrame FrameToJoin(Vector3D pos, IReadOnlyList<GridState> grids, Func<long, ProximityFrame> frameOf)
        {
            if (!Finite(pos) || grids == null) return null;
            ProximityFrame f = null; double bd = FrameReach;
            foreach (var g in grids)
            {
                if (!Finite(g.Position)) continue;
                double d = g.DistanceTo(pos);   // (to its hull when its box is known)
                if (d >= bd) continue;
                var gf = frameOf(g.Id);
                if (gf != null) { bd = d; f = gf; }
            }
            return f;
        }

        /// <summary>Farther than this from your frame's berth on foot (m) you are not in it: something else moved you (the
        /// game's fast travel, a respawn) - berths are 100 km and more apart, a frame splits you off at 20 km.</summary>
        public const double JumpDistance = 60000.0;

        /// <summary>On foot in frame f at pos: moved out of it by something else - leave it quietly (no split onto an orbit
        /// made of a berth-to-berth offset: thrown into space alone).</summary>
        public static bool MovedOut(ProximityFrame f, Vector3D pos) => f != null && Finite(pos) && (pos - f.BerthCenter).Length() > JumpDistance;

        /// <summary>Seated: the frame you are a member of (as a character: from walking, a stow) against the frame of the ship
        /// you sit in. Kept only when they are the same (or you are no member); otherwise left - the ship split off or arrived
        /// with you in it, and standing up in the old frame threw you 100+ km onto a nonsense orbit. Standing up, you join the
        /// ship's (FrameToJoin).</summary>
        public static bool KeepMembershipSeated(ProximityFrame memberOf, ProximityFrame seatFrame) => memberOf == null || ReferenceEquals(memberOf, seatFrame);

        /// <summary>The player's own state about the frame's parent at time t: the frame's state plus their offset and
        /// velocity (toCelestial: window -> celestial directions; identity in an inertial berth).</summary>
        public static StateVector Own(ProximityFrame f, in Result r, double t, Func<Vector3D, Vector3D> toCelestial = null)
        {
            var fc = OrbitPropagation.StateAt(f.Elements, t);
            Vector3D off = toCelestial != null ? toCelestial(r.Offset) : r.Offset;
            Vector3D vel = toCelestial != null ? toCelestial(r.Velocity) : r.Velocity;
            return new StateVector(fc.Position + off, fc.Velocity + vel);
        }

        static bool Finite(Vector3D v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
    }
}
