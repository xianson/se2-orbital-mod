namespace SEAerospace.Flight
{
    /// <summary>
    /// A maneuver's EXACT VALUES as typed (engine-free; Tests/FlightTests): "prograde, normal, radial" in m/s - commas,
    /// semicolons or spaces between them, a decimal point, missing trailing values 0 ("100" is 100 m/s prograde).
    /// 2026-10-18 (the UI audit, B4): README_PLAYERS promised "type its numbers"; the dialog existed only for the dev harness.
    /// </summary>
    public static class BurnNumbers
    {
        /// <summary>The largest delta-v one maneuver may take per axis (m/s).</summary>
        public const double MaxDv = 100000;

        public static string Format(double pro, double nor, double rad)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0}, {2:0.0}", pro, nor, rad);

        /// <summary>Parse the typed text; false with the reason (for the dialog) when it is no burn.</summary>
        public static bool TryParse(string text, out double pro, out double nor, out double rad, out string error)
        {
            pro = nor = rad = 0; error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "type prograde, normal, radial (m/s)"; return false; }
            var vals = new List<double>();
            foreach (var tok in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                    || double.IsNaN(v) || double.IsInfinity(v)) { error = $"'{tok}' is not a number"; return false; }
                vals.Add(v);
            }
            if (vals.Count == 0) { error = "type prograde, normal, radial (m/s)"; return false; }
            if (vals.Count > 3) { error = "three numbers at most: prograde, normal, radial"; return false; }
            foreach (double v in vals) if (Math.Abs(v) > MaxDv) { error = $"at most {MaxDv:N0} m/s each"; return false; }
            pro = vals[0]; nor = vals.Count > 1 ? vals[1] : 0; rad = vals.Count > 2 ? vals[2] : 0;
            return true;
        }
    }
}
