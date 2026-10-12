# aeroab_sum.py FILE TAG - the AeroFlightTest telemetry ([FLIGHT] t=... lines) summarised per phase: the mean of the
# model's own L/D, speed, vertical speed, angle of attack; for the glide also speed over sink (the flown glide ratio).
import re, sys
rows = {}
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"\b(SETUP|CRUISE|CLIMB|GLIDE)\b", line)
    if not m: continue
    v = dict((k, float(x)) for k, x in re.findall(r"(spd|vs|aoa|L/D|lift|drag|trim|gyroMax)=\s*(-?[0-9.]+)", line))
    rows.setdefault(m.group(1), []).append(v)
for ph in ("CRUISE", "CLIMB", "GLIDE"):
    r = rows.get(ph, [])
    if not r: continue
    # (the glide's first seconds still carry the climb: its second half only)
    if ph == "GLIDE": r = r[len(r) // 2:]
    avg = lambda k: sum(x.get(k, 0) for x in r) / len(r)
    sink = avg("vs")
    gr = f"  speed/sink {-avg('spd') / sink:.1f}" if ph == "GLIDE" and sink < -0.1 else ""
    tr = f"  trim {avg('trim'):6.0f} kNm (gyros {avg('gyroMax'):.0f})" if any("trim" in x for x in r) else ""
    print(f"  {sys.argv[2]} {ph:<6} n={len(r):3d}  L/D {avg('L/D'):6.2f}  spd {avg('spd'):6.0f}  vs {sink:6.1f}  aoa {avg('aoa'):5.1f}{gr}{tr}")
