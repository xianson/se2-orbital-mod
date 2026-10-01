using SEAerospace.Sensing;
using static SEAerospace.Sensing.SensorModel;

namespace SensingTest
{
    /// <summary>
    /// Offline checks of the sensor physics (Core/Sensing/SensorModel: what Contacts sees with) and of warp's
    /// store bill. No game, no framework; exit 0 = all pass. A sun at the origin, a planet between, rocks and
    /// stations at chosen places: each rule is checked on its own and against the numbers it was tuned to.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool cond, string detail = "")
        {
            if (cond) { _passed++; Console.WriteLine("   PASS " + name + (detail.Length > 0 ? " (" + detail + ")" : "")); }
            else { _failed++; Console.WriteLine("   FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }
        static void Near(string name, double got, double want, double tol) =>
            Ok(name, Math.Abs(got - want) <= tol, $"got {got:G6}, want {want:G6} +/- {tol:G3}");

        static readonly Vector3D Sun = Vector3D.Zero;
        const double AU = 1.5e10;
        static List<Body> Bodies(params Body[] extra)
        {
            var l = new List<Body> { new Body { Centre = Sun, Radius = 7e8, IsSun = true } };
            l.AddRange(extra);
            return l;
        }
        static Target Rock(Vector3D at) => new Target { At = at, Radius = 1500, Albedo = 0.15, TempK = 250 };
        static Target Station(Vector3D at) => new Target { At = at, Radius = 150, Albedo = 0.5, TempK = 290 };
        static Looker Look(Vector3D at, Kind k, double power = 1) => new Looker { At = at, Kind = k, Power = power };

        static int Main()
        {
            Console.WriteLine("Sensing (SensorModel) offline tests");
            Tuning();
            Phases();
            Shadow();
            SunExclusionRule();
            LineOfSight();
            RadarPower();
            PlanetShineRule();
            GlareRule();
            WarpStoreBill();
            TrackingRules();
            Console.WriteLine($"\n=== {_passed} passed, {_failed} failed ===");
            return _failed == 0 ? 0 : 1;
        }

        // The ranges the constants were tuned to (the design's numbers).
        static void Tuning()
        {
            Console.WriteLine("-- tuning");
            Near("optical: sunlit 1.5 km rock, full phase ~1510 km", OpticalReach(1500, 0.15, 1) / 1000, 1510, 5);
            Near("optical: 150 m station, full phase ~276 km", OpticalReach(150, 0.5, 1) / 1000, 276, 2);
            Near("infrared: 150 m warm hull ~94 km", InfraredReach(150, 290) / 1000, 93.1, 1);
            Near("radar: 150 m station ~300 km at full power", RadarReach(150, 1) / 1000, 300, 3);
            Near("radar: 1.5 km rock ~950 km at full power", RadarReach(1500, 1) / 1000, 949, 5);
            Ok("a bigger target is seen further by every sensor",
               OpticalReach(300, 0.3, 1) > OpticalReach(150, 0.3, 1) && InfraredReach(300, 290) > InfraredReach(150, 290) && RadarReach(300, 1) > RadarReach(150, 1));
            Ok("radar range grows as the 4th root of power (1/16 power: half the range)",
               Math.Abs(RadarReach(150, 1.0 / 16) * 2 - RadarReach(150, 1)) < 1e-6);
        }

        // Phase: full with the sun behind you, a glint with it behind the target.
        static void Phases()
        {
            Console.WriteLine("-- phase");
            var t = new Vector3D(AU, 0, 0);
            Near("sun behind the observer: full (1)", Phase(new Vector3D(AU - 1e6, 0, 0), t, Sun), 1, 1e-9);
            Near("sun behind the target: a glint (0.05)", Phase(new Vector3D(AU + 1e6, 0, 0), t, Sun), 0.05, 1e-9);
            Near("side on: half", Phase(new Vector3D(AU, 1e6, 0), t, Sun), 0.5, 1e-6);
            // the telescope sees a lit rock much further looking down-sun than into it
            var bodies = Bodies();
            var rock = Rock(t);
            var behind = Look(new Vector3D(AU - 1.2e6, 0, 0), Kind.Telescope);   // sun behind you, 1200 km
            var facing = Look(new Vector3D(AU + 1.2e6, 0, 0), Kind.Telescope);   // looking toward the sun
            Ok("1200 km, sun behind you: seen", SeenBy(new[] { behind }, rock, bodies, Sun) == 0);
            Ok("1200 km, looking into the sun's side of it: not seen (a crescent, and infrared is short)", SeenBy(new[] { facing }, rock, bodies, Sun) == -1);
        }

        // Shadow: a planet between the target and the sun blanks the optical channel; infrared still sees.
        static void Shadow()
        {
            Console.WriteLine("-- shadow");
            var planet = new Body { Centre = new Vector3D(AU, 0, 0), Radius = 60000 };
            var bodies = Bodies(planet);
            var inShadow = new Vector3D(AU + 150000, 0, 0);          // behind the planet from the sun
            var sunlit = new Vector3D(AU, 0, 150000);                 // beside it, in the light
            var eye = new Vector3D(AU + 150000, 400000, 0);           // 400 km off, clear of the planet
            Ok("the lit rock is not in shadow", Clear(sunlit, Sun, bodies, ignoreSun: true));
            Ok("the rock behind the planet is in shadow", !Clear(inShadow, Sun, bodies, ignoreSun: true));
            var tel = new[] { Look(eye, Kind.Telescope) };
            Ok("a rock in shadow at 400 km: its infrared (~690 km) still sees it", SeenBy(tel, Rock(inShadow), bodies, Sun) == 0);
            var farEye = new Vector3D(AU + 150000, 900000, 0);        // 900 km: beyond infrared, within optical if lit
            Ok("the same rock at 900 km in shadow: not seen", SeenBy(new[] { Look(farEye, Kind.Telescope) }, Rock(inShadow), bodies, Sun) == -1);
            Ok("... but by radar yes (it needs no light)", SeenBy(new[] { Look(farEye, Kind.Radar) }, Rock(inShadow), bodies, Sun) == 0);
        }

        // No optical within 15 degrees of the sun (infrared unaffected).
        static void SunExclusionRule()
        {
            Console.WriteLine("-- sun exclusion");
            var bodies = Bodies();
            var obs = new Vector3D(AU, 0, 0);
            var nearSunDir = new Vector3D(AU - 1.0e6, 1.0e6 * Math.Tan(10 * Math.PI / 180), 0);   // 10 deg off the sun, ~1000 km
            var clearDir = new Vector3D(AU, 1.0e6, 0);                                              // 90 deg off (side-lit, half phase)
            var l = new Looker { At = obs, Kind = Kind.Telescope };
            Ok("10 deg from the sun: optical blind (reach = infrared only)",
               Math.Abs(Reach(l, Rock(nearSunDir), Sun, true) - InfraredReach(1500, 250)) < 1e-6);
            Ok("90 deg from the sun: optical works (reach > infrared)", Reach(l, Rock(clearDir), Sun, true) > InfraredReach(1500, 250));
        }

        // A planet between looker and target blocks everything, radar and eyes too; the ground under a base does not.
        static void LineOfSight()
        {
            Console.WriteLine("-- line of sight");
            var planet = new Body { Centre = new Vector3D(AU, 0, 0), Radius = 60000 };
            var bodies = Bodies(planet);
            var a = new Vector3D(AU - 100000, 0, 0);
            var b = new Vector3D(AU + 100000, 0, 0);
            Ok("through the planet: blocked", !Clear(a, b, bodies));
            Ok("... radar too", SeenBy(new[] { Look(a, Kind.Radar) }, Station(b), bodies, Sun) == -1);
            Ok("past its limb: clear", Clear(a + new Vector3D(0, 70000, 0), b + new Vector3D(0, 70000, 0), bodies));
            var onGround = new Vector3D(AU, 60000, 0);                 // a base on the surface
            var above = new Vector3D(AU, 75000, 0);
            Ok("a base on the ground is not hidden by the ground under it", Clear(above, onGround, bodies));
            Ok("eyes: 20 km, no further", SeenBy(new[] { Look(a, Kind.Eyes) }, Station(a + new Vector3D(0, 0, 19000)), bodies, Sun) == 0
                                           && SeenBy(new[] { Look(a, Kind.Eyes) }, Station(a + new Vector3D(0, 0, 21000)), bodies, Sun) == -1);
        }

        static void RadarPower()
        {
            Console.WriteLine("-- radar power");
            var bodies = Bodies();
            var o = new Vector3D(AU, 0, 0);
            var st = Station(o + new Vector3D(0, 250000, 0));   // 250 km
            Ok("250 km station: full power sees it", SeenBy(new[] { Look(o, Kind.Radar, 1) }, st, bodies, Sun) == 0);
            Ok("250 km station: 25% power does not (~212 km)", SeenBy(new[] { Look(o, Kind.Radar, 0.25) }, st, bodies, Sun) == -1);
            Ok("off (0 power): nothing", Reach(Look(o, Kind.Radar, 0), st, Sun, true) == 0);
        }

        // Planet-shine: a planet's day side lights what is near it; its night side and a far planet do not matter.
        static void PlanetShineRule()
        {
            Console.WriteLine("-- planet-shine");
            var P = new Vector3D(AU, 0, 0);
            var planet = new Body { Centre = P, Radius = 60000, Albedo = 0.3 };
            var bodies = Bodies(planet);
            // a rock just sunward of the planet, over its noon (the planet's lit face toward it), seen from beyond it:
            // back-lit by the sun (a glint), front-lit by the planet
            var rock = Rock(P + new Vector3D(-80000, 0, 0));
            // seen from beyond the planet's side, 50 deg off the axis (the sight line clears the planet): the sun
            // lights the face away from you, the planet the face toward you
            var eye = rock.At + new Vector3D(1.0e6 * Math.Cos(50 * Math.PI / 180), 1.0e6 * Math.Sin(50 * Math.PI / 180), 0);
            double shine = PlanetShine(eye, rock.At, Sun, bodies);
            Ok("near a planet's day side: planet-shine lights the rock", shine > 0.05, $"{shine:F3} of full sun");
            Ok("... so a crescent rock is seen further than by sunlight alone",
               Reach(new Looker { At = eye, Kind = Kind.Telescope }, rock, Sun, true, bodies) > Reach(new Looker { At = eye, Kind = Kind.Telescope }, rock, Sun, true, null));
            // high above: (R/d)^2 makes it negligible
            var high = Rock(P + new Vector3D(-363000, 0, 0));
            double shineHigh = PlanetShine(high.At + new Vector3D(0, 5e5, 0), high.At, Sun, bodies);
            Ok("300 km up: planet-shine is a few percent at most", shineHigh < 0.03, $"{shineHigh:F4}");
            // over the planet's midnight (in its shadow): the face toward it is night
            var night = Rock(P + new Vector3D(80000, 0, 0));
            Ok("over the night side: no planet-shine", PlanetShine(night.At + new Vector3D(0, 5e5, 0), night.At, Sun, bodies) < 1e-6);
            Ok("with no planets: none", PlanetShine(eye, rock.At, Sun, Bodies()) == 0);
            var moonish = new Body { Centre = P, Radius = 60000, Albedo = 0.12 };
            Near("a darker body (albedo 0.12 vs 0.3) shines proportionally less", PlanetShine(eye, rock.At, Sun, Bodies(moonish)) / shine, 0.4, 1e-9);
        }

        // Glare: against a planet's sunlit face the optical range drops; the night side and space do not hide.
        static void GlareRule()
        {
            Console.WriteLine("-- glare");
            var P = new Vector3D(AU, 0, 0);
            var planet = new Body { Centre = P, Radius = 60000 };
            var bodies = Bodies(planet);
            // looking straight down at the planet's noon from above it (sunward): the target between, the lit face behind
            var tDay = P + new Vector3D(-100000, 0, 0);
            var eyeDay = P + new Vector3D(-400000, 0, 0);
            Near("against the full day side: optical down to GlareFactor", Glare(eyeDay, tDay, Sun, bodies), GlareFactor, 1e-6);
            // looking at the night side from beyond the planet
            var tNight = P + new Vector3D(100000, 0, 0);
            var eyeNight = P + new Vector3D(400000, 0, 0);
            Near("against the night side: no glare", Glare(eyeNight, tNight, Sun, bodies), 1, 1e-6);
            // against empty space (the sight line misses the planet)
            Near("against space: no glare", Glare(eyeDay, tDay + new Vector3D(0, 200000, 0), Sun, bodies), 1, 1e-6);
            // near the terminator: part way
            var tSide = P + new Vector3D(0, 100000, 0);
            var eyeSide = P + new Vector3D(0, 400000, 0);
            double g = Glare(eyeSide, tSide, Sun, bodies);
            Ok("over the terminator: little glare (grazing light)", g > 0.9, $"{g:F3}");
            // it only touches the optical channel
            var st = Station(tDay);
            var l = new Looker { At = eyeDay, Kind = Kind.Telescope };
            double withGlare = Reach(l, st, Sun, true, bodies), noGlare = Reach(l, st, Sun, true, null);
            Ok("a station against the day side: the telescope's reach falls (to infrared)", withGlare < noGlare, $"{withGlare / 1000:F0} km vs {noGlare / 1000:F0} km");
            Ok("... radar does not care", Math.Abs(Reach(new Looker { At = eyeDay, Kind = Kind.Radar, Power = 1 }, st, Sun, true, bodies) - RadarReach(150, 1)) < 1e-6);
        }

        // Detected vs tracked: eyes and radar fix an orbit at once, a telescope needs two minutes, lidar ~10 s.
        static void TrackingRules()
        {
            Console.WriteLine("-- tracking");
            Ok("radar tracks at once", Tracking.Of(true, Tracking.Watch(0, Kind.Radar, false, 0.5)) == Tracking.Quality.Tracked);
            Ok("eyes track at once", Tracking.Of(true, Tracking.Watch(0, Kind.Eyes, false, 0.5)) == Tracking.Quality.Tracked);
            double d = 0; int n = 0;
            while (Tracking.Of(true, d) != Tracking.Quality.Tracked && n < 10000) { d = Tracking.Watch(d, Kind.Telescope, false, 0.5); n++; }
            Near("a telescope alone: tracked after 120 s of watching", n * 0.5, 120, 1e-9);
            d = 0; n = 0;
            while (Tracking.Of(true, d) != Tracking.Quality.Tracked && n < 10000) { d = Tracking.Watch(d, Kind.Telescope, true, 0.5); n++; }
            Near("... with lidar on your target: 10 s", n * 0.5, 10, 1e-9);
            Ok("watching is kept, never undone (a fit stays made)", Tracking.Watch(120, Kind.Telescope, false, 0.5) == 120 && Tracking.Watch(60, Kind.Radar, false, 0.5) == 120);
            Ok("not seen at all: unknown", Tracking.Of(false, 0) == Tracking.Quality.Unknown);
            Near("progress half way at 60 s", Tracking.Progress(60), 0.5, 1e-12);
            Near("rough distance: 2 significant figures (421,870 m -> 420,000)", Tracking.Rough(421870), 420000, 1e-6);
            Near("... 5,950 m -> 6,000", Tracking.Rough(5950), 6000, 1e-6);
        }

        static void WarpStoreBill()
        {
            Console.WriteLine("-- warp store bill");
            bool dry;
            // x100 for a 1/60 s frame: 99/60 s of surplus
            double s = 99.0 / 60;
            Near("draining battery ages by flow x surplus", AgeStore(1.44e7, -875, 1.44e7, s, out dry), 1.44e7 - 875 * s, 1e-6);
            Ok("... not dry", !dry);
            Near("a charging store fills, capped at max", AgeStore(9.9e6, 1e6, 1e7, s, out dry), 1e7, 1e-6);
            Near("a draining store runs out at 0", AgeStore(100, -875, 1e4, s, out dry), 0, 0);
            Ok("... and says it ran dry", dry);
            AgeStore(0, 0, 1e4, s, out dry);
            Ok("an idle empty store is not 'running dry'", !dry);
            // the in-game measurement: x100 for 20 s real = 1200 frames
            double v = 1.44e7; for (int i = 0; i < 1200; i++) v = AgeStore(v, -875, 1.44e7, s, out dry);
            Near("x100 for 20 s drains ~1.73 M (the game measured 1.87 M incl. x1 use and frame jitter)", (1.44e7 - v) / 1e6, 1.7325, 0.001);
        }
    }
}
