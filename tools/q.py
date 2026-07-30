import sqlite3, sys, os

DB = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'kotor1_0_3.db')
if len(sys.argv) > 2 and sys.argv[1] == '--db':
    DB = sys.argv[2]
    del sys.argv[1:3]
con = sqlite3.connect(DB)
cur = con.cursor()

def rows(sql, args=()):
    cur.execute(sql, args)
    return cur.fetchall()

cmd = sys.argv[1] if len(sys.argv) > 1 else 'schema'

if cmd == 'schema':
    for (name,) in rows("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
        cur.execute(f"SELECT COUNT(*) FROM [{name}]")
        n = cur.fetchone()[0]
        cur.execute(f"PRAGMA table_info([{name}])")
        cols = ', '.join(c[1] for c in cur.fetchall())
        print(f"{name:24} {n:6} rows   ({cols})")

elif cmd == 'offsets':
    cls = sys.argv[2]
    for r in rows("SELECT member_name, offset, notes FROM offsets WHERE class_name=? ORDER BY offset", (cls,)):
        note = f"   -- {r[2]}" if r[2] else ""
        print(f"  0x{r[1]:03X}  {r[0]}{note}")

elif cmd == 'class':
    like = sys.argv[2]
    for r in rows("SELECT class_name, size, vtable FROM classes WHERE class_name LIKE ? ORDER BY class_name", (f'%{like}%',)):
        vt = f"0x{r[2]:X}" if isinstance(r[2], int) and r[2] else str(r[2])
        sz = f"0x{r[1]:X}" if isinstance(r[1], int) and r[1] else str(r[1])
        print(f"  {r[0]:34} size={sz:8} vtable={vt}")

elif cmd == 'func':
    like = sys.argv[2]
    q = "SELECT class_name, function_name, address, calling_convention FROM functions WHERE function_name LIKE ?"
    args = (f'%{like}%',)
    if len(sys.argv) > 3:
        q += " AND class_name LIKE ?"
        args = args + (f'%{sys.argv[3]}%',)
    for r in rows(q + " ORDER BY class_name, function_name", args):
        addr = f"0x{r[2]:08X}" if isinstance(r[2], int) else str(r[2])
        print(f"  {addr}  {r[0]}::{r[1]}   [{r[3]}]")

elif cmd == 'globals':
    pat = sys.argv[2] if len(sys.argv) > 2 else ''
    for r in rows("SELECT pointer_name, address FROM global_pointers WHERE pointer_name LIKE ? ORDER BY pointer_name", (f'%{pat}%',)):
        addr = f"0x{r[1]:08X}" if isinstance(r[1], int) else str(r[1])
        print(f"  {addr}  {r[0]}")

elif cmd == 'sql':
    for r in rows(sys.argv[2]):
        print('  ' + '  '.join(f"0x{v:X}" if isinstance(v, int) and v > 0x1000 else str(v) for v in r))

con.close()
