using System.Collections;
using System.Reflection;
using System.Globalization;
using Keen.Game2.Client.WorldObjects;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Gravity;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>DEV HARNESS helpers: render stats, private calls, engine screenshots, another mod's statics.</summary>
public static partial class PlanetRenderBridge
{
    /// <summary>Engine screenshots leave out the 2D UI (HUD, terminal); 3D labels stay.</summary>
    public static bool ShotWithoutUi = true;

    /// <summary>DEV: the render stats (ms, average/max over the current stats window): GPU, render thread, main thread.</summary>
    public static string FrameStats(Keen.VRage.Core.Game.Systems.Session session)
    {
        try
        {
            Assembly render = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "VRage.Render") { render = a; break; }
            Type t = render?.GetType("Keen.VRage.Render.SessionComponents.StatsRecorderSessionComponent");
            if (t == null) return "no stats type";
            object comps = session.SessionComponents;
            MethodInfo tryGet = null;
            foreach (var m in comps.GetType().GetMethods()) if (m.Name == "TryGet" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { tryGet = m; break; }
            object rec = tryGet?.MakeGenericMethod(t).Invoke(comps, null);
            object st = rec?.GetType().GetProperty("ImmediateStats")?.GetValue(rec);
            if (st == null)
            {
                // not in this session: every recorder registers with the engine's StatsRecorderEngineComponent
                object engine = typeof(Keen.VRage.Core.VRageCore).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                                ?.GetType().GetProperty("Engine")?.GetValue(typeof(Keen.VRage.Core.VRageCore).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null));
                Type te = render.GetType("Keen.VRage.Render.EngineComponents.StatsRecorderEngineComponent");
                object ec = null;
                if (engine != null && te != null)
                    foreach (var m in engine.GetType().GetMethods())
                        if (m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                        {
                            try { ec = m.MakeGenericMethod(te).Invoke(engine, null); } catch (Exception) { ec = null; }
                            if (ec != null) break;
                        }
                if (ec == null) return "no stats (engine " + (engine == null ? "null" : engine.GetType().Name) + ", recorder component not found)";
                var list = ec?.GetType().GetField("_statsRecorders", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ec) as System.Collections.IEnumerable;
                int nrec = 0;
                if (list != null) foreach (var r in list) { nrec++; st = r?.GetType().GetProperty("ImmediateStats")?.GetValue(r); if (st != null) break; }
                if (st == null) return $"no stats ({nrec} recorders registered)";
            }
            if (st == null) return "no stats";
            string F(string field)
            {
                FieldInfo f = st.GetType().GetField(field);
                object v = f.GetValue(st);   // a boxed copy: Calculate works on the copy (shares the buffer)
                v.GetType().GetMethod("Calculate").Invoke(v, null);
                double avg = (double)v.GetType().GetProperty("Average").GetValue(v), max = (double)v.GetType().GetProperty("Max").GetValue(v);
                return $"{avg:F1}/{max:F0}";
            }
            return $"gpu {F("GpuTimeWork")} render {F("RenderThreadTimeWork")} main {F("MainThreadTime")} ms (avg/max)";
        }
        catch (Exception e) { return "stats: " + e.Message; }
    }

    /// <summary>DEV: call a private parameterless method (the game's own input handlers).</summary>
    public static bool CallPrivate(object target, string method)
    {
        try
        {
            var m = target?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (m == null) return false;
            m.Invoke(target, null);
            return true;
        }
        catch (Exception e) { WarnOnce("call-" + method, method + " failed: " + Inner(e)); return false; }
    }

    /// <summary>
    /// DEV HARNESS: an ENGINE screenshot (RenderContracts.GetMainTarget().TakeScreenshotAsync), the
    /// path the blueprint tool uses. Rendered by the engine into the game's temp folder, so it works
    /// when desktop capture (GDI CopyFromScreen) fails because the display is asleep.
    /// Returns the absolute path it will be written to, or null.
    /// </summary>
    public static string EngineScreenshot(string name)
    {
        try
        {
            if (!ResolveRender()) return null;
            object target = _contracts.GetType().GetMethod("GetMainTarget", Type.EmptyTypes)?.Invoke(_contracts, null);
            if (target == null) { WarnOnce("shot", "GetMainTarget unavailable"); return null; }
            var handle = Keen.VRage.Library.Filesystem.FileSystem.Temp.GetFileHandleWritable(name);
            MethodInfo take = null;
            foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "TakeScreenshotAsync" && m.GetParameters().Length == 5) { take = m; break; }
            if (take == null) { WarnOnce("shot2", "TakeScreenshotAsync not found"); return null; }
            take.Invoke(target, new object[] { handle, null, null, ShotWithoutUi, true });
            // GetAbsolutePath throws until the file exists; the temp root is the game's Temp folder.
            try { return handle.GetAbsolutePath(); }
            catch { return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "Temp", name); }
        }
        catch (Exception e) { Log.Default?.Warning($"[ORBIT] engine screenshot failed: {Inner(e)}"); return null; }
    }

    /// <summary>DEV: read (value null) or set a static field or property of another mod's type (bool, int, float,
    /// double, string), e.g. the aero mod's tuning knobs and counters.</summary>
    public static string ForeignValue(string typeName, string member, string value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FieldInfo f = t.GetField(member, S); PropertyInfo p = f == null ? t.GetProperty(member, S) : null;
                Type ft = f?.FieldType ?? p?.PropertyType;
                if (ft == null) return $"{typeName}.{member}: no such static";
                if (value != null)
                {
                    object v = ft == typeof(bool) ? (object)(value == "on" || value == "true" || value == "1")
                             : ft == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(float) ? float.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(double) ? double.Parse(value, CultureInfo.InvariantCulture)
                             : ft == typeof(string) ? value : null;
                    if (v == null) return $"{typeName}.{member}: {ft.Name} not settable";
                    if (f != null) f.SetValue(null, v); else p.SetValue(null, v);
                }
                object now = f != null ? f.GetValue(null) : p.GetValue(null);
                return $"{typeName}.{member}={Convert.ToString(now, CultureInfo.InvariantCulture)}";
            }
            return $"{typeName} not loaded";
        }
        catch (Exception e) { return "failed: " + Inner(e); }
    }

    /// <summary>
    /// DEV HARNESS: switch another mod's static bool at runtime (e.g. AeroMod.AeroSwitch.Enabled). Runtime only; the other mod's files are untouched. Returns a description.
    /// </summary>
    public static string SetForeignFlag(string typeName, string field, bool value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                FieldInfo f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(bool)) return $"{typeName}.{field} not a static bool";
                f.SetValue(null, value);
                return $"{typeName}.{field}={value} (in {a.GetName().Name})";
            }
            return $"{typeName} not loaded";
        }
        catch (Exception e) { return "failed: " + Inner(e); }
    }

    /// <summary>DEV HARNESS: as SetForeignFlag, a static string field (null clears it).</summary>
    public static string SetForeignString(string typeName, string field, string value)
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(typeName); } catch { continue; }
                if (t == null) continue;
                FieldInfo f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(string)) return $"{typeName}.{field} not a static string";
                f.SetValue(null, value);
                return $"{typeName}.{field}={value ?? "null"} (in {a.GetName().Name})";
            }
            return $"{typeName} not loaded";
        }
        catch (Exception e) { return "failed: " + Inner(e); }
    }
}
