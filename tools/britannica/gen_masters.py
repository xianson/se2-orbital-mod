"""gen_masters.py [--per 4] - the brush programmes for Britannica's island masters (lab_islands.py paints them with
planeteditor's Terrain Mode brushes; 2026-10-26, the user: "Please use all the blender brushes").

Each master: an island's coast from a real-terrain contour (build_sea.cut - the organic outlines the user liked), as
the lab's sea map, and a brush programme for its type, laid out on the island's own geometry (its long axis, its
coast, its interior) - between them every brush in the mode:
  Alpine   Alpine spine, Folded side ranges, Glacial Valleys and Rivers from the spine to the coast, Smooth
  Mesa     Plateau over the land, Canyons cut in from the coast, Mesa stamps scattered, Badlands, a Cliff edge
  Plateau  a high hard Plateau (tepui), Cliff rims, Karst towers, a Canyon
  Forest   Rounded and Hills, Rivers, Domes, Smooth
  Burnt    Volcano, Shield Volcano, Caldera, Crater, a Rift, Badlands
  Desert   Dunes, a Basin, a Fault Block, Flatten, an Erase pass
Writes <lab>/masters.json and the masks (<lab>/masks/<name>.npy, 256 x 256 land)."""
import argparse, json, os, sys
import numpy as np
from scipy import ndimage
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import island_sea, build_sea

LAB = os.path.join(os.environ["LOCALAPPDATA"], "OrbitalPlanets", "Britannica", "lab")
N = 256


def smooth_mask(m, n_out):
    """A land mask resampled smoothly to n_out (bilinear, a light blur, the 0.5 contour) - nearest zoom drew the 60 m
    grid's staircase along every coast."""
    f = ndimage.zoom(m.astype(np.float32), n_out / m.shape[0], order=1)
    f = ndimage.gaussian_filter(f, 1.2)[:n_out, :n_out]
    if f.shape != (n_out, n_out): f = np.pad(f, ((0, n_out - f.shape[0]), (0, n_out - f.shape[1])))
    return f > 0.5


def geometry(mask):
    """Land texels' centre, long axis (unit, patch coords 0..1, y up), half-length, a few coast points and interior points."""
    ys, xs = np.nonzero(mask); n = mask.shape[0]
    P = np.stack([xs / (n - 1), 1 - ys / (n - 1)], 1)
    c = P.mean(0); cov = np.cov((P - c).T)
    w, v = np.linalg.eigh(cov); ax = v[:, 1]; L = np.sqrt(w[1]) * 2.0
    c = P[np.argmin(np.linalg.norm(P - c, axis=1))]   # (the centre ON the land: a crescent's centroid is sky - stamps missed)
    edge = mask & ~ndimage.binary_erosion(mask)
    ey, ex = np.nonzero(edge); E = np.stack([ex / (n - 1), 1 - ey / (n - 1)], 1)
    inner = ndimage.distance_transform_edt(mask)
    iy, ix = np.nonzero(inner > inner.max() * 0.35); I = np.stack([ix / (n - 1), 1 - iy / (n - 1)], 1)
    return c, ax, L, E, I


def line(a, b, k=6, wob=0.0, rng=None):
    t = np.linspace(0, 1, k)[:, None]; p = a + (b - a) * t
    if wob and rng is not None:
        nrm = np.array([-(b - a)[1], (b - a)[0]]); nrm /= max(np.linalg.norm(nrm), 1e-9)
        p += nrm * (np.sin(t * np.pi * rng.uniform(1, 2.5) + rng.uniform(0, 6)) * wob)
    return np.clip(p, 0.02, 0.98).tolist()


def programme(kind, mask, size_km, rng):
    c, ax, L, E, I = geometry(mask)
    S = []
    def add(brush, pts, r_km, strength=0.6, level=None, erase=False):
        d = {"brush": brush, "points": pts, "radius_km": float(r_km), "strength": float(strength)}
        if level is not None: d["level_m"] = float(level)
        if erase: d["erase"] = True
        S.append(d)
    a, b = c - ax * L * 0.8, c + ax * L * 0.8
    def coast_to(p, k=5):      # (a stroke from an interior point out to the nearest coast)
        e = E[np.argmin(np.linalg.norm(E - p, axis=1))]; return line(p, e, k, 0.02, rng)
    def inner_pt(): return I[rng.integers(len(I))]
    R = size_km
    if kind == "Alpine":
        add("alpine", line(a, b, 8, 0.04, rng), R * 0.09, 0.9)
        for _ in range(rng.integers(2, 4)):
            p = c + ax * L * rng.uniform(-0.6, 0.6); q = p + np.array([-ax[1], ax[0]]) * rng.choice([-1, 1]) * L * 0.5
            add("folded", line(p, q, 5, 0.02, rng), R * 0.04, 0.6)
        for _ in range(rng.integers(2, 4)): add("glacier", coast_to(c + ax * L * rng.uniform(-0.5, 0.5)), R * 0.03, 0.7, level=rng.uniform(150, 350))
        for _ in range(rng.integers(1, 3)): add("river", coast_to(inner_pt()), R * 0.015, 0.6)
        add("smooth", [inner_pt().tolist()], R * 0.05, 0.3)
        return S, 1.6, 1.4
    if kind == "Mesa":
        add("plateau", line(a, b, 6, 0.05, rng), R * 0.18, 0.8, level=rng.uniform(250, 450))
        for _ in range(rng.integers(2, 5)): add("canyon", coast_to(inner_pt(), 6), R * 0.025, 0.7, level=rng.uniform(150, 300))
        for _ in range(rng.integers(5, 10)): add("mesa", [inner_pt().tolist()], R * rng.uniform(0.02, 0.05), 0.8)
        add("badlands", line(*E[rng.integers(len(E), size=2)], 4), R * 0.05, 0.6)
        e = E[rng.integers(len(E))]; add("cliff", line(e, c, 4), R * 0.05, 0.7, level=rng.uniform(150, 300))
        return S, 1.0, 2.0
    if kind == "Plateau":
        add("plateau", [c.tolist(), (c + ax * L * 0.3).tolist(), (c - ax * L * 0.3).tolist()], R * 0.22, 0.9, level=rng.uniform(500, 900))
        for _ in range(rng.integers(2, 4)):
            e = E[rng.integers(len(E))]; add("cliff", line(e, e + (c - e) * 0.3, 4), R * 0.04, 0.8, level=rng.uniform(300, 600))
        for _ in range(rng.integers(1, 3)): add("karst", [inner_pt().tolist(), inner_pt().tolist()], R * 0.04, 0.7)
        add("canyon", coast_to(inner_pt(), 6), R * 0.02, 0.6, level=rng.uniform(150, 300))
        return S, 1.0, 1.8
    if kind == "Forest":
        add("rounded", line(a, b, 6, 0.06, rng), R * 0.12, 0.6)
        for _ in range(rng.integers(2, 4)): add("hills", [inner_pt().tolist(), inner_pt().tolist()], R * 0.08, 0.5)
        for _ in range(rng.integers(2, 4)): add("river", coast_to(inner_pt()), R * 0.015, 0.6)
        for _ in range(rng.integers(1, 3)): add("dome", [inner_pt().tolist()], R * 0.04, 0.6)
        add("smooth", [inner_pt().tolist(), inner_pt().tolist()], R * 0.06, 0.3)
        return S, 1.7, 0.8
    if kind == "Burnt":
        add(str(rng.choice(["volcano", "shield"])), [c.tolist()], R * rng.uniform(0.12, 0.2), 0.8)
        add("caldera", [inner_pt().tolist()], R * 0.08, 0.7)
        for _ in range(rng.integers(2, 5)): add("crater", [inner_pt().tolist()], R * rng.uniform(0.02, 0.05), 0.7)
        add("rift", line(a, b, 5, 0.03, rng), R * 0.04, 0.6, level=rng.uniform(80, 200))
        add("badlands", [inner_pt().tolist(), inner_pt().tolist()], R * 0.06, 0.5)
        for _ in range(rng.integers(1, 3)):          # (islets raised just off the coast)
            e = E[rng.integers(len(E))]; add("island", [np.clip(e + (e - c) * 0.12, 0.03, 0.97).tolist()], R * 0.025, 0.7)
        return S, 1.0, 1.6
    # Desert
    add("dunes", line(a, b, 6, 0.02, rng), R * 0.15, 0.6)
    add("basin", [inner_pt().tolist()], R * 0.07, 0.6, level=rng.uniform(60, 150))
    e = E[rng.integers(len(E))]; add("fault", line(c, e, 4), R * 0.06, 0.6, level=rng.uniform(100, 250))
    add("flatten", [inner_pt().tolist()], R * 0.05, 0.6)
    add("erase", [inner_pt().tolist()], R * 0.03, 0.5)
    e = E[rng.integers(len(E))]; add("coast", line(e, e + (c - e) * 0.35, 4), R * 0.03, 0.8)   # (a bay of sky cut in)
    return S, 0.8, 1.0


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--per", type=int, default=4); a = ap.parse_args()
    isl, _ = island_sea.generate(1915, 1400)
    rng = np.random.default_rng(42)
    os.makedirs(os.path.join(LAB, "masks"), exist_ok=True)
    out = []
    for kind in ["Alpine", "Mesa", "Plateau", "Forest", "Burnt", "Desert"]:
        src = "Mesa" if kind == "Desert" else kind      # (no Desert islands in the layout yet: Mesa-sized ones)
        cands = [i for i in isl if i["type"] == src and not i.get("satellite") and i["radius"] > 1200]
        for j in range(a.per):
            i = cands[rng.integers(len(cands))]
            h, sdf, m = build_sea.cut(rng, i["type"], i["rim"], i["radius"])
            n = m.shape[0]; size_km = build_sea.STEP * (n - 1) / 1000.0
            mk = smooth_mask(m, N)
            name = f"{kind.lower()}_{j}"
            np.save(os.path.join(LAB, "masks", name + ".npy"), mk)
            strokes, vertical, crag = programme(kind, mk, size_km, rng)
            out.append({"name": name, "type": kind, "size_km": round(size_km, 2), "vertical": vertical, "crag": crag,
                        "mask": os.path.join(LAB, "masks", name + ".npy"), "strokes": strokes, "max_ticks": 900})
            print(name, f"{size_km:.1f} km", len(strokes), "strokes:", sorted({s['brush'] for s in strokes}))
    json.dump(out, open(os.path.join(LAB, "masters.json"), "w"), indent=1)
    used = sorted({s["brush"] for r in out for s in r["strokes"]})
    print(len(out), "masters;", len(used), "brushes used:", used)
