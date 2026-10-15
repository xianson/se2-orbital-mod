#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// (API version 2) SITES AND CONTACTS. A SITE is a frame the mod placed in the solar system on its own: a sector's encounter
/// (a station, a wreck, its anchor kept even when empty), a ring rock, a Lagrange point's cluster. A CONTACT is a grid that is
/// not the local player's (an encounter's, an NPC's, someone else's); the player KNOWS of one once their sensors saw it, and
/// TRACKS it after watching it a while (the map shows its orbit). Threads: any; the sites are a published snapshot (no lock),
/// the contacts take only their own small locks.
/// </summary>
public static partial class OrbitalApi
{
    /// <summary>(v2) Every site's frame id into your buffer (as many as fit). Returns how many sites there are.</summary>
    public static int GetSiteFrames(long[] frameIds)
    {
        // (the published array, walked by index through its list interface: no enumerator, no copy)
        if (!(EncounterFrames.Sites is IReadOnlyList<EncounterFrames.Site> l)) return 0;
        if (frameIds != null) for (int i = 0; i < l.Count && i < frameIds.Length; i++) frameIds[i] = l[i].FrameId;
        return l.Count;
    }

    /// <summary>(v2) A site: its sector's name (its target key: SetTarget, TryGetTargetState), the body its home point is about,
    /// its label (a ring rock's or a station's name; "" none), whether it is its sector's anchor (kept when empty), and whether it sits at a
    /// Lagrange point (a co-orbital cluster: it does not fly a plain conic about its body). False: no such site.</summary>
    public static bool TryGetSite(long frameId, out string sector, out string host, out string label, out bool isAnchor, out bool isLagrange)
    {
        sector = host = label = ""; isAnchor = isLagrange = false;
        var s = EncounterFrames.SiteOf(frameId);
        if (s == null) return false;
        sector = s.Sector ?? ""; host = s.Host ?? ""; label = s.Label ?? ""; isAnchor = s.Anchor;
        isLagrange = s.Home != null && s.Home.Kind == SectorHomes.Kind.Lagrange;
        return true;
    }

    /// <summary>(v2) A grid (member id) as the local player's sensors have it: whether it is a contact (not theirs), whether they
    /// know of it, and whether they track it. Their own grids are known and tracked. With the mod's contacts off (Contacts
    /// disabled) everything is known. False: no such grid in orbital's views.</summary>
    public static bool TryGetContact(long memberId, out bool isContact, out bool known, out bool tracked)
    {
        isContact = known = tracked = false;
        if (!ServerFrames.View(memberId, out var v)) return false;
        isContact = Contacts.IsContact(v);
        known = Contacts.KnownGrid(memberId);
        tracked = !isContact || Contacts.TrackedGrid(memberId);
        return true;
    }
}
