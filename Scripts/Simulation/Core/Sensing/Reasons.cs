namespace SEAerospace.Sensing
{
    /// <summary>
    /// WHY SOMETHING CANNOT BE TARGETED, and what a target set on the ground means (the map's N / click, flight's N, the
    /// Contacts tab; game-free, tested offline). 2026-10-19: a miss said only "Nothing to target under the cursor" -
    /// whether nothing was there, it was not detected yet, or it sat somewhere it has no orbit, the player could not tell.
    /// The words never reveal a contact you have not detected: "nothing there" is said of empty space as of a hidden ship.
    /// A LANDED ship is a target (fx-flight's B1: its closest approach is to where it stands, no orbit) - said so when
    /// set; one whose place is unknown (deep inside a body: no real place) never is.
    /// </summary>
    public static class Reasons
    {
        public enum Why { None, Nothing, Unknown, NotOnView }

        /// <summary>The reason a candidate cannot be taken: its place unknown, then off this view, else none (targetable).</summary>
        public static Why Of(bool placeUnknown, bool onThisView)
            => placeUnknown ? Why.Unknown : !onThisView ? Why.NotOnView : Why.None;

        /// <summary>The message (null: none). Flight: said at the crosshair rather than the cursor.</summary>
        public static string Text(Why why, string name = null, bool flight = false)
        {
            switch (why)
            {
                case Why.Unknown:
                    return $"Can't target {name ?? "it"}: its position is unknown (no real place to meet)";
                case Why.NotOnView:
                    return "Not on this view: focus its planet first";
                case Why.Nothing:
                    return (flight ? "Nothing to target at the crosshair" : "Nothing to target under the cursor")
                           + ". Ships and rocks show once detected: get them in Telescope or Radar range";
                default: return null;
            }
        }

        /// <summary>What setting a landed ship as the target says: "Target: Red Ship · landed on Verdure (closest approach to
        /// where it stands)"; null body: the plain "Target: name".</summary>
        public static string TargetSet(string name, string landedOn)
            => landedOn == null ? "Target: " + name : $"Target: {name}  ·  landed on {landedOn} (closest approach to where it stands)";
    }
}
