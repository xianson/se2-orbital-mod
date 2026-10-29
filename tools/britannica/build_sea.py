"""build_sea.py [--seed N] [--preview-dir <dir>] - Britannica's island sea with REAL terrain on every island (2026-10-26):
island_sea.py's layout, and for each island a top cut from its biome's real place (Copernicus GLO-30, true scale):
  Mesa    Monument Valley / Navajo country (N36-37 W111)   Plateau  Roraima's tepuis (N05 W061)
  Alpine  Mont Blanc, Bernese Alps (N45 E006, N46 E007)    Forest   Olympic Mountains (N47 W124)
  Burnt   Iceland's lava highlands (N65 W017)
Each island picks, of several random spots in its biome's tiles, the one with the most relief over its outline (the
character: buttes, peaks, table edges), at a random rotation; samples it at STEP m over its bounding square; heights
relative to its low ground. Stored 8-bit per island (its own min and scale) in one base64 blob.
Writes Scripts/Simulation/Procedural/BritannicaSea.g.cs (the mod compiles it in: islands + tops) and, with
--preview-dir, hillshades of a few islands."""
import argparse, base64, os, sys
import numpy as np
import tifffile
from scipy import ndimage
from scipy.ndimage import map_coordinates, gaussian_filter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import island_sea, detect_islands as di

DEM = r"D:\downloadsarchive\copdem"
MOD = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
STEP = 60.0
SOURCES = {"Mesa": [(36, -111), (37, -111)], "Plateau": [(5, -61)], "Alpine": [(45, 6), (46, 7)],
           "Forest": [(47, -124)], "Burnt": [(65, -17)],
           "Outline": [(45, 6), (46, 7), (47, -124), (37, -111)]}   # (rugged ground whose contours make the flat types' coasts)
_tiles = {}


def tile(lat, lon):
    if (lat, lon) not in _tiles:
        n = f"Copernicus_DSM_COG_10_{'N' if lat >= 0 else 'S'}{abs(lat):02d}_00_{'E' if lon >= 0 else 'W'}{abs(lon):03d}_00_DEM.tif"
        a = gaussian_filter(tifffile.imread(os.path.join(DEM, n)).astype(np.float32), 0.8)
        _tiles[(lat, lon)] = a
    return _tiles[(lat, lon)]


def cut(rng, kind, rim, radius, tries=8):
    """The island's top grid [n, n] (m above its low ground) and its coast's signed distance [n, n] (m, + inside), from
    its biome's real terrain. The OUTLINE is a contour of that terrain (the user: the Voronoi chunks "look pretty
    angular ... more interesting"): the patch, biased gently toward its centre so the contour closes, cut at the level
    that keeps the island's planned area - a mesa's coast follows its real cliffs, an alpine island's its ridges, and
    a cut may break into a few pieces (a butte cluster)."""
    half = radius * 1.85 + 2 * STEP
    n = int(np.ceil(half / STEP)) * 2 + 3                                # (odd: the centre is a sample)
    c = (np.arange(n) - (n - 1) / 2) * STEP
    X, Y = np.meshgrid(c, c)
    rr = np.hypot(X, Y) / (radius * 1.35)
    disk = rr < 1                                                # (the area the island's size is measured in)
    reach = rr < 1.3                                             # (where its coast may run: beyond the disk, no hard arc)
    h = patch(rng, kind, X, Y, half, disk, tries)
    # the coastline's shape: the island's own terrain where it is rugged (alpine ridges, forest hills); for the flat
    # types a contour of a rugged real patch - on flat ground the contour fell back to the centre bias and drew circles
    shape = h if kind in ("Alpine", "Forest") else patch(rng, "Outline", X, Y, half, disk, tries)
    # (its large-scale trend removed - a patch on one long slope gave a straight or round contour: the ridges and
    #  valleys at the island's own scale draw the coast)
    shape = shape - gaussian_filter(shape, sigma=max(1.0, radius * 0.5 / STEP))
    relief = max(float(np.percentile(shape[disk], 90) - np.percentile(shape[disk], 10)), 30.0)
    frac = min(0.95, np.pi * radius ** 2 / max(1.0, disk.sum() * STEP ** 2))
    # (the centre bias, strengthened until the coast closes by itself inside the reach - where it ran into the reach's
    #  edge the coast was a circular arc)
    rim_band = (rr > 1.22) & reach
    for bias in (0.8, 1.2, 1.7, 2.4, 3.4, 5.0):
        hb = shape + bias * relief * (1 - rr ** 2)
        level = np.quantile(hb[disk], 1 - frac)
        mask = (hb >= level) & reach
        if not (mask & rim_band).any(): break
    return finish(h, mask, rr, n)


def patch(rng, kind, X, Y, half, disk, tries):
    """A real-terrain patch over the grid (X, Y m) from `kind`'s sources: of `tries` random spots and rotations, the one
    with the most character (forest: rolling ~500 m)."""
    best, best_score = None, -np.inf
    for _ in range(tries):
        lat, lon = SOURCES[kind][rng.integers(len(SOURCES[kind]))]
        A = tile(lat, lon); rows, cols = A.shape
        rot = rng.uniform(0, 2 * np.pi); cr, sr = np.cos(rot), np.sin(rot)
        xr, yr = cr * X - sr * Y, sr * X + cr * Y
        mlat = 111320.0 / rows; mlon = 111320.0 / cols * np.cos(np.radians(lat + 0.5))
        margin_r, margin_c = half / mlat + 2, half / mlon + 2
        if rows < 2 * margin_r + 4 or cols < 2 * margin_c + 4: continue
        r0 = rng.uniform(margin_r, rows - margin_r); c0 = rng.uniform(margin_c, cols - margin_c)
        h = map_coordinates(A, [r0 - yr / mlat, c0 + xr / mlon], order=1, mode="nearest")
        if np.isnan(h).any() or (h[disk] < -50).any(): continue
        hi = h[disk]; score = np.percentile(hi, 95) - np.percentile(hi, 10)
        if kind == "Forest": score = -abs(score - 500)          # (forest: rolling - not the highest peaks)
        if score > best_score: best_score, best = score, h
    return best if best is not None else np.zeros(X.shape, np.float32)


def finish(h, mask, rr, n):
    lab, k = ndimage.label(mask)
    if k > 1:                                                     # (specks off: pieces under 4 % of the biggest)
        sizes = ndimage.sum(mask, lab, range(1, k + 1)); keep = sizes >= max(3, 0.04 * sizes.max())
        mask = keep[lab - 1] & mask
    holes = ndimage.binary_fill_holes(mask) & ~mask               # (small holes filled; big ones stay as sky wells)
    hl, hk = ndimage.label(holes)
    if hk: mask |= (ndimage.sum(holes, hl, range(1, hk + 1)) < 12)[hl - 1] & holes
    if not mask.any(): mask = rr < 0.5
    sdf = (ndimage.distance_transform_edt(mask) - ndimage.distance_transform_edt(~mask)) * STEP
    lo = np.percentile(h[mask], 10)
    # (cropped, still centred and odd: to the land's farthest cell from the centre + 4)
    cc = (n - 1) // 2; ys, xs = np.nonzero(mask)
    e = int(max(np.abs(xs - cc).max(), np.abs(ys - cc).max())) + 4
    sl = slice(max(0, cc - e), min(n, cc + e + 1))
    return (h - lo).astype(np.float32)[sl, sl], sdf.astype(np.float32)[sl, sl], mask[sl, sl]


LAB = os.path.join(os.environ["LOCALAPPDATA"], "OrbitalPlanets", "Britannica", "lab")
HERO_STEP = 30.0
STRETCH = {"Mesa": 2.5, "Plateau": 2.5, "Alpine": 2.2, "Forest": 2.0, "Burnt": 2.0, "Desert": 1.8}


def prep_heroes(isl, heroes):
    """The heroes' brush programmes for lab_islands.py: each on its OWN coast (its contour mask, smoothed to the lab's
    256), its type's programme (gen_masters.programme)."""
    import json, gen_masters as gm
    rng = np.random.default_rng(77); os.makedirs(os.path.join(LAB, "heroes"), exist_ok=True); out = []
    for k in heroes:
        i = isl[k]; m = i["mask"]; n = m.shape[0]; size_km = STEP * (n - 1) / 1000.0
        mk = gm.smooth_mask(m, gm.N)
        name = f"hero_{k}"; np.save(os.path.join(LAB, "heroes", name + ".npy"), mk)
        strokes, vertical, crag = gm.programme(i["type"], mk, size_km, rng)
        out.append({"name": name, "type": i["type"], "size_km": round(size_km, 3), "vertical": vertical, "crag": crag,
                    "mask": os.path.join(LAB, "heroes", name + ".npy"), "strokes": strokes, "max_ticks": 900})
    json.dump(out, open(os.path.join(LAB, "heroes.json"), "w"), indent=1)
    print(len(out), "hero programmes ->", os.path.join(LAB, "heroes.json"))


def use_heroes(isl, heroes):
    """Each hero's baked master (lab/out_heroes/hero_<k>.npz) as its top and coast, at HERO_STEP: the brushes' own
    terrain (the master's land = its baked ground above the sea), heights above its low ground."""
    used = 0
    for k in heroes:
        f = os.path.join(LAB, "out_heroes", f"hero_{k}.npz")
        if not os.path.exists(f): continue
        d = np.load(f); h = d["height"]; ext = float(d["size_km"]) * 1000.0
        n = int(round(ext / HERO_STEP)) // 2 * 2 + 1                     # (odd: the centre is a sample)
        z = ndimage.zoom(ndimage.gaussian_filter(h, 0.6 * h.shape[0] / n), n / h.shape[0], order=1)[:n, :n]
        if z.shape != (n, n): z = np.pad(z, ((0, n - z.shape[0]), (0, n - z.shape[1])), mode="edge")
        mask = z > 1.0
        lab, nl = ndimage.label(mask)
        if nl > 1:
            sizes = ndimage.sum(mask, lab, range(1, nl + 1)); mask = (sizes >= max(3, 0.02 * sizes.max()))[lab - 1] & mask
        if not mask.any(): continue
        lo = np.percentile(z[mask], 10)
        i = isl[k]; i["grid"] = np.maximum(z - lo, 0).astype(np.float32); i["mask"] = mask; i["step"] = HERO_STEP
        if i["type"] != "Burnt": i["top"] = float(min(i["top"], 64300.0 - float(i["grid"][mask].max()) - i["radius"] * 0.02))
        used += 1
    print(f"  heroes: {used} of {len(heroes)} from the brush masters")


def write_cs(isl, path):
    blob = bytearray(); meta = []
    for i in isl:
        g = i["grid"]; lo, hi = float(g.min()), float(g.max()); sc = max(hi - lo, 1.0) / 255
        m = i["mask"]
        present = ndimage.binary_dilation(m, structure=np.ones((3, 3), bool), iterations=2)   # (BritannicaSea.Near, r 2)
        lo, hi = float(g[present].min()), float(g[present].max()); sc = max(hi - lo, 1.0) / 255
        q = np.clip(np.rint((g - lo) / sc), 0, 255).astype(np.uint8)
        meta.append((len(blob), g.shape[0], lo, sc))
        blob += np.packbits(m.reshape(-1), bitorder="big").tobytes()     # (the land mask, a bit a cell)
        blob += q[present].tobytes()                                     # (heights: only the land and its surround)
    b64 = base64.b64encode(bytes(blob)).decode()
    chunks = [b64[k:k + 60000] for k in range(0, len(b64), 60000)]
    L = ["// GENERATED by tools/britannica/build_sea.py - Britannica's island sea with real-terrain tops. Do not edit.",
         "#pragma warning disable CS1591", "namespace OrbitalMod;", "",
         "/// <summary>Britannica's islands (island_sea.py's layout) and their real-terrain tops (build_sea.py): per island",
         f"/// its centre direction (planet frame), type, top radius (m), area radius (m), outline ({di.BEARINGS} radii by bearing",
         f"/// from -pi in its e1 / e2 frame), satellite flag, and a top grid: n x n samples Step m apart (60; the brush-authored heroes 30) centred on it, 8-bit",
         "/// at Offset in the blob: its land mask (n x n bits; the outline is a contour of the island's real terrain), then",
         "/// heights (Lo + byte x Scale, m above its low ground) for the land and 2 cells round it, row by row.</summary>",
         "public static partial class BritannicaSea", "{", f"    public const int Bearings = {di.BEARINGS};", f"    public const float GridStep = {STEP:.0f}f;",
         f"    public const int Count = {len(isl)};"]
    def arr(name, typ, vals, per=10, fmt="{}"):
        L.append(f"    public static readonly {typ}[] {name} = {{")
        for k in range(0, len(vals), per): L.append("        " + ", ".join(fmt.format(v) for v in vals[k:k + per]) + ",")
        L.append("    };")
    arr("Dir", "float", [v for i in isl for v in i["dir"]], 9, "{:.6f}f")
    arr("Type", "BritannicaPaint.Type", [f"BritannicaPaint.Type.{i['type']}" for i in isl], 8)
    arr("Top", "float", [i["top"] for i in isl], 10, "{:.0f}f")
    arr("Radius", "float", [i["radius"] for i in isl], 10, "{:.0f}f")
    arr("Satellite", "bool", ["true" if i.get("satellite") else "false" for i in isl], 16)
    arr("Rim", "float", [v for i in isl for v in i["rim"]], 16, "{:.0f}f")
    arr("GridOffset", "int", [m[0] for m in meta], 12)
    arr("GridN", "short", [m[1] for m in meta], 16)
    arr("Step", "float", [i["step"] for i in isl], 16, "{:.0f}f")
    arr("GridLo", "float", [m[2] for m in meta], 10, "{:.1f}f")
    arr("GridScale", "float", [m[3] for m in meta], 10, "{:.4f}f")
    L.append("    static readonly string[] Blob64 = {")
    L += [f'        "{c}",' for c in chunks]
    L += ["    };", "}", ""]
    open(path, "w", encoding="utf-8", newline="\n").write("\n".join(L))
    return len(blob), len(b64)


def hillshade(g):
    gy, gx = np.gradient(g, STEP)
    return np.clip(0.55 + (-gx * 0.6 + gy * 0.6) / np.sqrt(1 + gx ** 2 + gy ** 2), 0, 1)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--seed", type=int, default=1915); ap.add_argument("--preview-dir")
    ap.add_argument("--out", default=os.path.join(MOD, "Scripts", "Simulation", "Procedural", "BritannicaSea.g.cs"))
    ap.add_argument("--heroes", type=int, default=40, help="the largest main islands authored with the Terrain Mode brushes")
    ap.add_argument("--prep-heroes", action="store_true", help="write their brush programmes (lab/heroes.json) and stop")
    a = ap.parse_args()
    isl, _ = island_sea.generate(a.seed, 1400)
    rng = np.random.default_rng(a.seed + 1)
    for k, i in enumerate(isl):
        i["grid"], i["sdf"], i["mask"] = cut(rng, i["type"], i["rim"], i["radius"])
        # (its real peaks stay inside the voxel storage: the cube reaches 65.5 km along the axes - 64.3 km keeps a margin
        #  for the diagonal of its own breadth)
        if i["type"] != "Burnt": i["top"] = float(min(i["top"], 64300.0 - float(i["grid"][i["mask"]].max()) - i["radius"] * 0.02))
        i["step"] = STEP
        if k % 200 == 0: print(f"  {k}/{len(isl)}", flush=True)
    heroes = sorted([k for k, i in enumerate(isl) if not i.get("satellite")], key=lambda k: -isl[k]["radius"])[:a.heroes]
    if a.prep_heroes:
        prep_heroes(isl, heroes); sys.exit(0)
    use_heroes(isl, heroes)
    # (the tops' relief stretched up - the user, 2026-10-26: islands read as flat cut-out plates at real-world scale; a
    #  floating island wants its buttes and peaks tall, as the reference image. Asked first: on Earth the user rejected
    #  vertical stretch - here "do it all". Then the top re-capped inside the storage)
    for i in isl:
        i["grid"] = (i["grid"] * STRETCH.get(i["type"], 2.0)).astype(np.float32)
        if i["type"] != "Burnt": i["top"] = float(min(i["top"], 64300.0 - float(i["grid"][i["mask"]].max()) - i["radius"] * 0.02))
    nb, n64 = write_cs(isl, a.out)
    print(f"{len(isl)} islands, tops {nb / 1e6:.1f} MB ({n64 / 1e6:.1f} MB base64) -> {a.out}")
    if a.preview_dir:
        from PIL import Image
        os.makedirs(a.preview_dir, exist_ok=True)
        big = sorted([i for i in isl if not i.get("satellite")], key=lambda i: -i["radius"])
        picks = {}
        for i in big:
            if i["type"] not in picks: picks[i["type"]] = i
        tiles = []
        for t, i in picks.items():
            g = i["grid"]; n = g.shape[0]; c = (np.arange(n) - (n - 1) / 2) * STEP; X, Y = np.meshgrid(c, c)
            inside = i["sdf"] > 0
            col = np.array(island_sea.COL[t]) / 255
            img = hillshade(g)[..., None] * (0.5 + 0.5 * col) * (0.6 + 0.4 * (g / max(1, g.max())))[..., None]
            img[~inside] = (0.08, 0.06, 0.12)
            tiles.append(Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).resize((400, 400)))
            print(f"  preview {t}: r {i['radius']:.0f} m, relief {g[inside].max():.0f} m")
        sheet = Image.new("RGB", (400 * len(tiles), 400))
        for k, im in enumerate(tiles): sheet.paste(im, (400 * k, 0))
        sheet.save(os.path.join(a.preview_dir, "sea_tops.png"))
