"""compose_globe.py [--preview <png>] - Britannica's painted globe, authored from REAL terrain (2026-10-26; the user's
norms: data first - Copernicus GLO-30 for natural features, no noise looks). Writes the BritannicaPaint planet
(planeteditor's PlanetTools planet, %LOCALAPPDATA%\\OrbitalPlanets\\BritannicaPaint): height, biome, fracture.

Britannica is small (R 60 km: ~45,000 km2, about four 1-degree tiles), so it holds six real regions at TRUE 1:1 scale,
40-70 km across, with gulfs of open sky between them (height below sea level 0 = sky):
  mesa desert   Monument Valley / Navajo country (N36-37 W111)   biome 1, medium fracture
  tepui plateau Roraima (N05 W061)                                biome 4, low fracture (big table-top islands)
  alpine giant  Bernese Alps (N46 E007)                           biome 3, low fracture (huge chunks)
  alpine        Mont Blanc (N45 E006)                             biome 3, medium
  forest        Olympic Mountains (N47 W124)                      biome 2, medium
  burnt         Iceland's lava highlands (N65 W017)               biome 5, high fracture (rubble under the clouds)
Each region: a crop of its tile laid on the sphere about a centre direction (rotated), heights relative to the crop's
low ground (+300 m: everything inside the outline is land), an irregular outline from the terrain itself (the
crop's lower ground falls away first) - nothing outside it."""
import argparse, os, sys
import numpy as np
import tifffile
from scipy.ndimage import map_coordinates, gaussian_filter
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\PlanetTools")
from planetkit import cube
from planetkit.planet import Planet

DEM = r"D:\downloadsarchive\copdem"
R = 60000.0

# name, tiles {(lat, lon): file}, crop centre (lat, lon), half-size km, centre dir (lat, lon on Britannica), rotation
# deg, biome, fracture 0..255
REGIONS = [
    ("mesa",   [(36, -111), (37, -111)], (36.95, -110.25), 32, (20, -30),  15, 1, 120),
    ("tepui",  [(5, -61)],               (5.20, -60.75),   24, (-25, 40),  -20, 4, 50),
    ("alpsB",  [(46, 7)],                (46.50, 7.52),    34, (35, 95),   30, 3, 40),
    ("alpsM",  [(45, 6)],                (45.80, 6.70),    22, (-10, 150), 0, 3, 90),
    ("forest", [(47, -124)],             (47.75, -123.60), 28, (-40, -95), 45, 2, 110),
    ("burnt",  [(65, -17)],              (65.05, -16.60),  26, (-75, 10),  10, 5, 220),
]


def tile_name(lat, lon):
    return f"Copernicus_DSM_COG_10_{'N' if lat >= 0 else 'S'}{abs(lat):02d}_00_{'E' if lon >= 0 else 'W'}{abs(lon):03d}_00_DEM.tif"


def mosaic(tiles):
    """The tiles as one array with its georeference: (array, lat_top, lon_left, dlat, dlon) per pixel."""
    lats = sorted({t[0] for t in tiles}, reverse=True); lons = sorted({t[1] for t in tiles})
    parts = {t: tifffile.imread(os.path.join(DEM, tile_name(*t))).astype(np.float32) for t in tiles}
    h, w = next(iter(parts.values())).shape
    A = np.zeros((h * len(lats), w * len(lons)), np.float32)
    for (la, lo), a in parts.items():
        i, j = lats.index(la), lons.index(lo); A[i * h:(i + 1) * h, j * w:(j + 1) * w] = a
    return A, lats[0] + 1, lons[0], 1.0 / h, 1.0 / w


def basis(lat, lon):
    la, lo = np.radians(lat), np.radians(lon)
    up = np.array([np.cos(la) * np.cos(lo), np.sin(la), np.cos(la) * np.sin(lo)])
    east = np.cross([0, 1, 0], up); east /= np.linalg.norm(east); north = np.cross(up, east)
    return up, east, north


def compose(n):
    dirs = np.stack([cube.texel_dirs(f, n) for f in range(6)]).reshape(-1, 3)
    height = np.full(len(dirs), -500.0, np.float32); biome = np.zeros(len(dirs), np.uint8); frac = np.zeros(len(dirs), np.uint8)
    for name, tiles, (clat, clon), half_km, (plat, plon), rot, b, fr in REGIONS:
        A, lat0, lon0, dlat, dlon = mosaic(tiles)
        A = gaussian_filter(A, 1.0)
        up, east, north = basis(plat, plon)
        cosu = dirs @ up; near = cosu > np.cos((half_km * 1.5e3) / R)
        d = dirs[near]
        # the texel's place on the region's plane (km), rotated
        x = R * (d @ east) / (d @ up) / 1000; y = R * (d @ north) / (d @ up) / 1000
        c, s = np.cos(np.radians(rot)), np.sin(np.radians(rot)); xr, yr = c * x - s * y, s * x + c * y
        # -> the crop (lat, lon) at true scale -> DEM pixels
        lat = clat + yr / 111.32; lon = clon + xr / (111.32 * np.cos(np.radians(clat)))
        row = (lat0 - lat) / dlat; col = (lon - lon0) / dlon
        hgt = map_coordinates(A, [row, col], order=1, mode="nearest")
        inside = (row >= 0) & (row < A.shape[0]) & (col >= 0) & (col < A.shape[1])
        # the outline: round (no crop square), and the TERRAIN decides the coast - the region's ground smoothed over
        # ~4 km, as an anomaly: higher ground juts out, valleys and basins cut in (the alpine valleys became fjords of sky)
        rr = np.hypot(xr, yr) / half_km
        base = np.percentile(hgt[inside & (rr < 1)], 25)
        rel = hgt - base
        As = gaussian_filter(A, 4000 / 30 / 3)
        big = map_coordinates(As, [row, col], order=1, mode="nearest") - base
        anom = (big - np.median(big[inside & (rr < 1)])) / max(1.0, np.std(big[inside & (rr < 1)]))
        reach = 0.82 + 0.16 * np.clip(anom, -1.5, 1.5)                  # the coast's radius here (of half_km)
        keep = inside & (rr < reach) & (rel + 600 * np.clip((reach - rr) / 0.12, 0, 1) - 450 > 0)
        h = np.where(keep, np.maximum(rel, 0) + 300, -500)
        idx = np.nonzero(near)[0]
        upd = keep & (h > height[idx])
        height[idx[upd]] = h[upd]; biome[idx[upd]] = b; frac[idx[upd]] = fr
        print(f"  {name}: {upd.sum()} texels, relief {np.ptp(h[keep]):.0f} m")
    return height.reshape(6, n, n), biome.reshape(6, n, n), frac.reshape(6, n, n)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--preview"); ap.add_argument("--n", type=int, default=0)
    a = ap.parse_args()
    p = Planet.load("BritannicaPaint")
    n = a.n or p.height.shape[1]
    h, b, f = compose(n)
    p.height[...] = h; p.layers["biome"][...] = b; p.layers["fracture"][...] = f
    p.save()
    land = (h > 0).mean()
    print(f"BritannicaPaint saved: land {land:.0%} of the sphere")
