using System;

namespace SEAerospace.Frames
{
    /// <summary>
    /// WHERE THE TERMINAL'S LAYOUT IS ON SCREEN (game-free; tested offline: Tests/PlayerOrbitTests). The game lays the terminal
    /// out in a fixed 1920 x 1080 design cell, centred between flexible margins (TerminalScreen: grid columns *, 1920 px, *;
    /// rows *, 1080 px, *; its tab row 160 px), in UI design units = screen pixels x DesignResolutionScale. The map's own
    /// drawing was placed as fractions of the whole screen - right at 16:9 only: on 16:10 it ran under the tab row, on 21:9
    /// onto the left panel. Here a fraction of the DESIGN cell (as all the map's numbers were measured at 16:9) becomes screen
    /// pixels at any shape and scale; at 16:9 it is the same number as before.
    /// </summary>
    public struct TerminalLayout
    {
        public const double CellW = 1920, CellH = 1080;
        /// <summary>The terminal's tab row ends here (a fraction of the cell's height: 160 / 1080).</summary>
        public const double TabRowBottom = 160.0 / 1080.0;

        public double W, H, Sx, Sy;   // screen pixels; design units per screen pixel

        /// <summary>For a screen of w x h pixels and the game's design scale (design units per pixel; NaN or 0: unknown - the
        /// cell fitted whole, uniformly).</summary>
        public static TerminalLayout For(double w, double h, double sx = double.NaN, double sy = double.NaN)
        {
            if (!(sx > 0) || !(sy > 0) || double.IsNaN(sx) || double.IsNaN(sy))
            {
                double s = Math.Min(w / CellW, h / CellH);   // (screen pixels per design unit, the cell fitted whole)
                sx = sy = s > 0 ? 1 / s : 1;
            }
            return new TerminalLayout { W = w, H = h, Sx = sx, Sy = sy };
        }

        /// <summary>Screen x of a fraction of the cell's width.</summary>
        public double X(double fx) => ((W * Sx - CellW) / 2 + fx * CellW) / Sx;
        /// <summary>Screen y of a fraction of the cell's height.</summary>
        public double Y(double fy) => ((H * Sy - CellH) / 2 + fy * CellH) / Sy;
        /// <summary>A length of a fraction of the cell's width, in screen pixels.</summary>
        public double LenX(double fx) => fx * CellW / Sx;
        /// <summary>A length of a fraction of the cell's height, in screen pixels.</summary>
        public double LenY(double fy) => fy * CellH / Sy;
    }
}
