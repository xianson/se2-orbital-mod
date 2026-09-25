using System;
using OrbitalMod;

// Offline checks for Frames/FrameMath.cs (game-free). Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;

    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

    static int Main()
    {
        Console.WriteLine("[frame hysteresis]");
        Check(!FrameMath.UpdateInFrame(false, 85e3, 81e3, 89.1e3), "outside stays outside above enter radius");
        Check(FrameMath.UpdateInFrame(false, 80e3, 81e3, 89.1e3), "enters below enter radius");
        Check(FrameMath.UpdateInFrame(true, 88e3, 81e3, 89.1e3), "inside stays inside until exit radius (no flicker at 88 km)");
        Check(!FrameMath.UpdateInFrame(true, 90e3, 81e3, 89.1e3), "leaves above exit radius");

        Console.WriteLine("[proxy projection]");
        var cam = new Keen.VRage.Library.Mathematics.Vector3D(1000, -2000, 3000);
        var body = new Keen.VRage.Library.Mathematics.Vector3D(5e8, 2e8, -1e8);
        double R = 6.0e6;
        FrameMath.ProjectProxy(cam, body, R, 0, out var c0, out var r0);
        Check((c0 - body).Length() < 1e-6 && r0 == R, "clamp 0 = true position and radius");
        FrameMath.ProjectProxy(cam, body, R, 2e6, out var c1, out var r1);
        double dTrue = (body - cam).Length(), dRend = (c1 - cam).Length();
        Check(Near(dRend, 2e6, 1e-3), "clamped render distance equals clamp");
        Check(Near(Math.Asin(r1 / dRend), Math.Asin(R / dTrue), 1e-12), "angular size preserved exactly");
        var dirT = (body - cam) * (1.0 / dTrue); var dirR = (c1 - cam) * (1.0 / dRend);
        Check((dirT - dirR).Length() < 1e-12, "direction preserved");
        FrameMath.ProjectProxy(cam, cam + new Keen.VRage.Library.Mathematics.Vector3D(1e5, 0, 0), R, 2e6, out var c2, out var r2);
        Check(r2 == R, "inside the clamp nothing changes");

        Console.WriteLine("[gravity law: SE2 vanilla linear shell, Verdure]");
        var lin = new GravityLaw { G0 = 9.81, R0 = 63e3, Falloff = -1, Reach = 81e3 };
        Check(Near(lin.At(63e3), 9.81, 1e-9), "full g at r0");
        Check(Near(lin.At(72e3), 4.905, 1e-9), "half g halfway through the shell");
        Check(lin.At(81.1e3) == 0, "zero beyond reach (hard cutoff)");
        Check(Near(lin.At(50e3), 9.81, 1e-9), "clamped to g0 below r0");
        Check(!lin.IsInverseSquare, "linear shell is not Keplerian");

        Console.WriteLine("[gravity law: inverse-square patch]");
        var inv = new GravityLaw { G0 = 9.81, R0 = 63e3, Falloff = 2, Reach = 2000e3 };
        Check(inv.IsInverseSquare, "falloff 2 is Keplerian");
        Check(Near(inv.At(300e3), 9.81 * (63.0 / 300.0) * (63.0 / 300.0), 1e-12), "g(300 km) = g0 (r0/d)^2 = 0.433");
        Check(Near(inv.MuAt(300e3), 9.81 * 63e3 * 63e3, 1e-3), "mu exact and altitude-independent");
        Check(Near(inv.MuAt(300e3), inv.MuAt(900e3), 1e-3), "mu same at any radius");

        Console.WriteLine("[world GravityMultiplier]");
        var m2 = inv; m2.Multiplier = 2;
        Check(Near(m2.At(300e3), 2 * inv.At(300e3), 1e-12), "multiplier 2 doubles g (measured in game: world multiplier = 2)");
        Check(Near(m2.MuAt(300e3), 2 * inv.MuAt(300e3), 1e-3), "multiplier 2 doubles mu");
        var m0 = inv; m0.Multiplier = 0;
        Check(Near(m0.At(300e3), inv.At(300e3), 1e-12), "multiplier 0 treated as 1");

        Console.WriteLine($"\npassed={_pass} failed={_fail}");
        return _fail == 0 ? 0 : 1;
    }
}
