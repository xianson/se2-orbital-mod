"""Builds and checks APTEST.txt (the autopilot test ship). Writes the spec body to stdout, the checks to stderr; exit 1 on an
overlap, a block that joins nothing (mount faces: the aero gridgen's measured table), a main grid that is not one piece, or an
exhaust lane that hits a block.

THE CELLS A BLOCK TAKES (measured in game, 2026-10-20, `gen dump` of a spawned APTEST): a block at spec position P with
orientation M occupies the cells P + M @ c for every integer cell c in its catalog box [lo..hi] INCLUSIVE (the catalog's raw
numbers, tools/gridgen/data/catalog.txt / gameblocks.json "bounds"; a 2.5 m armour cube at 0: cells -4..5). The aero gridgen's
se2blocks model shifts that box by one cell (its aabb_low_offset): right for blocks of one size side by side, one cell off
against a block of another size - the first APTEST overlapped by a cell in two places, the game dropped a thruster and a
later spawn crashed the server (a corrupted grid). So the boxes here are this rule's, not se2blocks'.

  python tools/harness/ships/build_aptest.py > body.txt      (AERO_ROOT: the Aerodynamics Mod checkout; default the sibling)
"""
import os, sys, itertools
import numpy as np
AERO = os.environ.get("AERO_ROOT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", "Aerodynamics Mod"))
sys.path.insert(0, os.path.join(AERO, "tools", "gridgen"))
import se2blocks as sb

cat = sb.Catalog(snapshot=True)
# raw inclusive boxes (cells) and GUIDs; the catalog's own where it has them
RAW = {}
for n in ("ArmorCubeLight250", "Battery250", "ControlSeat250", "Hinge250", "AtmosphericThruster250"):
    b = cat[n]; RAW[n] = (b.guid, b.lo + 1, b.hi, b.mass)
EXTRA = {   # (tools/gridgen/data/gameblocks.json bounds; the Autopilot (Large) is the Event Controller's box - measured)
    "Gyroscope250": ("4465b025-4193-407f-9a2e-b04f0e51271c", [-4, -4, -4], [5, 5, 5], 1902.4),
    "InteriorTurret150": ("5ac58356-45bf-4d3a-b082-5300d9c354fa", [-2, -2, -2], [3, 3, 3], 348.0),
    "Autopilot_Large": ("ae200001-ae00-a901-0011-000000000001", [-1, 0, 0], [2, 1, 1], 101.3),
    "HingeHead250": ("3dce6578-f7b4-4404-80b4-861fcb73602c", [-4, -4, -3], [5, 5, 4], 541.7),
}
for n, (g, lo, hi, m) in EXTRA.items():
    RAW[n] = (g, np.array(lo), np.array(hi), m)
    cat.by_name.setdefault(n, sb.BlockDef(n, g, m, np.array(lo) - 1, np.array(hi), False))
ASSUMED = {"Gyroscope250", "InteriorTurret150", "Autopilot_Large"}   # no measured mount faces: every face assumed


def box(n, pos, o=("Forward", "Up")):
    """Half-open [lo, hi) of the cells block n takes at pos with orientation o (the measured rule above)."""
    _, lo, hi, _ = RAW[n]
    M = sb.orient_matrix(*o)
    c = np.array([M @ np.array(p) for p in itertools.product(*zip(lo, hi))])
    return np.asarray(pos) + c.min(0), np.asarray(pos) + c.max(0) + 1


def pos_for(n, lo_want, o=("Forward", "Up")):
    """The position putting block n's lowest cell at lo_want."""
    lo, _ = box(n, (0, 0, 0), o)
    return tuple(int(v) for v in np.asarray(lo_want) - lo)


blocks = []   # (name, pos, orient, extra)


def add(name, pos, orient=("Forward", "Up"), extra=""):
    blocks.append((name, tuple(int(v) for v in pos), orient, extra))
    return len(blocks) - 1


def thr(push, up, lo):
    """An Atmo250 pushing the ship along `push` (its Forward), its lowest cell at lo."""
    o = (push, up)
    return add("AtmosphericThruster250", pos_for("AtmosphericThruster250", lo, o), o)


ARM, BAT = "ArmorCubeLight250", "Battery250"
# the core: armour cubes at 10-cell steps (cells -4..5 each), the battery on -X
C = add(ARM, (0, 0, 0))                      # cells x -4..5
add(BAT, (-10, 0, 0), extra="charge=1")      # x -14..-5
add(ARM, (10, 0, 0))                         # R  x 6..15
add(ARM, (0, 0, -10))                        # F  z -14..-5
add(ARM, (0, 0, 10))                         # B  z 6..15
add(ARM, (0, -10, 0))                        # D  y -14..-5
add(ARM, (0, 10, -10))                       # T  y 6..15, z -14..-5
add("ControlSeat250", pos_for("ControlSeat250", (-4, 6, -4)))                 # on C's top
HINGE = add("Hinge250", (10, 10, 0))                                            # on R's top
add("Gyroscope250", (0, 10, 10))                                                # on B's top
AP = add("Autopilot_Large", pos_for("Autopilot_Large", (-18, 1, 1)))           # on the battery's -X face
# every thruster touches the core with a SIDE (an atmospheric thruster mounts on its four sides only, not its ends)
# lift (push Up): on D's +-X sides, exhaust down
thr("Up", "Backward", (6, -14, -3)); thr("Up", "Backward", (-10, -14, -3))
# push Down: on T's +-X sides, exhaust up
thr("Down", "Forward", (6, 8, -12)); thr("Down", "Forward", (-10, 8, -12))
# push Forward (-Z): on B's +-X sides, exhaust aft
thr("Forward", "Up", (6, -3, 8)); thr("Forward", "Up", (-10, -3, 8))
# push Backward (+Z): on F's +-X sides, exhaust forward
thr("Backward", "Up", (6, -3, -14)); thr("Backward", "Up", (-10, -3, -14))
# push Right (+X): on D's front face; push Left (-X): on D's back face (exhausts sideways)
thr("Right", "Up", (-4, -12, -10)); thr("Left", "Up", (-4, -12, 6))

# the hinge head's grid (frame: the head at 0 0 0, cells -4..5 / -4..5 / -3..4, its mount face +Y - measured in game, `gen dump`
# of a spawned head grid): an interior turret on the head (the Enemy switch's subgrid block)
HEAD = ("HingeHead250", (0, 0, 0), ("Forward", "Up"))
FLAP = [("InteriorTurret150", pos_for("InteriorTurret150", (-2, 6, -2)), ("Forward", "Up"))]


def check(items, label):
    bad = []
    bx = [box(n, p, o) for n, p, o, *_ in items]
    for i, j in itertools.combinations(range(len(items)), 2):
        lo = np.maximum(bx[i][0], bx[j][0]); hi = np.minimum(bx[i][1], bx[j][1])
        if np.all(hi > lo): bad.append(f"{label}: overlap {i}:{items[i][0]} / {j}:{items[j][0]}")
    edges = set()
    for i, j in itertools.combinations(range(len(items)), 2):
        (alo, ahi), (blo, bhi) = bx[i], bx[j]
        for ax in range(3):
            for sgn in (1, -1):
                if not (ahi[ax] == blo[ax] if sgn == 1 else alo[ax] == bhi[ax]): continue
                o = [k for k in range(3) if k != ax]
                if not all(min(ahi[k], bhi[k]) > max(alo[k], blo[k]) for k in o): continue
                d = [0, 0, 0]; d[ax] = sgn; d = tuple(d); nd = tuple(-x for x in d)
                na, nb = items[i][0], items[j][0]
                if na in ASSUMED or nb in ASSUMED:
                    ok = (na in ASSUMED or d in sb.mount_dirs(na, items[i][2])) and (nb in ASSUMED or nd in sb.mount_dirs(nb, items[j][2]))
                else:
                    ok = sb.joins(na, items[i][2], nb, items[j][2], d)
                if ok: edges.add((i, j))
    par = list(range(len(items)))
    def f(x):
        while par[x] != x: par[x] = par[par[x]]; x = par[x]
        return x
    for i, j in edges: par[f(i)] = f(j)
    groups = {}
    for i in range(len(items)): groups.setdefault(f(i), []).append(i)
    if len(groups) > 1:
        main = max(groups.values(), key=len)
        for g in groups.values():
            if g is not main: bad.append(f"{label}: not joined: " + ", ".join(f"{i}:{items[i][0]}" for i in g))
    for i, (n, p, o, *_) in enumerate(items):
        if "Thruster" not in n: continue
        ex = sb.thruster_exhaust(n, o); lo, hi = bx[i]
        ax = int(np.nonzero(ex)[0][0]); s = int(ex[ax])
        llo, lhi = lo.copy(), hi.copy()
        if s > 0: llo[ax], lhi[ax] = hi[ax], hi[ax] + 30
        else: llo[ax], lhi[ax] = lo[ax] - 30, lo[ax]
        for j in range(len(items)):
            if j == i: continue
            a = np.maximum(llo, bx[j][0]); b = np.minimum(lhi, bx[j][1])
            if np.all(b > a): bad.append(f"{label}: exhaust of {i}:{n} hits {j}:{items[j][0]}")
    return bad


bad = check(blocks, "main") + check([HEAD] + FLAP, "head grid")
mass = sum(RAW[n][3] for n, *_ in blocks) + sum(RAW[n][3] for n, *_ in [HEAD] + FLAP)
print(f"# {len(blocks)} blocks + {1 + len(FLAP)} on the hinge head's grid, {mass / 1000:.1f} t", file=sys.stderr)
for b in bad: print("BAD", b, file=sys.stderr)
print("assumed mounts (unmeasured): " + ", ".join(sorted(ASSUMED)), file=sys.stderr)
out = [f"B {RAW[n][0]} {p[0]} {p[1]} {p[2]} {o[0]} {o[1]}{(' ' + extra) if extra else ''}   # {n}" for n, p, o, extra in blocks]
out.append(f"H {HINGE} -10 10")
out += [f"F 0 {RAW[n][0]} {p[0]} {p[1]} {p[2]} {o[0]} {o[1]}   # {n} (on the hinge head's grid)" for n, p, o in FLAP]
print("\n".join(out))
sys.exit(1 if bad else 0)
