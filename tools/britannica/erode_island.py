"""erode_island.py - each Britannica island grown its own geology with planeteditor's terrain tools (2026-10-26, the user:
"could we get some actual variation using planeteditor tools?"): Schott et al.'s stream-power erosion (PlanetTools
planetkit/spe.py) run on the ISLAND - its coast the base level, so every island grows its own drainage: valleys and
ravines cutting out to its cliff edges, ridges branching between them. The uplift is the island's real-terrain top
(blurred - the user's norm: erosion driven by real data) shaped by a RECIPE, so islands cut from one place differ:
  spine     a long ridge along the island's long axis          fork      a spine that splits in two
  twin      two summits with a saddle                          offset    one summit off-centre, a long apron
  escarp    a plateau ending in an escarpment on one side      caldera   a ring of peaks round a sunken centre (burnt)
  real      the real top as cut (no erosion)
The eroded relief is scaled to the type's range (and to the island's size: small ones stay modest)."""
import sys
import numpy as np
from scipy import ndimage
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\PlanetTools")
from planetkit.spe import SPE

RECIPES = {   # type: [(recipe, weight)]
    "Alpine": [("spine", 3), ("fork", 3), ("twin", 2), ("offset", 2), ("real", 1)],
    "Forest": [("offset", 2), ("spine", 2), ("twin", 2), ("fork", 1), ("real", 2)],
    "Mesa": [("real", 4), ("escarp", 3), ("offset", 1)],
    "Plateau": [("escarp", 4), ("real", 2), ("offset", 1)],
    "Burnt": [("caldera", 3), ("real", 2), ("offset", 1)],
    "Desert": [("real", 2), ("escarp", 1)],
}
RELIEF = {"Alpine": (700, 1900), "Forest": (220, 700), "Mesa": (200, 550), "Plateau": (250, 700), "Burnt": (150, 500), "Desert": (80, 250)}


def recipe_shape(name, mask, step, rng):
    """The recipe's uplift shape over the island (0..1)."""
    n = mask.shape[0]
    ys, xs = np.nonzero(mask)
    cy, cx = ys.mean(), xs.mean()
    # the island's long axis (its land's principal direction)
    cov = np.cov(np.stack([xs - cx, ys - cy])) if len(xs) > 2 else np.eye(2)
    w, v = np.linalg.eigh(cov); ax = v[:, 1]; ln = np.sqrt(max(w[1], 1)) * 2.2
    Y, X = np.mgrid[0:n, 0:n].astype(float)
    u = (X - cx) * ax[0] + (Y - cy) * ax[1]; t = -(X - cx) * ax[1] + (Y - cy) * ax[0]
    inner = ndimage.distance_transform_edt(mask)
    core = np.clip(inner / max(inner.max(), 1), 0, 1)
    if name == "spine":
        s = np.exp(-(t / (ln * 0.28)) ** 2) * np.clip(1 - np.abs(u) / (ln * 1.1), 0, 1) ** 0.5
    elif name == "fork":
        side = rng.choice([-1, 1]); bend = np.clip(side * u / ln, 0, 1)
        s = np.maximum(np.exp(-((t - bend * ln * 0.45) / (ln * 0.22)) ** 2), np.exp(-((t + bend * ln * 0.45) / (ln * 0.22)) ** 2))
        s *= np.clip(1 - np.abs(u) / (ln * 1.1), 0, 1) ** 0.5
    elif name == "twin":
        a = ln * rng.uniform(0.35, 0.55)
        s = np.maximum(np.exp(-((u - a) ** 2 + t ** 2) / (ln * 0.35) ** 2), 0.85 * np.exp(-((u + a) ** 2 + t ** 2) / (ln * 0.32) ** 2))
    elif name == "offset":
        a = rng.uniform(0, 2 * np.pi); d = ln * rng.uniform(0.3, 0.6)
        ox, oy = cx + np.cos(a) * d * 0.6, cy + np.sin(a) * d * 0.6
        s = np.exp(-((X - ox) ** 2 + (Y - oy) ** 2) / (ln * 0.55) ** 2)
    elif name == "escarp":
        a = rng.uniform(0, 2 * np.pi); proj = (X - cx) * np.cos(a) + (Y - cy) * np.sin(a)
        s = 0.35 + 0.65 * np.clip(0.5 + proj / (ln * 0.25), 0, 1) ** 2      # (a table rising to a high edge)
    elif name == "caldera":
        r = np.hypot(X - cx, Y - cy); r0 = ln * rng.uniform(0.35, 0.5)
        s = np.exp(-((r - r0) / (ln * 0.2)) ** 2) + 0.15
    else:
        s = np.ones((n, n))
    return np.clip(s, 0, None) * (0.25 + 0.75 * core)


PAPER_N, PAPER_EXTENT = 256, 150000.0   # (planeteditor: SPE validated at the paper's scale - 256 x 256 over 150 km)


def erode(h, mask, step, kind, radius, rng, max_steps=6000):
    """The island's top [n, n] (m) grown its own geology - and the recipe used. planeteditor's validated flow: the
    stream-power erosion at the PAPER's scale (its constants are tuned to it: run at game scale it diverged), to steady
    state (the top 5 %'s drift < 0.5 m over 20 steps), then mapped UNIFORMLY onto the island - one horizontal, one
    vertical factor (the user rejected non-uniform squash) - to the type's relief."""
    names, wts = zip(*RECIPES.get(kind, [("real", 1)])); name = rng.choice(names, p=np.array(wts) / sum(wts))
    if name == "real" or mask.sum() < 30: return h, name
    n = mask.shape[0]
    base = ndimage.gaussian_filter(np.where(mask, h, 0.0), 2.0)
    base = (base - base[mask].min()) / max(np.ptp(base[mask]), 1.0)
    U = np.where(mask, (0.3 + 0.7 * base) * recipe_shape(name, mask, step, rng), 0.0)
    # (onto the paper's grid: the island's square, every map resampled; the coast mask nearest)
    z = PAPER_N / n
    Up = np.clip(ndimage.zoom(U, z, order=1), 0, None)[:PAPER_N, :PAPER_N]
    Mp = ndimage.zoom(mask.astype(np.uint8), z, order=0)[:PAPER_N, :PAPER_N].astype(bool)
    m = Up.shape[0]
    noise = 0.75 + 0.5 * ndimage.gaussian_filter(rng.random((m, m)), 1.5).ravel() * 2 - 0.5   # (erodibility: rock that varies)
    sim = SPE(m, m, PAPER_EXTENT, PAPER_EXTENT, height=None, uplift=np.where(Mp, Up, 0.0), noise=np.clip(noise, 0.5, 1.5))
    off = ~Mp.ravel(); prev = None; done = 0
    while done < max_steps:
        sim.run(20); done += 20; sim.h[off] = 0.0                         # (the coast: the base level)
        hh = sim.h[Mp.ravel()]
        top = np.sort(hh)[-max(1, len(hh) // 20):].mean()
        if prev is not None and done > 400 and abs(top - prev) < 0.5: break
        prev = top
    e = ndimage.zoom(sim.height(), n / m, order=1)[:n, :n]
    if e.shape != (n, n): e = np.pad(e, ((0, n - e.shape[0]), (0, n - e.shape[1])), mode="edge")
    lo_r, hi_r = RELIEF.get(kind, (200, 600))
    target = np.interp(radius, [150, 4500], [lo_r * 0.25, hi_r]) * rng.uniform(0.75, 1.15)
    e = e / max(np.percentile(e[mask], 99), 1e-6) * target            # (one vertical factor)
    e = e + 0.15 * (h - ndimage.gaussian_filter(h, 3.0))               # (a little of the real top's texture)
    return np.where(mask, np.maximum(e, 0), 0.0).astype(np.float32), name
