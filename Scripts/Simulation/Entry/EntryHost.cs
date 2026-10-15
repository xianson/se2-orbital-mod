using SEAerospace;
using SEAerospace.Entry;
using SEAerospace.Frames;
using SEAerospace.Orbital;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// Reentry in game (the rules: Core/Entry/Reentry). Server: every frame on rails about a body with an atmosphere
/// is braked through that body's band, its heat kept per frame. Client: your own frame's next pass predicted (the
/// orbit card, the map, and warp stopping ahead of it). Inside the planet's frame the world's speed cap applies;
/// the band makes sure you reach it at or under the cap.
/// </summary>
public static class EntryHost
{
    public static bool Enabled = true;
    /// <summary>The world's speed cap (m/s): what the band slows you to.</summary>
    public static double Cap => double.IsNaN(TestCap) ? FrameHost.SpeedCap : TestCap;   // (the arrival's cap: a hair under the world's)
    /// <summary>Harness: a lower cap for the reentry rules only (NaN: the world's).</summary>
    public static double TestCap = double.NaN;
    /// <summary>Warp stops this long before the braking starts.</summary>
    public const double WarpLead = 30.0;

    static readonly Dictionary<long, double> _last = new Dictionary<long, double>();
    static readonly Dictionary<long, double> _heat = new Dictionary<long, double>();
    static readonly Dictionary<long, (double decel, double t)> _braking = new Dictionary<long, (double, double)>();
    static readonly HashSet<long> _inPass = new HashSet<long>();
    // per grid, this entry: its tolerance (shielded or not), the heat it has been worn up to, and how much worn
    static readonly Dictionary<long, (double limit, double upTo, double worn)> _wear = new Dictionary<long, (double, double, double)>();
    /// <summary>For the harness: made when read from the last tick's numbers (a new string every server tick before, 2026-10-15).</summary>
    public static string Status => _stTicked ? $"{_stBraking} frame(s) braking, cap {_stCap:F0} m/s" : "-";
    static volatile bool _stTicked; static int _stBraking; static double _stCap;
    /// <summary>ServerTick's kept pass log (reset per frame: nothing keeps it past its step) and list. Under FramesLock.</summary>
    static readonly Pass _tickLog = new Pass();
    static readonly List<long> _tickDone = new List<long>();

    // ── the aero mod's entry plasma: grids on rails sit still in their frame while the frame is braked through the
    //    air, so the aero mod cannot see their speed; it is told, per grid, the air past it (world axes are the
    //    inertial axes here) and how hard it burns. Its public API's ENTRY SOURCE (AeroMod.AeroApi, version 2 or
    //    later; the source's own contract is 2 - checked across both repos by tools/check_contract.sh): we register
    //    Glow under "OrbitalMod"; while registered our strength owns the grid's plasma ──
    static readonly Dictionary<Entity, (Vector3D air, double strength, double t, double rho)> _glow = new Dictionary<Entity, (Vector3D, double, double, double)>();
    static bool _glowHooked;
    /// <summary>Diagnostics: grid glows written; asked for / answered.</summary>
    public static int GlowWrites, GlowAsks, GlowAnswers;
    /// <summary>The entry-source contract this build speaks (= AeroMod.AeroEntryFx.EntrySourceContract, what
    /// RegisterEntrySource returns; aero's API version, AeroApi.Version, may be higher - members are only added).</summary>
    public const int AeroEntryContract = 2;
    const string AeroOwner = "OrbitalMod";
    static System.Reflection.MethodInfo _aeroRegister, _aeroHas;
    static int _aeroGen = -1;
    static Func<Entity, ValueTuple<Vector3, float>> _glowSource;
    /// <summary>Diagnostics: why the aero hook is not set ("" when it is).</summary>
    public static string GlowWhy = "not tried";

    /// <summary>Register with the aero mod (about every 10 s): bound once per change of the aero assembly (AeroLink; no scan
    /// otherwise - the aero mod absent costs nothing), then a cheap "still registered?" (a new session or a reloaded
    /// aero mod re-registers).</summary>
    static void HookAero()
    {
        _glowSource ??= Glow;
        try
        {
            // (bound again whenever the aero assembly changes - AeroLink, the last loaded copy: bound to the first, a hot-reloaded
            //  aero mod's glow went into its stale copy and still read as hooked, 2026-10-15)
            int gen = AeroLink.Generation;
            if (gen != _aeroGen)
            {
                _aeroGen = gen; _aeroRegister = _aeroHas = _aeroPredict = _aeroAtmoAt = _aeroRequestTable = null; _glowHooked = false;
                var t = AeroLink.Find("AeroMod.AeroApi");
                if (t != null)
                {
                    // (every lookup with its parameter types: an overload added on aero's side must never make one
                    //  ambiguous - AmbiguousMatchException - or bind the wrong one; API.md: public members are never overloaded)
                    var vr = typeof(Vector3).MakeByRefType(); var fr = typeof(float).MakeByRefType();
                    _aeroRegister = t.GetMethod("RegisterEntrySource", new[] { typeof(string), typeof(Delegate) });
                    _aeroHas = t.GetMethod("HasEntrySource", new[] { typeof(string), typeof(Delegate) });
                    _aeroPredict = t.GetMethod("PredictHullForces", new[] { typeof(Entity), typeof(Vector3), typeof(float), typeof(float), vr, vr, fr });   // (aerobraking: each ship's drag area)
                    _aeroAtmoAt = t.GetMethod("TryGetAtmosphereAt", new[] { typeof(Vector3D), fr, fr, fr, fr });   // (the border's density: the rails' drag matched to aero's)
                    _aeroRequestTable = t.GetMethod("RequestForceTable", new[] { typeof(Entity) });   // (a railed grid has none until asked)
                }
                if (_aeroRegister == null || _aeroHas == null) { GlowWhy = "aero mod not loaded (or older than contract " + AeroEntryContract + ")"; return; }
            }
            if (_aeroRegister == null) return;
            if (_glowHooked && (bool)_aeroHas.Invoke(null, new object[] { AeroOwner, _glowSource })) return;
            int c = (int)_aeroRegister.Invoke(null, new object[] { AeroOwner, _glowSource });
            _glowHooked = c == AeroEntryContract;
            GlowWhy = _glowHooked ? "" : $"aero mod speaks contract {c}, this build {AeroEntryContract}";
        }
        catch (Exception e) { _glowHooked = false; _aeroRegister = null; _aeroGen = -1; GlowWhy = "hook failed: " + e.GetType().Name; }
    }

    // ── the ENTRY STATE (OrbitalApi.TryGetEntryState / TryGetEntryState2, 2026-10-14): per grid, written on EVERY tick its
    //    frame is braked - not only while the plasma shows (the glow is for the eye; the state is the physics: a gentle
    //    aerobraking pass heats and loads too) - and FADED as the glow is once no longer refreshed (held GlowHold, then
    //    down to nothing over GlowFade: it returned full density for 1.75 s after the braking had stopped) ──
    //    travel: the TRUE air velocity, v - w x r (the frame's inertial velocity less the planet's turning air at that
    //    point; world axes = inertial axes on rails): the direction the grid TRAVELS INTO the air, at its real speed - not
    //    the wind (the wind it feels is the opposite). rho: the band's density there (kg/m3). tempK: the band's air
    //    temperature (K; the border's - the band's envelope is exponential in height, i.e. isothermal; 0 unknown). brake:
    //    the frame's braking acceleration (world, m/s2: -travel x the tick's peak deceleration - the drag and the cap rule).
    static readonly Dictionary<Entity, (Vector3D air, double rho, double tempK, Vector3D brake, double t)> _state = new Dictionary<Entity, (Vector3D, double, double, Vector3D, double)>();
    public static int StateWrites;

    /// <summary>The share (0..1) a state or glow written at t still holds now: 1 for GlowHold, then down to 0 over GlowFade.</summary>
    static double Fade(double t)
    {
        double age = SystemHost.Now - t;
        if (age > GlowHold + GlowFade) return 0;
        return age <= GlowHold ? 1.0 : Math.Clamp(1.0 - (age - GlowHold) / GlowFade, 0.0, 1.0);
    }

    /// <summary>A grid's server copy (the key of the glow and the state): itself, or - a client copy in the same process -
    /// the server grid matched by OrbitalApi.GetMemberId (nearest within 20 m). Null: none. Takes no lock of ours but
    /// GridPositions' (never call it under _glow).</summary>
    static Entity ServerCopy(Entity grid)
    {
        if (grid == null) return null;
        long id = OrbitalApi.GetMemberId(grid);
        var g = id >= 0 ? GridMembers.Get(id) : null;
        return g != null && g.IsServer ? g.Entity : null;
    }

    static bool TryState(Entity grid, out (Vector3D air, double rho, double tempK, Vector3D brake, double t) s)
    {
        s = default;
        if (grid == null) return false;
        lock (_glow)
        {
            if (_state.Count == 0) return false;   // (no entry anywhere: no client-copy search either)
            if (_state.TryGetValue(grid, out s)) return true;
        }
        var server = ServerCopy(grid);   // (a client copy: by its server grid - outside _glow)
        if (server == null || ReferenceEquals(server, grid)) return false;
        lock (_glow) return _state.TryGetValue(server, out s);
    }

    /// <summary>A grid's re-entry STATE (OrbitalApi.TryGetEntryState): its true air velocity (world, m/s: the travel) and the
    /// band's density there (kg/m3, faded as the glow once the braking stops). False: none.</summary>
    public static bool StateOf(Entity grid, out Vector3 airWorld, out float density)
        => State2Of(grid, out airWorld, out density, out _, out _) && density > 0f;

    /// <summary>The fuller state (OrbitalApi.TryGetEntryState2): the travel (world, m/s), density (kg/m3), the air's temperature
    /// (K; 0 unknown) and the braking acceleration (world, m/s2) - density and braking faded as the glow is.</summary>
    public static bool State2Of(Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingWorld)
    {
        travelWorld = brakingWorld = default; density = temperature = 0f;
        if (!TryState(grid, out var s)) return false;
        double k = Fade(s.t);
        if (!(k > 0)) return false;
        travelWorld = new Vector3((float)s.air.X, (float)s.air.Y, (float)s.air.Z);
        density = (float)(s.rho * k); temperature = (float)s.tempK;
        brakingWorld = new Vector3((float)(s.brake.X * k), (float)(s.brake.Y * k), (float)(s.brake.Z * k));
        return density > 0f || brakingWorld.LengthSquared() > 0f;
    }

    /// <summary>The entry source: a grid's air velocity (world, m/s) and strength (0..1, 0 none) while its frame is braked.</summary>
    static ValueTuple<Vector3, float> Glow(Entity grid)
    {
        var v = GlowOf(grid);
        return (new Vector3(v.X, v.Y, v.Z), v.W);
    }
    /// <summary>Braking (m/s^2) at which the plasma is full.</summary>
    public const double GlowFullDecel = 50.0;
    /// <summary>Drag braking (m/s^2) under which a pass is not shown as an entry (no plasma, no event); the cap rule's
    /// braking always is.</summary>
    public const double GlowMinDecel = 1.0;

    /// <summary>For the aero mod: a grid's air velocity (xyz, m/s) and glow (w, 0..1) while its frame is braked.</summary>
    /// <summary>A grid's glow, once no longer refreshed, held this long (s) then faded to 0 over GlowFade.</summary>
    public const double GlowHold = 0.25, GlowFade = 1.5;
    public static Vector4 GlowOf(Entity grid)
    {
        (Vector3D air, double strength, double t, double rho) g;
        lock (_glow)
        {
            GlowAsks++;
            // (no longer refreshed - the pass over, the braking under the edge: it FADES, held 0.25 s then down to 0 over
            //  1.5 s; it was full strength until 1 s stale, then gone - a hard cut of the plasma at the end of every pass)
            if (grid == null || _glow.Count == 0) return default;
            if (!_glow.TryGetValue(grid, out g)) g.t = double.NaN;
        }
        if (double.IsNaN(g.t))
        {
            // (a client copy in the same process: by its server grid, matched outside _glow - OrbitalApi accepts either copy)
            var server = ServerCopy(grid);
            if (server == null || ReferenceEquals(server, grid)) return default;
            lock (_glow) if (!_glow.TryGetValue(server, out g)) return default;
        }
        double k = Fade(g.t);
        if (!(k > 0)) return default;
        lock (_glow) GlowAnswers++;
        return new Vector4((float)g.air.X, (float)g.air.Y, (float)g.air.Z, (float)(g.strength * k));
    }

    // ── aerobraking: each frame's ballistic coefficient ──
    static System.Reflection.MethodInfo _aeroPredict, _aeroRequestTable;
    static readonly Dictionary<long, (double beta, double wall)> _beta = new Dictionary<long, (double, double)>();
    /// <summary>Seconds (wall) a frame's ballistic coefficient is kept before it is measured again.</summary>
    public const double BetaRefresh = 30;
    /// <summary>Diagnostics: whether the last ballistic coefficient came from aero's force table (false: the default).</summary>
    public static bool LastBetaMeasured;

    /// <summary>The directions (grid frame) the drag area is averaged over: the 6 axes and the 8 diagonals.</summary>
    static readonly Vector3[] BetaDirs = BuildBetaDirs();
    static Vector3[] BuildBetaDirs()
    {
        var d = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) d.Add(Vector3.Normalize(new Vector3(x, y, z)));
        return d.ToArray();
    }

    /// <summary>A frame's ballistic coefficient (kg/m2: its grids' mass over their drag area, Cd x A). ORIENTATION-FREE:
    /// the drag area is the mean over 14 directions (axes and diagonals) of the aero mod's force table
    /// (PredictHullForces) - a railed ship has no attitude the braking should depend on, and a prediction must not change
    /// when the player turns (or be gamed by pointing the slim end forward). It changes only with the ship's shape and
    /// mass. Without aero, or no table yet: Reentry.DefaultBeta (a table is asked for and it is measured again in ~1 s).
    /// Kept for BetaRefresh seconds. (airWorld: unused - kept for the callers.)</summary>
    /// <summary>The client's: the server's last measured value (or the default) - measuring asks Aero for a table on the
    /// server's grid entity, which the client must not touch.</summary>
    public static double BetaCached(ProximityFrame f)
    {
        if (f == null) return Reentry.DefaultBeta;
        lock (_beta) return _beta.TryGetValue(f.Id, out var c) ? c.beta : Reentry.DefaultBeta;
    }

    public static double BetaOf(ProximityFrame f, Vector3D airWorld)
    {
        if (f == null) return Reentry.DefaultBeta;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_beta) if (_beta.TryGetValue(f.Id, out var c) && wall - c.wall < BetaRefresh) return c.beta;
        // (the member list is the server's: copied under its lock - the client calls this too)
        long[] ids; lock (ServerFrames.FramesLock) ids = f.Members.ToArray();
        // (under FramesLock - the server's frame step: aero is not called here (the lock rule, API.md): the frame is measured
        //  right after the lock is let go, this tick (ServerAfterLock); until then its last value, or the default)
        if (AeroCallsDeferred) { lock (_aeroWork) _betaWork[f.Id] = ids; lock (_beta) return _beta.TryGetValue(f.Id, out var c2) ? c2.beta : Reentry.DefaultBeta; }
        return MeasureBeta(f.Id, ids, wall);
    }

    /// <summary>The ballistic coefficient measured from aero's force tables (BetaOf's work; never under FramesLock).</summary>
    static double MeasureBeta(long frameId, long[] ids, double wall)
    {
        double mass = 0, area = 0; bool measured = false, asked = false;
        foreach (long id in ids)
        {
            if (!GridMembers.IsGridId(id) || !(GridMembers.Get(id) is OrbitalGridComponent g) || g.Entity == null) continue;
            double gmass = GridMembers.Mass(g);
            if (_aeroPredict == null) continue;
            try
            {
                // (unit dynamic pressure at Mach ~3 - the band's speeds - from each direction, in the grid's own frame)
                const float v = 1000f, rho = 1f;
                double sum = 0; int n = 0;
                foreach (var dir in BetaDirs)
                {
                    var args = new object[] { g.Entity, dir * v, rho, 330f, null, null, null };
                    if (!(bool)_aeroPredict.Invoke(null, args))
                    {
                        // (no table yet - a grid on rails in space has had no air to build one: ask, measure again soon)
                        if (_aeroRequestTable != null) { _aeroRequestTable.Invoke(null, new object[] { g.Entity }); asked = true; }
                        break;
                    }
                    double drag = -Vector3.Dot((Vector3)args[4], dir);   // (along the travel: opposing it)
                    if (drag > 0) sum += drag / (0.5 * rho * v * v);
                    n++;
                }
                if (n == BetaDirs.Length) { area += sum / n; mass += gmass; measured = true; }   // (mass of the measured grids only: a part-measured ship is not all mass, half the drag)
            }
            catch { }
        }
        double beta = measured ? Reentry.Beta(mass, area) : Reentry.DefaultBeta;
        // (any table asked for: measured again in ~1 s, not BetaRefresh - until every grid has one)
        lock (_beta) _beta[frameId] = (beta, asked ? wall - BetaRefresh + 1 : wall);
        LastBetaMeasured = measured;
        return beta;
    }

    /// <summary>A body's braking band (invalid: none).</summary>
    public static Band BandOf(string body)
    {
        var def = SystemHost.Registry?.FindDefinition(body);
        if (def == null || !def.HasAtmosphere) return default;
        var b = Reentry.For(def.RadiusMeters, def.AtmosphereHeightMeters, PlanetBerths.ShellRadius(def));
        b.Rho0 = BorderDensity(body, b.Bottom);
        return b;
    }

    static System.Reflection.MethodInfo _aeroAtmoAt;
    static readonly Dictionary<string, (double rho, double tempK, double wall)> _borderRho = new Dictionary<string, (double, double, double)>();

    /// <summary>Aero's air density at a body's border (kg/m3; 0: unknown - the setting's DragDensity is used), sampled in
    /// the planet's cell every 30 s: the rails' drag ends where the physics' begins, at the same density (they stepped ~3x
    /// at the handover: the rails' fixed 2e-3 against aero's tail there). Under FramesLock (the server's frame step) aero is
    /// not called: the sample is taken right after the lock (ServerAfterLock), the last one (or 0) used until then.</summary>
    static double BorderDensity(string body, double borderRadius)
    {
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_borderRho) if (_borderRho.TryGetValue(body, out var c) && wall - c.wall < 30) return c.rho;
        if (AeroCallsDeferred) { lock (_aeroWork) _rhoWork[body] = borderRadius; lock (_borderRho) return _borderRho.TryGetValue(body, out var c2) ? c2.rho : 0; }
        return SampleBorder(body, borderRadius, wall);
    }

    /// <summary>The border's air from aero (TryGetAtmosphereAt): density, and its temperature - the band's (its envelope is
    /// exponential in height: an isothermal one). Never under FramesLock.</summary>
    static double SampleBorder(string body, double borderRadius, double wall)
    {
        double rho = 0, tempK = 0;
        try
        {
            if (_aeroAtmoAt != null && VoxelBerthRegistry.TryGetCell(body, SystemHost.Registry, out Vector3D cell))
            {
                var args = new object[] { cell + new Vector3D(borderRadius, 0, 0), 0f, 0f, 0f, 0f };
                if ((bool)_aeroAtmoAt.Invoke(null, args))
                {
                    double d = (float)args[1]; if (d > 0 && d < 1) rho = d;
                    double tk = (float)args[3]; if (tk > 3 && tk < 2000) tempK = tk;
                }
            }
        }
        catch { rho = 0; tempK = 0; }
        lock (_borderRho) _borderRho[body] = (rho, tempK, wall);
        return rho;
    }

    /// <summary>The band's air temperature at a body (K; 0: not sampled yet, or no aero) - its border's, as last sampled.</summary>
    public static double BorderTemperature(string body)
    {
        if (body == null) return 0;
        lock (_borderRho) return _borderRho.TryGetValue(body, out var c) ? c.tempK : 0;
    }

    // ── calls into the aero mod: NEVER under FramesLock (the lock rule, both API.md files: never call the other mod while
    //    holding your own lock - its answer may wait on a lock of its own, and a frame step holds FramesLock for its whole
    //    length). The server's locked step collects what it needs (a frame's ballistic coefficient, a border's air); they
    //    are measured right after the lock, this same tick (ServerAfterLock); the hook is kept up before it
    //    (ServerBeforeLock) ──
    static readonly object _aeroWork = new object();
    static readonly Dictionary<long, long[]> _betaWork = new Dictionary<long, long[]>();
    static readonly Dictionary<string, double> _rhoWork = new Dictionary<string, double>();
    static int _hookTicks;
    /// <summary>This thread holds FramesLock: calls into aero wait for ServerAfterLock.</summary>
    static bool AeroCallsDeferred => System.Threading.Monitor.IsEntered(ServerFrames.FramesLock);

    /// <summary>Server, before FramesLock is taken: the aero hook kept (about every 10 s: found, then kept registered).</summary>
    public static void ServerBeforeLock()
    {
        if (Enabled && _hookTicks++ % 600 == 0) HookAero();
    }

    /// <summary>Server, after FramesLock is let go: the aero calls the locked step asked for (border air, ballistic
    /// coefficients), made now.</summary>
    public static void ServerAfterLock()
    {
        List<KeyValuePair<string, double>> rho = null; List<KeyValuePair<long, long[]>> beta = null;
        lock (_aeroWork)
        {
            if (_rhoWork.Count > 0) { rho = new List<KeyValuePair<string, double>>(_rhoWork); _rhoWork.Clear(); }
            if (_betaWork.Count > 0) { beta = new List<KeyValuePair<long, long[]>>(_betaWork); _betaWork.Clear(); }
        }
        if (rho == null && beta == null) return;
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (rho != null) foreach (var kv in rho) SampleBorder(kv.Key, kv.Value, wall);
        if (beta != null) foreach (var kv in beta) MeasureBeta(kv.Key, kv.Value, wall);
    }

    /// <summary>A body's spin (axis x rate, inertial): the air turns with it.</summary>
    public static Vector3D SpinOf(string body)
    {
        var b = SystemHost.Registry?.Find(body);
        return b != null && PlanetBerths.TryBodySpin(b, out Vector3D axis, out double omega) ? axis * omega : Vector3D.Zero;
    }

    /// <summary>A frame's heat (J/kg) now.</summary>
    public static double HeatOf(long frameId) { lock (_heat) return _heat.TryGetValue(frameId, out double h) ? h : 0; }
    /// <summary>A frame is being braked now (and how hard, m/s^2).</summary>
    public static bool Braking(long frameId, out double decel)
    {
        lock (_heat)
        {
            decel = 0;
            if (!_braking.TryGetValue(frameId, out var b) || SystemHost.Now - b.t > 1.0) return false;
            decel = b.decel; return true;
        }
    }
    /// <summary>A grid frame's heat goes with it when its grids arrive (the damage is applied from it).</summary>
    /// <summary>A new world: frame ids start over, so every per-frame entry state (heat, braking, the ballistic
    /// coefficients, the glow) and the prediction are dropped.</summary>
    public static void ResetWorld()
    {
        lock (_heat) { _last.Clear(); _heat.Clear(); _braking.Clear(); _inPass.Clear(); _wear.Clear(); _jolted.Clear(); }
        lock (_beta) _beta.Clear();
        lock (_glow) { _glow.Clear(); _state.Clear(); }
        lock (_borderRho) _borderRho.Clear();
        lock (_aeroWork) { _betaWork.Clear(); _rhoWork.Clear(); }
        Prediction = null; Airless = null;
    }

    public static void Forget(long frameId) { lock (_heat) { _last.Remove(frameId); _heat.Remove(frameId); _braking.Remove(frameId); _inPass.Remove(frameId); _jolted.Remove(frameId); } lock (_beta) _beta.Remove(frameId); }

    /// <summary>A frame leaves the registry (ServerFrames.FrameRetiring, FramesLock held): its per-frame state goes (Forget
    /// had no caller: heat, braking, the ballistic coefficient grew per frame for good, _inPass stayed true after an
    /// arrival); dissolved (not merged), its members' entry ends too - their wear and plasma (2026-10-15: an arrival kept
    /// a grid's spent wear, upTo = MaxValue, so it took no heat wear again for the world).</summary>
    internal static void Retire(ProximityFrame f, bool merged)
    {
        Forget(f.Id);
        if (merged) return;   // (its grids go on in the host: their entry with them)
        lock (_heat) foreach (long id in f.Members) _wear.Remove(id);
        ForgetGlow(f);
    }

    /// <summary>Server tick (FramesLock held): brake every railed frame inside a band. Calls no aero member (the ballistic
    /// coefficients and the border's air it needs are measured after the lock: ServerAfterLock).</summary>
    public static void ServerTick(List<ProximityFrame> frames, double t)
    {
        if (!Enabled) return;
        // (the aero hook: ServerBeforeLock, outside FramesLock - this runs under it)
        int braking = 0;
        foreach (var f in frames)
        {
            if (f.IsEncounter) continue;
            double t0;
            lock (_heat)
            {
                if (!_last.TryGetValue(f.Id, out t0)) { _last[f.Id] = t; continue; }
                _last[f.Id] = t;
            }
            if (!(t > t0)) continue;
            var b = BandOf(f.ParentBodyName);
            double heat = HeatOf(f.Id);
            if (!b.IsValid)
            {
                heat = Reentry.Cool(heat, t - t0);
                heat += AirlessBorder(f, t);   // an airless body: over the cap at its border, clamped (a jolt)
                lock (_heat) _heat[f.Id] = heat;
                Wear(f, t, heat);
                continue;
            }
            var log = _tickLog; log.Reset();
            double beta = 0;
            if (Reentry.DragDensity > 0 && f.Elements.PeriapsisRadius < b.Top)
            {
                var s0 = OrbitPropagation.StateAt(f.Elements, t0);
                beta = BetaOf(f, Reentry.AirVelocity(s0.Position, s0.Velocity, SpinOf(f.ParentBodyName)));
            }
            var el = Reentry.Advance(f.Elements, b, SpinOf(f.ParentBodyName), Cap, t0, Math.Min(t, t0 + 3600), ref heat, log, beta);
            lock (_heat)
            {
                _heat[f.Id] = heat;
                if (log.Braked) _braking[f.Id] = (log.PeakDecel, t);
            }
            if (!log.Braked)
            {
                // The pass ends on leaving the band through its top (under the cap inside it is still the pass).
                bool outside = OrbitPropagation.StateAt(f.Elements, t).Position.Length() >= b.Top;
                if (outside) lock (_heat) if (_inPass.Remove(f.Id)) EndPass(f, heat);
                continue;
            }
            if (!IsFinite(el)) { FrameHost.Event($"ENTRY frame #{f.Id}: braking gave no orbit (kept the old)"); continue; }
            f.Elements = el;
            braking++;
            // (a whisper of drag - grazing the band's top, a low orbit decaying - is no entry: no plasma, no event)
            // (hysteresis: on at GlowMinDecel, off only under half of it - at the band's edge it flickered on and off)
            bool inPass; lock (_heat) inPass = _inPass.Contains(f.Id);
            bool visible = log.PeakDecel >= (inPass ? 0.5 * GlowMinDecel : GlowMinDecel) || log.Shed > log.DragShed;
            {
                // the STATE on every braked tick (heating, structural loads: any braking is physics), the GLOW only while it
                // shows (a whisper of drag makes no plasma)
                var s = OrbitPropagation.StateAt(el, t);
                Vector3D air = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName));
                double strength = Math.Clamp(Math.Sqrt(log.PeakDecel / GlowFullDecel), 0.15, 1.0);
                double rho = Reentry.Density(b, s.Position.Length());   // (the band's air there: the aero mod's hull heats in it)
                double va = air.Length();
                Vector3D brake = va > 1 && log.PeakDecel > 0 ? air * (-log.PeakDecel / va) : Vector3D.Zero;   // (against the travel)
                double tempK = BorderTemperature(f.ParentBodyName);
                lock (_glow)
                    foreach (long id in f.Members)
                        if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent gm && gm.Entity != null)
                        {
                            _state[gm.Entity] = (air, rho, tempK, brake, t); StateWrites++;
                            if (visible) { _glow[gm.Entity] = (air, strength, t, rho); GlowWrites++; }
                        }
            }
            Wear(f, t, heat);
            bool first = false; if (visible) lock (_heat) first = _inPass.Add(f.Id);
            if (first) FrameHost.Event($"ENTRY frame #{f.Id} into {f.ParentBodyName}'s braking band at {log.ArrivalSpeed:F0} m/s (cap {Cap:F0})");
        }
        lock (_heat)
        {
            var done = _tickDone; done.Clear();
            foreach (var kv in _jolted) if (t - kv.Value > 60) done.Add(kv.Key);
            foreach (long id in done) { _jolted.Remove(id); var fr = SystemHost.Frames?.Get(id); if (fr != null) foreach (long m in fr.Members) _wear.Remove(m); }
            done.Clear();
        }
        _stBraking = braking; _stCap = Cap; _stTicked = true;
        if (++_pruneTick % 60 == 0) PruneFaded();
    }

    // (a grid deleted mid-entry is no member any more - ForgetGlow finds it by its id: its glow and state, keyed by its Entity,
    //  held it for good. Faded to nothing, an entry answers exactly as none: dropped, once a second - 2026-10-15)
    static readonly List<Entity> _faded = new List<Entity>();
    static int _pruneTick;
    static void PruneFaded()
    {
        lock (_glow)
        {
            var dead = _faded; dead.Clear();
            foreach (var kv in _glow) if (!(Fade(kv.Value.t) > 0)) dead.Add(kv.Key);
            foreach (var e in dead) _glow.Remove(e);
            dead.Clear();
            foreach (var kv in _state) if (!(Fade(kv.Value.t) > 0)) dead.Add(kv.Key);
            foreach (var e in dead) _state.Remove(e);
            dead.Clear();
        }
    }

    /// <summary>
    /// An airless body has no band: a frame crossing its border over the cap is slowed to the cap there (the
    /// world would cap it anyway, silently); the speed lost is a jolt, a quarter of the same heat. Returns it.
    /// </summary>
    static double AirlessBorder(ProximityFrame f, double t)
    {
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        if (def == null || def.HasAtmosphere || string.IsNullOrEmpty(def.ParkSubtype)) return 0;
        var s = OrbitPropagation.StateAt(f.Elements, t);
        double r = s.Position.Length();
        Vector3D w = SpinOf(f.ParentBodyName), spin = Vector3D.Cross(w, s.Position), air = s.Velocity - spin;
        double va = air.Length();
        // (at the border - or within the arrival's lead of it: the frame is materialized up to max(0.1 s, 0.05 s x warp) before
        //  the crossing, at the cap, so a jolt checked only inside the shell never came: no heat, no wear, no warning)
        double lead = s.Velocity.Length() * Math.Max(0.1, SystemHost.Timescale * 0.05) + 1.0;
        double shellR = PlanetBerths.ShellRadius(def);
        if (!(r <= shellR || (r <= shellR + lead && Vector3D.Dot(s.Position, s.Velocity) < 0))) return 0;   // (the lead: inbound only)
        if (!(va > Cap + 0.5)) return 0;   // (clamped to the cap last tick: a hair over it after the elements' round trip - not a new jolt)
        var el = Reentry.ToElements(new StateVector(s.Position, spin + air * (Cap / va)), f.Elements.Mu, t);
        if (!IsFinite(el)) return 0;
        f.Elements = el;
        double jolt = Reentry.Jolt(va, Cap);
        FrameHost.Event($"ENTRY frame #{f.Id}: {f.ParentBodyName} has no air: {va:F0} -> {Cap:F0} m/s at its border (a jolt, {Reentry.Share(jolt, false):P0} of tolerance)");
        lock (_heat) _jolted[f.Id] = t;
        return jolt;
    }
    static readonly Dictionary<long, double> _jolted = new Dictionary<long, double>();

    /// <summary>
    /// Heat past a grid's tolerance wears its forward layer (EntryDamage): by how far past it the heat has gone
    /// (beyond what it was already worn for), at most EntryDamage.MaxPerEntry an entry. A grid's tolerance is
    /// set at the start of the entry (doubled by heavy armour in front).
    /// </summary>
    static void Wear(ProximityFrame f, double t, double heat)
    {
        if (heat < Reentry.ToleranceJPerKg) return;   // under even an unshielded tolerance: nothing to do
        var s = OrbitPropagation.StateAt(f.Elements, t);
        Vector3D motion = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName));
        if (motion.LengthSquared() < 1) return;
        motion = Vector3D.Normalize(motion);   // (the berth's world axes are the inertial axes)
        foreach (long id in f.Members)
        {
            if (!GridMembers.IsGridId(id) || !(GridMembers.Get(id) is OrbitalGridComponent g) || !g.IsServer || !GridMembers.IsDynamic(g)) continue;
            (double limit, double upTo, double worn) w;
            lock (_heat)
                if (!_wear.TryGetValue(id, out w))
                {
                    double lim = Reentry.ToleranceJPerKg * (EntryDamage.Shielded(g, motion) ? Reentry.ShieldFactor : 1);
                    w = (lim, lim, 0);
                }
            double room = EntryDamage.MaxPerEntry - w.worn;
            double frac = Math.Min((heat - w.upTo) / w.limit, room);
            // In chunks of 5% (or the last of this entry's allowance), not a sliver every tick.
            if (frac > 1e-4 && (frac >= 0.05 || frac >= room - 1e-9))
            {
                int n = EntryDamage.Apply(g, motion, frac);
                w.worn += frac;
                w.upTo = heat;
                FrameHost.Event($"ENTRY '{g.DisplayName}': too hot ({Reentry.Share(heat, w.limit > Reentry.ToleranceJPerKg):P0} of its tolerance): {n} forward block(s) worn {frac:P0} (this entry {w.worn:P0})");
            }
            if (w.worn >= EntryDamage.MaxPerEntry - 1e-9) w.upTo = double.MaxValue;   // this entry's allowance is spent
            lock (_heat) _wear[id] = w;
        }
    }

    /// <summary>A grid's wear this entry (for OrbitalApi): its heat tolerance (J/kg; doubled by heavy armour in front), the
    /// heat it has been worn up to (J/kg; double.MaxValue once this entry's allowance is spent), and the fraction of its
    /// forward layer worn so far (at most EntryDamage.MaxPerEntry). False: not worn this entry.</summary>
    public static bool TryGetWear(long gridId, out double limit, out double upTo, out double worn)
    {
        lock (_heat)
        {
            if (_wear.TryGetValue(gridId, out var w)) { limit = w.limit; upTo = w.upTo; worn = w.worn; return true; }
            limit = upTo = worn = 0; return false;
        }
    }

    /// <summary>The frame's members' plasma directions and entry states forgotten (an arrival turned them into the planet's
    /// chart; a pass over).</summary>
    public static void ForgetGlow(ProximityFrame f)
    {
        lock (_glow)
            foreach (long id in f.Members)
                if (GridMembers.IsGridId(id) && GridMembers.Get(id) is OrbitalGridComponent gm && gm.Entity != null) { _glow.Remove(gm.Entity); _state.Remove(gm.Entity); }
    }

    static void EndPass(ProximityFrame f, double heat)
    {
        ForgetGlow(f);
        lock (_heat) foreach (long id in f.Members) _wear.Remove(id);
        Log(f, heat);
    }
    static void Log(ProximityFrame f, double heat)
        => FrameHost.Event($"ENTRY frame #{f.Id}: out of the band, heat {Reentry.Share(heat, false):P0} of tolerance, a={f.Elements.SemiMajorAxis / 1000:F0} km e={f.Elements.Eccentricity:F3}");

    static bool IsFinite(KeplerianElements el)
        => !double.IsNaN(el.SemiMajorAxis) && !double.IsInfinity(el.SemiMajorAxis) && !double.IsNaN(el.Eccentricity) && !double.IsNaN(el.MeanMotion);

    // ── your own frame's next pass (client) ──
    public static Pass Prediction;
    public static string PredictedBody;
    /// <summary>The orbit the prediction started from, and the ballistic coefficient it used (kg/m2; 0: no drag).</summary>
    public static KeplerianElements PredictedBefore;
    public static double PredictedBeta;

    /// <summary>The predicted orbit after the coming pass, as a line: "after pass: Ap 212 km (-338) · Pe 61 km (-4)",
    /// "into the air" (the pass reaches the frame's border) or "captured" (an escape orbit made bound). Null: none.</summary>
    public static string AfterLine()
    {
        var p = Prediction;
        if (p == null) return null;
        if (p.Landing) return "into the air";
        if (!p.HasAfter) return null;
        var def = SystemHost.Registry?.FindDefinition(PredictedBody);
        double R = def?.RadiusMeters ?? 0;
        var a = p.After; var b0 = PredictedBefore;
        string pe = $"Pe {(a.PeriapsisRadius - R) / 1000:N0} km";
        if (a.Eccentricity >= 1) return $"after pass: escaping · {pe}";
        double ap = a.SemiMajorAxis * (1 + a.Eccentricity);
        string apS = $"Ap {(ap - R) / 1000:N0} km";
        if (b0.Eccentricity >= 1) return $"after pass: captured · {apS} · {pe}";
        double ap0 = b0.SemiMajorAxis * (1 + b0.Eccentricity);
        return $"after pass: {apS} ({(ap - ap0) / 1000:+0;-0} km) · {pe}";
    }
    static double _predWall;

    public static void ClientTick(double t)
    {
        var f = FrameHost.PlayerFrame;
        if (!Enabled || f == null) { Prediction = null; return; }
        double wall = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (wall - _predWall < 0.5) return;
        _predWall = wall;
        var b = BandOf(f.ParentBodyName);
        if (!b.IsValid) { Prediction = null; AirlessAhead(f, t); return; }
        Airless = null;
        try
        {
            KeplerianElements el; lock (ServerFrames.FramesLock) el = f.Elements;
            double horizon = el.Eccentricity < 1 ? Math.Min(el.Period, 6 * 3600) : 6 * 3600;
            double beta = 0;
            if (Reentry.DragDensity > 0 && el.PeriapsisRadius < b.Top)
            {
                beta = BetaCached(f);   // (the client: the server measured it)
            }
            var p = Reentry.Predict(el, b, SpinOf(f.ParentBodyName), Cap, t, horizon, HeatOf(f.Id), beta);
            Prediction = p.Braked ? p : null;
            PredictedBefore = el;
            PredictedBeta = beta;
            PredictedBody = f.ParentBodyName;
        }
        catch (Exception e) { FrameHost.Fault("entry prediction", e); Prediction = null; }
    }

    /// <summary>An airless body ahead that you would reach over the cap: when, and how fast (null: none).</summary>
    public static (double t, double speed, string body)? Airless;

    static void AirlessAhead(ProximityFrame f, double t)
    {
        Airless = null;
        var def = SystemHost.Registry?.FindDefinition(f.ParentBodyName);
        if (def == null || def.HasAtmosphere || string.IsNullOrEmpty(def.ParkSubtype)) return;
        KeplerianElements el; lock (ServerFrames.FramesLock) el = f.Elements;
        double shell = PlanetBerths.ShellRadius(def);
        if (!(el.PeriapsisRadius < shell) || !OrbitPropagation.TryTimeToRadius(el, shell, out _, out double tIn)) return;
        double tc = FrameHost.NextInboundCrossingPublic(el, tIn, t);
        if (double.IsNaN(tc) || tc < t) return;
        var s = OrbitPropagation.StateAt(el, tc);
        double va = Reentry.AirVelocity(s.Position, s.Velocity, SpinOf(f.ParentBodyName)).Length();
        if (va > Cap) Airless = (tc, va, f.ParentBodyName);
    }

    /// <summary>When warp must stop: the lead time before the braking starts (NaN: none ahead).</summary>
    public static double NextWarpStop(double now)
    {
        var air = Airless;
        if (air.HasValue && air.Value.t - WarpLead > now) return air.Value.t - WarpLead;   // (an airless border: before it)
        var p = Prediction;
        // (only BEFORE a pass: once in it, warp is yours again; the braking is warp-proof)
        var f = FrameHost.PlayerFrame;
        if (p == null || double.IsNaN(p.EnterTime) || p.EnterTime < now + 1 || (f != null && Braking(f.Id, out _))) return double.NaN;
        if (now > p.EnterTime - WarpLead) return double.NaN;   // already stopped once for it
        return p.EnterTime - WarpLead;
    }

    /// <summary>The orbit card's line: the next pass (ahead), or the braking now.</summary>
    public static string CardLine(double now)
    {
        var f = FrameHost.PlayerFrame;
        if (f == null) return null;
        double heat = HeatOf(f.Id);
        if (Braking(f.Id, out double decel))
            return $"Entry · braking {decel / 9.81:F1} g · heat {Reentry.Share(heat, false):P0}";
        var air = Airless;
        if (air.HasValue)
        {
            string at = air.Value.t - now <= 10 ? "now" : "in " + Maneuvers.Clock(air.Value.t - now);
            double jolt = Reentry.Share(Reentry.Jolt(air.Value.speed, Cap), false);
            return $"No air {at} · {air.Value.speed:N0} > {Cap:N0} m/s · burn to slow (jolt {jolt:P0})";
        }
        var p = Prediction;
        if (p == null || double.IsNaN(p.EnterTime)) return null;
        string when = p.EnterTime - now <= 10 ? "now" : "in " + Maneuvers.Clock(p.EnterTime - now);
        string to = double.IsNaN(p.SpeedAtBottom) ? "aerobrake" : $"{p.ArrivalSpeed:N0} → {p.SpeedAtBottom:N0} m/s";
        double share = Reentry.Share(p.PeakHeat, false);
        string after = AfterLine();
        if (p.HasAfter && p.DragShed > 0 && p.DragShed >= p.Shed * 0.5) to = "aerobrake";
        return $"Entry {when} · {to} · heat {share:P0}{(share > 1 ? " — too hot" : "")}{(after != null ? " · " + after : "")}";
    }
}
