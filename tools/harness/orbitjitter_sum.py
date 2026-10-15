# orbitjitter_sum.py FILE - the orbit readout's samples (orbitnow lines): Ap, Pe, speed, altitude each; their
# sample-to-sample changes (median, max) - a smooth climb or drag moves them a little each sample, noise jumps
import re, sys, statistics as st
rows = []
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    t = float(line.split()[0])
    alt = re.search(r"alt (-?[0-9.]+) km", line); v = re.search(r"v ([0-9.]+) m/s", line)
    pe = re.search(r"Pe (-?[0-9.]+) km", line); ap = re.search(r"Ap (-?[0-9.]+) km", line)
    if alt and v and pe: rows.append((t, float(alt.group(1)), float(v.group(1)), float(pe.group(1)), float(ap.group(1)) if ap else float("nan")))
if len(rows) < 3: print("  too few samples:", len(rows)); sys.exit()
print(f"  {len(rows)} samples over {rows[-1][0] - rows[0][0]:.1f} s; alt {rows[0][1]:.2f} -> {rows[-1][1]:.2f} km, v {rows[0][2]:.0f} -> {rows[-1][2]:.0f} m/s")
for k, name in ((3, "Pe"), (4, "Ap"), (2, "v")):
    d = [abs(b[k] - a[k]) for a, b in zip(rows, rows[1:]) if b[k] == b[k] and a[k] == a[k]]
    if d: print(f"  {name}: sample-to-sample change median {st.median(d):.3f}, max {max(d):.3f} ({'km' if k > 2 else 'm/s'}); range {min(r[k] for r in rows):.2f} .. {max(r[k] for r in rows):.2f}")
