namespace SEAerospace.Frames
{
    /// <summary>
    /// WHICH LEVEL OF THE MAP DRAWS A ZONE (game-free; tested offline: Tests/PlayerOrbitTests). The star's view, a body's own
    /// view, or its parent planet's view: the star's zones and a planet's L3-L5 (about the star) with the star; a planet's
    /// own space, rings, L1/L2 with the planet; a moon's L1-L5 (about its planet) with that planet - drawn round the star
    /// they were off by a whole planet's orbit (virtual sectors give moons Lagrange zones; the campaign's never did).
    /// </summary>
    public static class ZoneLevel
    {
        public enum Level { Star, Body, Parent }

        /// <summary>hostIsStar: the zone's host is the star; hostIsPlanet: a planet (the star's child), not a moon.</summary>
        public static Level Of(bool isLagrange, int point, bool hostIsStar, bool hostIsPlanet)
        {
            if (hostIsStar) return Level.Star;
            if (!isLagrange) return Level.Body;
            if (!hostIsPlanet) return Level.Parent;   // a moon's points: about its planet
            return point >= 3 ? Level.Star : Level.Body;
        }
    }
}
