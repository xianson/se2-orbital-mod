using System.IO;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// The settings tab's ADAPTER for this mod (the one Ui/ file that is not shared with the Aerodynamics Mod - see SettingsTab.cs):
/// the section's name and Tag, the log tag, the files, and the registry calls (OrbitalSettings' public API). No engine types:
/// the offline tests (Tests/SettingsUiTests) compile it with SettingsModel.cs over the real registry.
/// </summary>
public static partial class SettingsTab
{
    /// <summary>The section's id (its Tag: "ModSettings/v1/" + ModId) and its header on the tab.</summary>
    internal const string ModId = "OrbitalMod", ModTitle = "Orbital";
    const string LogTag = "[ORBIT]";
    const string ClientFile = "OrbitalMod.client.cfg", WorldFile = "OrbitalMod.world.cfg";

    static string Folder => OrbitalSettings.Folder;
    internal static string WorldPath => Path.Combine(Folder, WorldFile);
    internal static string ClientPath => Path.Combine(Folder, ClientFile);
    static int SettingsVersion => OrbitalSettings.Version;
    internal static bool CanEditWorld() => OrbitalSettings.CanEditWorld();
    internal static bool IsForced(string name) => OrbitalSettings.IsForced(name);
    static bool WorldUnsaved => OrbitalSettings.WorldUnsaved;
    static bool TryGet(string name, out double v) => OrbitalSettings.TryGet(name, out v);
    static bool Set(string name, double v, out string why) => OrbitalSettings.Set(name, v, out why);
    /// <summary>A group's every setting (any scope) back to its default.</summary>
    static int ResetGroup(string group, out string why) => OrbitalSettings.ResetGroup(null, group, out why);

    /// <summary>Every registered setting as the menu's rows, in registration order (Order).</summary>
    internal static void Gather(List<SettingsModel.Row> rows)
    {
        rows.Clear();
        var names = new List<string>();
        OrbitalSettings.Names(names);
        for (int k = 0; k < names.Count; k++)
        {
            string n = names[k];
            if (!OrbitalSettings.Info(n, out string type, out double def, out double min, out double max, out string scope, out bool reload, out string doc)) continue;
            OrbitalSettings.UiInfo(n, out string label, out string group, out string tier, out string unit, out double step, out string[] choices);
            rows.Add(new SettingsModel.Row
            {
                Name = n, Type = type, Scope = scope, Label = string.IsNullOrEmpty(label) ? n : label, Group = group, Tier = tier ?? "basic",
                Unit = unit ?? "", Doc = doc, Note = reload ? "Applies after a world reload." : null,
                Min = min, Max = max, Default = def, Step = step, Choices = choices, Order = k,
            });
        }
    }
}
