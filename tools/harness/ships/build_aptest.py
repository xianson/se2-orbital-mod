"""Builds and checks APTEST.txt (the autopilot test ship) with the aero mod's gridgen block model (se2blocks: the measured
catalog boxes and mount faces). Writes the spec body to stdout, the checks to stderr; exit 1 on an overlap, a block that
joins nothing, a main grid that is not one piece through mount faces, or an exhaust lane that hits a block. Blocks with no
measured mounts are assumed to mount on every face (listed).

  python tools/harness/ships/build_aptest.py > body.txt      (AERO_ROOT: the Aerodynamics Mod checkout; default the sibling)
"""
import os, sys, itertools
import numpy as np
AERO = os.environ.get("AERO_ROOT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", "Aerodynamics Mod"))
sys.path.insert(0, os.path.join(AERO, "tools", "gridgen"))
import se2blocks as sb

cat = sb.Catalog(snapshot=True)
# blocks the gridgen catalog lacks: their boxes (tools/gridgen/data/gameblocks.json bounds, low corner -1 as measured)
EXTRA = {
    "Gyroscope250": ("4465b025-4193-407f-9a2e-b04f0e51271c", [-4, -4, -4], [5, 5, 5], 1902.4),
    "InteriorTurret150": ("5ac58356-45bf-4d3a-b082-5300d9c354fa", [-2, -2, -2], [3, 3, 3], 348.0),
    # the aero Autopilot (Large) looks like the vanilla Event Controller (1 m): its box
    "Autopilot_Large": ("ae200001-ae00-a901-0011-000000000001", [-1, 0, 0], [2, 1, 1], 101.3),
}
for n, (g, lo, hi, m) in EXTRA.items():
    cat.by_name[n] = sb.BlockDef(n, g, m, np.array(lo) - 1, np.array(hi), False)
ASSUMED = set(EXTRA)

blocks = []   # (name, pos, orient, extra)


def add(name, pos, orient=("Forward", "Up"), extra=""):
    blocks.append((name, tuple(int(v) for v in pos), orient, extra))
    return len(blocks) - 1


def thr(push, up, lo):
    """An Atmo250 pushing the ship along `push` (its Forward), its box's low corner at lo."""
    o = (push, up)
    return add("AtmosphericThruster250", cat.pos_for("AtmosphericThruster250", lo, o), o)


ARM, BAT = "ArmorCubeLight250", "Battery250"
C = add(ARM, (0, 0, 0))
add(BAT, (-10, 0, 0), extra="charge=1")
R = add(ARM, (10, 0, 0))
F = add(ARM, (0, 0, -10))
B = add(ARM, (0, 0, 10))
D = add(ARM, (0, -10, 0))
T = add(ARM, (0, 10, -10))
add("ControlSeat250", cat.pos_for("ControlSeat250", (-5, 5, -5)))           # on C's top
HINGE = add("Hinge250", (10, 10, 0))                                           # on R's top
add("Gyroscope250", (0, 10, 10))                                               # on B's top
AP = add("Autopilot_Large", cat.pos_for("Autopilot_Large", (-19, 0, 0)))      # on the battery's -X face (as H25F's)
# up (lift): beside D, +-X, low; exhaust down
thr("Up", "Backward", (5, -18, -3)); thr("Up", "Backward", (-11, -18, -3))
# down: beside T, +-X; exhaust up
thr("Down", "Forward", (5, 6, -14)); thr("Down", "Forward", (-11, 6, -14))
# forward (push -Z): beside B, +-X; exhaust backward
thr("Forward", "Up", (5, -3, 8)); thr("Forward", "Up", (-11, -3, 8))
# backward (push +Z): beside F, +-X; exhaust forward
thr("Backward", "Up", (5, -3, -16)); thr("Backward", "Up", (-11, -3, -16))
# right (push +X): in front of D; exhaust -X.  left (push -X): behind D; exhaust +X
thr("Right", "Up", (-4, -13, -11)); thr("Left", "Up", (-4, -13, 5))

# the hinge head's grid (frame: the head at 0 0 0 - TURR's convention): an armour cube above it, the turret on that
FLAP = [("ArmorCubeLight250", (0, 10, 0), ("Forward", "Up"))]
FLAP.append(("InteriorTurret150", tuple(int(v) for v in cat.pos_for("InteriorTurret150", (-3, 15, -3))), ("Forward", "Up")))

# ── checks ──
boxes = [cat.box(n, p, o) for n, p, o, _ in blocks]
bad = []
for i, j in itertools.combinations(range(len(blocks)), 2):
    lo = np.maximum(boxes[i][0], boxes[j][0]); hi = np.minimum(boxes[i][1], boxes[j][1])
    if np.all(hi > lo): bad.append(f"overlap {i}:{blocks[i][0]} / {j}:{blocks[j][0]}")
edges = set()
for i, j in itertools.combinations(range(len(blocks)), 2):
    (alo, ahi), (blo, bhi) = boxes[i], boxes[j]
    for ax in range(3):
        for sgn in (1, -1):
            touch = ahi[ax] == blo[ax] if sgn == 1 else alo[ax] == bhi[ax]
            if not touch: continue
            o = [k for k in range(3) if k != ax]
            if not all(min(ahi[k], bhi[k]) > max(alo[k], blo[k]) for k in o): continue
            d = [0, 0, 0]; d[ax] = sgn; d = tuple(d)
            na, nb = blocks[i][0], blocks[j][0]
            ok = (na in ASSUMED or nb in ASSUMED) and (na in ASSUMED or d in sb.mount_dirs(na, blocks[i][2])) \
                 and (nb in ASSUMED or tuple(-x for x in d) in sb.mount_dirs(nb, blocks[j][2])) \
                 or sb.joins(na, blocks[i][2], nb, blocks[j][2], d)
            if ok: edges.add((i, j))
# connectivity
par = list(range(len(blocks)))
def f(x):
    while par[x] != x: par[x] = par[par[x]]; x = par[x]
    return x
for i, j in edges: par[f(i)] = f(j)
groups = {}
for i in range(len(blocks)): groups.setdefault(f(i), []).append(i)
if len(groups) > 1:
    main = max(groups.values(), key=len)
    for g in groups.values():
        if g is not main: bad.append("not joined: " + ", ".join(f"{i}:{blocks[i][0]}" for i in g))
# exhaust lanes: 30 cells behind each thruster free of blocks
for i, (n, p, o, _) in enumerate(blocks):
    if "Thruster" not in n: continue
    ex = sb.thruster_exhaust(n, o)
    lo, hi = boxes[i]
    ax = int(np.nonzero(ex)[0][0]); s = int(ex[ax])
    llo, lhi = lo.copy(), hi.copy()
    if s > 0: llo[ax], lhi[ax] = hi[ax], hi[ax] + 30
    else: llo[ax], lhi[ax] = lo[ax] - 30, lo[ax]
    for j in range(len(blocks)):
        if j == i: continue
        a = np.maximum(llo, boxes[j][0]); b = np.minimum(lhi, boxes[j][1])
        if np.all(b > a): bad.append(f"exhaust of {i}:{n} hits {j}:{blocks[j][0]}")
mass = sum(cat[n].mass for n, _, _, _ in blocks) + sum(cat[n].mass for n, _, _ in FLAP) + 541.7
com = sum(np.array(cat.centre(n, p, o)) * cat[n].mass for n, p, o, _ in blocks) / sum(cat[n].mass for n, _, _, _ in blocks)
print(f"# blocks {len(blocks)} + head grid {1 + len(FLAP)}; mass {mass/1000:.1f} t; CoM (cells) {np.round(com, 1)}", file=sys.stderr)
for b in bad: print("BAD", b, file=sys.stderr)
print("assumed mounts (unmeasured): " + ", ".join(sorted(ASSUMED)), file=sys.stderr)
out = []
for n, p, o, extra in blocks:
    out.append(f"B {cat[n].guid} {p[0]} {p[1]} {p[2]} {o[0]} {o[1]}{(' ' + extra) if extra else ''}   # {n}")
out.append(f"H {HINGE} -10 10")
for n, p, o in FLAP:
    out.append(f"F 0 {cat[n].guid} {p[0]} {p[1]} {p[2]} {o[0]} {o[1]}   # {n} (on the hinge head's grid)")
print("\n".join(out))
sys.exit(1 if bad else 0)
