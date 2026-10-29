"""masters_sheet.py - the island masters (lab_islands.py's .npz) on one review sheet: each hillshaded, lit from the
north-west, tinted by its type and height, the sky (sea map) dark; labelled with name, size and relief."""
import glob, json, os
import numpy as np
from PIL import Image, ImageDraw

LAB = os.path.join(os.environ["LOCALAPPDATA"], "OrbitalPlanets", "Britannica", "lab")
TINT = {"alpine": (0.92, 0.92, 0.95), "mesa": (0.78, 0.45, 0.3), "plateau": (0.78, 0.7, 0.45), "forest": (0.42, 0.6, 0.38),
        "burnt": (0.42, 0.36, 0.34), "desert": (0.88, 0.78, 0.55)}
import sys as _s
SUB = _s.argv[1] if len(_s.argv) > 1 else "out"
KIND = {}
if SUB == "out_heroes":
    KIND = {r["name"]: r["type"].lower() for r in json.load(open(os.path.join(LAB, "heroes.json")))}
files = sorted(glob.glob(os.path.join(LAB, SUB, "*.npz")))
files = [f for f in files if not os.path.basename(f).startswith("smoke")]
T = 360; cols = 8
sheet = Image.new("RGB", (T * cols, T * ((len(files) + cols - 1) // cols)), (14, 10, 24))
for k, f in enumerate(files):
    d = np.load(f); h = d["height"]; n = h.shape[0]; size = float(d["size_km"]) * 1000
    st = max(1, n // 720); g = h[::st, ::st]; step = size / n * st
    gy, gx = np.gradient(g, step)
    sh = np.clip(0.55 + (-gx * 0.7 + gy * 0.7) / np.sqrt(1 + gx ** 2 + gy ** 2), 0, 1)
    land = g > 0.5
    name = os.path.basename(f)[:-4]; tint = np.array(TINT.get(KIND.get(name, name.split("_")[0]), (0.8, 0.8, 0.8)))
    top = max(1.0, float(g[land].max()) if land.any() else 1.0)
    rgb = sh[..., None] * tint * (0.65 + 0.35 * g / top)[..., None]
    rgb[~land] = (0.06, 0.05, 0.1)
    im = Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).resize((T, T))
    ImageDraw.Draw(im).text((6, 4), f"{name} {KIND.get(name, '')}  {size / 1000:.1f} km  {top:.0f} m", fill=(255, 255, 255))
    sheet.paste(im, ((k % cols) * T, (k // cols) * T))
out = os.path.join(LAB, "..", "preview", "masters.png" if SUB == "out" else SUB + ".png"); sheet.save(out); print(os.path.normpath(out), len(files))
