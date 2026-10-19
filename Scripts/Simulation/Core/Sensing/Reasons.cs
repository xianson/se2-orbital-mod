namespace SEAerospace.Sensing
{
    /// <summary>
    /// WHY SOMETHING CANNOT BE TARGETED (the map's N / click, flight's N; game-free, tested offline). 2026-10-19: a miss
    /// said only "Nothing to target under the cursor" - whether nothing was there, or it was not detected yet, or it sat
    /// on a planet, the player could not tell. The words never reveal a contact you have not detected: "not detected"
    /// is said of empty space as of a hidden ship.
    /// </summary>
    public static class Reasons
    {
        public enum Why { None, Nothing, Landed, NotOnView }

        /// <summary>The reason for a candidate: landed (a body's name) wins, then off this view, else none (targetable).</summary>
        public static Why Of(string landedOn, bool onThisView)
            => landedOn != null ? Why.Landed : !onThisView ? Why.NotOnView : Why.None;

        /// <summary>The message. Flight: said at the crosshair rather than the cursor.</summary>
        public static string Text(Why why, string name = null, string body = null, bool flight = false)
        {
            switch (why)
            {
                case Why.Landed:
                    return $"{name ?? "It"} is landed on {body ?? "a planet"}: no orbit to meet - target {body ?? "the planet"}, or fly to it";
                case Why.NotOnView:
                    return "Not on this view: focus its planet first";
                case Why.Nothing:
                    return (flight ? "Nothing to target at the crosshair" : "Nothing to target under the cursor")
                           + ". Ships and rocks show once detected: get them in Telescope or Radar range";
                default: return null;
            }
        }
    }
}
