using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Materials;
using Keen.VRage.Core.Render.Materials.Templates;
using Keen.VRage.Core.Game.Systems;

#pragma warning disable
namespace OrbitalMod;

/// <summary>
/// DEV SPIKE: reentry plasma as our own mesh instead of particles - textured quads (streamers off the frontal
/// outline, a glow at the nose) cut from a particle atlas by UV, on the engine's transparent material with no opacity
/// term (it adds its light, as the map rings do). The mesh is built in the grid's own space, follows the grid's
/// transform every frame, and is rebuilt every few frames with the next atlas cells (the animation).
/// Harness: plasmaspike &lt;gridId&gt; &lt;noseX&gt; &lt;noseY&gt; &lt;noseZ&gt; &lt;radius&gt; &lt;axis: +x|-x|+y|-y|+z|-z&gt; [emitterGuid] [cells]
///          plasmaspike off; plasmaset &lt;knob&gt; &lt;value&gt; (Emissive, Length, Width, Flare, Count, Every, GlowSize).
/// </summary>
public static class PlasmaSpike
{
    public static float Emissive = 20f, Length = 4f, Width = 0.35f, Flare = 0.6f, GlowSize = 2.2f, SpecA = 0f, Fresnel = 1f;
    public static int Count = 16, Every = 4;
    /// <summary>0: separate streamers (each its own cell); 1: one grid sheet - a sleeve off the frontal outline and a
    /// cap on the nose - whose UVs together cover ONE atlas cell, all of it stepping to the next cell (the animation).</summary>
    public static int Mode = 1, Around = 32, Along = 8;
    /// <summary>Mode 2, the bow shock: a sheet standing off the hull's windward surface (the front-most hull point
    /// along the flow in each cell of a Res x Res grid across it: the shadow map's lit surface), blurred Blur times so it
    /// follows the hull loosely, pushed upwind by Standoff x R, and past the silhouette swept back downwind
    /// (Sweep x d^2 / R) out to Margin x R. Its UVs span one atlas cell.</summary>
    public static float Standoff = 0.4f, Sweep = 1.0f, Margin = 0.8f;   // (0.4 and Blur 12: rounder, smoother)
    /// <summary>The shock never closer to the hull than MinStandoff x R (after the blur: a nose's lone peak was
    /// averaged down and poked through); the hull's peaks grown Dilate cells first; the sheet's outer band (past
    /// TrimPast of the margin: black texture, only the shader's grey sheen) not drawn.</summary>
    public static float MinStandoff = 0.12f, TrimPast = 0.85f;
    public static int Dilate = 2;
    public static int Res = 32, Blur = 12;   // (64 / 48 tried: finer, but the texture was the problem)
    /// <summary>Mode 3, the look prototyped offline (D:\aero\tools\plasma_proto\proto.py): the bow shock as Shells
    /// sheets ShellStep x R apart (the atlas in its own colours), and a trailing skirt - the shock continued past the
    /// silhouette downwind - in three overlapping sleeves (lengths SkirtLen x R x 0.45 / 0.75 / 1, hue-replaced orange,
    /// red, violet), each round the ship in SkirtTiles overlapping strips of one frame stretched along the flow.
    /// The frames flicker between FrameLo and FrameHi (ping-pong at FrameFps), not the atlas's whole sequence.</summary>
    public static int Shells = 4, SkirtTiles = 9, FrameLo = 10, FrameHi = 16, SkirtFrame = 12;
    /// <summary>Segments down each skirt strip; the rim (the skirt's start round the silhouette) smoothed RimSmooth
    /// passes (a ragged hull outline made a jagged skirt). Single-image textures (an emitter with a 1 x 1 "atlas"):
    /// TileU x TileV repeats over each sheet (the texture's own detail, not one 128 px cell stretched over the ship),
    /// scrolled downwind at Scroll repeats a second (the motion, in place of the atlas frames).</summary>
    public static int SkirtAlong = 12, RimSmooth = 0;
    public static float TileU = 3f, TileV = 2f, Scroll = 0.6f;
    public static float ShellStep = 0.1f, SkirtFlare = 0.35f, SkirtLen = 3f, SkirtOverlap = 0.6f, SkirtV0 = 0.25f, SkirtV1 = 0.97f, FrameFps = 8f;
    /// <summary>The skirts' colouring (hue, saturation replaced; value shifted): orange, red, violet.</summary>
    public static float[] SkirtHue = { 0.06f, 0.015f, 0.78f }, SkirtSat = { 0.75f, 0.85f, 0.75f }, SkirtDim = { 0f, -0.05f, -0.05f };
    public static string Status = "off";
    /// <summary>The plasma's own light (the mesh is lit by the scene - PBRTransparent: glass - so at night only a light
    /// makes it, and the hull, glow): the aero mod's light-only effect (Assets/Particles/PlasmaLight_1000, 1000 at
    /// scale 1, orange; tools/particles/gen_plasma_light.py) between the hull's nose and the shock. Its intensity is
    /// 1000 x scale^2 (ParticleLightEntityComponent), so Light (intensity) sets the scale; extended radius on (the
    /// default cap would cut its reach); 0: none. LightAhead: where, as a fraction of the way from the hull to the shock.</summary>
    public static float Light = 16000f, LightAhead = 0.5f;
    public static float[] LightTint = { 1f, 1f, 1f };
    private static readonly Guid LightEffect = new Guid("8a7994a9-ef54-5cef-909a-c86f27aa0f27");
    private static object _light, _lightParams;
    private static Vector3 _lightAt;
    private static string _lightWhy;

    // PBRTransparent, two-sided (as MapRingMesh)
    private static readonly Guid LitState = new Guid("5c12f1bd-f4c8-4626-86b5-0778e0613762");
    // the same state on the UNLIT transparent pass (the aero mod's Assets/Plasma/PlasmaTransparentUnlit_State.def):
    // lit, the plasma was dull glass at night; unlit, the atlas (alpha 1: "metal") is added as light / exposure
    private static readonly Guid UnlitState = new Guid("6f24f63f-f546-5f0d-b602-8529fdf7561c");
    public static bool Unlit = false;   // (unlit drew flat grey: the lit state + the plasma's own light instead)
    private static Guid TransparentState => Unlit ? UnlitState : LitState;
    // the meteor fire's main emitter (V3): an 8x8 animated flame atlas
    private static Guid _emitter = new Guid("7ad5b453-1c7e-41a6-b0be-43727eee6b3f");
    // the atlas as the emitter plays it: its grid (columns x rows), the frames it uses (from First, Count of them) and
    // their rate - an atlas often uses only part of its sheet (the meteor fire: frames 32-63 of 8 x 8, at 24 a second)
    private static int _cellsX = 8, _cellsY = 8, _first, _count = 64;
    private static float _fps = 24f;
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static int _shownFrame = -1;
    /// <summary>The frame now: First + (seconds x rate) mod Count.</summary>
    private static int FrameNow()
    {
        if (Mode == 3 && _cellsX == 1 && _cellsY == 1) return (int)(_clock.Elapsed.TotalSeconds * FrameFps);
        if (Mode == 3)
        {
            int n = Math.Max(1, FrameHi - FrameLo), k = (int)(_clock.Elapsed.TotalSeconds * FrameFps) % (2 * n);
            return FrameLo + (k <= n ? k : 2 * n - k);   // ping-pong
        }
        return _first + (int)(_clock.Elapsed.TotalSeconds * _fps) % Math.Max(1, _count);
    }

    private static long _grid;
    private static Vector3 _nose, _axis;
    private static float _radius;
    private static bool _on;
    private static PBRTransparentMaterialDefinition _mat, _matShock, _matSkirt;
    // the aero mod's plasma materials (tools/particles/gen_plasma_tex.py): textures laid out as the transparent shader
    // reads them; a 4 x 2 atlas of Fire_A frames 9..16
    private static readonly Guid ShockMat = new Guid("77d70b1d-f064-5102-be1d-9dd028f505b7"), SkirtMat = new Guid("4eb8f117-0131-5789-9e83-08aabe263a56");
    private const int PlasmaAtlasFirst = 9;
    // the safe-zone (unlit, additive) plasma material and its state; the colours: the shock, then the three skirts
    private static readonly Guid GlowMat = new Guid("5b9374a6-ef3f-5cbc-9720-e41d7fb2aa21"), GlowState = new Guid("6566e25c-7571-4f2b-b74b-3298226a80e6");
    private static Definition _matGlow;
    /// <summary>Mode 4, calibration: eight flat patches beside the ship, each one uniform (every corner on the same
    /// texel, CalibUV, of the glow atlas), white at CalibLevels (sRGB) - a screenshot gives the game's curve.</summary>
    public static float[] CalibLevels = { 1f, 0.7f, 0.5f, 0.35f, 0.25f, 0.18f, 0.12f, 0.08f };
    public static Vector2 CalibUV = new Vector2(0.125f, 0.25f);
    /// <summary>The patches' colour (x each level; live: CalR CalG CalB) - white, or a hue to see how the game keeps it.</summary>
    public static float[] CalibTint = { 1f, 1f, 1f };
    public static float CalibSize = 8f;
    /// <summary>The colours (sRGB, packed 8-bit): the shock, then the skirts - calibrated so the game shows the offline
    /// prototype's look (tools/plasma_proto): measured, the game shows glow g as Reinhard(1.77 g) at night, and the
    /// material's strength is 4; Bright scales them all (live).</summary>
    public static float[][] GlowColor = { new[] { 0.278f, 0.181f, 0.074f }, new[] { 0.254f, 0.195f, 0.118f }, new[] { 0.241f, 0.111f, 0.079f }, new[] { 0.207f, 0.111f, 0.241f } };
    public static float Bright = 1f;
    /// <summary>Mode 3's material: false (default) the game's own particle atlas as it is (the meteor fire, on the
    /// transparent material: the first in-game look); true the generated glow textures on the safe-zone material.</summary>
    public static bool UseGlow = false;
    /// <summary>Saturation (live): each colour pushed from its grey (linear) by this - the game's tone map pulls bright
    /// colours to white, so they need more than the prototype's.</summary>
    public static float Sat = 1f;
    // one model entity per slot (a tint is per model): 0 the shock (or modes 0-2), 1-3 the skirts
    private const int Slots = 8;
    private static readonly object[] _roots = new object[Slots], _models = new object[Slots], _runtimes = new object[Slots];
    private static readonly bool[] _colored = new bool[Slots];
    private static object _model => _models[0];
    private static int _frame, _tick;

    public static string Command(string[] a)
    {
        if (a.Length > 1 && a[1] == "off") { Dispose(); StopLight(); StopPuffs(); _client = null; _on = false; Status = "off"; return "plasma spike off"; }
        if (a.Length < 7) return "usage: plasmaspike <gridId> <noseX> <noseY> <noseZ> <radius> <axis> [emitterGuid] [cells] | plasmaspike off";
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _grid = long.Parse(a[1]);
        _nose = new Vector3(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci));
        _radius = float.Parse(a[5], ci);
        _axis = a[6] switch { "+x" => Vector3.UnitX, "-x" => -Vector3.UnitX, "+y" => Vector3.UnitY, "-y" => -Vector3.UnitY, "+z" => Vector3.UnitZ, "-z" => -Vector3.UnitZ, _ => ParseAxis(a[6]) };
        if (a.Length > 7) { var g = new Guid(a[7]); if (g != _emitter) { _emitter = g; Dispose(); _mat = null; } }
        _hull = null; _hullWhy = null;
        StopLight(); StopPuffs(); _lightAt = default; _client = null; _shapeKey = null;
        _on = true; _shownFrame = -1;
        return $"plasma spike on grid {_grid}: nose {_nose} radius {_radius} axis {_axis}";
    }

    /// <summary>Any flow axis, "x,y,z" (grid space, normalised): e.g. 30 degrees nose up on a +x ship "0.866,-0.5,0".</summary>
    private static Vector3 ParseAxis(string s)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var p = s.Split(',');
        if (p.Length != 3) return -Vector3.UnitZ;
        var v = new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
        return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : -Vector3.UnitZ;
    }

    public static string Set(string knob, string value)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float v = float.Parse(value, ci);
        switch (knob)
        {
            case "Emissive": Emissive = v; Dispose(); _mat = null; break;   // (a material property: a new material)
            case "SpecA": SpecA = v; Dispose(); _mat = null; break;
            case "Fresnel": Fresnel = v; Dispose(); _mat = null; break;
            case "Length": Length = v; break;
            case "Width": Width = v; break;
            case "Flare": Flare = v; break;
            case "GlowSize": GlowSize = v; break;
            case "Count": Count = (int)v; break;
            case "Mode": Mode = (int)v; Dispose(); StopPuffs(); _shownFrame = -1; break;
            case "PuffTest": PuffTest = (int)v; StopPuffs(); break;
            case "FrontEvery": FrontEvery = Math.Max(0, (int)v); StopPuffs(); break;
            case "FrontDepth": FrontDepth = v; StopPuffs(); break;
            case "SparkBright": SparkBright = v; StopPuffs(); break;
            case "SparkRate": SparkRate = (int)v is 100 or 75 or 50 or 25 or 12 or 6 ? (int)v : SparkRate; StopPuffs(); break;
            case "LineDensity": LineDensity = Math.Clamp((int)v, 1, 8); StopPuffs(); break;
            case "SparkLevel": SparkLevel = Math.Clamp((int)v, 1, 5); StopPuffs(); break;
            case "SparkColor": SparkColor = (int)v; StopPuffs(); break;
            case "PuffSource": PuffSource = Math.Clamp((int)v, 0, 7); StopPuffs(); break;
            case "SegLen": SegLen = Math.Max(0.5f, v); StopPuffs(); break;
            case "LineScale": LineScale = Math.Max(0.1f, v); StopPuffs(); break;
            case "LineSmooth": LineSmooth = Math.Max(0, (int)v); StopPuffs(); break;
            case "EdgeEvery": EdgeEvery = Math.Max(1, (int)v); StopPuffs(); break;
            case "SurfaceLift": SurfaceLift = v; StopPuffs(); break;
            case "Spot": Spot = v; StopPuffs(); break;
            case "SpawnPerTick": SpawnPerTick = Math.Max(1, (int)v); break;
            case "SpotDist": SpotDist = v; StopPuffs(); break;
            case "SpotCover": SpotCover = v; StopPuffs(); break;
            case "SpotFalloff": SpotFalloff = v; StopPuffs(); break;
            case "SpotShadow": SpotShadow = v != 0; StopPuffs(); break;
            case "SpotFlip": SpotFlip = v != 0; StopPuffs(); break;
            case "LightGrid": LightGrid = Math.Max(0, (int)v); StopPuffs(); StopLight(); break;
            case "LightEach": LightEach = v; StopPuffs(); break;
            case "LightLift": LightLift = v; StopPuffs(); break;
            case "LightMax": LightMax = Math.Max(1, (int)v); StopPuffs(); break;
            case "SparkSpeed": SparkSpeed = v; StopPuffs(); break;
            case "Sparks": Sparks = Math.Clamp((int)v, 0, 2); StopPuffs(); break;
            case "PuffEvery": PuffEvery = Math.Max(1, (int)v); StopPuffs(); break;
            case "StreamEvery": StreamEvery = Math.Max(1, (int)v); StopPuffs(); break;
            case "PuffScale": PuffScale = v; StopPuffs(); break;
            case "StreamScale": StreamScale = v; StopPuffs(); break;
            case "SkirtAlong": SkirtAlong = Math.Clamp((int)v, 2, 64); break;
            case "RimSmooth": RimSmooth = Math.Max(0, (int)v); _shapeKey = null; break;
            case "TileU": TileU = v; break;
            case "TileV": TileV = v; break;
            case "Scroll": Scroll = v; break;
            case "Light": Light = v; _lightSet = false; if (v <= 0f) StopLight(); break;
            case "LightAhead": LightAhead = v; _lightAt = default; StopLight(); break;   // (respawned where it now goes)
            case "LightR": LightTint[0] = v; _lightSet = false; break;
            case "LightG": LightTint[1] = v; _lightSet = false; break;
            case "LightB": LightTint[2] = v; _lightSet = false; break;
            case "Around": Around = Math.Max(3, (int)v); break;
            case "Along": Along = Math.Max(1, (int)v); break;
            case "Every": Every = Math.Max(1, (int)v); break;
            case "Standoff": Standoff = v; _shapeKey = null; break;
            case "MinStandoff": MinStandoff = v; break;
            case "TrimPast": TrimPast = v; break;
            case "Dilate": Dilate = Math.Max(0, (int)v); break;
            case "Sweep": Sweep = v; break;
            case "Margin": Margin = v; break;
            case "Res": Res = Math.Clamp((int)v, 8, 96); break;
            case "Blur": Blur = Math.Max(0, (int)v); break;
            case "Shells": Shells = Math.Clamp((int)v, 1, 8); break;
            case "ShellStep": ShellStep = v; break;
            case "SkirtTiles": SkirtTiles = Math.Clamp((int)v, 1, 32); break;
            case "SkirtLen": SkirtLen = v; break;
            case "SkirtFlare": SkirtFlare = v; break;
            case "SkirtOverlap": SkirtOverlap = v; break;
            case "SkirtV0": SkirtV0 = v; break;
            case "SkirtV1": SkirtV1 = v; break;
            case "FrameLo": FrameLo = (int)v; break;
            case "FrameHi": FrameHi = (int)v; break;
            case "FrameFps": FrameFps = v; break;
            case "SkirtFrame": SkirtFrame = (int)v; break;
            case "Glow": Emissive = v; Dispose(); _matGlow = null; _mat = null; break;   // (the glow material's strength)
            case "Bright": Bright = v; for (int i = 0; i < Slots; i++) _colored[i] = false; break;   // (re-applied next build)
            case "Unlit": Unlit = v != 0; Dispose(); _mat = null; break;
            case "UseGlow": UseGlow = v != 0; Dispose(); _mat = null; _matGlow = null; break;
            case "Sat": Sat = v; for (int i = 0; i < Slots; i++) _colored[i] = false; break;
            case "CalR": CalibTint[0] = v; for (int i = 0; i < Slots; i++) _colored[i] = false; break;
            case "CalG": CalibTint[1] = v; for (int i = 0; i < Slots; i++) _colored[i] = false; break;
            case "CalB": CalibTint[2] = v; for (int i = 0; i < Slots; i++) _colored[i] = false; break;
            case "CalibU": CalibUV.X = v; break;
            case "CalibV": CalibUV.Y = v; break;
            case "Dim0": SkirtDim[0] = v; _colored[1] = false; break;
            case "Dim1": SkirtDim[1] = v; _colored[2] = false; break;
            case "Dim2": SkirtDim[2] = v; _colored[3] = false; break;
            default: return "knobs: Light LightAhead LightR LightG LightB Emissive SpecA Length Width Flare GlowSize Count Every Mode Around Along Standoff Sweep Margin Res Blur Shells ShellStep SkirtTiles SkirtLen SkirtFlare SkirtOverlap SkirtV0 SkirtV1 FrameLo FrameHi FrameFps SkirtFrame Dim0 Dim1 Dim2";
        }
        return $"plasma spike {knob}={value}";
    }

    /// <summary>Client tick: follow the grid; every Every ticks, the next atlas cells.</summary>
    public static void Tick()
    {
        if (!_on) return;
        try
        {
            var g = GridMembers.Get(_grid);
            if (g?.Entity == null) { Status = "no grid " + _grid; return; }
            // everything drawn follows the CLIENT copy (the one rendered); the server copy only gives the hull's shape
            // (the aero mod's chunk table lives there)
            _client ??= ClientTwin(g);
            if (_client == null) { Status = "no client copy of grid " + _grid; return; }
            var wt = _client.Data.GetWorldTransform();
            if (_mat == null && (Status = MakeMaterial()) != null) { _on = false; return; }
            int fr = FrameNow();
            if (Mode == 5 ? (!_fieldUp && _puffWhy == null) || _lightAt == default : _model == null || fr != _shownFrame)
            {
                _frame = fr;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                string why = Build(wt);
                _buildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (why != null) { Status = "build: " + why; _on = false; Dispose(); return; }
                _shownFrame = fr;
            }
            for (int k = 0; k < Slots; k++) if (_roots[k] != null) PlanetRenderBridge.UpdateRootTransform(_roots[k], wt);
            if (_spotRoot != null) PlanetRenderBridge.UpdateRootTransform(_spotRoot, wt);
            if (_spawnQueue.Count > 0)
            {
                _draining = true;
                try { for (int k = 0; k < SpawnPerTick && _spawnQueue.Count > 0 && _puffWhy == null; k++) _spawnQueue.Dequeue()(); }
                finally { _draining = false; }
            }
            if ((Mode == 3 || (Mode == 5 && LightGrid == 0 && Spot <= 0f)) && Light > 0f && _light == null && _lightAt != default && _lightWhy == null) SpawnLight(_client);
            if (_light != null && !_lightSet) SetLight();
            Status = $"on: grid {_grid} frame {_frame} (atlas {_cellsX}x{_cellsY}, frames {_first}+{_count} at {_fps}/s) build {_buildMs:F1} ms {_n} verts"
                   + (_light != null ? $" light {Light:F0} at {_lightAt}" : _lightWhy != null ? " light: " + _lightWhy : "")
                   + (Mode == 5 ? $" puffs {_puffs.Count}" + (_puffWhy != null ? " (" + _puffWhy + ")" : "")
                                + (_lineBad > 0 ? $" badlines {_lineBad}" : "") + (_spot != null ? " spot" : _spotWhy != null ? " spot: " + _spotWhy : "") + $" lights {_gridLights.Count}" + (_gridLightWhy != null ? " (" + _gridLightWhy + ")" : "") : "");
        }
        catch (Exception e) { Status = "failed: " + PlanetRenderBridge.Inner(e); _on = false; Dispose(); StopLight(); StopPuffs(); }
    }

    /// <summary>A runtime copy of a shipped plasma material (its textures), with this spike's emissive and specular knobs.</summary>
    private static string CopyMaterial(Guid id, out PBRTransparentMaterialDefinition mat)
    {
        mat = null;
        if (!DefinitionManager.Instance.TryGetDefinition(TransparentState, out MaterialStateDefinition state)) return "PBRTransparent material state not found";
        if (!DefinitionManager.Instance.TryGetDefinition(id, out PBRTransparentMaterialDefinition src) || src == null) return "plasma material not loaded: " + id;
        var ob = DefinitionHelper.CreateObjectBuilder<PBRTransparentMaterialDefinitionObjectBuilder>();
        ob.DefaultState = state;
        var t = typeof(PBRTransparentMaterialDefinitionObjectBuilder);
        foreach (var name in new[] { "ColorMetalTexture", "ExtensionsTexture", "NormalRoughnessTexture" })
        {
            object h = src.GetType().GetProperty(name)?.GetValue(src) ?? src.GetType().GetField(name)?.GetValue(src);
            if (h != null) t.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(ob, h);
        }
        ob.EmissivityMultiplier = Emissive;
        ob.SpecularBaseColor = new ColorSRGB(0f, 0f, 0f, SpecA);
        ob.FresnelTightness = Fresnel;
        mat = RuntimeDefinitionHelper.Create<PBRTransparentMaterialDefinition>(ob, null, keepBuilderGuid: true);
        mat.SetRuntimeState(state);
        if (!PlanetRings.MaterialSystemCall("AddRuntimeMaterial", mat)) return "material registration failed";
        return null;
    }

    /// <summary>A runtime copy of the plasma glow material (safe-zone template) with this spike's strength (Emissive).</summary>
    private static string GlowMaterial()
    {
        // the shipped material as it is (content materials are registered by the engine); its strength is in the
        // .def (tools/particles/gen_plasma_tex.py) - a runtime copy by reflection failed (no object builder)
        if (!DefinitionManager.Instance.TryGetDefinition(GlowMat, out Definition src) || src == null) return "plasma glow material not loaded: " + GlowMat;
        _matGlow = src;
        return null;
    }

    private static string MakeMaterial()
    {
        if ((Mode == 3 && UseGlow) || Mode == 4)
        {
            string gw = GlowMaterial();
            if (gw != null) return gw;
            _cellsX = 4; _cellsY = 2; _first = 0; _count = 8;
            return null;
        }
        if (!DefinitionManager.Instance.TryGetDefinition(TransparentState, out MaterialStateDefinition state)) return "PBRTransparent material state not found";
        if (!DefinitionManager.Instance.TryGetDefinition(_emitter, out Definition em) || em == null) return "emitter definition not found: " + _emitter;
        // its atlas: AnimationParameters.AtlasTexture (a ResourceHandle<TextureAsset>, as the material's textures)
        object anim = em.GetType().GetProperty("AnimationParameters")?.GetValue(em) ?? em.GetType().GetField("AnimationParameters")?.GetValue(em);
        object atlas = anim?.GetType().GetField("AtlasTexture")?.GetValue(anim);
        if (atlas == null) return "emitter has no atlas";
        // its grid (columns x rows) and frame count: a strip (8 x 1), a sheet (8 x 8) or one image (1 x 1)
        object grid = anim.GetType().GetField("NumFramesInAtlas")?.GetValue(anim);
        _cellsX = Math.Max(1, Convert.ToInt32(grid?.GetType().GetField("X")?.GetValue(grid) ?? 1));
        _cellsY = Math.Max(1, Convert.ToInt32(grid?.GetType().GetField("Y")?.GetValue(grid) ?? 1));
        _first = Math.Max(0, Convert.ToInt32(anim.GetType().GetField("FirstFrameIndex")?.GetValue(anim) ?? 0));
        _count = Convert.ToInt32(anim.GetType().GetField("NumFramesInAnimation")?.GetValue(anim) ?? 0);
        if (_count <= 0 || _first + _count > _cellsX * _cellsY) _count = Math.Max(1, _cellsX * _cellsY - _first);
        _fps = Convert.ToSingle(anim.GetType().GetField("AnimationFramerate")?.GetValue(anim) ?? 24f);
        if (!(_fps > 0f)) _fps = 24f;
        var ob = DefinitionHelper.CreateObjectBuilder<PBRTransparentMaterialDefinitionObjectBuilder>();
        ob.DefaultState = state;
        var fColor = typeof(PBRTransparentMaterialDefinitionObjectBuilder).GetField("ColorMetalTexture", BindingFlags.Public | BindingFlags.Instance);
        var fExt = typeof(PBRTransparentMaterialDefinitionObjectBuilder).GetField("ExtensionsTexture", BindingFlags.Public | BindingFlags.Instance);
        fColor.SetValue(ob, atlas); fExt.SetValue(ob, atlas);
        ob.EmissivityMultiplier = Emissive;
        ob.SpecularBaseColor = new ColorSRGB(0f, 0f, 0f, SpecA);
        ob.FresnelTightness = Fresnel;
        var mat = RuntimeDefinitionHelper.Create<PBRTransparentMaterialDefinition>(ob, null, keepBuilderGuid: true);
        mat.SetRuntimeState(state);
        if (!PlanetRings.MaterialSystemCall("AddRuntimeMaterial", mat)) return "material registration failed";
        _mat = mat;
        return null;
    }

    private static Type _tRmd, _tVs0, _tVs1, _tSub, _tRuntimeModel;
    private static object _cullNone;
    private static MethodInfo _createRuntimeModel, _toHandle, _add0, _add1;

    private static string Resolve()
    {
        if (_tRmd != null) return null;
        var render = PlanetRenderBridge.RenderAssembly;
        if (render == null) return "VRage.Render not loaded";
        _tVs0 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatPositionUV0Packed");
        _tVs1 = render.GetType("Keen.VRage.Render.Data.VertexFormat.VertexFormatNormalTangentPacked");
        _tSub = render.GetType("Keen.VRage.Render.Data.IRuntimeMeshData+SubPart");
        _tRuntimeModel = render.GetType("Keen.VRage.Render.Contracts.RuntimeModel");
        _cullNone = render.GetType("Keen.VRage.Render.Data.BackfaceCullingCone")?.GetProperty("CullNone", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var rmd = render.GetType("Keen.VRage.Render.Data.RuntimeMeshData`3");
        foreach (var m in PlanetRenderBridge.Contracts?.GetType().GetMethods() ?? new MethodInfo[0])
            if (m.Name == "CreateRuntimeModel" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType.Name == "IRuntimeMeshData") _createRuntimeModel = m;
        _toHandle = _tRuntimeModel?.GetMethod("op_Implicit", new[] { _tRuntimeModel });
        if (_tVs0 == null || _tVs1 == null || _tSub == null || rmd == null || _createRuntimeModel == null || _toHandle == null) return "runtime mesh types not found";
        _tRmd = rmd.MakeGenericType(_tVs0, _tVs1, typeof(Keen.VRage.Core.Render.Data.VertexFormatNull));
        return null;
    }

    private static object NewBuffer(Type elem) =>
        Activator.CreateInstance(typeof(Buffer<>).MakeGenericType(elem), new object[] { Allocator.Heap, "PlasmaSpike" });

    private static object _vs0, _vs1;
    private static Buffer<int> _idx;
    private static readonly object[] _a1 = new object[1], _a2 = new object[2];
    private static int _n;

    private static void Vert(Vector3 p, Vector2 uv, Vector3 n)
    {
        _a2[0] = p; _a2[1] = uv; _a1[0] = Activator.CreateInstance(_tVs0, _a2); _add0.Invoke(_vs0, _a1);
        _a2[0] = n; _a2[1] = new Vector4(Vector3.Normalize(Vector3.Cross(n, Math.Abs(n.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)), 1f);
        _a1[0] = Activator.CreateInstance(_tVs1, _a2); _add1.Invoke(_vs1, _a1);
        _n++;
    }

    /// <summary>A quad p0 p1 (start edge) p2 p3 (end edge), the atlas cell c stretched over it (u across, v along).</summary>
    private static void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int c)
    {
        float sx = 1f / _cellsX, sy = 1f / _cellsY; int cx = c % _cellsX, cy = (c / _cellsX) % _cellsY;
        float u0 = cx * sx, v0 = cy * sy, u1 = u0 + sx, v1 = v0 + sy;
        var n = Vector3.Cross(p1 - p0, p3 - p0); n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY;
        int b = _n;
        Vert(p0, new Vector2(u0, v1), n); Vert(p1, new Vector2(u1, v1), n); Vert(p2, new Vector2(u1, v0), n); Vert(p3, new Vector2(u0, v0), n);
        _idx.Add(b); _idx.Add(b + 1); _idx.Add(b + 2); _idx.Add(b); _idx.Add(b + 2); _idx.Add(b + 3);
    }

    private static float _shapeR;

    /// <summary>The hull points, the nose, the flow axis and R, to a file (the offline prototype renders the same input).</summary>
    public static string Dump(string path)
    {
        var g = GridMembers.Get(_grid);
        var pts = g?.Entity != null ? HullPoints(g.Entity) : null;
        if (pts == null) return _hullWhy ?? "no grid";
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append($"axis {_axis.X.ToString(ci)} {_axis.Y.ToString(ci)} {_axis.Z.ToString(ci)}\n");
        sb.Append($"nose {_nose.X.ToString(ci)} {_nose.Y.ToString(ci)} {_nose.Z.ToString(ci)}\n");
        foreach (var q in pts) sb.Append($"{q.X.ToString(ci)} {q.Y.ToString(ci)} {q.Z.ToString(ci)}\n");
        System.IO.File.WriteAllText(path, sb.ToString());
        return $"{pts.Count} hull points to {path}";
    }

    private static List<Vector3> _hull;
    private static string _hullWhy;

    /// <summary>The hull's face centres (grid space, m), from the aero mod's chunk table (AeroGridComponent._chunks:
    /// every chunk's face centres, ChunkedTable.Occluders, as 1/8 m packed points).</summary>
    private static List<Vector3> HullPoints(Keen.VRage.DCS.Components.Entity grid)
    {
        if (_hull != null || _hullWhy != null) return _hull;
        try
        {
            object aero = null;
            foreach (var c in grid.Components) if (c != null && c.GetType().FullName == "AeroMod.AeroGridComponent") { aero = c; break; }
            if (aero == null) { _hullWhy = "no aero component on the grid"; return null; }
            object chunks = aero.GetType().GetField("_chunks", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(aero);
            if (chunks == null) { _hullWhy = "the grid has no chunk table yet"; return null; }
            var occ = chunks.GetType().GetField("Occluders")?.GetValue(chunks) as System.Collections.IEnumerable;
            if (occ == null) { _hullWhy = "no Occluders"; return null; }
            var pts = new List<Vector3>();
            PropertyInfo pv = null;
            foreach (System.Collections.IEnumerable list in occ)
                foreach (object p16 in list)
                {
                    pv ??= p16.GetType().GetProperty("V");
                    pts.Add((Vector3)pv.GetValue(p16));
                }
            if (pts.Count == 0) { _hullWhy = "no hull points"; return null; }
            _hull = pts;
            return pts;
        }
        catch (Exception e) { _hullWhy = "hull points: " + PlanetRenderBridge.Inner(e); return null; }
    }

    /// <summary>The bow shock sheet over the hull's windward surface (see <see cref="Standoff"/>).</summary>
    private static void BuildShock(Vector3 f, Vector3 a, Vector3 b, float R, int cell, List<Vector3> pts)
    {
        ShockGrid(f, a, b, R, pts, out var P3, out _);
        int N = Res;
        Sheet(N, N, (u, v) => P3[(int)MathF.Round(u * N), (int)MathF.Round(v * N)], (u, v) => f, cell);
    }

    /// <summary>The shock surface: positions on a (Res+1)^2 grid, and how far past the hull each is (0 on it, 1 at the margin).</summary>
    private static void ShockGrid(Vector3 f, Vector3 a, Vector3 b, float R, List<Vector3> pts, out Vector3[,] P3, out float[,] past)
    {
        // the extent across the flow: the hull's projection, plus the margin the shock sweeps back over
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        foreach (var p in pts) { float x = Vector3.Dot(p, a), y = Vector3.Dot(p, b); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        float m = Margin * R; x0 -= m; x1 += m; y0 -= m; y1 += m;
        int N = Res; float dx = (x1 - x0) / N, dy = (y1 - y0) / N;
        // the front-most hull point per cell (the surface the air reaches first)
        var depth = new float[N + 1, N + 1]; var has = new bool[N + 1, N + 1];
        foreach (var p in pts)
        {
            int i = (int)MathF.Round((Vector3.Dot(p, a) - x0) / dx), j = (int)MathF.Round((Vector3.Dot(p, b) - y0) / dy);
            if (i < 0 || j < 0 || i > N || j > N) continue;
            float d = Vector3.Dot(p, f);
            if (!has[i, j] || d > depth[i, j]) { depth[i, j] = d; has[i, j] = true; }
        }
        _hullHas = (bool[,])has.Clone(); _hullSurf = new Vector3[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (has[i, j]) _hullSurf[i, j] = a * (x0 + i * dx) + b * (y0 + j * dy) + f * depth[i, j];
        // every other cell takes its nearest hull cell's depth, and its distance from it
        var dist = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) dist[i, j] = has[i, j] ? 0f : float.MaxValue;
        for (int pass = 0; pass < 2 * N; pass++)
        {
            bool changed = false;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    int ii = i + di, jj = j + dj;
                    if ((di == 0 && dj == 0) || ii < 0 || jj < 0 || ii > N || jj > N || dist[ii, jj] == float.MaxValue) continue;
                    float step = MathF.Sqrt(di * di * dx * dx + dj * dj * dy * dy), nd = dist[ii, jj] + step;
                    if (nd < dist[i, j] - 1e-4f) { dist[i, j] = nd; depth[i, j] = depth[ii, jj]; changed = true; }
                }
            if (!changed) break;
        }
        // the hull's peaks grown (a max over neighbours, hull cells only): a lone nose cell must not be averaged away
        var hullZ = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) hullZ[i, j] = has[i, j] ? depth[i, j] : float.MinValue;
        for (int k = 0; k < Dilate; k++)
        {
            var nz = (float[,])hullZ.Clone();
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    int ii = i + di, jj = j + dj;
                    if (ii < 0 || jj < 0 || ii > N || jj > N) continue;
                    if (hullZ[ii, jj] > nz[i, j]) nz[i, j] = hullZ[ii, jj];
                }
            hullZ = nz;
        }
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (hullZ[i, j] > float.MinValue && hullZ[i, j] > depth[i, j]) depth[i, j] = hullZ[i, j];
        // the shock: standing off the hull, swept back past the silhouette, then blurred (loosely over the hull)
        var z = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            float d = dist[i, j] == float.MaxValue ? m : dist[i, j];
            z[i, j] = depth[i, j] + Standoff * R - Sweep * d * d / Math.Max(R, 0.1f);
        }
        var t = new float[N + 1, N + 1];
        for (int k = 0; k < Blur; k++)
        {
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            {
                float sum = 0; int cnt = 0;
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                { int ii = i + di, jj = j + dj; if (ii < 0 || jj < 0 || ii > N || jj > N) continue; sum += z[ii, jj]; cnt++; }
                t[i, j] = sum / cnt;
            }
            var sw = z; z = t; t = sw;
        }
        // never closer than MinStandoff to the hull (its grown surface), wherever there is hull
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (hullZ[i, j] > float.MinValue) z[i, j] = Math.Max(z[i, j], hullZ[i, j] + MinStandoff * R);
        P3 = new Vector3[N + 1, N + 1]; past = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            float u = i / (float)N, v = j / (float)N;
            P3[i, j] = a * (x0 + u * (x1 - x0)) + b * (y0 + v * (y1 - y0)) + f * z[i, j];
            past[i, j] = (dist[i, j] == float.MaxValue ? m : dist[i, j]) / Math.Max(m, 1e-3f);
        }
    }

    /// <summary>UV in cell c, u and v straight across it (as the prototype: v down the frame).</summary>
    private static Vector2 Direct(int c, float u, float v)
    {
        if (_cellsX == 1 && _cellsY == 1)
            return new Vector2(u * TileU, v * TileV - (float)(_clock.Elapsed.TotalSeconds * Scroll % 1000.0));
        int cx = c % _cellsX, cy = (c / _cellsX) % _cellsY;
        return new Vector2((cx + u) / _cellsX, (cy + v) / _cellsY);
    }

    /// <summary>A grid of points, its UVs from uv(i, j), on the current buffers.</summary>
    private static Func<int, int, bool> _skipQuad;

    private static void Grid(Vector3[,] P, Func<int, int, Vector2> uv, Vector3 normal)
    {
        int nu = P.GetLength(0) - 1, nv = P.GetLength(1) - 1;
        for (int side = 0; side < ((Mode == 3 && UseGlow) || Mode == 4 ? 2 : 1); side++)   // (glow: both sides, the back with its normal turned)
        {
            int b0 = _n; var nrm = side == 0 ? normal : -normal;
            for (int j = 0; j <= nv; j++) for (int i = 0; i <= nu; i++) Vert(P[i, j], uv(i, j), nrm);
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (_skipQuad != null && _skipQuad(i, j)) continue;
                    int p = b0 + j * (nu + 1) + i, q = p + 1, r = p + nu + 1, t = r + 1;
                    if (side == 0) { _idx.Add(p); _idx.Add(r); _idx.Add(q); _idx.Add(q); _idx.Add(r); _idx.Add(t); }
                    else { _idx.Add(p); _idx.Add(q); _idx.Add(r); _idx.Add(q); _idx.Add(t); _idx.Add(r); }
                }
        }
    }

    /// <summary>The light: on the axis through the shock's front-most point, LightAhead of the way from hull to shock.</summary>
    private static void LightPlace(Vector3[,] P3, List<Vector3> pts, Vector3 f)
    {
        Vector3 front = P3[0, 0];
        foreach (var q in P3) if (Vector3.Dot(q, f) > Vector3.Dot(front, f)) front = q;
        float hullFront = float.MinValue;
        foreach (var q in pts) hullFront = Math.Max(hullFront, Vector3.Dot(q, f));
        float zs = Vector3.Dot(front, f);
        _lightAt = front - f * ((zs - hullFront) * (1f - LightAhead));
    }

    /// <summary>The windward ("wetted") hull surface on the shock grid: which cells have hull, and its front-most point.</summary>
    private static bool[,] _hullHas;
    private static Vector3[,] _hullSurf;

    private static string _shapeKey;
    private static Vector3[,] _P3;
    private static float[,] _past;
    private static Vector3[] _ring;
    private static Vector3 _ringC;

    /// <summary>A closed ring (last = first) averaged with its neighbours (+-2), passes times.</summary>
    private static Vector3[] SmoothRing(Vector3[] ring, int passes)
    {
        int n = ring.Length - 1;
        var r = (Vector3[])ring.Clone();
        for (int k = 0; k < passes; k++)
        {
            var t = new Vector3[n + 1];
            for (int i = 0; i < n; i++)
            {
                Vector3 sum = Vector3.Zero;
                for (int d = -2; d <= 2; d++) sum += r[((i + d) % n + n) % n];
                t[i] = sum / 5f;
            }
            t[n] = t[0];
            r = t;
        }
        return r;
    }

    /// <summary>The skirt's rim: round the silhouette (Around angles), the outermost shock point just past the hull.</summary>
    private static Vector3[] Rim(Vector3[,] P3, float[,] past, Vector3 a, Vector3 b, out Vector3 c, int around = 99)
    {
        int N = P3.GetLength(0) - 1; c = Vector3.Zero; int cnt = 0;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) { c += P3[i, j]; cnt++; }
        c /= cnt;
        var ring = new Vector3[around + 1]; var best = new float[around]; var got = new bool[around];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            if (!(past[i, j] > 0.02f && past[i, j] < 0.2f)) continue;
            Vector3 rel = P3[i, j] - c;
            float x = Vector3.Dot(rel, a), y = Vector3.Dot(rel, b), ang = MathF.Atan2(y, x);
            int k = (int)MathF.Floor((ang + MathF.PI) / (2 * MathF.PI) * around) % around;
            float rr = MathF.Sqrt(x * x + y * y);
            if (!got[k] || rr > best[k]) { best[k] = rr; ring[k] = P3[i, j]; got[k] = true; }
        }
        for (int k = 0; k < around; k++)
            if (!got[k])
                for (int d = 1; d < around; d++) { int kk = (k + d) % around; if (got[kk]) { ring[k] = ring[kk]; break; } }
        ring[around] = ring[0];
        return ring;
    }

    /// <summary>One skirt sleeve on the current buffers: SkirtTiles overlapping strips round the rim, each the frame's
    /// whole width (its clear sides overlap the neighbours'), v from SkirtV0 (at the shock) to SkirtV1 (the tail).</summary>
    private static void Skirt(Vector3[] ring, Vector3 c, Vector3 f, Vector3 a, float R, float frac, float rot0, int cell)
    {
        int around = ring.Length - 1, per = Math.Max(1, around / SkirtTiles), span = (int)MathF.Round(per * (1 + SkirtOverlap)), along = SkirtAlong;
        float L = SkirtLen * R * frac;
        int shift = (int)(rot0 * per) - (span - per) / 2;
        for (int tt = 0; tt < SkirtTiles; tt++)
        {
            var P = new Vector3[span + 1, along + 1];
            for (int ii = 0; ii <= span; ii++)
            {
                int k = ((tt * per + ii + shift) % around + around) % around;
                Vector3 r0 = ring[k] - c, radial = r0 - Vector3.Dot(r0, f) * f;
                radial = radial.LengthSquared() > 1e-8f ? Vector3.Normalize(radial) : a;
                for (int j = 0; j <= along; j++)
                {
                    float t = j / (float)along;
                    P[ii, j] = ring[k] - f * (L * t) + radial * (SkirtFlare * R * MathF.Pow(t, 0.8f));
                }
            }
            int sp = span;
            Grid(P, (i, j) => Direct(cell, 0.02f + 0.96f * i / sp, SkirtV0 + (SkirtV1 - SkirtV0) * j / (float)along), f);
        }
    }

    /// <summary>The cell's UV for a point (u, v) in 0..1 of the sheet (v 0 at the nose: the bottom of a flame frame).</summary>
    private static Vector2 CellUV(int c, float u, float v)
    {
        int cx = c % _cellsX, cy = (c / _cellsX) % _cellsY;
        return new Vector2((cx + u) / _cellsX, (cy + 1f - v) / _cellsY);
    }

    /// <summary>A grid of (nu+1) x (nv+1) shared vertices, at(u, v) its points, all of it on the one cell c.</summary>
    private static void Sheet(int nu, int nv, Func<float, float, Vector3> at, Func<float, float, Vector3> normal, int c)
    {
        int b = _n;
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
            {
                float u = i / (float)nu, v = j / (float)nv;
                Vert(at(u, v), CellUV(c, u, v), normal(u, v));
            }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int p = b + j * (nu + 1) + i, q = p + 1, r = p + nu + 1, t = r + 1;
                _idx.Add(p); _idx.Add(r); _idx.Add(q); _idx.Add(q); _idx.Add(r); _idx.Add(t);
            }
    }

    private static void BuildSheet(Vector3 f, Vector3 a, Vector3 b, float R, int cell)
    {
        float L = Length * R;
        // the sleeve: round the frontal outline (u), back along the flow (v), widening downwind
        Sheet(Around, Along,
              (u, v) => { double th = 2 * Math.PI * u; Vector3 r = a * (float)Math.Cos(th) + b * (float)Math.Sin(th);
                          return _nose - f * (0.3f * R + v * L) + r * (R * (0.95f + Flare * MathF.Pow(v, 0.7f))); },
              (u, v) => { double th = 2 * Math.PI * u; return a * (float)Math.Cos(th) + b * (float)Math.Sin(th); },
              cell);
        // the cap on the nose: a disc, round (u) and out from the stagnation point (v), on the same cell
        float gs = GlowSize * R * 0.5f;
        Sheet(Around, Math.Max(2, Along / 2),
              (u, v) => { double th = 2 * Math.PI * u; Vector3 r = a * (float)Math.Cos(th) + b * (float)Math.Sin(th);
                          return _nose + f * (0.15f * R * (1f - v * v)) + r * (gs * v); },
              (u, v) => f, cell);
    }

    private static void Begin()
    {
        _vs0 = NewBuffer(_tVs0); _vs1 = NewBuffer(_tVs1); _n = 0;
        _add0 ??= _vs0.GetType().GetMethod("Add", new[] { _tVs0 });
        _add1 ??= _vs1.GetType().GetMethod("Add", new[] { _tVs1 });
        _idx = new Buffer<int>(Allocator.Heap, "PlasmaSpike");
    }

    private static string Build(WorldTransform wt)
    {
        string why = Resolve();
        if (why != null) return why;
        Begin();

        // grid space: f into the air, a / b across it
        Vector3 f = _axis, a = Vector3.Normalize(Vector3.Cross(f, Math.Abs(f.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)), b = Vector3.Cross(f, a);
        float R = _radius, L = Length * R, w = Width * R;
        int cellsN = _cellsX * _cellsY;
        if (Mode == 4)
        {
            // the patches: in the plane of the flow and b, a row along the flow beside the nose, each its own model
            for (int k = 0; k < CalibLevels.Length; k++)
            {
                Begin();
                Vector3 c0 = _nose + a * (2.5f * CalibSize) - f * (k * CalibSize * 1.3f);
                float h = CalibSize * 0.5f;
                var P = new Vector3[2, 2] { { c0 - f * h - b * h, c0 - f * h + b * h }, { c0 + f * h - b * h, c0 + f * h + b * h } };
                Grid(P, (i, j) => CalibUV, a);
                why = Commit(k, wt, -1);
                if (why != null) return why;
            }
            return null;
        }
        if (Mode == 3 || Mode == 5)
        {
            var g = GridMembers.Get(_grid);
            var pts = g?.Entity != null ? HullPoints(g.Entity) : null;
            if (pts == null) return _hullWhy ?? "no grid";
            // R as the prototype: half the hull's extent across the flow (wings and all), not the aero frontal radius
            float xa = float.MaxValue, xb = float.MinValue, ya = float.MaxValue, yb = float.MinValue;
            foreach (var q in pts) { float x = Vector3.Dot(q, a), y = Vector3.Dot(q, b); xa = Math.Min(xa, x); xb = Math.Max(xb, x); ya = Math.Min(ya, y); yb = Math.Max(yb, y); }
            R = 0.5f * Math.Max(xb - xa, yb - ya);
            _shapeR = R;
            string key = string.Join(",", _grid, _axis, pts.Count, Res, Blur, Standoff, Sweep, Margin, MinStandoff, Dilate, RimSmooth);
            if (key != _shapeKey)
            {
                ShockGrid(f, a, b, R, pts, out _P3, out _past);
                _ring = SmoothRing(Rim(_P3, _past, a, b, out _ringC), RimSmooth);
                _shapeKey = key;
            }
            var P3 = _P3; var past = _past;
            int N = P3.GetLength(0) - 1;
            if (Mode == 5)
            {
                if (!_fieldUp && _puffWhy == null) { SpawnPuffs(P3, past, _ring, f, R); _fieldUp = true; }
                if (_lightAt == default) LightPlace(P3, pts, f);
                return null;
            }
            if (_lightAt == default) LightPlace(P3, pts, f);
            // the shock: Shells sheets, each ShellStep x R further upwind
            for (int sh = 0; sh < Shells; sh++)
            {
                var Ps = new Vector3[N + 1, N + 1];
                for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) Ps[i, j] = P3[i, j] + f * (sh * ShellStep * R);
                int cell = UseGlow ? Math.Clamp(_frame - PlasmaAtlasFirst + 3 * sh, 0, 7) : _frame + 3 * sh, nn = N;
                var pa = past;
                _skipQuad = (i, j) => pa[i, j] > TrimPast && pa[i + 1, j] > TrimPast && pa[i, j + 1] > TrimPast && pa[i + 1, j + 1] > TrimPast;
                Grid(Ps, (i, j) => Direct(cell, i / (float)nn, j / (float)nn), f);
                _skipQuad = null;
            }
            why = Commit(0, wt, -1);
            if (why != null) return why;
            // the skirt: three sleeves, each its own model (its own tint)
            var ring = _ring; var c = _ringC;
            float[] fracs = { 0.45f, 0.75f, 1f }, rots = { 0f, 0.31f, 0.62f };
            for (int l = 0; l < 3; l++)
            {
                Begin();
                Skirt(ring, c, f, a, R, fracs[l], rots[l], UseGlow ? Math.Clamp(SkirtFrame + (_frame - FrameLo) - PlasmaAtlasFirst, 0, 7) : SkirtFrame + (_frame - FrameLo));
                why = Commit(1 + l, wt, l);
                if (why != null) return why;
            }
            return null;
        }
        if (Mode == 2)
        {
            var g = GridMembers.Get(_grid);
            var pts = g?.Entity != null ? HullPoints(g.Entity) : null;
            if (pts == null) return _hullWhy ?? "no grid";
            BuildShock(f, a, b, R, _frame, pts);
        }
        else if (Mode == 1) BuildSheet(f, a, b, R, _frame);
        else
        // streamers off the frontal outline: two crossed quads each (seen from any side), flaring out downwind
        for (int k = 0; k < Count; k++)
        {
            double th = 2 * Math.PI * k / Count;
            Vector3 r = a * (float)Math.Cos(th) + b * (float)Math.Sin(th), t = Vector3.Cross(f, r);
            Vector3 s0 = _nose - f * (0.3f * R) + r * (0.9f * R);
            Vector3 s1 = s0 - f * L + r * (Flare * R);
            int c = _first + (_frame - _first + k * 7) % Math.Max(1, _count);
            float we = w * 1.6f;
            Quad(s0 - t * (w * 0.5f), s0 + t * (w * 0.5f), s1 + t * (we * 0.5f), s1 - t * (we * 0.5f), c);
            Quad(s0 - r * (w * 0.5f), s0 + r * (w * 0.5f), s1 + r * (we * 0.5f), s1 - r * (we * 0.5f), _first + (c - _first + 13) % Math.Max(1, _count));
        }
        // the glow at the nose: three crossed quads
        if (Mode != 1) {
        float gs = GlowSize * R * 0.5f; Vector3 gc = _nose - f * (0.1f * R);
        int gcell = _frame;
        Quad(gc - a * gs - b * gs, gc + a * gs - b * gs, gc + a * gs + b * gs, gc - a * gs + b * gs, gcell);
        Quad(gc - a * gs - f * gs, gc + a * gs - f * gs, gc + a * gs + f * gs, gc - a * gs + f * gs, _first + (gcell - _first + 5) % Math.Max(1, _count));
        Quad(gc - b * gs - f * gs, gc + b * gs - f * gs, gc + b * gs + f * gs, gc - b * gs + f * gs, _first + (gcell - _first + 11) % Math.Max(1, _count));
        }

        return Commit(0, wt, -1);
    }

    /// <summary>The current buffers as slot k's model (made, or swapped into its entity); colour: -1 none, else skirt colouring l.</summary>
    private static string Commit(int k, WorldTransform wt, int colour)
    {
        float R = _radius;
        object subs = NewBuffer(_tSub);
        var sections = new Buffer<Keen.VRage.Core.Model.Data.MeshData.Section>(Allocator.Heap, "PlasmaSpike");
        object sub = Activator.CreateInstance(_tSub);
        _tSub.GetField("Name").SetValue(sub, StringId.Get("PlasmaSpike"));
        _tSub.GetField("IndexStart").SetValue(sub, 0);
        _tSub.GetField("IndicesCount").SetValue(sub, _idx.Count);
        _tSub.GetField("Material").SetValue(sub, (Mode == 3 && UseGlow) || Mode == 4 ? (object)_matGlow : _mat);
        if (_cullNone != null) _tSub.GetField("CullingCone")?.SetValue(sub, _cullNone);
        subs.GetType().GetMethod("Add", new[] { _tSub }).Invoke(subs, new[] { sub });
        sections.Add(new Keen.VRage.Core.Model.Data.MeshData.Section
        {
            Name = new Keen.VRage.Core.Model.MeshSectionId(StringId.Get("PlasmaSpike")),
            Parts = new[] { new Keen.VRage.Core.Model.Data.MeshData.Section.SectionPart { PartIndex = 0, IndicesOffset = 0, IndicesCount = _idx.Count } },
        });
        object rmd = Activator.CreateInstance(_tRmd);
        SetField(rmd, "_vertexStream0", _vs0);
        SetField(rmd, "_vertexStream1", _vs1);
        SetField(rmd, "_vertexStream2", NewBuffer(typeof(Keen.VRage.Core.Render.Data.VertexFormatNull)));
        SetField(rmd, "_indices", _idx);
        SetField(rmd, "_subParts", subs);
        SetField(rmd, "_meshSections", sections);
        SetField(rmd, "_bones", NewBuffer(typeof(Keen.VRage.Core.Model.ModelBone)));
        SetField(rmd, "_debugName", "PlasmaSpike");
        float e = (Math.Max(Length, SkirtLen) + 3f) * R + _nose.Length() + 100f;
        _tRmd.GetProperty("AABB").SetValue(rmd, new BoundingBox(new Vector3(-e), new Vector3(e)));

        object contracts = PlanetRenderBridge.Contracts;
        object dataType = Enum.ToObject(_createRuntimeModel.GetParameters()[1].ParameterType, (int)RenderRuntimeDataType.UI3D);
        object newRuntime = _createRuntimeModel.Invoke(contracts, new[] { rmd, dataType, (object)true, (object)false });
        var handle = (ResourceHandle)_toHandle.Invoke(null, new[] { newRuntime });
        if (_models[k] == null)
        {
            _roots[k] = PlanetRenderBridge.CreateRootEntity("PlasmaSpikeRoot" + k, wt);
            // Visible | SkipCulling | SkipFarPlaneCulling | ForceHighestLOD
            _models[k] = PlanetRenderBridge.CreateModelEntity("PlasmaSpike" + k, handle, _roots[k], 0x1 | 0x2 | 0x10 | 0x20);
            if (_models[k] == null) { PlanetRenderBridge.DisposeRender(newRuntime); return "model entity not created"; }
            _colored[k] = false;
        }
        else
        {
            // the game's own way to change a mesh: a new model into the same entity, the old one disposed
            _models[k].GetType().GetMethod("UpdateModel", new[] { typeof(ResourceHandle) })?.Invoke(_models[k], new object[] { handle });
            PlanetRenderBridge.DisposeRender(_runtimes[k]);
            _colored[k] = false;   // (a new model: its colouring again)
        }
        _runtimes[k] = newRuntime;
        if (Mode == 4) { if (!_colored[k]) { float v = CalibLevels[k]; _colored[k] = GlowColour(_models[k], new[] { v * CalibTint[0], v * CalibTint[1], v * CalibTint[2] }); } }
        else if (Mode == 3) { if (UseGlow && !_colored[k]) _colored[k] = GlowColour(_models[k], GlowColor[k]); }
        else if (colour >= 0 && !_colored[k]) _colored[k] = Colour(_models[k], SkirtHue[colour], SkirtSat[colour], SkirtDim[colour]);
        return null;
    }

    private static Type _tSafeZone;

    /// <summary>A glow model's colour and scale (SafeZoneMeshData: the safe-zone shader's per-entity data).</summary>
    private static bool GlowColour(object model, float[] c)
    {
        _tSafeZone ??= PlanetRenderBridge.RenderAssembly?.GetType("Keen.VRage.Render.Data.SafeZoneMeshData");
        if (_tSafeZone == null || model == null) return true;
        // in linear: away from its grey by Sat, then x Bright; back to sRGB (the shader unpacks it as sRGB)
        static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        static float Enc(float v) { v = Math.Clamp(v, 0f, 1f); return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f; }
        float r = Lin(c[0]), g = Lin(c[1]), b = Lin(c[2]), grey = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        r = (grey + (r - grey) * Sat) * Bright; g = (grey + (g - grey) * Sat) * Bright; b = (grey + (b - grey) * Sat) * Bright;
        object d = Activator.CreateInstance(_tSafeZone, new object[] { 1f, new ColorSRGB(Enc(r), Enc(g), Enc(b), 1f) });
        return PlanetRenderBridge.SetEntityCustomData(model, d);
    }

    private static Type _tColoring;

    /// <summary>A model's colouring (ColoringCustomData, as the map rings): hue and saturation replaced, value shifted.</summary>
    private static bool Colour(object model, float hue, float sat, float dim)
    {
        _tColoring ??= PlanetRenderBridge.RenderAssembly?.GetType("Keen.VRage.Render.Materials.Templates.ColoringCustomData");
        if (_tColoring == null || model == null) return true;
        object d = Activator.CreateInstance(_tColoring);
        _tColoring.GetField("ColoringHSV").SetValue(d, new ColorHSV(hue, sat, 0.55f + dim, 1f));
        var fFlags = _tColoring.GetField("ColoringFlags");
        fFlags.SetValue(d, Enum.ToObject(fFlags.FieldType, 1));   // IgnoreColorMask: all of it
        return PlanetRenderBridge.SetEntityCustomData(model, d);
    }

    private const BindingFlags AnyInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static bool _lightSet;

    private static Type FindType(string asm, string name)
    {
        foreach (var x in AppDomain.CurrentDomain.GetAssemblies()) if (x.GetName().Name == asm) return x.GetType(name);
        return null;
    }

    /// <summary>The client's copy of a (server) grid - the one with a render component: same name, nearest.</summary>
    private static Keen.VRage.DCS.Components.Entity _client;
    private static double _buildMs;

    private static Keen.VRage.DCS.Components.Entity ClientTwin(OrbitalGridComponent g)
    {
        if (!g.IsServer) return g.Entity;
        var at = GridMembers.Position(g); string name = g.DisplayName;
        OrbitalGridComponent best = null; double bd = 50.0;
        foreach (var x in GridMembers.All())
        {
            if (x.IsServer || x.Entity == null || x.DisplayName != name) continue;
            double d = (GridMembers.Position(x) - at).Length();
            if (d < bd) { bd = d; best = x; }
        }
        return best?.Entity;
    }

    private static MethodInfo _spawn;
    private static Type _tParams;
    private static object _fxParent, _fxSession;
    private static Keen.VRage.DCS.Components.Entity _fxFor;

    /// <summary>An effect under the (client) grid's render root at a grid-space transform - it follows the grid - with
    /// its own user parameters (out); null and why on failure.</summary>
    private static object SpawnEffect(Keen.VRage.DCS.Components.Entity grid, Guid effect, RelativeTransform at, out object prm, out string why)
    {
        prm = null; why = null;
        try
        {
            if (grid == null) { why = "no client copy of the grid"; return null; }
            if (_spawn == null)
            {
                var tSession = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
                _tParams = FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
                if (tSession == null || _tParams == null) { why = "particle types not found"; return null; }
                foreach (var m in tSession.GetMethods(AnyInst))
                    if (m.Name == "TrySpawnEffect") { var ps = m.GetParameters(); if (ps.Length > 3 && ps[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; break; } }
                if (_spawn == null) { why = "TrySpawnEffect not found"; return null; }
            }
            if (!ReferenceEquals(_fxFor, grid))
            {
                var tSession = _spawn.DeclaringType;
                var tRender = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Render.RootEntityRenderComponent");
                _fxParent = _fxSession = null;
                foreach (var c in grid.Components) if (c != null && tRender.IsInstanceOfType(c)) { _fxParent = c; break; }
                var comps = grid.GetSession()?.SessionComponents;
                if (comps != null) foreach (var c in comps.Components) if (c != null && tSession.IsInstanceOfType(c)) { _fxSession = c; break; }
                if (_fxParent == null || _fxSession == null) { why = _fxParent == null ? "no render parent" : "no particle session"; return null; }
                _fxFor = grid;
            }
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect; return null; }
            prm = Activator.CreateInstance(_tParams);
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = at; args[1] = def; args[2] = prm; args[3] = _fxParent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "PlasmaSpike";
            object h = _spawn.Invoke(_fxSession, args);
            if (h == null) why = "spawn refused";
            return h;
        }
        catch (Exception e) { why = "spawn: " + PlanetRenderBridge.Inner(e); return null; }
    }

    private static void DisposeEffect(object h)
    {
        if (h == null) return;
        try { h.GetType().GetMethod("Dispose", AnyInst, null, new[] { typeof(bool) }, null)?.Invoke(h, new object[] { true }); }
        catch (Exception) { }
    }

    /// <summary>The light effect at _lightAt (grid space), under the grid's render root: it follows the grid.</summary>
    private static void SpawnLight(Keen.VRage.DCS.Components.Entity grid)
    {
        _light = SpawnEffect(grid, LightEffect, new RelativeTransform(_lightAt), out _lightParams, out _lightWhy);
        _lightSet = false;
    }

    // ── Mode 5: the plasma as particles over the shock (the aero mod's PlasmaField effects, tools/particles/gen_plasma_field.py)

    /// <summary>Mode 5: a Shock puff every PuffEvery cells of the shock grid (inside TrimPast), a Stream every
    /// StreamEvery rim angles (of 99); each scaled by the spacing x PuffScale / StreamScale (their sizes and speeds are
    /// authored per metre of it), turned so its +Z runs downwind.</summary>
    public static int PuffEvery = 4, StreamEvery = 6;
    public static float PuffScale = 0.3f, StreamScale = 0.6f;   // (the user's pick: small, many - rows E/F)
    /// <summary>The cap ahead of the nose denser: shock points within FrontDepth x R of the front-most one every
    /// FrontEvery cells (finer sparks there: their scale is the spacing); 0: no extra.</summary>
    public static int FrontEvery = 2;
    /// <summary>Where the sparks come from: 0 the shock sheet, 1 the hull's windward surface (every PuffEvery cells),
    /// 2 its silhouette edge (lit cells beside unlit ones, every EdgeEvery of them), 3 surface and edge. Hull
    /// emitters sit SurfaceLift x R upwind of the hull.</summary>
    public static int PuffSource = 5, EdgeEvery = 2;
    /// <summary>PuffSource bit 4: LINE emitters along the hull shadow's outline (marching squares over the windward
    /// mask, smoothed LineSmooth passes, cut into SegLen metre segments): each a spawn box along its segment, spraying
    /// downwind - continuous along the outline. Sparks LineScale big; within FrontDepth x R of the front the short
    /// spray, behind it the long one.</summary>
    public static float SegLen = 1.5f, LineScale = 3f;   // (SegLen: the spacing of the emitters along the outline)
    public static int LineSmooth = 2;
    private static readonly Guid LineShockViolet = new Guid("ccbf284f-50ca-59f8-8898-c870b3122f31"), LineTailViolet = new Guid("3b24bc6d-83a9-5468-87ef-df83ce644f98");
    private static readonly Guid LineShockOrange = new Guid("a4274abe-a27c-5279-ad54-c6ae558dee2a"), LineTailOrange = new Guid("3c4c31a7-66f0-5222-9cbf-c107360be119");
    public static float SurfaceLift = 0.05f;
    /// <summary>Lights over the windward surface, every LightGrid cells (0: the one light at the nose), each LightEach
    /// strong, LightLift x R off the hull; at most LightMax of them.</summary>
    public static int LightGrid = 0, LightMax = 24;   // (point lights spilled everywhere: the spot below instead)
    /// <summary>The plasma's light as ONE spot light (RenderContracts.CreateSpotLightEntity), SpotDist x R upwind of
    /// the front, aimed down the flow, its cone just covering SpotCover x R: the windward surface lit (with the hull's
    /// own shadows when SpotShadow), nothing below or behind - a "plasma sun". Spot: its strength (linear; a vanilla
    /// spotlight is 300-700), 0: none. SpotFlip: aim the other way (if the engine's spot faces +Z).</summary>
    public static float Spot = 3000000f, SpotDist = 2.5f, SpotCover = 1.3f, SpotFalloff = 2f;
    public static bool SpotShadow = true, SpotFlip;
    private static object _spotRoot, _spot;
    private static string _spotWhy;
    public static float LightEach = 6000f, LightLift = 0.08f;
    private static readonly List<object> _gridLights = new List<object>();
    private static string _gridLightWhy;
    public static float FrontDepth = 0.5f;
    /// <summary>Sparks' brightness (their colour multiplier: the emissive glow too) and colour: 0 orange to red,
    /// 1 orange through magenta to violet.</summary>
    public static float SparkBright = 1f;   // (the colour multiplier barely dims them: SparkLevel does)
    public static int SparkColor = 2;
    private static readonly Guid ShockPuff = new Guid("f492f164-c71f-59da-be2d-c43dca9738f3"), StreamPuff = new Guid("48e61a78-3bed-50f7-8547-bca713518ce6");
    /// <summary>What each point spews: 0 fire puffs, 1 sparks (a streaking spray downwind), 2 both.</summary>
    public static int Sparks = 1;
    /// <summary>The sparks' speed x (on top of the spacing the effects are authored per metre of).</summary>
    public static float SparkSpeed = 1f;
    /// <summary>TEST: one spark effect at the nose only - 1 scale 9 turned downwind, 2 scale 1 turned, 3 scale 1 unturned.</summary>
    public static int PuffTest;
    private static readonly Guid ShockSparksOrange = new Guid("88d92e33-42d6-57d6-92e5-6c1b6e145b75"), TailSparksOrange = new Guid("a5dffd10-c970-5b4b-b93c-60fb8b1eb3fe");
    private static readonly Guid ShockSparksViolet = new Guid("65e9fe79-ae22-5e17-9378-95aab9f9beb7"), TailSparksViolet = new Guid("8d41a37a-bc8a-53d5-9e6a-e8822ba2d124");
    /// <summary>SparkColor 2: sparks born the spot light's colour (PlasmaColor), cooling to violet, at SparkLevel
    /// 1-5 (emissivity 3000 / 1000 / 330 / 165 / 80 at birth: the aero mod's PlasmaField_*Matched*) - the spot and the sparks balanced.</summary>
    public static int SparkLevel = 4;
    public static readonly float[] PlasmaColor = { 1f, 0.55f, 0.25f };   // (sRGB; = PLASMA in gen_plasma_field.py)
    private static readonly Guid[] ShockMatched = { new Guid("0066f51e-7ba2-563b-9fc5-f1b275496dea"), new Guid("d3c5ace4-ad42-51a6-8952-18046f197789"), new Guid("e15e192a-3b2c-5ada-ae5c-cb2c2116ad04"), new Guid("1feed661-b4df-5371-a7e7-0fa0aeaf1719"), new Guid("0c8b0d2b-5098-504a-bc02-6ccf4c2f96e3") };
    private static readonly Guid[] TailMatched = { new Guid("5b840f1b-d19b-5605-8afe-750473a56d05"), new Guid("f3cd3f03-31f6-5386-9777-53b7a1801b7b"), new Guid("89c9fe35-49d7-5f00-98da-61221a71dcd4"), new Guid("7eb50314-40d3-5d02-9981-f4df249559d3"), new Guid("171b20ba-98ab-572c-8a60-d9d61dc0408b") };
    /// <summary>Sparks per emitter, % of the authored rate: 100 / 75 / 50 / 25 (the emitters as dense; fewer sparks
    /// each - gen_plasma_field.py's rate variants, Guid 5d0c7a3e-000K-4LLL-8RRR-000000000001).</summary>
    public static int SparkRate = 75;
    private static Guid RateGuid(int kind, int rate = -1) => new Guid($"5d0c7a3e-{kind:x4}-4{Math.Clamp(SparkLevel, 1, 5):x3}-8{(rate < 0 ? SparkRate : rate):x3}-000000000001");
    /// <summary>Outline emitters LineDensity x closer (SegLen / it), each at SparkRate / it (the nearest built rate:
    /// 100 50 25 12 6 - deep sparks only) - the same sparks in all, finer along the outline. Sizes and speeds unchanged.</summary>
    public static int LineDensity = 1;
    private static int LineRate
    {
        get
        {
            float want = SparkRate / (float)Math.Max(1, LineDensity); int best = 100;
            foreach (int r in new[] { 100, 50, 25, 12, 6 }) if (Math.Abs(r - want) < Math.Abs(best - want)) best = r;
            return best;
        }
    }
    /// <summary>SparkColor 3: DEEP - born saturated orange (DeepColor; the spot the same), thinner; levels 2-5, rates 100/50/25.</summary>
    public static readonly float[] DeepColor = { 1f, 0.35f, 0.06f };   // (= DEEP in gen_plasma_field.py)
    private static float[] SparkBirth => SparkColor == 3 ? DeepColor : PlasmaColor;
    private static Guid ShockSparks => SparkColor == 3 ? RateGuid(3) : SparkColor == 2 ? RateGuid(1) : SparkColor == 1 ? ShockSparksViolet : ShockSparksOrange;
    private static Guid TailSparks => SparkColor == 3 ? RateGuid(4) : SparkColor == 2 ? RateGuid(2) : SparkColor == 1 ? TailSparksViolet : TailSparksOrange;
    private static Guid ShockSparksOld => SparkColor == 2 ? ShockMatched[Math.Clamp(SparkLevel, 1, 5) - 1] : SparkColor == 1 ? ShockSparksViolet : ShockSparksOrange;
    private static Guid TailSparksOld => SparkColor == 2 ? TailMatched[Math.Clamp(SparkLevel, 1, 5) - 1] : SparkColor == 1 ? TailSparksViolet : TailSparksOrange;
    private static readonly List<object> _puffs = new List<object>();
    private static string _puffWhy;

    /// <summary>The hull shadow's outline as closed chains of grid-space points (marching squares over _hullHas,
    /// outside the grid empty): each point midway between a hull node and an empty one, at the hull node's depth.</summary>
    private static List<List<Vector3>> Outline(Vector3[,] P3, Vector3 f, int N)
    {
        bool Has(int i, int j) => i >= 0 && j >= 0 && i <= N && j <= N && _hullHas[i, j];
        Vector3 Plane(int i, int j)
        {
            int ci = Math.Clamp(i, 0, N), cj = Math.Clamp(j, 0, N);
            Vector3 q = P3[ci, cj] - f * Vector3.Dot(P3[ci, cj], f);
            // (outside the grid: one cell further on)
            if (i != ci || j != cj) q += (P3[Math.Min(N, ci + 1), cj] - P3[ci, cj]) * (i - ci) + (P3[ci, Math.Min(N, cj + 1)] - P3[ci, cj]) * (j - cj);
            return q - f * Vector3.Dot(q, f);
        }
        // an edge between node (i,j) and its +i (dir 0) or +j (dir 1) neighbour, crossing the outline: its point
        var pts = new Dictionary<long, Vector3>();
        long Key(int i, int j, int d) => ((long)(i + 2) * 1000 + (j + 2)) * 2 + d;
        Vector3 EdgePoint(int i, int j, int d)
        {
            int i2 = d == 0 ? i + 1 : i, j2 = d == 0 ? j : j + 1;
            var (hi, hj) = Has(i, j) ? (i, j) : (i2, j2);
            return (Plane(i, j) + Plane(i2, j2)) * 0.5f + f * Vector3.Dot(_hullSurf[hi, hj], f);
        }
        var adj = new Dictionary<long, List<long>>();
        void Link(long x, long y) { (adj.TryGetValue(x, out var lx) ? lx : adj[x] = new List<long>()).Add(y); (adj.TryGetValue(y, out var ly) ? ly : adj[y] = new List<long>()).Add(x); }
        for (int i = -1; i <= N; i++)
            for (int j = -1; j <= N; j++)
            {
                // the square's edges: bottom (i,j)-(i+1,j), right (i+1,j)-(i+1,j+1), top (i,j+1)-(i+1,j+1), left (i,j)-(i,j+1)
                bool c0 = Has(i, j), c1 = Has(i + 1, j), c2 = Has(i + 1, j + 1), c3 = Has(i, j + 1);
                var cut = new List<long>(4);
                if (c0 != c1) { long k = Key(i, j, 0); pts[k] = EdgePoint(i, j, 0); cut.Add(k); }
                if (c1 != c2) { long k = Key(i + 1, j, 1); pts[k] = EdgePoint(i + 1, j, 1); cut.Add(k); }
                if (c3 != c2) { long k = Key(i, j + 1, 0); pts[k] = EdgePoint(i, j + 1, 0); cut.Add(k); }
                if (c0 != c3) { long k = Key(i, j, 1); pts[k] = EdgePoint(i, j, 1); cut.Add(k); }
                if (cut.Count == 2) Link(cut[0], cut[1]);
                else if (cut.Count == 4) { Link(cut[0], cut[1]); Link(cut[2], cut[3]); }
            }
        var chains = new List<List<Vector3>>();
        var seen = new HashSet<long>();
        foreach (var start in adj.Keys)
        {
            if (seen.Contains(start)) continue;
            var chain = new List<Vector3>();
            long cur = start, prev = long.MinValue;
            while (cur != long.MinValue && seen.Add(cur))
            {
                chain.Add(pts[cur]);
                long next = long.MinValue;
                foreach (var nb in adj[cur]) if (nb != prev && !seen.Contains(nb)) { next = nb; break; }
                prev = cur; cur = next;
            }
            if (chain.Count >= 3) chains.Add(chain);
        }
        return chains;
    }

    /// <summary>Line emitters along the outline: each chain smoothed, cut into SegLen segments, one effect per segment.</summary>
    private static void SpawnLines(Vector3[,] P3, Vector3 f, float R, int N, Quaternion turn)
    {
        float zFront = float.MinValue;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) zFront = Math.Max(zFront, Vector3.Dot(_hullSurf[i, j], f));
        Vector3 lift = f * (SurfaceLift * R);
        foreach (var raw in Outline(P3, f, N))
        {
            // smoothed (closed: the chain wraps)
            var c = raw;
            for (int k = 0; k < LineSmooth; k++)
            {
                var t = new List<Vector3>(c.Count);
                for (int i = 0; i < c.Count; i++) t.Add((c[(i - 1 + c.Count) % c.Count] + c[i] * 2f + c[(i + 1) % c.Count]) * 0.25f);
                c = t;
            }
            // cut into SegLen pieces along the (closed) chain
            Vector3 segStart = c[0]; float have = 0f;
            for (int i = 1; i <= c.Count; i++)
            {
                Vector3 a = c[i - 1], b = c[i % c.Count];
                float l = (b - a).Length();
                if (l < 1e-4f) continue;
                Vector3 dir = (b - a) / l;
                float at = 0f;
                float seg = SegLen / Math.Max(1, LineDensity);
                while (have + (l - at) >= seg)
                {
                    float take = seg - have;
                    at += take;
                    Vector3 segEnd = a + dir * at;
                    LineSegment(segStart, segEnd, f, R, zFront, lift);
                    if (_puffWhy != null) return;
                    segStart = segEnd; have = 0f;
                }
                have += l - at;
            }
        }
    }

    private static int _lineBad;

    private static bool _draining;

    private static void LineSegment(Vector3 p0, Vector3 p1, Vector3 f, float R, float zFront, Vector3 lift)
    {
        if (!_draining) { _spawnQueue.Enqueue(() => LineSegment(p0, p1, f, R, zFront, lift)); return; }
        Vector3 sdir = p1 - p0; sdir -= f * Vector3.Dot(sdir, f);
        float len = sdir.Length();
        if (len < 0.05f * SegLen) return;   // (a run straight along the flow: no width across it)
        sdir /= len;
        // local X along the segment, +Z downwind (-f): forward (-Z) = f, right (X) = sdir, up = sdir x f
        var q = Quaternion.CreateFromOrthonormalBasis(f, Vector3.Cross(sdir, f), sdir);
        // (checked, not assumed: the first lines sprayed outward - the basis's convention is the inverse one here)
        if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, q), -f) < 0.99f || Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), sdir) < 0.99f)
            q = Quaternion.Inverse(q);
        if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, q), -f) < 0.99f) { _lineBad++; return; }
        Vector3 mid = (p0 + p1) * 0.5f + lift;
        bool front = zFront - Vector3.Dot(mid, f) < FrontDepth * R;
        // (a stretched spawn box cannot be a line: the GPU emitter sends each particle along normalize(its spawn offset) -
        //  Shaders/Transparent/Particles/ParticleEmission.hlsl - so a long box sprays along itself. Round point emitters,
        //  close together along the outline, instead.)
        Guid e = SparkColor == 3 && LineDensity > 1 ? RateGuid(front ? 3 : 4, LineRate) : front ? ShockSparks : TailSparks;
        object h = SpawnEffect(_client, e, new RelativeTransform(mid, q), out object prm, out _puffWhy);
        if (h == null) return;
        _puffs.Add(h);
        try
        {
            var t = prm.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(prm, LineScale);
            t.GetField("VelocityMultiplier").SetValue(prm, Math.Max(0.01f, SparkSpeed));
            t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
            h.GetType().GetMethod("SetParameters", AnyInst).Invoke(h, new[] { prm });
        }
        catch (Exception ex) { _puffWhy = "set: " + PlanetRenderBridge.Inner(ex); }
    }

    private static void SpawnSpot(Vector3 f, float R, int N)
    {
        try
        {
            // the front, and the middle of the hull's shadow across the flow
            float zFront = float.MinValue; Vector3 mid = Vector3.Zero; int cnt = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                if (_hullHas[i, j]) { var q = _hullSurf[i, j]; zFront = Math.Max(zFront, Vector3.Dot(q, f)); mid += q - f * Vector3.Dot(q, f); cnt++; }
            if (cnt == 0) { _spotWhy = "no hull"; return; }
            mid /= cnt;
            float d = SpotDist * R;
            Vector3 at = mid + f * (zFront + d);
            float cone = 2f * MathF.Atan(SpotCover / Math.Max(0.1f, SpotDist));
            // aimed down the flow: the engine's forward is -Z (SpotFlip: +Z)
            var q2 = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);
            _spotRoot = PlanetRenderBridge.CreateRootEntity("PlasmaSpotRoot", _client.Data.GetWorldTransform());
            if (_spotRoot == null) { _spotWhy = "no root"; return; }
            object contracts = PlanetRenderBridge.Contracts;
            var m = contracts.GetType().GetMethod("CreateSpotLightEntity");
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            // the sparks' birth colour (PlasmaColor, sRGB) in linear
            var pc = SparkBirth;
            float lr = MathF.Pow(pc[0] * LightTint[0], 2.2f), lg = MathF.Pow(pc[1] * LightTint[1], 2.2f), lb = MathF.Pow(pc[2] * LightTint[2], 2.2f);
            for (int i = 0; i < ps.Length; i++)
                switch (ps[i].Name)
                {
                    case "debugName": args[i] = "PlasmaSpot"; break;
                    case "lightIntensityRGB": args[i] = new ColorLinear(lr * Spot, lg * Spot, lb * Spot, 1f); break;
                    case "outerConeAngle": args[i] = cone; break;
                    case "localLightTransform": args[i] = new RelativeTransform(at, q2); break;
                    case "rootEntity": args[i] = _spotRoot; break;
                    case "tryForceShadowMapAlwaysAllocated": args[i] = SpotShadow; break;
                    case "falloffMultiplier": args[i] = SpotFalloff; break;
                }
            _spot = m.Invoke(contracts, args);
            _spotWhy = null;
        }
        catch (Exception e) { _spotWhy = "spot: " + PlanetRenderBridge.Inner(e); }
    }

    private static void StopSpot()
    {
        try { (_spot as IDisposable)?.Dispose(); } catch (Exception) { }
        PlanetRenderBridge.DisposeRender(_spotRoot);
        _spot = null; _spotRoot = null;
    }

    /// <summary>Orange lights over the windward surface: every LightGrid cells with hull, lifted off it upwind.</summary>
    private static void SpawnGridLights(Vector3 f, float R, int N)
    {
        int g = Math.Max(1, LightGrid);
        for (int i = g / 2; i <= N; i += g)
            for (int j = g / 2; j <= N; j += g)
            {
                if (_gridLights.Count >= LightMax) return;
                // the cell's hull, or the nearest hull cell within the block
                Vector3? at = null;
                for (int di = -g / 2; di <= g / 2 && at == null; di++)
                    for (int dj = -g / 2; dj <= g / 2 && at == null; dj++)
                    {
                        int ii = i + di, jj = j + dj;
                        if (ii >= 0 && jj >= 0 && ii <= N && jj <= N && _hullHas[ii, jj]) at = _hullSurf[ii, jj];
                    }
                if (at == null) continue;
                object h = SpawnEffect(_client, LightEffect, new RelativeTransform(at.Value + f * (LightLift * R)), out object prm, out _gridLightWhy);
                if (h == null) return;
                try
                {
                    var t = prm.GetType();
                    t.GetField("EmitterScaleMultiplier").SetValue(prm, MathF.Sqrt(Math.Max(0f, LightEach) / 1000f));
                    t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(LightTint[0], LightTint[1], LightTint[2], 1f));
                    t.GetField("UseExtendedLightRadius")?.SetValue(prm, true);
                    h.GetType().GetMethod("SetParameters", AnyInst).Invoke(h, new[] { prm });
                    h.GetType().GetMethod("FixEffectTime", AnyInst).Invoke(h, new object[] { true, TimeSpan.FromSeconds(1.5) });
                }
                catch (Exception e) { _gridLightWhy = "set: " + PlanetRenderBridge.Inner(e); }
                _gridLights.Add(h);
            }
    }

    private static bool _fieldUp;

    private static void StopPuffs()
    {
        _spawnQueue.Clear(); _fieldUp = false;
        foreach (var h in _puffs) DisposeEffect(h);
        _puffs.Clear(); _puffWhy = null;
        foreach (var h in _gridLights) DisposeEffect(h);
        _gridLights.Clear(); _gridLightWhy = null;
        StopSpot(); _spotWhy = null;
    }

    private static void SpawnPuffs(Vector3[,] P3, float[,] past, Vector3[] ring, Vector3 f, float R)
    {
        StopPuffs();
        int N = P3.GetLength(0) - 1, step = Math.Max(1, PuffEvery);
        var turn = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
        if (PuffTest > 0)
        {
            Puff(ShockSparks, _nose, PuffTest == 3 ? Quaternion.Identity : turn, PuffTest == 1 ? 9f : 1f);
            return;
        }
        float cell = (P3[N, 0] - P3[0, 0]).Length() / N;
        if (LightGrid > 0) SpawnGridLights(f, R, N);
        if (Spot > 0f) SpawnSpot(f, R, N);
        if ((PuffSource & 4) != 0) { SpawnLines(P3, f, R, N, turn); if (_puffWhy != null) return; }
        if ((PuffSource & 3) != 0)
        {
            var H = _hullHas; var S = _hullSurf; Vector3 up = f * (SurfaceLift * R);
            float hz = float.MinValue;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (H[i, j]) hz = Math.Max(hz, Vector3.Dot(S[i, j], f));
            int fs2 = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step, edgeN = 0;
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    if (!H[i, j]) continue;
                    bool edge = i == 0 || j == 0 || i == N || j == N || !H[i - 1, j] || !H[i + 1, j] || !H[i, j - 1] || !H[i, j + 1];
                    if ((PuffSource & 2) != 0 && edge && edgeN++ % Math.Max(1, EdgeEvery) == 0)
                        Puff(TailSparks, S[i, j] + up, turn, cell * EdgeEvery * StreamScale);
                    else if ((PuffSource & 1) != 0)
                    {
                        bool front = FrontEvery > 0 && hz - Vector3.Dot(S[i, j], f) < FrontDepth * R && i % fs2 == 0 && j % fs2 == 0;
                        if (!(i % step == 0 && j % step == 0) && !front) continue;
                        Puff(ShockSparks, S[i, j] + up, turn, cell * (front ? fs2 : step) * PuffScale);
                    }
                    if (_puffWhy != null) return;
                }
            return;
        }
        if (PuffSource != 0) return;
        float zFront = float.MinValue;
        foreach (var q in P3) zFront = Math.Max(zFront, Vector3.Dot(q, f));
        int fs = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step;
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                if (past[i, j] > TrimPast) continue;
                bool onGrid = i % step == 0 && j % step == 0;
                bool front = FrontEvery > 0 && zFront - Vector3.Dot(P3[i, j], f) < FrontDepth * R && i % fs == 0 && j % fs == 0;
                if (!onGrid && !front) continue;
                float sc = cell * (front ? fs : step) * PuffScale;
                if (Sparks != 1) Puff(ShockPuff, P3[i, j], turn, sc);
                if (Sparks >= 1) Puff(ShockSparks, P3[i, j], turn, sc);
                if (_puffWhy != null) return;
            }
        int around = ring.Length - 1, rs = Math.Max(1, StreamEvery);
        float arc = 0f;
        for (int k = 0; k < around; k++) arc += (ring[k + 1] - ring[k]).Length();
        for (int k = 0; k < around; k += rs)
        {
            if (Sparks != 1) Puff(StreamPuff, ring[k], turn, arc / around * rs * StreamScale);
            if (Sparks >= 1) Puff(TailSparks, ring[k], turn, arc / around * rs * StreamScale);
            if (_puffWhy != null) return;
        }
    }

    private static readonly Queue<Action> _spawnQueue = new Queue<Action>();
    public static int SpawnPerTick = 12;

    private static void Puff(Guid effect, Vector3 at, Quaternion turn, float scale)
    {
        if (!_draining) { _spawnQueue.Enqueue(() => Puff(effect, at, turn, scale)); return; }
        object h = SpawnEffect(_client, effect, new RelativeTransform(at, turn), out object prm, out _puffWhy);
        if (h == null) return;
        _puffs.Add(h);
        try
        {
            var t = prm.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(prm, Math.Max(0.1f, scale));
            bool spark = effect != ShockPuff && effect != StreamPuff;
            if (spark) t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
            // (measured: a local-space effect's particle speed is already x its EmitterScaleMultiplier - x the spacing
            //  again sent the sparks 9x too fast, starting a plane-length behind their point; puffs kept as tuned)
            t.GetField("VelocityMultiplier").SetValue(prm, spark ? Math.Max(0.01f, SparkSpeed) : Math.Max(0.1f, scale));
            h.GetType().GetMethod("SetParameters", AnyInst).Invoke(h, new[] { prm });
        }
        catch (Exception e) { _puffWhy = "set: " + PlanetRenderBridge.Inner(e); }
    }

    /// <summary>The light's strength (scale = sqrt(Light / 1000)), tint, extended radius; its time held at full.</summary>
    private static void SetLight()
    {
        try
        {
            var t = _lightParams.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(_lightParams, MathF.Sqrt(Math.Max(0f, Light) / 1000f));
            t.GetField("ColorMultiplier").SetValue(_lightParams, new ColorSRGB(LightTint[0], LightTint[1], LightTint[2], 1f));
            t.GetField("UseExtendedLightRadius")?.SetValue(_lightParams, true);
            _light.GetType().GetMethod("SetParameters", AnyInst).Invoke(_light, new[] { _lightParams });
            _light.GetType().GetMethod("FixEffectTime", AnyInst).Invoke(_light, new object[] { true, TimeSpan.FromSeconds(1.5) });   // (the 2 s effect's full-strength stretch)
            _lightSet = true;
        }
        catch (Exception e) { _lightWhy = "set: " + PlanetRenderBridge.Inner(e); StopLight(); }
    }

    private static void StopLight()
    {
        DisposeEffect(_light);
        _light = null; _lightWhy = null;
    }

    private static void Dispose()
    {
        for (int k = 0; k < Slots; k++)
        {
            PlanetRenderBridge.DisposeRender(_models[k]);
            PlanetRenderBridge.DisposeRender(_roots[k]);
            PlanetRenderBridge.DisposeRender(_runtimes[k]);
            _models[k] = _roots[k] = _runtimes[k] = null; _colored[k] = false;
        }
    }

    private static void SetField(object boxed, string name, object value)
    {
        for (Type t = boxed.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) { f.SetValue(boxed, value); return; }
        }
        throw new MissingFieldException(boxed.GetType().Name, name);
    }
}
