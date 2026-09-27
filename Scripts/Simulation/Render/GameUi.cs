using System.Reflection;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Library.Localization;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The game's REAL UI, driven from the mod (reflection is confined to Render/: the view-models derive
/// from VRage.UI types mod code cannot reference by name):
///  - a live notification CARD (NotificationViewModel via InGameUI.DisplayNotification): title and
///    content are plain strings the view follows, so the card is updated in place (the orbit readout);
///  - TOASTS (IUserMessages.DisplayMessage, keyed so a new one replaces the last: warp, route status);
///  - the numeric input DIALOG (TextInputDialogViewModel with the game's NumericInputDialog
///    definition, SharedUIComponent.ShowDialog): typing an exact delta-v.
/// </summary>
public static class GameUi
{
    public static string LastError = "";

    // ── the card ──
    public sealed class Card { internal object Vm; internal string Title, Content; }

    /// <summary>Show a card that stays until closed; null if the game's HUD is not available.</summary>
    public static Card ShowCard(Keen.VRage.Core.Game.Systems.Session session, string title, string content)
    {
        try
        {
            object ui = Service(session, "Keen.Game2.Client.UI.InGame.InGameUI");
            if (ui == null) { LastError = "no InGameUI"; return null; }
            var vmType = FindType("Keen.Game2.Client.UI.Shared.SystemNotification.NotificationViewModel");
            ConstructorInfo ctor = null;
            foreach (var c in vmType.GetConstructors()) if (c.GetParameters().Length == 4 && c.GetParameters()[0].ParameterType == typeof(string)) ctor = c;
            object vm = ctor.Invoke(new object[] { title, content, null, null });
            var show = ui.GetType().GetMethod("DisplayNotification");
            object handle = show.Invoke(ui, new[] { vm });
            if (handle == null) { LastError = "card refused (menu open?)"; return null; }
            return new Card { Vm = vm, Title = title, Content = content };
        }
        catch (Exception e) { LastError = "card: " + (e.InnerException ?? e).Message; return null; }
    }

    /// <summary>Change the card's text in place (only when it changed).</summary>
    public static void UpdateCard(Card c, string title, string content)
    {
        if (c?.Vm == null) return;
        try
        {
            if (title != c.Title) { c.Vm.GetType().GetProperty("Title").SetValue(c.Vm, title); c.Title = title; }
            if (content != c.Content) { c.Vm.GetType().GetProperty("Content").SetValue(c.Vm, content); c.Content = content; }
        }
        catch (Exception e) { LastError = "card update: " + (e.InnerException ?? e).Message; }
    }

    public static void CloseCard(Card c)
    {
        try { (c?.Vm as IDisposable)?.Dispose(); } catch { }
    }

    public static bool IsOpen(Card c)
    {
        try { return c?.Vm != null && c.Vm.GetType().GetProperty("ScreenViewModel")?.GetValue(c.Vm) != null; } catch { return false; }
    }

    // ── toasts ──
    private static readonly Dictionary<string, IDisposable> _toasts = new Dictionary<string, IDisposable>();

    /// <summary>A HUD toast; a toast with the same key replaces the previous one.</summary>
    public static void Toast(Keen.VRage.Core.Game.Systems.Session session, string key, string title, string text, double seconds = 4)
    {
        try
        {
            var msgs = session.Get<IUserMessages>();
            if (msgs == null) { LastError = "no IUserMessages"; return; }
            if (_toasts.TryGetValue(key, out var old)) { try { old.Dispose(); } catch { } _toasts.Remove(key); }
            var m = new UserMessage
            {
                TitleId = LocKey.FromString(title), ContentId = LocKey.FromString(text),
                Type = UserMessageType.Game, TextTimeout = TimeSpan.FromSeconds(seconds),
                MessageKey = Keen.VRage.Library.Utils.StringId.Get("orbital." + key),
            };
            var h = msgs.DisplayMessage(m);
            if (h != null) _toasts[key] = h;
        }
        catch (Exception e) { LastError = "toast: " + (e.InnerException ?? e).Message; }
    }

    // ── the numeric dialog ──

    /// <summary>The game's numeric input dialog; onOk gets the typed number (validated).</summary>
    public static bool NumberDialog(Keen.VRage.Core.Game.Systems.Session session, string title, double current, Action<double> onOk)
    {
        try
        {
            object shared = Service(session, "Keen.Game2.Client.UI.Library.SharedUIComponent");
            if (shared == null) { LastError = "no SharedUIComponent"; return false; }
            object cfg = shared.GetType().GetProperty("DialogsConfiguration").GetValue(shared);
            object def = cfg.GetType().GetProperty("NumericInputDialog")?.GetValue(cfg) ?? cfg.GetType().GetField("NumericInputDialog")?.GetValue(cfg);
            var vmType = FindType("Keen.Game2.Client.UI.Library.Dialogs.TextInputDialog.TextInputDialogViewModel") ?? FindTypeBySimpleName("TextInputDialogViewModel");
            ConstructorInfo ctor = null;
            foreach (var c in vmType.GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length >= 3 && ps[1].ParameterType == typeof(string) && ps[2].ParameterType == typeof(bool)) { ctor = c; break; }
            }
            var args = new object[ctor.GetParameters().Length];
            args[0] = def; args[1] = current.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); args[2] = true;
            for (int i = 3; i < args.Length; i++) args[i] = null;
            object vm = ctor.Invoke(args);
            Action<string> confirm = s =>
            {
                if (double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) onOk(v);
            };
            Func<string, string> validate = s =>
                double.TryParse((s ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? null : "Enter a number (m/s)";
            vmType.GetProperty("ConfirmAction").SetValue(vm, confirm);
            vmType.GetProperty("InputValidator")?.SetValue(vm, validate);
            vmType.GetProperty("TitleOverride")?.SetValue(vm, title);
            MethodInfo showDialog = null;
            foreach (var m in shared.GetType().GetMethods()) if (m.Name == "ShowDialog" && m.GetParameters().Length == 1) showDialog = m;
            showDialog.Invoke(shared, new[] { vm });
            return true;
        }
        catch (Exception e) { LastError = "dialog: " + (e.InnerException ?? e).Message; return false; }
    }

    // ── lookup ──

    private static object Service(Keen.VRage.Core.Game.Systems.Session session, string typeName)
    {
        var t = FindType(typeName);
        if (t == null) return null;
        foreach (var m in session.GetType().GetMethods())
            if (m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                try { return m.MakeGenericMethod(t).Invoke(session, null); } catch { }
        // extension methods (e.g. GameEntityExtensions.TryGet<T>(this Session))
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var ty in types)
            {
                if (!ty.IsSealed || !ty.IsAbstract) continue;
                foreach (var m in ty.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if ((m.Name != "TryGet" && m.Name != "Get") || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsInstanceOfType(session)) continue;
                    try { var r = m.MakeGenericMethod(t).Invoke(null, new object[] { session }); if (r != null) return r; } catch { }
                }
            }
        }
        return null;
    }

    private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
    private static Type FindType(string fullName)
    {
        if (_types.TryGetValue(fullName, out var t)) return t;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(fullName);
            if (t != null) break;
        }
        _types[fullName] = t;
        return t;
    }

    private static Type FindTypeBySimpleName(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var ty in types) if (ty.Name == name) return ty;
        }
        return null;
    }
}
