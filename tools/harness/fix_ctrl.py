# fix_ctrl.py FILE... — repair sed back-references mangled into control bytes (\1 -> 0x01) by string escaping.
import sys
for p in sys.argv[1:]:
    b = open(p, 'rb').read()
    n = b.count(b'\x01') + b.count(b'\x02')
    if n:
        open(p, 'wb').write(b.replace(b'\x01', b'\\1').replace(b'\x02', b'\\2'))
    print(f"{p}: {n} fixed")
