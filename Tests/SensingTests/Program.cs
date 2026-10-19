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
        static Target Rock(Vector3D at) => new Target { At = at, Radius = 1500, Albedo = 0.15, TempK = 250, RadarAlbedo = Signature.RockRadarAlbedo };
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
            Signatures();
            EmissionRule();
            RangeSanity();
            BearingRule();
            AgingRules();
            TrackingGate();
            ToastBatching();
            ReasonTexts();
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
            // (2026-10-19 retune: RadarK 18400 -> 75000, so a large ship on the ground is in reach from a 300 km orbit)
            Near("radar: 150 m station ~1220 km at full power", RadarReach(150, 1) / 1000, 1223, 5);
            Near("radar: 1.5 km rock (radar albedo 0.1) ~2180 km at full power", EchoReach(Rock(Vector3D.Zero), 1) / 1000, 2175, 10);
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
            var far = Station(o + new Vector3D(0, 1000000, 0));   // 1000 km
            Ok("250 km station: full power sees it", SeenBy(new[] { Look(o, Kind.Radar, 1) }, st, bodies, Sun) == 0);
            Ok("1000 km station: full power sees it (~1220 km)", SeenBy(new[] { Look(o, Kind.Radar, 1) }, far, bodies, Sun) == 0);
            Ok("1000 km station: 25% power does not (~865 km)", SeenBy(new[] { Look(o, Kind.Radar, 0.25) }, far, bodies, Sun) == -1);
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

        static Target Ship(Vector3D at, double x, double y, double z, double firingN = 0)
            => new Target { At = at, Radius = Signature.EquivalentRadius(Signature.MeanArea(x, y, z)), Albedo = 0.5, TempK = 290, Plume = Signature.PlumeReach(firingN) };

        // A ship's signature from its size and its engines (it was a fixed 150 m / 290 K ball).
        static void Signatures()
        {
            Console.WriteLine("-- signatures");
            Near("mean cross-section of a box: (xy+yz+zx)/2 (a 10 m cube: 150 m^2)", Signature.MeanArea(10, 10, 10), 150, 1e-9);
            Near("... a missing side counts as 10 m", Signature.MeanArea(0, 10, 10), 150, 1e-9);
            Near("equivalent radius: a disc of the same area", Math.PI * Math.Pow(Signature.EquivalentRadius(1000), 2), 1000, 1e-9);
            var o = new Vector3D(AU, 0, 0); var at = o + new Vector3D(0, 300000, 0);
            var big = Ship(at, 35, 34, 12); var small = Ship(at, 8, 6, 4);
            double rBig = Reach(Look(o, Kind.Radar), big, Sun, true), rSmall = Reach(Look(o, Kind.Radar), small, Sun, true);
            Ok("radar: a 35 m airliner is seen much further than an 8 m fighter", rBig > 1.8 * rSmall, $"{rBig / 1000:F0} km vs {rSmall / 1000:F0} km");
            Ok("telescope: a bigger hull is seen further too", Reach(Look(o, Kind.Telescope), big, Sun, true) > Reach(Look(o, Kind.Telescope), small, Sun, true));
            double cold = Reach(Look(o, Kind.Telescope), Ship(at, 35, 34, 12), Sun, false);
            double hot = Reach(Look(o, Kind.Telescope), Ship(at, 35, 34, 12, 4e6), Sun, false);
            Ok("in shadow, drifting: the telescope sees a large ship only close (infrared hull)", cold < 20000, $"{cold / 1000:F1} km");
            Near("... burning 4 MN: its plume is seen ~600 km off", hot / 1000, 600, 1);
            // a planet sunward of both (the ship in its shadow), well off the sight line
            var shade = Bodies(new Body { Centre = new Vector3D(AU - 1e6, 200000, 0), Radius = 3e5 });
            var burning = Ship(o + new Vector3D(0, 400000, 0), 35, 34, 12, 4e6);
            Ok("(the 400 km ship is in that planet's shadow)", !Clear(burning.At, Sun, shade, ignoreSun: true));
            Ok("a 4 MN burn at 400 km in shadow: seen, by the plume", Detect(new[] { Look(o, Kind.Telescope) }, burning, shade, Sun, out var via) == 0 && via == Via.Plume, via.ToString());
            Ok("... the same ship drifting: not seen", SeenBy(new[] { Look(o, Kind.Telescope) }, Ship(o + new Vector3D(0, 400000, 0), 35, 34, 12), shade, Sun) == -1);
            Ok("the radar ignores plumes (an echo is an echo)", Reach(Look(o, Kind.Radar), Ship(at, 35, 34, 12, 4e6), Sun, true) == rBig);
            Ok("kinds: tiny or light = debris", Signature.Classify(false, false, 3, 50000) == Signature.Class.Debris && Signature.Classify(false, false, 12, 800) == Signature.Class.Debris);
            Ok("kinds: fixed = station", Signature.Classify(true, false, 60, 1e6) == Signature.Class.Station);
            Ok("kinds: no owner = wreck", Signature.Classify(false, true, 30, 1e5) == Signature.Class.Wreck);
            Ok("contact rule: an unowned wreck must be detected, even if your identity reads as nobody", Signature.IsContact(false, true, true, true) && Signature.IsContact(false, true, true, false));
            Ok("contact rule: yours is not, another's is, an encounter's is", !Signature.IsContact(false, true, false, true) && Signature.IsContact(false, true, false, false) && Signature.IsContact(true, true, false, true));
            Ok("contact rule: ownership unknown counts as yours", !Signature.IsContact(false, false, false, false));
            Ok("kinds: by size, small / large ship", Signature.Classify(false, false, 12, 2e4) == Signature.Class.SmallShip && Signature.Classify(false, false, 40, 2e5) == Signature.Class.LargeShip);
        }

        // A transmitting radar is heard far beyond its own reach (one way), by telescopes and radars - with a bearing only.
        static void EmissionRule()
        {
            Console.WriteLine("-- emission (a loud radar)");
            Near("heard at ~4000 km at full power", Signature.EmissionReach(1) / 1000, 4000, 1e-6);
            Near("... a quarter power: half as far", Signature.EmissionReach(0.25) / Signature.EmissionReach(1), 0.5, 1e-12);
            var bodies = Bodies(); var o = new Vector3D(AU, 0, 0);
            var quiet = Ship(o + new Vector3D(0, 2.0e6, 0), 35, 34, 12);
            var loud = quiet; loud.Emit = Signature.EmissionReach(1);
            Ok("2000 km: a quiet ship is not seen by a telescope", SeenBy(new[] { Look(o, Kind.Telescope) }, quiet, bodies, Sun) == -1);
            Ok("... the same ship transmitting is heard", Detect(new[] { Look(o, Kind.Telescope) }, loud, bodies, Sun, out var v1) == 0 && v1 == Via.Emission);
            Ok("... by a radar too (its receiver), still only heard (no echo that far)", Detect(new[] { Look(o, Kind.Radar) }, loud, bodies, Sun, out var v2) == 0 && v2 == Via.Emission);
            Ok("... heard is a bearing, not an orbit", !Fixes(Via.Emission) && Fixes(Via.Echo) && Fixes(Via.Eyes));
            var near = Ship(o + new Vector3D(0, 100000, 0), 35, 34, 12); near.Emit = Signature.EmissionReach(1);
            Ok("within its echo reach a radar ranges it (echo, not just heard)", Detect(new[] { Look(o, Kind.Radar) }, near, bodies, Sun, out var v3) == 0 && v3 == Via.Echo);
            Ok("eyes do not hear radio", Reach(Look(o, Kind.Eyes), loud, Sun, true) == EyeRange);
            Ok("a planet between: not heard", SeenBy(new[] { Look(o, Kind.Telescope) }, loud, Bodies(new Body { Centre = o + new Vector3D(0, 1.0e6, 0), Radius = 60000 }), Sun) == -1);
            Ok("two lookers: the one that ranges it is preferred over one that only hears it",
               Detect(new[] { Look(o, Kind.Telescope), Look(o + new Vector3D(0, 1.95e6, 0), Kind.Radar) }, loud, bodies, Sun, out var v4) == 1 && v4 == Via.Echo);
        }

        // From a 300 km orbit over a 63 km planet (Verdure), a full-power radar sees large ships on the ground in view.
        static void RangeSanity()
        {
            Console.WriteLine("-- range sanity (300 km orbit, Verdure-size planet)");
            var P = new Vector3D(AU, 0, 0); const double R = 63000, h = 300000;
            var bodies = Bodies(new Body { Centre = P, Radius = R });
            var me = P + new Vector3D(0, R + h, 0);
            var radar = new[] { Look(me, Kind.Radar, 1) };
            var below = Ship(P + new Vector3D(0, R, 0), 35, 34, 12);
            double a = 60 * Math.PI / 180;
            var side = Ship(P + new Vector3D(R * Math.Sin(a), R * Math.Cos(a), 0), 35, 34, 12);
            Ok("a large ship straight below (300 km): seen", SeenBy(radar, below, bodies, Sun) == 0, $"reach {Reach(radar[0], below, Sun, true) / 1000:F0} km");
            Ok($"a large ship 60 deg round the planet ({(side.At - me).Length() / 1000:F0} km): seen", SeenBy(radar, side, bodies, Sun) == 0);
            var behind = Ship(P + new Vector3D(0, -R, 0), 35, 34, 12);
            Ok("... on the far side: hidden by the planet", SeenBy(radar, behind, bodies, Sun) == -1);
            var smallBelow = Ship(P + new Vector3D(0, R, 0), 8, 6, 4);
            Ok($"a small 8 m ship straight below: out of reach at 300 km ({Reach(radar[0], smallBelow, Sun, true) / 1000:F0} km)", SeenBy(radar, smallBelow, bodies, Sun) == -1);
            Ok($"... at quarter power the large one drops out too ({Reach(Look(me, Kind.Radar, 0.25), below, Sun, true) / 1000:F0} km)", SeenBy(new[] { Look(me, Kind.Radar, 0.25) }, below, bodies, Sun) == -1);
        }

        static void BearingRule()
        {
            Console.WriteLine("-- bearing");
            var r = new Vector3D(0, 7e6, 0); var v = new Vector3D(7500, 0, 0);   // over the body's +Y, moving +X
            Signature.Bearing(new Vector3D(1000, 0, 0), r, v, out double az, out double el);
            Near("straight ahead: azimuth 0", az, 0, 1e-9); Near("... level", el, 0, 1e-9);
            Signature.Bearing(new Vector3D(0, 1000, 0), r, v, out az, out el);
            Near("straight up: elevation 90", el, 90, 1e-6);
            Signature.Bearing(new Vector3D(-1000, 0, 0), r, v, out az, out el);
            Near("behind: azimuth 180", az, 180, 1e-9);
            Signature.Bearing(new Vector3D(0, 0, -1000), r, v, out double azR, out _);
            Signature.Bearing(new Vector3D(0, 0, 1000), r, v, out double azL, out _);
            Ok("right and left are 90 and 270 (one each)", Math.Abs(azR - 90) < 1e-9 && Math.Abs(azL - 270) < 1e-9 || Math.Abs(azR - 270) < 1e-9 && Math.Abs(azL - 90) < 1e-9, $"{azR:F0} / {azL:F0}");
            Ok("in words", Signature.BearingText(5, 12) == "ahead, 12° up" && Signature.BearingText(40, -5) == "040°, 5° down" && Signature.BearingText(180, 0.2) == "behind, level", Signature.BearingText(40, -5));
            Ok("at rest (on the ground) still defined", Signature.Bearing(new Vector3D(1, 2, 3), r, Vector3D.Zero, out _, out _));
        }

        static void AgingRules()
        {
            Console.WriteLine("-- aging");
            const double stale = 1800, forget = 86400;
            Ok("seen 2 s ago: in sight", Aging.Of(2, stale, forget, true) == Aging.State.InSight);
            Ok("never stamped: in sight", Aging.Of(double.NaN, stale, forget, true) == Aging.State.InSight);
            Ok("10 min: recent", Aging.Of(600, stale, forget, true) == Aging.State.Recent);
            Ok("2 h: stale (an estimate)", Aging.Of(7200, stale, forget, true) == Aging.State.Stale);
            Ok("2 days, moving: lost (forgotten)", Aging.Of(2 * 86400, stale, forget, true) == Aging.State.Lost);
            Ok("a station never goes stale nor is forgotten", Aging.Of(30 * 86400, stale, forget, false) == Aging.State.Recent);
            Ok("forget 0: never forgotten", Aging.Of(1e9, stale, 0, true) == Aging.State.Stale);
            Ok("stale 0: never stale", Aging.Of(7200, 0, 0, true) == Aging.State.Recent);
            Near("fade: 1 when recent", Aging.Fade(600, stale, forget, true), 1, 1e-12);
            Ok("fade: dims across the stale span", Aging.Fade(stale + 1, stale, forget, true) > Aging.Fade(forget - 1, stale, forget, true) && Aging.Fade(forget - 1, stale, forget, true) >= 0.4);
            Ok("last seen in words", Aging.Ago(2) == "in sight" && Aging.Ago(40) == "40 s ago" && Aging.Ago(720) == "12 min ago" && Aging.Ago(3 * 3600 + 5) == "3 h ago" && Aging.Ago(2 * 86400 + 5) == "2 d ago");
        }

        // Closest-approach predictions need an orbit: detected-but-untracked (bearing only) gets none, ships as rocks.
        static void TrackingGate()
        {
            Console.WriteLine("-- tracking gate");
            double d = Tracking.Watch(0, Kind.Telescope, false, 30);
            Ok("a telescope's 30 s on a ship: detected, not tracked (no prediction)", Tracking.Of(true, d) == Tracking.Quality.Detected);
            Near("... 25% along", Tracking.Progress(d), 0.25, 1e-12);
            Ok("a radar echo: tracked at once (predictions)", Tracking.Of(true, Tracking.Watch(0, Kind.Radar, false, 0.5)) == Tracking.Quality.Tracked);
            Ok("heard only: watched like a telescope (not tracked at once)", Tracking.Of(true, Tracking.Watch(0, Tracking.KindFor(Via.Emission), false, 0.5)) == Tracking.Quality.Detected);
            Ok("... an echo or sight fixes it", Tracking.KindFor(Via.Echo) == Kind.Radar && Tracking.KindFor(Via.Eyes) == Kind.Eyes && Tracking.KindFor(Via.Plume) == Kind.Telescope);
            Ok("the gate: no prediction below 100%", !Tracking.CanPredict(0.99) && Tracking.CanPredict(1) && Tracking.CanPredict(null));
        }

        static void ToastBatching()
        {
            Console.WriteLine("-- rock toasts: one summary per pass");
            var b = new Batcher();
            double t = 0; int toasts = 0; var lines = new List<string>();
            // the pass in the audit: a rock every ~13 s for 90 s, 7 in all (one toast each before)
            for (int i = 0; i < 7; i++) { b.Add("Oblivara", 1, t); t += 9; if (b.Due(t)) { toasts++; lines = b.Take(t); } }
            for (int k = 0; k < 30; k++) { t += 1; if (b.Due(t)) { toasts++; lines = b.Take(t); } }
            Ok("7 rocks over a minute: one toast", toasts == 1, $"{toasts}");
            Ok("... saying 'Oblivara: 7 rocks spotted'", lines.Count == 1 && lines[0] == "Oblivara: 7 rocks spotted", lines.Count > 0 ? lines[0] : "-");
            b = new Batcher(); t = 0; toasts = 0;
            for (int i = 0; i < 600; i++) { if (i % 3 == 0) b.Add("Oblivara", 1, t); if (b.Due(t)) { toasts++; b.Take(t); } t += 1; }
            Ok("a 10 min trickle: at most one per 45 s", toasts >= 1 && toasts <= 600 / 45 + 1, $"{toasts}");
            b = new Batcher(); t = 0; b.Add("Oblivara", 3, t); b.Add("Kemik ring", 2, t + 1);
            Ok("not before it goes quiet", !b.Due(t + 5));
            Ok("two rings: one summary, a line each", b.Due(t + 12) && b.Take(t + 12).Count == 2);
            Ok("nothing pending: nothing due", !b.Due(t + 1000));
            b.Add("Oblivara", 1, 2000);
            Ok("held while it cannot be shown, still there later (the caller asks again)", b.Due(2020) && b.Pending == 1);
        }

        static void ReasonTexts()
        {
            Console.WriteLine("-- targeting reasons");
            Ok("landed wins", Reasons.Of("Verdure", false) == Reasons.Why.Landed);
            Ok("off this view", Reasons.Of(null, false) == Reasons.Why.NotOnView);
            Ok("else targetable", Reasons.Of(null, true) == Reasons.Why.None && Reasons.Text(Reasons.Why.None) == null);
            string t = Reasons.Text(Reasons.Why.Landed, "Red Ship", "Verdure");
            Ok("landed: says where", t.Contains("Red Ship") && t.Contains("landed on Verdure"), t);
            string n = Reasons.Text(Reasons.Why.Nothing);
            Ok("nothing: says how to find things (Telescope / Radar)", n.Contains("Telescope or Radar") && n.StartsWith("Nothing to target under the cursor"), n);
            Ok("... in flight: at the crosshair", Reasons.Text(Reasons.Why.Nothing, flight: true).StartsWith("Nothing to target at the crosshair"));
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
