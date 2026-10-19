using SEAerospace.Flight;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

public static partial class Maneuvers
{
    // ── a maneuver's EXACT VALUES typed in (2026-10-18, the UI audit B4: README_PLAYERS promised "type its numbers"; the
    //    game's number dialog was opened only by the dev harness). Right-click a maneuver -> "Edit exact values..." ──

    /// <summary>Open the number dialog for a maneuver: "prograde, normal, radial" in m/s (BurnNumbers). False: the dialog
    /// could not open (GameUi.LastError says why).</summary>
    public static bool EditExact(Node n)
    {
        var session = _session ?? WarpControl.Session;
        if (n == null || session == null) return false;
        bool ok = GameUi.TextDialog(session, "Burn: prograde, normal, radial (m/s)", BurnNumbers.Format(n.Pro, n.Nor, n.Rad),
            text =>
            {
                // (the dialog's thread, not the client tick's: the notice is queued)
                string why = ApplyExact(n, text);
                Messages.Post(MessageKinds.Burn, why ?? $"Maneuver set: {BurnNumbers.Format(n.Pro, n.Nor, n.Rad)} m/s");
            },
            text => BurnNumbers.TryParse(text, out _, out _, out _, out string err) ? null : err);
        if (!ok) Messages.Say(MessageKinds.Burn, "Can't open the number dialog");
        return ok;
    }

    /// <summary>Apply typed values to a maneuver: the handles' own path (the values set, the node re-targeted - Edit - on the
    /// next trajectory pass), under the nodes' lock, only while the node is still planned. Null when applied, else why not.</summary>
    public static string ApplyExact(Node n, string text)
    {
        if (n == null) return "No maneuver";
        if (!BurnNumbers.TryParse(text, out double pro, out double nor, out double rad, out string err)) return "Maneuver not changed: " + err;
        lock (Nodes)
        {
            if (!Nodes.Contains(n)) return "Maneuver not changed: it is gone";
            n.Pro = pro; n.Nor = nor; n.Rad = rad;
            n.Edit();
        }
        Selected = n;
        return null;
    }
}
