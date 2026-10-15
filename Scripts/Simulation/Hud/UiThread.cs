using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// Avalonia by reflection (scripts cannot reference it): the UI thread's dispatcher, a visual's descendants, root, bounds,
/// position in another, visibility and opacity, and an Animation-priority RenderTransform held. Everything that touches a
/// control must run on the UI thread (Post).
/// </summary>
public static class UiThread
{
    static bool _looked; static object _dispatcher; static MethodInfo _post; static object[] _postDefaults;
    static MethodInfo _descendants, _translatePoint, _setValue;
    static Type _pointType, _translateType; static object _renderTransformProp, _animPriority;
    /// <summary>Where TranslatePoint was found (status).</summary>
    public static string TranslateFrom = "-";

    static void Look()
    {
        if (_looked) return; _looked = true;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name != "Avalonia.Base") continue;
            var dt = a.GetType("Avalonia.Threading.Dispatcher");
            _dispatcher = dt?.GetProperty("UIThread")?.GetValue(null);
            foreach (var m in dt?.GetMethods() ?? Array.Empty<MethodInfo>())
            {
                var ps = m.GetParameters();
                if (m.Name != "Post" || ps.Length == 0 || ps[0].ParameterType != typeof(Action)) continue;
                _post = m; _postDefaults = new object[ps.Length];
                for (int k = 1; k < ps.Length; k++)
                    _postDefaults[k] = ps[k].HasDefaultValue && ps[k].DefaultValue != null ? ps[k].DefaultValue : ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                break;
            }
            // (GetVisualDescendants is Avalonia.VisualTree.VisualExtensions'; TranslatePoint is Avalonia.VisualExtensions' -
            //  looked for in the first only, it was never found and every rectangle came out 0x0)
            foreach (var vn in new[] { "Avalonia.VisualTree.VisualExtensions", "Avalonia.VisualExtensions" })
                foreach (var m in a.GetType(vn)?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
                {
                    if (m.Name == "GetVisualDescendants" && m.GetParameters().Length == 1) _descendants ??= m;
                    if (m.Name == "TranslatePoint" && m.GetParameters().Length == 3) _translatePoint ??= m;
                }
            // (not there: any public static class of Avalonia.Base with a TranslatePoint(visual, point, relativeTo))
            if (_translatePoint == null)
                try
                {
                    foreach (var t in a.GetExportedTypes())
                    {
                        if (!t.IsAbstract || !t.IsSealed) continue;
                        var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "TranslatePoint" && x.GetParameters().Length == 3);
                        if (m != null) { _translatePoint = m; break; }
                    }
                }
                catch { }
            TranslateFrom = _translatePoint?.DeclaringType?.FullName ?? "not found";
            _pointType = a.GetType("Avalonia.Point");
            _translateType = a.GetType("Avalonia.Media.TranslateTransform");
            var visual = a.GetType("Avalonia.Visual");
            _renderTransformProp = visual?.GetField("RenderTransformProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var prio = a.GetType("Avalonia.Data.BindingPriority");
            _animPriority = prio != null ? Enum.Parse(prio, "Animation") : null;
            for (var t = visual; t != null && _setValue == null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "SetValue" && !m.IsGenericMethod && ps.Length == 3 && ps[2].ParameterType == prio && ps[1].ParameterType == typeof(object)) { _setValue = m; break; }
                }
            break;
        }
    }

    /// <summary>Run on the UI thread (false: no dispatcher).</summary>
    public static bool Post(Action a)
    {
        Look();
        if (_dispatcher == null || _post == null) return false;
        var args = (object[])_postDefaults.Clone(); args[0] = a;
        try { _post.Invoke(_dispatcher, args); return true; } catch { return false; }
    }

    public static IEnumerable<object> Descendants(object v)
    {
        Look();
        if (v == null || _descendants == null) yield break;
        System.Collections.IEnumerable all = null;
        try { all = _descendants.Invoke(null, new[] { v }) as System.Collections.IEnumerable; } catch { }
        if (all == null) yield break;
        foreach (var d in all) if (d != null) yield return d;
    }

    public static object Descendant(object v, string typeName)
    {
        foreach (var d in Descendants(v)) if (d.GetType().Name == typeName) return d;
        return null;
    }

    public static object VisualRoot(object v) { try { return v == null ? null : Prop(v.GetType(), "VisualRoot")?.GetValue(v); } catch { return null; } }
    public static bool IsVisible(object v) { try { return v == null ? false : Prop(v.GetType(), "IsEffectivelyVisible")?.GetValue(v) is bool b ? b : Prop(v.GetType(), "IsVisible")?.GetValue(v) is bool c && c; } catch { return false; } }
    public static float Opacity(object v) { try { return v == null ? 1f : Prop(v.GetType(), "Opacity")?.GetValue(v) is double d ? (float)d : 1f; } catch { return 1f; } }
    /// <summary>A descendant of this type name and control name (x:Name), or null.</summary>
    public static object Named(object v, string typeName, string name)
    {
        foreach (var d in Descendants(v))
            if (d.GetType().Name == typeName && Prop(d.GetType(), "Name")?.GetValue(d) as string == name) return d;
        return null;
    }

    /// <summary>A property by name, the most derived one (GetProperty threw AmbiguousMatchException for Bounds - declared on
    /// more than one class of the hierarchy - and every rectangle came out 0x0).</summary>
    static PropertyInfo Prop(Type t, string name)
    {
        for (var x = t; x != null; x = x.BaseType)
        {
            var p = x.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    public static (float x, float y, float w, float h) Bounds(object v)
    {
        try
        {
            var r = v == null ? null : Prop(v.GetType(), "Bounds")?.GetValue(v);
            if (r == null) return default;
            var t = r.GetType();
            return ((float)(double)Prop(t, "X").GetValue(r), (float)(double)Prop(t, "Y").GetValue(r), (float)(double)Prop(t, "Width").GetValue(r), (float)(double)Prop(t, "Height").GetValue(r));
        }
        catch { return default; }
    }

    /// <summary>v's rectangle in root's coordinates (UI units).</summary>
    public static (float x, float y, float w, float h) RectIn(object v, object root)
    {
        Look();
        var b = Bounds(v);
        try
        {
            if (_translatePoint == null || _pointType == null) return default;
            // (both corners: a Viewbox or a transform between them scales the size too)
            var r0 = _translatePoint.Invoke(null, new[] { v, Activator.CreateInstance(_pointType, 0.0, 0.0), root });
            var r1 = _translatePoint.Invoke(null, new[] { v, Activator.CreateInstance(_pointType, (double)b.w, (double)b.h), root });
            if (r0 == null || r1 == null) return default;
            var pt = r0.GetType();
            double x0 = (double)Prop(pt, "X").GetValue(r0), y0 = (double)Prop(pt, "Y").GetValue(r0);
            double x1 = (double)Prop(pt, "X").GetValue(r1), y1 = (double)Prop(pt, "Y").GetValue(r1);
            return ((float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Abs(x1 - x0), (float)Math.Abs(y1 - y0));
        }
        catch { return default; }
    }

    /// <summary>A RenderTransform translate held on v at Animation priority (above the game's own); dispose to let go.</summary>
    public static IDisposable HoldTranslate(object v, double x, double y)
    {
        Look();
        if (v == null || _setValue == null || _translateType == null || _renderTransformProp == null || _animPriority == null) return null;
        try
        {
            var tr = Activator.CreateInstance(_translateType, x, y);
            return _setValue.Invoke(v, new[] { _renderTransformProp, tr, _animPriority }) as IDisposable;
        }
        catch { return null; }
    }

    /// <summary>Breadth-first through fields and a few view properties, the first object whose type is so named.</summary>
    public static object FindByTypeName(object root, string typeName, int maxDepth, int budget)
    {
        if (root == null) return null;
        // (the walk's set and queue reused, and each type's fields listed once: GetFields copies its array on every call -
        //  for every object of a 400-object walk, twice a second, on the game thread, 2026-10-14)
        var seen = _seen; seen.Clear();
        var q = _queue; q.Clear(); q.Enqueue((root, 0));
        try
        {
            while (q.Count > 0 && budget-- > 0)
            {
                var (o, d) = q.Dequeue();
                if (o == null || o is string || !seen.Add(o)) continue;
                var t = o.GetType();
                if (t.IsPrimitive || t.IsEnum) continue;
                if (t.Name == typeName) return o;
                if (d >= maxDepth) continue;
                foreach (var f in WalkFields(t))
                {
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v != null) q.Enqueue((v, d + 1));
                }
            }
            return null;
        }
        finally { seen.Clear(); q.Clear(); }   // (nothing of the HUD kept alive between walks)
    }
    [ThreadStatic] static HashSet<object> _seenTs; [ThreadStatic] static Queue<(object o, int d)> _queueTs;
    static HashSet<object> _seen => _seenTs ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
    static Queue<(object o, int d)> _queue => _queueTs ??= new Queue<(object o, int d)>();
    static readonly Dictionary<Type, FieldInfo[]> _walkFields = new Dictionary<Type, FieldInfo[]>();
    /// <summary>The fields the walk follows (not primitive, string, enum or pointer), the type's own first, then its bases'.</summary>
    static FieldInfo[] WalkFields(Type t)
    {
        lock (_walkFields)
        {
            if (_walkFields.TryGetValue(t, out var fs)) return fs;
            var l = new List<FieldInfo>();
            for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!(f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum || f.FieldType.IsPointer)) l.Add(f);
            _walkFields[t] = fs = l.ToArray();
            return fs;
        }
    }
}
