"""lab_islands.py - Britannica's island masters authored with planeteditor's Terrain Mode brushes (PlanetTools add-on
se2_planet_tools/mountain_lab.py), headless in the SE2 portable Blender (2026-10-26, the user: "Please use all the
blender brushes").

  blender.exe --no-window-focus --window-geometry 0 0 640 400 --python lab_islands.py -- <recipes.json> <out dir> [names...]

(not -b: the erosion and amplification are GPU compute shaders, and Blender's background mode has no GPU; the work
runs from a timer once the window is up, then Blender quits.)

Per master (a recipe in the JSON: name, size_km, vertical, crag, strokes): a fresh lab patch, all sea (base -300 m);
each stroke = {brush, radius_km, strength, level_m?, points: [[x, y] 0..1 across the patch], erase?} painted with
mountain_lab.paint_stroke - the same dabs as a mouse drag; the stream-power erosion (GPU) ticked until it settles (the
mode's own criterion, `_tick`), then Bake (the amplification + rock strata, ~20 m a texel). Writes <out>/<name>.npz:
height (baked, game m), sea (the coast mask at 256), size_km, and a log line with timings."""
import bpy, json, os, sys, time
import numpy as np

def main():
    try:
        argv = sys.argv[sys.argv.index("--") + 1:]
        recipes = json.load(open(argv[0], encoding="utf-8")); out = argv[1]; only = set(argv[2:])
        os.makedirs(out, exist_ok=True)
        bpy.ops.preferences.addon_enable(module="se2_planet_tools")
        import se2_planet_tools.mountain_lab as ml
        ctx = bpy.context
        s = ctx.scene.se2planet_lab
        N = ml.N

        for rec in recipes:
            if only and rec["name"] not in only: continue
            t0 = time.perf_counter()
            s.size_km = rec.get("size_km", 10.0); s.vertical = rec.get("vertical", 0.5); s.crag = rec.get("crag", 1.0)
            s.steps_per_tick = 400
            # (the coast: a real-terrain contour's land mask when the recipe has one - the Coastline brush's own map,
            #  sea where it is not land; the land at sea level, the sea's floor below - else all sea, for Island strokes)
            land = np.load(rec["mask"]) if rec.get("mask") else np.zeros((N, N), bool)
            sea = {"sea": (~land).astype(np.float32)}
            ml._new_patch(ctx, np.where(land, 0.0, -300.0).astype(np.float32), maps=sea)
            for st in rec["strokes"]:
                s.radius_km = st.get("radius_km", 1.0); s.strength = st.get("strength", 0.5); s.level_m = st.get("level_m", 400.0)
                pts = [((1 - y) * (N - 1), x * (N - 1)) for x, y in st["points"]]          # (x right, y up -> row, col)
                ml.paint_stroke(ctx, st["brush"], pts, erase=st.get("erase", False))
            # (settle: the mode's own tick, as its timer would call it, until it reports settled)
            ticks = 0
            ml._state["dirty"] = True; ml._start_running()
            while ml._state["running"] and ticks < rec.get("max_ticks", 400):
                ml._tick(); ticks += 1
            t1 = time.perf_counter()
            bpy.ops.se2lab.bake()
            h = ml._state["baked_full"]
            np.savez_compressed(os.path.join(out, rec["name"] + ".npz"), height=h.astype(np.float32),
                                sea=(ml._state["maps"]["sea"] > 0.5), size_km=s.size_km)
            print(f"MASTER {rec['name']}: {len(rec['strokes'])} strokes, {ticks} ticks ({ml._state['status']}), "
                  f"settle {t1 - t0:.1f} s, bake {time.perf_counter() - t1:.1f} s, {h.shape[0]}^2, {h.min():.0f}..{h.max():.0f} m", flush=True)

    except Exception:
        import traceback; traceback.print_exc()
    finally:
        sys.stdout.flush(); bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(main, first_interval=1.0)
