"""fracture.py [<painted planet name>] [--synthetic] [--preview <png>]

Britannica from a painted globe (2026-10-26, the user: "So I want to paint a globe, and then fracture it?"): the user
paints a whole planet in the PlanetTools add-on - height (below sea level = open sky), biome, and a "fracture" layer
(low = big intact chunks, high = shattered) - and this breaks the land into sky islands:
  - seeds: Poisson-disc on the sphere, spacing from the fracture paint (big chunks .. rubble);
  - cells: spherical Voronoi of the seeds (nearest seed per texel), clipped to land: each cell is one island, its
    outline the cell's land (so coasts stay where they were painted), the crack between neighbours a gap of sky;
  - altitude: the shattered land drifted apart - a smooth continental lift plus a per-fragment offset that grows
    where the fracture is high and for small pieces; biome under the cloud deck only if painted burnt;
  - each island keeps the painted terrain as its top (neighbours read as one landscape cracked apart).
Output: the island list (centre dir, type, top, area radius, 64-bearing outline) as detect_islands.py writes it, and
a preview. --synthetic: a noise globe to test without a painting."""
import argparse, os, sys
import numpy as np
from scipy.spatial import cKDTree
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\PlanetTools")
from planetkit import cube
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detect_islands as di

R = 60000.0
MAX_ISLAND = 10000.0   # m across
GAP = 300.0            # m of sky between neighbouring islands, at least ~0.8 of it
VOID_PROB = 0.33       # cells dropped whole (VoidCarver's owner-final ratio)
BIOMES = {1: "Mesa", 2: "Forest", 3: "Alpine", 4: "Plateau", 5: "Burnt", 6: "Desert"}


def fbm(d, freq, seed, octaves=5):
    acc, amp, tot = 0, 1.0, 0
    for o in range(octaves):
        r = np.random.default_rng(seed + o); k = r.normal(size=(3, 32)) * freq * 2 ** o; ph = r.uniform(0, 6.283, 32)
        acc = acc + amp * np.sin(d @ k + ph).mean(-1) * 1.6; tot += amp; amp *= 0.5
    return acc / tot


def synthetic(n=256):
    """A test globe: continents (height > 0 = land), biome regions, a fracture map."""
    d = np.stack([cube.texel_dirs(f, n) for f in range(6)])
    h = fbm(d, 2.2, 1) * 2600 + 300                                     # land where > 0
    b = np.digitize(fbm(d, 1.5, 9, 3), [-0.25, -0.05, 0.12, 0.3]) + 1    # 1..5 regions
    b = np.where(b == 5, 4, b).astype(np.uint8)
    b[(fbm(d, 3.0, 21, 3) > 0.42)] = 5                                   # burnt patches
    fr = np.clip((fbm(d, 2.6, 33, 3) * 0.5 + 0.5) * 255, 0, 255).astype(np.uint8)
    return h.astype(np.float32), b, fr, d


def poisson_seeds(d_flat, spacing_m, rng, tries=40000):
    """Dart throwing over the land texels, each seed keeping the spacing painted at its place."""
    order = rng.permutation(len(d_flat))[:tries]
    seeds, sp = [], []
    tree_pts = []
    for i in order:
        p = d_flat[i]; s = spacing_m[i]
        if tree_pts:
            t = cKDTree(np.array(tree_pts)) if len(tree_pts) % 64 == 1 or len(tree_pts) < 64 else t
            near = t.query_ball_point(p, 2 * np.sin(min(s, max(sp)) / R / 2) * 1.0)
            # (recent seeds not yet in the tree: checked directly)
            recent = np.array(tree_pts[len(t.data):]) if len(tree_pts) > len(t.data) else np.zeros((0, 3))
            if near or (len(recent) and (np.linalg.norm(recent - p, axis=1) < s / R).any()): continue
        seeds.append(i); sp.append(s); tree_pts.append(p)
    return np.array(seeds)


def fracture(height, biome, frac, dirs, seed=1915, sea=0.0):
    rng = np.random.default_rng(seed)
    n = height.shape[1]
    land = height > sea
    d_flat = dirs.reshape(-1, 3); l_flat = land.reshape(-1)
    f01 = frac.reshape(-1).astype(np.float32) / 255
    spacing = 1200 + 4800 * (1 - f01) ** 1.5                              # 6 km islands .. 1.2 km rubble (the user: smaller cells, 10 km max)
    li = np.nonzero(l_flat)[0]
    s_idx = li[poisson_seeds(d_flat[li], spacing[li], rng)]
    # cells: the nearest seed (domain-warped a little: wandering cracks); a cell wider than MaxIsland is split at its
    # farthest land texel until none is (the user: "maybe 10km max")
    warp = np.stack([fbm(d_flat, 14.0, 101 + k, 3) for k in range(3)], -1) * 0.006
    for _ in range(12):
        _, cell = cKDTree(d_flat[s_idx]).query(d_flat + warp)
        dist = np.linalg.norm(d_flat - d_flat[s_idx][cell], axis=1) * R
        far = np.where(l_flat, dist, 0)
        add = []
        for c in np.unique(cell[l_flat]):
            m = l_flat & (cell == c)
            i = np.argmax(np.where(m, far, -1))
            if far[i] > MAX_ISLAND / 2: add.append(i)
        if not add: break
        s_idx = np.concatenate([s_idx, add])
    # the gaps (the user: "don't let the cells touch"): a texel within GAP/2 of its cell's border - the bisector to
    # the second-nearest seed, (d2 - d1) / 2 on the sphere - is sky: every island stands apart, by >= GAP all round,
    # seam-free (no per-face pixel test)
    dd, cc = cKDTree(d_flat[s_idx]).query(d_flat + warp, k=2)
    half_gap = (GAP / 2) * (0.8 + 0.4 * (fbm(d_flat, 30.0, 202, 2) * 0.5 + 0.5))
    near_border = (dd[:, 1] - dd[:, 0]) * R / 2 < half_gap
    # whole cells voided at random (the user: "cut out some parts of voronoi"; VoidCarver's locked voidProb 0.33)
    voided = rng.random(len(s_idx) + 1) < VOID_PROB
    cell = np.where(l_flat & ~near_border & ~voided[cell], cell + 1, 0).astype(np.int64)
    c6 = cell.reshape(6, n, n)
    # altitudes: a continental lift (smooth) + each fragment's own drift
    lift = fbm(d_flat[s_idx], 1.2, 55, 3)
    islands = []
    paint = np.zeros((6, n, n), np.uint8)
    for k, si in enumerate(s_idx):
        m = (c6.reshape(-1) == k + 1)
        if m.sum() < di.MIN_TEXELS: continue
        bvals = biome.reshape(-1)[m]; b = int(np.bincount(bvals).argmax()); kind = BIOMES.get(b, "Plateau")
        size = np.sqrt(m.sum()) * (2 * np.pi * R / (4 * n))
        drift = (rng.random() - 0.5) * (1500 + 4000 * f01[si]) * (1.6 if size < 1500 else 1.0)
        if kind == "Burnt": top = 46000 + 4300 * rng.random()
        else: top = float(np.clip(59500 + 2200 * lift[k] + drift, 56600, 63300))
        paint.reshape(-1)[m] = b
        islands.append((m, kind, top))
    return islands, paint, c6


def to_records(islands, dirs, n):
    out = []
    d_flat = dirs.reshape(-1, 3); texel_m = 2 * np.pi * R / (4 * n)
    for m, kind, top in islands:
        d = d_flat[m]; up = d.mean(0); up /= np.linalg.norm(up); e1, e2 = di.frame(up)
        cosu = d @ up; x = top * (d @ e1) / cosu; y = top * (d @ e2) / cosu
        r = np.hypot(x, y) + texel_m * 0.5; th = np.arctan2(y, x)
        b = ((th + np.pi) / (2 * np.pi) * di.BEARINGS).astype(int) % di.BEARINGS
        rim = np.zeros(di.BEARINGS); np.maximum.at(rim, b, r)
        for _ in range(di.BEARINGS):
            z = rim == 0
            if not z.any(): break
            rim[z] = np.maximum(np.roll(rim, 1), np.roll(rim, -1))[z]
        rim = (np.roll(rim, 1) + 2 * np.maximum(rim, texel_m * 0.6) + np.roll(rim, -1)) / 4
        out.append({"dir": up, "type": kind, "top": top, "radius": float(np.sqrt(m.sum() * texel_m ** 2 / np.pi)), "rim": rim, "texels": int(m.sum())})
    return out


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("planet", nargs="?"); ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--preview"); ap.add_argument("--out")
    a = ap.parse_args()
    if a.synthetic:
        h, b, fr, d = synthetic()
    else:
        from planetkit.planet import Planet
        p = Planet.load(a.planet); h = p.height; b = p.layers["biome"]; fr = p.layers.get("fracture", np.zeros_like(b))
        n = h.shape[1]
        if b.shape[1] != n:   # (layers at their own resolution: nearest down/up to the height's)
            k = b.shape[1] / n; idx = (np.arange(n) * k).astype(int); b = b[:, idx][:, :, idx]; fr = fr[:, idx][:, :, idx]
        d = np.stack([cube.texel_dirs(f, n) for f in range(6)])
    isl, paint, cells = fracture(h, b, fr, d)
    rec = to_records(isl, d, h.shape[1])
    if a.out: di.write_cs(rec, a.out)
    if a.preview: di.preview(paint, rec, a.preview)
    kinds = {}
    for r in rec: kinds[r["type"]] = kinds.get(r["type"], 0) + 1
    rr = np.array([r["radius"] for r in rec])
    print(f"{len(rec)} islands {kinds}; radius median {np.median(rr):.0f}, max {rr.max():.0f} m")
