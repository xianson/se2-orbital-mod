"""island_sea.py [--seed N] [--preview <png>] - Britannica as an ASTEROID ISLAND SEA (2026-10-26, the user: "big clumps
... lame. I want an asteroid island sea, with some areas of clusters, some thin"): islands over the whole planet, the
interest in how their density varies -
  - clusters: dense swarms round a centre - rubble swarms (tiny, packed) or a giant with satellites;
  - belts: long winding streams of islands arcing round the planet (wavy great-circle bands);
  - thin scatter: lone islands drifting everywhere between;
  - voids: big stretches of open sky;
  - sizes: a long tail (200 m .. 2 km mostly, some 3-6 km, rare ~10 km giants anchoring clusters);
  - altitude: the whole band, so islands overlap at many levels; burnt ones in the gloom under the cloud sea;
  - biome: broad regions (smooth on the sphere), each cluster themed.
Placement: dart-throwing with a density field and a size-scaled clearance (no two islands touch: >= GAP of sky
shore to shore). Each island's outline: a chunky broken-rock polygon (6-11 sides, noise-jagged), 64 bearings.
Writes the records (detect_islands.write_cs format) and a preview: two hemispheres from space + a close oblique view."""
import argparse, os, sys
import numpy as np
from scipy.spatial import cKDTree
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detect_islands as di

R = 60000.0
GAP = 250.0
REACH = 1.75   # (an island's coast reaches up to this x its radius: build_sea.py's real-terrain contours)
BIOMES = ["Mesa", "Forest", "Alpine", "Plateau"]
COL = {"Mesa": (184, 86, 52), "Forest": (46, 112, 54), "Alpine": (226, 228, 236), "Plateau": (196, 170, 96), "Burnt": (52, 44, 44)}


def rand_dirs(rng, k):
    v = rng.normal(size=(k, 3)); return v / np.linalg.norm(v, axis=1, keepdims=True)


def smooth_field(seed, freq, octaves=3):
    rng = np.random.default_rng(seed)
    ks = [rng.normal(size=(3, 24)) * freq * 2 ** o for o in range(octaves)]; ph = [rng.uniform(0, 6.283, 24) for _ in range(octaves)]
    def f(d):
        acc, amp, tot = 0, 1.0, 0
        for k, p in zip(ks, ph): acc = acc + amp * np.sin(d @ k + p).mean(-1) * 1.6; tot += amp; amp *= 0.5
        return acc / tot
    return f


class World:
    def __init__(self, seed):
        rng = self.rng = np.random.default_rng(seed)
        # clusters: centre, spread (m), kind, theme
        self.clusters = []
        for c in rand_dirs(rng, 16):
            kind = rng.choice(["swarm", "giant", "mixed"], p=[0.35, 0.3, 0.35])
            self.clusters.append((c, rng.uniform(2500, 7000), kind, BIOMES[rng.integers(4)], rng.random() < 0.15))
        # belts: a wavy great circle each - its pole, wave, width
        self.belts = [(rand_dirs(rng, 1)[0], rng.uniform(0.05, 0.16), rng.integers(2, 5), rng.uniform(0, 6.28), rng.uniform(1800, 4500)) for _ in range(3)]
        self.voids = [(c, rng.uniform(12000, 22000)) for c in rand_dirs(rng, 6)]
        self.biome_f = smooth_field(seed + 7, 2.0); self.alt_f = smooth_field(seed + 9, 3.0)
        self.biome_q = np.quantile(self.biome_f(rand_dirs(rng, 20000)), [0.25, 0.5, 0.75])   # (four equal shares)

    def density(self, d):
        """Relative island density at directions d [k, 3] (0..~12), and the cluster index each is in (or -1)."""
        dens = np.full(len(d), 0.12)                                           # the thin scatter
        cl = np.full(len(d), -1)
        best = np.zeros(len(d))
        for i, (c, s, kind, _, _) in enumerate(self.clusters):
            a = np.arccos(np.clip(d @ c, -1, 1)) * R
            w = np.exp(-(a / s) ** 2) * (14.0 if kind == "swarm" else 9.0)
            cl = np.where(w > best, i, cl); best = np.maximum(best, w); dens += w
        for pole, amp, nw, ph, width in self.belts:
            e1 = np.cross(pole, [0, 1, 0] if abs(pole[1]) < 0.9 else [1, 0, 0]); e1 /= np.linalg.norm(e1); e2 = np.cross(pole, e1)
            lon = np.arctan2(d @ e2, d @ e1); lat = np.arcsin(np.clip(d @ pole, -1, 1))
            off = (lat - amp * np.sin(nw * lon + ph)) * R
            dens += 5.0 * np.exp(-(off / width) ** 2) * (0.4 + 0.6 * np.sin(3 * lon + ph * 2) ** 2)
        for c, s in self.voids:
            a = np.arccos(np.clip(d @ c, -1, 1)) * R
            dens *= np.clip((a - s * 0.7) / (s * 0.3), 0, 1)
        cl = np.where(best > 0.5, cl, -1)
        return dens, cl


def outline(rng, r):
    """A chunky broken-rock outline: an irregular polygon (6-11 corners), jagged a little, 64 bearings."""
    k = rng.integers(6, 12)
    ang = np.sort(rng.uniform(-np.pi, np.pi, k)); rad = r * rng.uniform(0.7, 1.15, k)
    pts = np.stack([np.cos(ang) * rad, np.sin(ang) * rad], 1)
    th = -np.pi + (np.arange(di.BEARINGS) + 0.5) * 2 * np.pi / di.BEARINGS
    out = np.zeros(di.BEARINGS)
    for j, t in enumerate(th):               # ray / polygon: the distance to the polygon's edge along bearing t
        dx, dy = np.cos(t), np.sin(t); best = 0
        for a, b in zip(pts, np.roll(pts, -1, 0)):
            ex, ey = b - a; den = dx * ey - dy * ex
            if abs(den) < 1e-9: continue
            s = (a[0] * ey - a[1] * ex) / den; u = (a[0] * dy - a[1] * dx) / den
            if s > 0 and 0 <= u <= 1: best = max(best, s)
        out[j] = best
    return out * (1 + 0.05 * rng.normal(size=di.BEARINGS))


def generate(seed=1915, target=900):
    w = World(seed); rng = w.rng
    isl, pts, rads = [], [], []
    tree = None
    for attempt in range(target * 400):
        if len(isl) >= target: break
        d = rand_dirs(rng, 1)[0]
        dens, cl = w.density(d[None]); dens, cl = dens[0], cl[0]
        if rng.random() * 14 > dens: continue
        # size: a long tail; swarms small, giants' centres big
        u = rng.random()
        r = 350 + 2800 * u ** 2 + (rng.random() < 0.08) * rng.uniform(1800, 3200)   # (the user: bigger than 1 km, ~10 km max; then "slightly bigger")
        if cl >= 0:
            c, s, kind, theme, burnt_cl = w.clusters[cl]
            a = np.arccos(np.clip(d @ c, -1, 1)) * R
            if kind == "swarm": r = 60 + 450 * u ** 2.5
            elif kind == "giant" and a < s * 0.3 and sum(i.get("giant_of") == cl for i in isl) < 2: r = rng.uniform(3500, 5000)
        # clearance: shore to shore >= GAP (size-scaled neighbours)
        if pts:
            if tree is None or len(pts) % 32 == 0: tree, tn = cKDTree(np.array(pts)), len(pts)
            cand = tree.query_ball_point(d, (REACH * (r + 5200) + GAP) / R) + list(range(tn, len(pts)))
            if any(np.linalg.norm(pts[j] - d) * R < REACH * r + rads[j] + GAP for j in set(cand)): continue
        biome = BIOMES[int(np.searchsorted(w.biome_q, w.biome_f(d[None])[0]))]
        burnt = rng.random() < 0.08
        if cl >= 0:
            if rng.random() < 0.8: biome = w.clusters[cl][3]
            burnt = burnt or (w.clusters[cl][4] and rng.random() < 0.85)
        if burnt: biome, top = "Burnt", rng.uniform(46000, 50300)
        else: top = float(np.clip(59800 + 2600 * w.alt_f(d[None])[0] + rng.normal(0, 1300), 56600, 63300 - r * 0.1))
        rec = {"dir": d, "type": biome, "top": top, "radius": r, "rim": outline(rng, r), "texels": 0}
        if cl >= 0 and r >= 3500: rec["giant_of"] = cl
        isl.append(rec); pts.append(d); rads.append(REACH * r)
    # satellites (kept small - the user: "slightly bigger islands (not including satellites)"): 0-4 rocks of 30-200 m
    # just off each larger island's coast, near its top level, its biome; clear of every island
    tree = cKDTree(np.array(pts)); n_main = len(isl)
    for k in range(n_main):
        a = isl[k]
        if a["radius"] < 600: continue
        f1, f2 = di.frame(a["dir"])
        for _ in range(rng.integers(0, 5)):
            r = 30 + 170 * rng.random() ** 2
            th = rng.uniform(0, 2 * np.pi); off = REACH * a["radius"] * rng.uniform(1.0, 1.25) + REACH * r + GAP
            d = a["dir"] * R + (f1 * np.cos(th) + f2 * np.sin(th)) * off; d /= np.linalg.norm(d)
            near = tree.query_ball_point(d, (REACH * (5200 + r) + GAP) / R)
            if any(np.linalg.norm(pts[j] - d) * R < REACH * r + rads[j] + GAP for j in near): continue
            if any(np.linalg.norm(pts[j] - d) * R < REACH * r + rads[j] + GAP for j in range(n_main, len(pts))): continue
            top = a["top"] + rng.uniform(-400, 250)
            if a["type"] != "Burnt": top = float(np.clip(top, 56400, 63300))
            isl.append({"dir": d, "type": a["type"], "top": top, "radius": r, "rim": outline(rng, r), "texels": 0, "satellite": True})
            pts.append(d); rads.append(REACH * r)
    return isl, w


def preview(isl, path):
    from PIL import Image, ImageDraw
    W = 900
    def hemisphere(view):
        up = np.array([0, 1, 0.0]) if abs(view[1]) < 0.9 else np.array([1, 0, 0.0])
        e1 = np.cross(up, view); e1 /= np.linalg.norm(e1); e2 = np.cross(view, e1)
        c = (np.arange(W) + 0.5) / W * 2 - 1; X, Y = np.meshgrid(c, -c)
        on = X ** 2 + Y ** 2 < 1; Z = np.sqrt(np.clip(1 - X ** 2 - Y ** 2, 0, 1))
        d = (X[..., None] * e1 + Y[..., None] * e2 + Z[..., None] * view)[on]
        img = np.zeros((W, W, 3)); img[...] = (14, 10, 24)
        sea = np.array([150, 132, 178]) * (0.35 + 0.65 * Z[on])[:, None]    # the cloud sea, lavender, limb-darkened
        col = sea.copy()
        D = np.array([i["dir"] for i in isl]); tree = cKDTree(D)
        dist, k = tree.query(d, k=6)
        for j in range(6):
            ii = k[:, j]
            for idx in np.unique(ii):
                m = ii == idx
                isl_ = isl[idx]; up_ = isl_["dir"]; f1, f2 = di.frame(up_)
                x = d[m] @ f1 * isl_["top"]; y = d[m] @ f2 * isl_["top"]
                th = np.arctan2(y, x); b = ((th + np.pi) / (2 * np.pi) * di.BEARINGS).astype(int) % di.BEARINGS
                inside = np.hypot(x, y) < isl_["rim"][b]
                if not inside.any(): continue
                shade = 0.55 + 0.45 * (isl_["top"] - 46000) / 17000
                cc = np.array(COL[isl_["type"]]) * shade * (0.45 + 0.55 * Z[on][m][inside])[:, None]
                sel = np.nonzero(m)[0][inside]
                col[sel] = cc if isl_["type"] != "Burnt" else col[sel] * 0.75 + cc * 0.25   # (under the cloud sea: a shadow through it)
        img[on] = col
        return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    a = hemisphere(np.array([0, 0, 1.0])); b = hemisphere(np.array([0, 0, -1.0]))
    sheet = Image.new("RGB", (2 * W, W)); sheet.paste(a, (0, 0)); sheet.paste(b, (W, 0)); sheet.save(path)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--seed", type=int, default=1915); ap.add_argument("--preview")
    ap.add_argument("--n", type=int, default=1400); ap.add_argument("--out")
    a = ap.parse_args()
    isl, w = generate(a.seed, a.n)
    if a.preview: preview(isl, a.preview)
    if a.out: di.write_cs(isl, a.out)
    kinds = {}
    for i in isl: kinds[i["type"]] = kinds.get(i["type"], 0) + 1
    main = [i for i in isl if not i.get("satellite")]
    rr = np.array([i["radius"] for i in main])
    area = sum(np.pi * i["radius"] ** 2 for i in isl) / (4 * np.pi * R ** 2)
    print(f"{len(main)} islands + {len(isl) - len(main)} satellites, {area:.1%} of the sphere; main radius p50 {np.median(rr):.0f}, p90 {np.percentile(rr, 90):.0f}, max {rr.max():.0f} m")
