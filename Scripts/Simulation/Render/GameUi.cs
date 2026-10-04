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

    // ---- the game's speed readout (SPD) ----

    private static object _moveVm; private static PropertyInfo _speedProp; private static double _nextFind;
    public static string SpeedStatus = "-";

    /// <summary>
    /// Show a speed in the game's own SPD box (the character or cockpit HUD). On rails you sit still in
    /// your frame, so the game's readout (your physics velocity) says 0; this puts your speed about the
    /// body there instead. The view model is found once through the HUD component's screen handle (a
    /// bounded reflection search) and re-found when the HUD changes (seat, stand up).
    /// </summary>
    public static void SetHudSpeed(Keen.VRage.Core.Game.Systems.Session session, float speed)
    {
        try
        {
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (now > _nextFind)   // (every 2 s, found or not: with nothing found it searched ~400 nodes EVERY frame)
            {
                _nextFind = now + 2.0;
                object found = null;
                foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CockpitHUDComponent>())
                    found ??= FindIn(e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CockpitHUDComponent>(), "_hud");
                if (found == null)
                    foreach (var e in session.GetEntitiesOfType<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CharacterHUDComponent>())
                        found ??= FindIn(e.TryGet<Keen.Game2.Client.GameSystems.CameraSystems.Adapters.CharacterHUDComponent>(), "_hud");
                if (found != null && !ReferenceEquals(found, _moveVm)) { _moveVm = found; _speedProp = found.GetType().GetProperty("Speed"); }
                SpeedStatus = _moveVm != null ? "hud speed: " + _moveVm.GetType().Name : "hud speed: not found";
            }
            _speedProp?.SetValue(_moveVm, speed);
        }
        catch (Exception e) { SpeedStatus = "hud speed: " + (e.InnerException ?? e).Message; _moveVm = null; }
    }

    /// <summary>From a component's HUD handle, the MovementHUDScreenViewModel behind it (breadth-first, bounded).</summary>
    private static object FindIn(object comp, string field)
    {
        if (comp == null) return null;
        object root = PlanetRenderBridge.GetMember(comp, field);
        if (root == null) return null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var q = new Queue<(object o, int d)>(); q.Enqueue((root, 0));
        int budget = 400;
        while (q.Count > 0 && budget-- > 0)
        {
            var (o, d) = q.Dequeue();
            if (o == null || !seen.Add(o)) continue;
            var t = o.GetType();
            if (t.Name == "MovementHUDScreenViewModel") return o;
            var direct = t.GetProperty("MovementHUDScreenViewModel");
            if (direct != null) { try { var v = direct.GetValue(o); if (v != null) return v; } catch { } }
            if (d >= 5) continue;
            foreach (var name in new[] { "ViewModel", "DataContext", "Screen", "Target", "Value" })
            {
                var pp = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pp != null && pp.GetIndexParameters().Length == 0) { try { var v = pp.GetValue(o); if (v != null && !v.GetType().IsValueType) q.Enqueue((v, d + 1)); } catch { } }
            }
            for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                    string ns = f.FieldType.Namespace ?? "";
                    if (!(ns.StartsWith("Keen") || f.FieldType.IsInterface || f.FieldType == typeof(object))) continue;
                    try { var v = f.GetValue(o); if (v != null) q.Enqueue((v, d + 1)); } catch { }
                }
        }
        return null;
    }

    private static object _shared; private static MethodInfo _topInput;
    public static string TopInputScreen = "";

    /// <summary>
    /// The top screen taking UI input (a dialog, the terminal, a menu), by its view model's type name,
    /// or null in flight. Keys we read raw (warp) must not act while one of these has the keyboard.
    /// </summary>
    private static double _topAt = -1, _topRetry; private static string _topCached;

    private static object _topObj;
    /// <summary>The top screen's view model itself (the terminal's, while the map is open), or null.</summary>
    public static object TopScreenObject(Keen.VRage.Core.Game.Systems.Session session) { TopScreenNeedingInput(session); return _topObj; }

    public static string TopScreenNeedingInput(Keen.VRage.Core.Game.Systems.Session session)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - _topAt < 0.03) return _topCached;            // once a frame is enough
        if (_shared == null && now < _topRetry) return null;    // failed lately: not every frame
        _topAt = now;
        _topCached = TopScreenUncached(session);
        if (_shared == null) _topRetry = now + 5;
        return _topCached;
    }

    private static string TopScreenUncached(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            if (_shared == null) { _shared = Service(session, "Keen.Game2.Client.UI.Library.SharedUIComponent"); _topInput = _shared?.GetType().GetMethod("TryGetTopScreenNeedingInput"); }
            object vm = _topInput?.Invoke(_shared, null);
            _topObj = vm;
            TopInputScreen = vm?.GetType().Name ?? "";
            return vm?.GetType().Name;
        }
        catch { _shared = null; return null; }
    }

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
