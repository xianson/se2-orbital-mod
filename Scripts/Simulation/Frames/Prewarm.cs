#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// The first map open stalled ~0.1-0.3 s (seen in ModCost's peak, in a different part of the draw each time):
/// the first call of each of the map's methods is compiled then. Once the system is built, a background
/// thread compiles the map's and the HUD's methods ahead of time (RuntimeHelpers.PrepareMethod), so the
/// first open runs already compiled code.
/// </summary>
public static class Prewarm
{
    public static string Status = "not yet";
    static bool _started;

    static readonly System.Type[] Types =
    {
        typeof(CleanMap), typeof(MapPipeline), typeof(MapView), typeof(GameMap), typeof(MapCamera), typeof(MapGlobes),
        typeof(MapRingMesh), typeof(Maneuvers), typeof(RendezvousView), typeof(Porkchop), typeof(RingRocks),
        typeof(OrbitHud), typeof(OrbitDisplay), typeof(BodyMarkers), typeof(FrameMarkers), typeof(Contacts), typeof(HudPanel),
    };

    /// <summary>Client tick: start once the system is built (its types touched on this thread first).</summary>
    public static void Tick()
    {
        if (_started || !SystemHost.Built) return;
        _started = true;
        Status = "compiling";
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0, failed = 0;
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var t in Types)
            {
                var list = new List<System.Type> { t };
                foreach (var nt in t.GetNestedTypes(all)) if (!nt.ContainsGenericParameters) list.Add(nt);   // (lambdas, local functions)
                foreach (var tt in list)
                    foreach (var m in tt.GetMethods(all))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); n++; }
                        catch { failed++; }
                    }
            }
            Status = $"{n} method(s) compiled ahead in {sw.ElapsedMilliseconds} ms ({failed} skipped)";
            Log.Default?.Info("[ORBIT] prewarm: " + Status);
        });
    }
}
