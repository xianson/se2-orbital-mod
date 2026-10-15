# fbw_sum.py FILE TAG - [AERO-PITCH] lines summarised: Mach, the rates' spread (RMS of each axis, the peak), how often the
# pitch rate changes sign (an oscillation's half-periods), the attitude error's peak, the air's torque and the gyros' and
# thrusters' torque (mean sizes, kN m), and how many lines flew by the aero-aware law
import re, sys, math
rows = []
num = r"(-?[0-9.]+)"
v3 = r"\(" + num + "," + num + "," + num + r"\)"
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"M=([0-9.]+) w=" + v3 + " err=" + v3 + "deg aeroT=" + v3 + "kNm gyro=" + v3 + "kNm rcs=" + v3 + r"kNm undone=" + v3 + r" aware=([01])", line)
    if m: rows.append([float(x) for x in m.groups()])
if not rows: print(f"  {sys.argv[2]}: no samples"); sys.exit()
rows = rows[5:] or rows   # (the launch's first half second)
def rms(i): return math.sqrt(sum(r[i] ** 2 for r in rows) / len(rows))
def mag(i): return sum(math.sqrt(r[i] ** 2 + r[i + 1] ** 2 + r[i + 2] ** 2) for r in rows) / len(rows)
flips = [sum(1 for a, b in zip(rows, rows[1:]) if a[i] * b[i] < 0) for i in (1, 2, 3)]
err = max(math.sqrt(r[4] ** 2 + r[5] ** 2 + r[6] ** 2) for r in rows)
print(f"  {sys.argv[2]}: n={len(rows)} Mach {sum(r[0] for r in rows) / len(rows):.2f}  rate RMS x {rms(1):.3f} y {rms(2):.3f} z {rms(3):.3f} rad/s"
      f"  peak {max(max(abs(r[1]), abs(r[2]), abs(r[3])) for r in rows):.3f}  sign flips x/y/z {flips}  error peak {err:.2f} deg"
      f"  air {mag(7):.0f} gyro {mag(10):.0f} rcs {mag(13):.0f} kN m  aware {sum(int(r[19]) for r in rows)}/{len(rows)}")
