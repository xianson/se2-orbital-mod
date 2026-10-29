"""detect_islands.py <islands.npy> [elevation.npy] [--out <.g.cs>] [--preview <png>]

Britannica's islands from the user's painted map (2026-10-26, the user: "Can we auto detect islands from the painted
map!"). The paint is a [6, M, M] uint8 layer of the PlanetTools add-on (planetkit face order, cube.texel_dirs): 0 = open
sky, any other value = an island TYPE (BritannicaPaint.Type). Each connected blob of one value is one island - connected
across the cube's seams - and its outline is what was painted:
  - centre: the blob's mean direction; size: the radius of a disc of its area;
  - outline: the furthest painted texel in each of 64 bearings round the centre (the island's own e1 / e2 frame -
    BritannicaIsland.Init's), in metres at its top radius, lightly smoothed;
  - top: the optional elevation layer's mean over the blob (1..255 -> the type's height band), else a seeded spread.
Blobs under MinTexels are dropped as stray brush dabs; small ones become satellites by size alone.
Writes Scripts/Simulation/Procedural/BritannicaPainted.g.cs (a generated data file the mod compiles in - a mod cannot
read loose files) and a preview PNG of what it found."""
import argparse, os, sys
import numpy as np
from scipy import ndimage
from scipy.spatial import cKDTree
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\PlanetTools")
from planetkit import cube

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", ".."))
BEARINGS = 64
MIN_TEXELS = 3
# the types' top bands (m from the planet's centre): BritannicaWorld's (alive over the cloud sea, burnt under it)
TYPES = {1: "Mesa", 2: "Forest", 3: "Alpine", 4: "Plateau", 5: "Burnt", 6: "Desert"}
BANDS = {"Burnt": (46000.0, 50300.0)}
ALIVE = (56300.0, 63600.0)


def frame(up):
    e1 = np.cross(up, [0, 1, 0] if abs(up[1]) < 0.9 else [1, 0, 0]); e1 /= np.linalg.norm(e1)
    return e1, np.cross(up, e1)


def detect(paint, elev=None, seed=1915):
    n = paint.shape[1]
    dirs = np.stack([cube.texel_dirs(f, n) for f in range(6)])          # [6, n, n, 3]
    texel_m = 2 * np.pi * 60000 / (4 * n)                              # (a texel's size at the island band, m)
    # per face labels, then joined across seams: each edge texel unions with its nearest texels on the other faces
    labels = np.zeros(paint.shape, np.int64); nxt = 0
    for f in range(6):
        for v in np.unique(paint[f]):
            if v == 0: continue
            lab, k = ndimage.label(paint[f] == v, structure=np.ones((3, 3)))
            labels[f][lab > 0] = lab[lab > 0] + nxt; nxt += k
    parent = np.arange(nxt + 1)
    def find(a):
        while parent[a] != a: parent[a] = parent[parent[a]]; a = parent[a]
        return a
    edge = np.zeros((n, n), bool); edge[0, :] = edge[-1, :] = edge[:, 0] = edge[:, -1] = True
    flat_dirs = dirs.reshape(-1, 3); tree = cKDTree(flat_dirs)
    flat_lab = labels.reshape(-1); flat_val = paint.reshape(-1)
    for f in range(6):
        idx = np.nonzero(edge & (labels[f] > 0))
        if not len(idx[0]): continue
        q = dirs[f][idx]; _, nb = tree.query(q, k=6)
        own = (f * n + idx[0]) * n + idx[1]
        for a, row in zip(own, nb):
            for b in row:
                if b // (n * n) != f and flat_lab[b] and flat_val[b] == flat_val[a]:
                    ra, rb = find(flat_lab[a]), find(flat_lab[b])
                    if ra != rb: parent[max(ra, rb)] = min(ra, rb)
    roots = np.array([find(l) if l else 0 for l in range(nxt + 1)])
    comp = roots[flat_lab]
    rng = np.random.default_rng(seed)
    islands = []
    for c in np.unique(comp):
        if c == 0: continue
        m = comp == c
        if m.sum() < MIN_TEXELS: continue
        d = flat_dirs[m]; t = int(flat_val[m][0]); kind = TYPES.get(t, "Plateau")
        up = d.mean(0); up /= np.linalg.norm(up)
        lo, hi = BANDS.get(kind, ALIVE)
        if elev is not None:
            e = elev.reshape(-1)[m]; e = e[e > 0]
            top = lo + (hi - lo) * (e.mean() - 1) / 254 if len(e) else lo + (hi - lo) * rng.random() ** 0.8
        else:
            top = lo + (hi - lo) * rng.random() ** 0.8
        e1, e2 = frame(up)
        # each texel's place on the island's plane (gnomonic about the centre, metres at its top)
        cosu = d @ up; x = top * (d @ e1) / cosu; y = top * (d @ e2) / cosu
        r = np.hypot(x, y) + texel_m * 0.5; th = np.arctan2(y, x)
        b = ((th + np.pi) / (2 * np.pi) * BEARINGS).astype(int) % BEARINGS
        rim = np.zeros(BEARINGS); np.maximum.at(rim, b, r)
        # (an empty bearing - a thin blob, a single texel - takes its neighbours'; then a light circular smoothing)
        for _ in range(BEARINGS):
            z = rim == 0
            if not z.any(): break
            rim[z] = np.maximum(np.roll(rim, 1), np.roll(rim, -1))[z]
        rim = np.maximum(rim, texel_m * 0.6)
        rim = (np.roll(rim, 1) + 2 * rim + np.roll(rim, -1)) / 4
        area_r = np.sqrt(m.sum() * texel_m ** 2 / np.pi)
        islands.append({"dir": up, "type": kind, "top": float(top), "radius": float(area_r), "rim": rim, "texels": int(m.sum())})
    return islands


def write_cs(islands, path):
    L = ["// GENERATED by tools/britannica/detect_islands.py from the user's painted map - do not edit by hand.",
         "#pragma warning disable CS1591", "namespace OrbitalMod;", "",
         "/// <summary>Britannica's painted islands (see detect_islands.py): per island its centre direction (planet frame),",
         f"/// type, top radius (m), area radius (m) and outline - {BEARINGS} radii (m) by bearing from -pi, in its e1 / e2 frame.</summary>",
         "public static class BritannicaPainted", "{", f"    public const int Bearings = {BEARINGS};",
         "    public static readonly float[] Dir = {"]
    L += ["        " + ", ".join(f"{v:.6f}f" for v in i["dir"]) + "," for i in islands]
    L += ["    };", "    public static readonly BritannicaPaint.Type[] Type = {"]
    L += ["        " + ", ".join(f"BritannicaPaint.Type.{i['type']}" for i in islands[k:k + 8]) + "," for k in range(0, len(islands), 8)]
    L += ["    };", "    public static readonly float[] Top = {"]
    L += ["        " + ", ".join(f"{i['top']:.0f}f" for i in islands[k:k + 10]) + "," for k in range(0, len(islands), 10)]
    L += ["    };", "    public static readonly float[] Radius = {"]
    L += ["        " + ", ".join(f"{i['radius']:.0f}f" for i in islands[k:k + 10]) + "," for k in range(0, len(islands), 10)]
    L += ["    };", "    public static readonly float[] Rim = {"]
    L += ["        " + ", ".join(f"{v:.0f}f" for v in i["rim"]) + "," for i in islands]
    L += ["    };", "}", ""]
    open(path, "w", encoding="utf-8", newline="\n").write("\n".join(L))


def preview(paint, islands, path):
    """The six faces in a cross, the paint in its types' colours, each island's detected outline drawn on top."""
    from PIL import Image, ImageDraw
    n = paint.shape[1]; COL = {0: (40, 36, 60), 1: (180, 80, 50), 2: (40, 110, 50), 3: (230, 230, 240), 4: (190, 170, 90), 5: (60, 50, 50), 6: (220, 200, 140)}
    lut = np.array([COL.get(v, (255, 0, 255)) for v in range(256)], np.uint8)
    img = Image.new("RGB", (4 * n, 3 * n), (0, 0, 0))
    place = {0: (2, 1), 1: (0, 1), 2: (1, 0), 3: (1, 2), 4: (1, 1), 5: (3, 1)}
    for f, (cx, cy) in place.items(): img.paste(Image.fromarray(lut[paint[f]]), (cx * n, cy * n))
    dr = ImageDraw.Draw(img)
    for i in islands:
        e1, e2 = frame(i["dir"])
        th = -np.pi + (np.arange(BEARINGS) + 0.5) * 2 * np.pi / BEARINGS
        pts = []
        for a, r in zip(th, i["rim"]):
            p = i["dir"] * i["top"] + (e1 * np.cos(a) + e2 * np.sin(a)) * r; p /= np.linalg.norm(p)
            f, u, v = cube.project(p[None])
            f, u, v = int(f[0]), float(u[0]), float(v[0])
            cx, cy = place[f]; pts.append((cx * n + (u + 1) / 2 * n, cy * n + (v + 1) / 2 * n))
        for a, b in zip(pts, pts[1:] + pts[:1]):
            if abs(a[0] - b[0]) < n / 2 and abs(a[1] - b[1]) < n / 2: dr.line([a, b], fill=(255, 255, 0))
    img.save(path)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("islands"); ap.add_argument("elevation", nargs="?")
    ap.add_argument("--out", default=os.path.join(MOD, "Scripts", "Simulation", "Procedural", "BritannicaPainted.g.cs"))
    ap.add_argument("--preview")
    a = ap.parse_args()
    paint = np.load(a.islands); elev = np.load(a.elevation) if a.elevation else None
    isl = detect(paint, elev)
    write_cs(isl, a.out)
    if a.preview: preview(paint, isl, a.preview)
    kinds = {}
    for i in isl: kinds[i["type"]] = kinds.get(i["type"], 0) + 1
    print(f"{len(isl)} islands: {kinds}; radius {min(i['radius'] for i in isl):.0f}..{max(i['radius'] for i in isl):.0f} m -> {a.out}")
