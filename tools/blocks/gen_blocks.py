# gen_blocks.py — generate the sensor blocks' definitions (the Orbital Mod's Assets/Sensors) from vanilla blocks.
# Each block is a COPY of a vanilla block (its model, recipe, power, components) with its own name, block
# kind and build-menu group; the script mod (Orbital Mod) attaches the sensor component to its server
# prefab at load (Injection). Everything is read from the vanilla files, so a game update that renumbers
# component keys only needs a re-run. Build the caches: python gen_blocks.py --build
import json, os, sys

VANILLA = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content"
# Into the Orbital Mod project itself: a world loads definitions only from the mods on its own mod list,
# and the Orbital Mod is already on it (a separate blocks mod was not).
OUT = r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Orbital Mod"
PROJ = os.path.join(OUT, "Orbital Mod.vrgproj")
VER = "2.4.0.16"

def g(block, n):   # deterministic GUIDs: block 1..9, item 1..99
    return f"0b17{block:04x}-5e50-4000-8000-{n:012x}"

BLOCKS = [
    dict(n=1, key="Telescope", name="Telescope", power=50,
         desc="Passive optical and infrared telescope. Spots sunlit ships and rocks far off, and warm hulls in shadow. Silent: it gives nothing away.",
         dir=r"Blocks\RSS_Scanner\100", base="OreDetector100",
         powerable="OreDetector100_OreDetectorPowerableBlockDefinition.def",
         kind=r"UI\Screens\GScreen\Tools\Detectors\OreDetector_BlockKindDefinition.def"),
    dict(n=2, key="Radar", name="Radar", power=20,
         desc="Active radar. Ranges every contact in reach at once, fast and exact, but anyone about twice as far away sees you.",
         dir=r"Blocks\Antennas\Antennas\150", base="Antenna150",
         powerable="Antenna150_AntennasPowerableBlockDefinition.def",
         kind=r"UI\Screens\GScreen\Communication\Antennas\Antenna\AntennaBlockKindDefinition.def"),
]

def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)["$Value"]

def find_group(guid):
    for root, _, files in os.walk(os.path.join(VANILLA, "UI")):
        for fn in files:
            if fn.endswith(".def"):
                p = os.path.join(root, fn)
                with open(p, encoding="utf-8-sig") as f:
                    t = f.read()
                if f'"Guid": "{guid}"' in t:
                    return json.loads(t)["$Value"]
    raise SystemExit(f"group {guid} not found")

def bundles(game=True):
    b = {"System.Runtime": "1.0.0.0", "VRage": VER}
    if game: b = {"Game2": VER, **b}
    return b

def partial(base, kind_of, manip, game=True, how="Copy"):
    return {"$Bundles": bundles(game), "$Type": "VRage:Keen.VRage.ContentPipeline.Definitions.PartialDefinitionDiff",
            "$Value": {"BaseDefinition": base, "PartialDefinitionKind": how, "Manipulator": manip,
                       "DefinitionType": kind_of, "PriorityOverride": False}}

def meta(asset_id, out_name=None, processor=None):
    items = []
    if processor:
        items = [
            {"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Structure.FileInfoMetaDataItem", "FileHash": ""},
            {"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Features.AssetProcessing.AssetProcessingMetaDataItem",
             "ProcessorInfo": {"Version": 1 if processor == "P_PARTIAL_DEF" else 2, "Identifier": processor},
             "ContentFiles": {"MainOutput": {"FileHash": "", "FileName": out_name}, "AdditionalOutputs": []},
             "Problems": [], "DependenciesHashes": [], "AssetHash": "", "OverrideDetachedState": False, "IgnoredErrors": "None"},
        ]
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER},
            "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": asset_id, "InternalItems": items}}

def write(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=2)

def asset(folder, fname, obj, asset_id):
    write(os.path.join(folder, fname), obj)
    proc = "P_PARTIAL_DEF" if fname.endswith(".partialdef") else "P_DEF"
    out = fname.replace(".partialdef", ".def")
    write(os.path.join(folder, fname + ".meta"), meta(asset_id, out, proc))

def comp_changed(comp, old_def, new_def):
    """The composition's component keys, and an Update of the entry whose definition is old_def."""
    c = comp["Components"]
    for ch in c.get("Changed", []):
        v = ch.get("Value") or {}
        if v.get("Definition") == old_def:
            return c["Keys"], [{"Kind": "Update", "Index": ch["Index"], "Value": {"Definition": new_def}}]
    raise SystemExit(f"{old_def} not in composition {comp['Guid']}")

report = {}
BASE_FILES = [os.path.join(VANILLA, r"System\Configurations\ProgressionInitialUnlockConfiguration.def")]
assets = os.path.join(OUT, "Assets")
for b in BLOCKS:
    d = os.path.join(VANILLA, b["dir"])
    bind = load(os.path.join(d, b["base"] + ".def"))
    srv = load(os.path.join(d, b["base"] + "_Server.def"))
    scomp = load(os.path.join(d, b["base"] + "_ServerComposition.def"))
    cli = load(os.path.join(d, b["base"] + "_Client.def"))
    ccomp = load(os.path.join(d, b["base"] + "_ClientComposition.def"))
    pdef = load(os.path.join(d, b["powerable"]))
    BASE_FILES.extend(os.path.join(d, b["base"] + x) for x in (".def", "_Server.def", "_ServerComposition.def", "_Client.def", "_ClientComposition.def"))
    BASE_FILES.append(os.path.join(d, b["powerable"]))
    kind = load(os.path.join(VANILLA, b["kind"]))
    group = find_group(kind["BlockGroups"][0]["Value"])
    n, K = b["n"], b["key"]
    BIND, SPREF, SCOMP, CPREF, CCOMP, PDEF, KIND, GROUP = (g(n, i) for i in range(1, 9))
    folder = os.path.join(assets, "Sensors", K)
    P, C, BD = "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition", \
               "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"
    asset(folder, f"{K}.partialdef", partial(bind["Guid"], "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition",
          {"Guid": BIND, "ServerComposite": SCOMP, "ClientPrefab": CPREF}, game=False), BIND)
    asset(folder, f"{K}_Server.partialdef", partial(srv["Guid"], P, {"Guid": SPREF, "_entity": {"Definition": SCOMP}}, game=False), SPREF)
    asset(folder, f"{K}_Client.partialdef", partial(cli["Guid"], P, {"Guid": CPREF, "_entity": {"Definition": CCOMP}}, game=False), CPREF)
    keys, changed = comp_changed(scomp, pdef["Guid"], PDEF)
    asset(folder, f"{K}_ServerComposition.partialdef", partial(scomp["Guid"], C,
          {"Guid": SCOMP, "Components": {"$DeltaEncoded": True, "Keys": keys, "Changed": changed, "Removed": []}}), SCOMP)
    keys, changed = comp_changed(ccomp, pdef["Guid"], PDEF)
    asset(folder, f"{K}_ClientComposition.partialdef", partial(ccomp["Guid"], C,
          {"Guid": CCOMP, "Components": {"$DeltaEncoded": True, "Keys": keys, "Changed": changed, "Removed": []}}), CCOMP)
    asset(folder, f"{K}_PowerableBlockDefinition.partialdef", partial(pdef["Guid"], BD,
          {"Guid": PDEF, "UIData": {"Name": b["name"]}, "BlockKind": KIND,
           # (electricity: our own figure, not the antenna's / detector's)
           "ConsumedResource": {"Type": "bcded093-f5c0-4997-af3a-a6fbd853ad66", "Amount": b["power"]}}), PDEF)
    asset(folder, f"{K}_BlockKind.def", {"$Bundles": bundles(), "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
          "$Value": {"Guid": KIND, "Name": b["name"], "Description": b["desc"], "OrderingPriority": 0,
                     "BlockGroups": [{"Key": kind["BlockGroups"][0]["Key"], "Value": GROUP}]}}, KIND)
    asset(folder, f"{K}_BlockGroup.def", {"$Bundles": bundles(), "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
          "$Value": {"Guid": GROUP, "Name": b["name"], "Order": 0, "Subcategories": group["Subcategories"], "BlockKinds": []}}, GROUP)
    write(os.path.join(assets, "Sensors", K.lower() + ".meta"), meta(g(n, 90)))
    report[K] = {"server_prefab": SPREF, "kind": KIND}

# Unlocked from the start (blocks by server prefab, and their kinds), appended to the game's list. The game
# merges a partial with its base at load (the compiled .def holds only the change), so this applies to the
# current game's list.
PROG = "c7b8e669-e76b-4edb-aa18-8067d0340b96"
prog = partial(PROG, "Game2:Keen.Game2.Simulation.GameSystems.Progression.ProgressionInitialUnlockConfiguration",
               {"Blocks": {"$DeltaEncoded": True, "Removed": [], "Added": [r["server_prefab"] for r in report.values()]},
                "BlockKinds": {"$DeltaEncoded": True, "Removed": [], "Added": [r["kind"] for r in report.values()]}}, how="Override")
asset(os.path.join(assets, "Sensors"), "Override of ProgressionInitialUnlockConfiguration.partialdef", prog, g(9, 1))
write(os.path.join(assets, "sensors.meta"), meta(g(9, 90)))
# (project-level files: the Orbital Mod's own)
print(json.dumps(report, indent=1))

# ───────────────────────────── build ─────────────────────────────
# python gen_blocks.py --build : compile the assets and the caches (Content/). SE2 2.4 ships no working
# content builder for mods: the Mod SDK is stale (2.0.2 editor, a half-updated 2.2 Game2) and the game's
# own builder (Game2\VRage.ContentPipeline.Builder.exe, 2.4) needs an editor plugin it does not ship and
# a Vanilla project WITH SOURCES (the game has only compiled Content). So, in BUILD_ENV (outside the repo;
# never touching the game or the SDK install):
#   game2\      a copy of the game's Game2, plus a stand-in VRage.Plugin.Editor.dll (empty, v2.4.0.95:
#               the builder only loads it for its metadata)
#   root\GameData\Vanilla\   a stand-in Vanilla project (the real one's GUID) holding just the game's own
#               definitions our copies derive from; root\Game2 -> game2 (a junction)
# and the build runs against a builder-only copy of the project pointing at it. The runtime project keeps
# the game's GameData first (runbook section 2): the game merges each copy with the REAL base at load.
BUILD_ENV = os.path.join(os.environ["LOCALAPPDATA"], "OrbitalModBuild")
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2"

def build():
    import subprocess, shutil
    game2 = os.path.join(BUILD_ENV, "game2")
    ver_file = os.path.join(game2, ".copied_from")
    stamp = str(os.path.getmtime(os.path.join(GAME, "Game2", "Game2.Simulation.dll")))
    if not os.path.exists(ver_file) or open(ver_file).read() != stamp:
        print("copying the game's Game2 (a game update since the last build) ...")
        shutil.rmtree(game2, ignore_errors=True)
        shutil.copytree(os.path.join(GAME, "Game2"), game2)
        stub = os.path.join(BUILD_ENV, "stub")
        os.makedirs(stub, exist_ok=True)
        open(os.path.join(stub, "Stub.csproj"), "w").write(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework>'
            '<AssemblyName>VRage.Plugin.Editor</AssemblyName><AssemblyVersion>2.4.0.95</AssemblyVersion></PropertyGroup></Project>')
        open(os.path.join(stub, "Stub.cs"), "w").write("namespace Keen.VRage.Plugin.Editor { internal static class Stub { } }")
        subprocess.run(["dotnet", "build", "-c", "Release", "-o", "out"], cwd=stub, check=True, capture_output=True)
        shutil.copy(os.path.join(stub, "out", "VRage.Plugin.Editor.dll"), game2)
        open(ver_file, "w").write(stamp)
    root = os.path.join(BUILD_ENV, "root")
    van = os.path.join(root, "GameData", "Vanilla")
    jc = os.path.join(van, "Content")
    if os.path.lexists(jc): os.rmdir(jc)   # (a junction: removing it leaves the game's content alone)
    shutil.rmtree(van, ignore_errors=True)
    os.makedirs(os.path.join(van, "Assets"))
    if not os.path.exists(os.path.join(root, "Game2")):
        subprocess.run(["cmd", "/c", "mklink", "/J", os.path.join(root, "Game2"), game2], check=True, capture_output=True)
    pj = load_raw(os.path.join(GAME, "GameData", "Vanilla", "Vanilla.vrgproj"))
    pj["$Value"]["ProjectDirectories"] = [os.path.join(GAME, "VRage", "GameData")]
    write(os.path.join(van, "Vanilla.vrgproj"), pj)
    write(os.path.join(van, "Assets.meta"), {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER},
          "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
          "$Value": {"AssetID": g(0xff, 1), "InternalItems": [{"$Type": "VRage:Keen.VRage.ContentPipeline.Definitions.DefinitionSetItem", "DefinitionSetName": "World"}]}})

    # its compiled Content: the game's real one (the definition sets find the bases there), via a junction
    subprocess.run(["cmd", "/c", "mklink", "/J", os.path.join(van, "Content"), os.path.join(GAME, "GameData", "Vanilla", "Content")],
                   check=True, capture_output=True)
    # Each base as VANILLA'S OWN SOURCE declares it (the Mod SDK's Vanilla Assets: most bases are themselves
    # partial copies of templates, and the game rejects a definition that one set calls partial and another
    # not), following each partial's chain to its root, every file at its own path (a definition counts only
    # when its compiled output exists at the same path under Content: the game's real one, via the
    # junction), with the folders' metas.
    sdk_assets = r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets"
    index = {}
    for rt, _, files in os.walk(sdk_assets):
        for f in files:
            if f.endswith(".def.meta") or f.endswith(".partialdef.meta"):
                try:
                    with open(os.path.join(rt, f), encoding="utf-8-sig") as fh:
                        t = fh.read(400)
                    k = t.find('"AssetID": "')
                    if k >= 0: index[t[k + 12:k + 48]] = os.path.join(rt, f[:-5])
                except OSError: pass
    # where each definition's compiled output lives in the game's 2.4 content (some were renamed since the
    # SDK's sources: BasePoweableBlockBreakable -> BasePowerableBlockBreakable)
    compiled = {}
    for rt, _, files in os.walk(VANILLA):
        for f in files:
            if f.endswith(".def"):
                try:
                    with open(os.path.join(rt, f), encoding="utf-8-sig") as fh:
                        t = fh.read(600)
                    k = t.find('"Guid": "')
                    if k >= 0: compiled.setdefault(t[k + 9:k + 45], os.path.relpath(os.path.join(rt, f), VANILLA))
                except OSError: pass
    want = [load(src)["Guid"] for src in BASE_FILES]
    seen, folders = set(), set()
    while want:
        gid = want.pop()
        if gid in seen: continue
        seen.add(gid)
        src = index.get(gid)
        if src is None: raise SystemExit(f"base {gid} not in the Mod SDK's Vanilla sources")
        out = compiled.get(gid)
        if out is None: raise SystemExit(f"base {gid} has no compiled output in the game's content")
        ext = ".partialdef" if src.endswith(".partialdef") else ".def"
        rel = out[:-4] + ext   # the source at the compiled output's path (and name)
        dst = os.path.join(van, "Assets", rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy(src, dst)
        m = load_raw(src + ".meta")
        for it in m["$Value"]["InternalItems"]:
            if "ContentFiles" in it: it["ContentFiles"]["MainOutput"]["FileName"] = os.path.basename(out)
        write(dst + ".meta", m)
        parts = os.path.dirname(rel).split(os.sep)
        for k in range(1, len(parts) + 1): folders.add(os.sep.join(parts[:k]))
        if src.endswith(".partialdef"):
            want.append(load_raw(src)["$Value"]["BaseDefinition"])
    for n_, f in enumerate(sorted(folders)):
        parent, name = os.path.split(f)
        write(os.path.join(van, "Assets", parent, name.lower() + ".meta"), meta(g(0xfe, n_ + 1)))
    print(f"stand-in Vanilla: {len(seen)} source definition(s)")
    # A staging copy of the project (same id and name, the Assets only: the builder would otherwise also
    # compile the scripts, against assemblies it cannot find), built against the stand-in Vanilla; its
    # Content (the compiled definitions and caches) is copied back into the mod.
    stage = os.path.join(BUILD_ENV, "stage", os.path.basename(OUT))
    shutil.rmtree(stage, ignore_errors=True)
    os.makedirs(stage)
    shutil.copytree(os.path.join(OUT, "Assets"), os.path.join(stage, "Assets"))
    shutil.copy(os.path.join(OUT, "Assets.meta"), stage)
    pj = load_raw(PROJ)
    pj["$Value"]["ProjectDirectories"] = [os.path.join(GAME, "VRage", "GameData"), os.path.join(root, "GameData")]
    pj["$Value"]["ReferencedAssemblies"] = []
    proj = os.path.join(stage, os.path.basename(PROJ))
    write(proj, pj)
    log = os.path.join(BUILD_ENV, "build.log")
    exe = os.path.join(game2, "VRage.ContentPipeline.Builder.exe")
    r = subprocess.run([exe, "-p", proj, "-v"], capture_output=True, text=True, cwd=game2, timeout=1800)
    # then the caches (this pass crashes in the runtime's shutdown AFTER writing them: judged by the files)
    r2 = subprocess.run([exe, "-p", proj, "--generateCaches", "-v"], capture_output=True, text=True, cwd=game2, timeout=1800)
    open(log, "w", encoding="utf-8").write(r.stdout + r.stderr + " ==== caches ==== " + r2.stdout + r2.stderr)
    fails = [l for l in (r.stdout + r.stderr + r2.stdout + r2.stderr).splitlines() if " fail: " in l]
    ok = r.returncode == 0 and not fails and os.path.exists(os.path.join(stage, "Content", "contentcache.vrb"))
    if ok:
        for root_, _, files in os.walk(os.path.join(stage, "Content")):
            for f in files:
                src = os.path.join(root_, f)
                dst = os.path.join(OUT, "Content", os.path.relpath(src, os.path.join(stage, "Content")))
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy(src, dst)
    built = sum(len(f) for _, _, f in os.walk(os.path.join(stage, "Content", "Sensors")))
    print(f"builder exit {r.returncode}, {built} compiled file(s), {len(fails)} failure line(s){', copied into the mod' if ok else ''}; log {log}")
    for l in fails[:10]: print("  " + l[:300])
    return ok

def load_raw(path):
    with open(path, encoding="utf-8-sig") as f: return json.load(f)

if "--build" in sys.argv:
    sys.exit(0 if build() else 1)
