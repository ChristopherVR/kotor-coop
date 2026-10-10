"""Dump the structure of a KOTOR 1 binary MDL (study aid). Usage: mdldump.py file.mdl [--nodes] [--anims] [--cover]"""
import struct
import sys

B = 12
NODE_FLAGS = {0x1: "base", 0x2: "light", 0x4: "emitter", 0x10: "ref", 0x20: "mesh", 0x40: "skin", 0x100: "dangly",
              0x200: "aabb", 0x800: "saber"}


class R:
    def __init__(self, data):
        self.d = data
        self.cover = []

    def u(self, fmt, off, what=""):
        size = struct.calcsize("<" + fmt)
        self.cover.append((off + B, off + B + size, what))
        return struct.unpack_from("<" + fmt, self.d, off + B)

    def s(self, off, n, what=""):
        self.cover.append((off + B, off + B + n, what))
        raw = self.d[off + B:off + B + n]
        return raw.split(b"\0")[0].decode("latin1")

    def cstr(self, off, what=""):
        end = self.d.index(b"\0", off + B)
        self.cover.append((off + B, end + 1, what))
        return self.d[off + B:end].decode("latin1")


def flags(f):
    return "+".join(v for k, v in NODE_FLAGS.items() if f & k) + (f"(0x{f:X})" if f & ~0xB77 else "")


def dump_ctrl(r, off, count, doff, dcount, indent, verbose):
    out = []
    for i in range(count):
        t, unk, rows, tk, vk, cols, p0, p1, p2 = r.u("IHHHHBBBB", off + 16 * i, "ctrl")
        out.append((t, unk, rows, tk, vk, cols, (p0, p1, p2)))
    if dcount:
        r.u(f"{dcount}f", doff, "ctrldata")
    if verbose:
        for t, unk, rows, tk, vk, cols, pad in out:
            vals = ""
            if rows <= 3 and not (t == 20 and cols == 2):
                n = (cols & 0xF) * (3 if cols & 0x10 else 1)
                times = struct.unpack_from(f"<{rows}f", r.d, B + doff + 4 * tk)
                v = struct.unpack_from(f"<{rows * n}f", r.d, B + doff + 4 * vk)
                vals = f" t={[round(x, 3) for x in times]} v={[round(x, 3) for x in v]}"
            print(f"{indent}  ctrl type={t} unk=0x{unk:04X} rows={rows} tk={tk} vk={vk} cols={cols} pad={pad}{vals}")
    return out


def dump_node(r, names, off, depth, verbose, anim=False):
    f, supernode, nameidx, pad, offroot, offparent = r.u("HHHHII", off, "node")
    pos = r.u("3f", off + 16, "node")
    ori = r.u("4f", off + 28, "node")
    ch = r.u("3I", off + 44, "node")
    ct = r.u("3I", off + 56, "node")
    cd = r.u("3I", off + 68, "node")
    indent = "  " * depth
    line = (f"{indent}{names[nameidx]} [{flags(f)}] #{supernode} nameidx={nameidx} pad={pad} @{off} root={offroot} parent={offparent} "
            f"pos=({pos[0]:.3f},{pos[1]:.3f},{pos[2]:.3f}) ori=({ori[0]:.3f},{ori[1]:.3f},{ori[2]:.3f},{ori[3]:.3f}) "
            f"children={ch[0]}x{ch[1]} ctrl={ct[0]}x{ct[1]} data={cd[0]}x{cd[1]}")
    if verbose:
        print(line)
    p = off + 80
    if f & 0x20:
        m = r.u("II", p, "mesh")
        faces = r.u("3I", p + 8, "mesh")
        bb = r.u("6f", p + 20, "mesh")
        radius, = r.u("f", p + 44, "mesh")
        avg = r.u("3f", p + 48, "mesh")
        dif = r.u("3f", p + 60, "mesh")
        amb = r.u("3f", p + 72, "mesh")
        thint, = r.u("I", p + 84, "mesh")
        tex = r.s(p + 88, 32, "mesh")
        tex2 = r.s(p + 120, 32, "mesh")
        unk24 = r.u("24B", p + 152, "mesh")
        icount = r.u("3I", p + 176, "mesh")
        ioff = r.u("3I", p + 188, "mesh")
        inv = r.u("3I", p + 200, "mesh")
        unk3 = r.u("3i", p + 212, "mesh")
        saber = r.u("8B", p + 224, "mesh")
        anim_uv = r.u("I4f", p + 232, "mesh")
        mdxsize, mdxbits = r.u("II", p + 252, "mesh")
        mdxoffs = r.u("11i", p + 260, "mesh")
        nverts, ntex = r.u("HH", p + 304, "mesh")
        fl = r.u("6B", p + 308, "mesh")
        pad2 = r.u("H", p + 314, "mesh")
        area, unk4, mdxoff, vertoff = r.u("fIII", p + 316, "mesh")
        if verbose:
            print(f"{indent}  mesh fn=({m[0]},{m[1]}) faces={faces} bb={[round(x, 3) for x in bb]} r={radius:.3f} avg={[round(x, 3) for x in avg]}")
            print(f"{indent}  dif={dif} amb={amb} thint={thint} tex='{tex}' tex2='{tex2}' unk24={bytes(unk24).hex()}")
            print(f"{indent}  icount={icount} ioff={ioff} inv={inv} unk3={unk3} saber={bytes(saber).hex()} animuv={anim_uv}")
            print(f"{indent}  mdxsize={mdxsize} bits=0x{mdxbits:X} mdxoffs={mdxoffs} nverts={nverts} ntex={ntex} "
                  f"flags(lm,rot,bg,shadow,beam,render)={fl} pad={pad2} area={area:.4f} unk4={unk4} mdxoff={mdxoff} vertoff={vertoff}")
        if faces[1]:
            r.u(f"{faces[1] * 32}B", faces[0], "faces")
            if verbose:
                for i in range(min(2, faces[1])):
                    fc = struct.unpack_from("<4fI6H", r.d, B + faces[0] + 32 * i)
                    print(f"{indent}  face{i}: n=({fc[0]:.3f},{fc[1]:.3f},{fc[2]:.3f}) d={fc[3]:.3f} mat={fc[4]} adj={fc[5:8]} v={fc[8:11]}")
        if icount[1]:
            cnt = r.u(f"{icount[1]}I", icount[0], "icount")
            offs = r.u(f"{ioff[1]}I", ioff[0], "ioff")
            invv = r.u(f"{inv[1]}I", inv[0], "inv")
            for c, o in zip(cnt, offs):
                r.u(f"{c}H", o, "indices")
            if verbose:
                print(f"{indent}  indexcounts={cnt} indexoffs={offs} invcounters={invv}")
        if nverts and vertoff:
            r.u(f"{nverts * 3}f", vertoff, "verts")
        p += 332
    if f & 0x40:
        sk = r.u("3I", p, "skin")
        wo, bo, bmo, bmc = r.u("IIII", p + 12, "skin")
        q = r.u("3I", p + 28, "skin")
        t = r.u("3I", p + 40, "skin")
        g = r.u("3I", p + 52, "skin")
        r.u("16H", p + 64, "skin")
        r.u("I", p + 96, "skin")
        if bmc:
            r.u(f"{bmc}f", bmo, "bonemap")
        if q[1]:
            r.u(f"{q[1] * 4}f", q[0], "qbones")
        if t[1]:
            r.u(f"{t[1] * 3}f", t[0], "tbones")
        if g[1]:
            r.u(f"{g[1]}I", g[0], "garbage")
        if verbose:
            print(f"{indent}  skin bonemap={bmc} q={q} t={t} g={g}")
        p += 100
    if f & 0x100:
        c = r.u("3I", p, "dangly")
        r.u("3fI", p + 12, "dangly")
        p += 28
    if f & 0x2 and verbose:
        print(f"{indent}  (light)")
    if f & 0x4 and verbose:
        print(f"{indent}  (emitter)")
    if f & 0x10:
        ref = r.s(p, 32, "ref")
        if verbose:
            print(f"{indent}  ref '{ref}'")
    ctrls = dump_ctrl(r, ct[0], ct[1], cd[0], cd[1], indent, verbose) if ct[1] else []
    kids = r.u(f"{ch[1]}I", ch[0], "children") if ch[1] else ()
    n = 1
    for k in kids:
        n += dump_node(r, names, k, depth + 1, verbose, anim)
    return n


def main():
    path = sys.argv[1]
    opts = set(sys.argv[2:])
    data = open(path, "rb").read()
    r = R(data)
    zero, mdlsize, mdxsize = struct.unpack_from("<III", data, 0)
    print(f"file {len(data)} bytes, header: {zero} mdl={mdlsize} mdx={mdxsize}")
    fn1, fn2 = r.u("II", 0, "geom")
    name = r.s(8, 32, "geom")
    rootoff, nodecount = r.u("II", 40, "geom")
    rt = r.u("6I", 48, "geom")
    refcount, gtype, p1, p2, p3 = r.u("IBBBB", 72, "geom")
    print(f"geom fn=({fn1},{fn2}) name='{name}' root={rootoff} nodes={nodecount} rt={rt} ref={refcount} type={gtype} pad={(p1, p2, p3)}")
    cls, sub, unk, fog, nchild = r.u("BBBBI", 80, "model")
    anims = r.u("3I", 88, "model")
    superref, = r.u("I", 100, "model")
    bb = r.u("6f", 104, "model")
    radius, scale = r.u("ff", 128, "model")
    supername = r.s(136, 32, "model")
    headroot, unused, mdxsize2, mdxoff = r.u("IIII", 168, "model")
    namearr = r.u("3I", 184, "model")
    print(f"model class={cls} sub={sub} unk={unk} fog={fog} childmodels={nchild} anims={anims} superref={superref}")
    print(f"  bb={[round(x, 3) for x in bb]} radius={radius:.3f} animscale={scale:.3f} super='{supername}' headroot={headroot} unused={unused} mdxsize={mdxsize2} mdxoff={mdxoff} names={namearr}")
    nameoffs = r.u(f"{namearr[1]}I", namearr[0], "nameoffs")
    names = [r.cstr(o, "names") for o in nameoffs]
    print(f"names({len(names)}): {names}")
    n = dump_node(r, names, rootoff, 0, "--nodes" in opts)
    print(f"geometry nodes walked: {n}")
    if anims[1]:
        aoffs = r.u(f"{anims[1]}I", anims[0], "animoffs")
        for ao in aoffs:
            afn1, afn2 = r.u("II", ao, "anim")
            aname = r.s(ao + 8, 32, "anim")
            aroot, anodes = r.u("II", ao + 40, "anim")
            art = r.u("6I", ao + 48, "anim")
            aref, atype, ap1, ap2, ap3 = r.u("IBBBB", ao + 72, "anim")
            length, trans = r.u("ff", ao + 80, "anim")
            animroot = r.s(ao + 88, 32, "anim")
            ev = r.u("3I", ao + 120, "anim")
            unk, = r.u("I", ao + 132, "anim")
            events = []
            for i in range(ev[1]):
                t, = r.u("f", ev[0] + 36 * i, "events")
                events.append((round(t, 3), r.s(ev[0] + 36 * i + 4, 32, "events")))
            print(f"anim '{aname}' @{ao} fn=({afn1},{afn2}) root={aroot} nodes={anodes} ref={aref} type={atype} len={length:.3f} trans={trans:.3f} animroot='{animroot}' unk={unk} events={events}")
            cnt = dump_node(r, names, aroot, 2, "--anims" in opts, True)
            if cnt != anodes:
                print(f"   !! walked {cnt} nodes vs header {anodes}")
    if "--cover" in opts:
        spans = sorted(set((a, b, w) for a, b, w in r.cover))
        pos = 12
        last = None
        runs = []
        for a, b, w in spans:
            if a > pos:
                runs.append((pos, a, "GAP " + data[pos:a][:16].hex()))
            if runs and runs[-1][2] == w and runs[-1][1] >= a:
                runs[-1] = (runs[-1][0], max(b, runs[-1][1]), w)
            else:
                runs.append((a, b, w))
            pos = max(pos, b)
        if pos < len(data):
            runs.append((pos, len(data), "GAP(tail)"))
        for a, b, w in runs[:400]:
            print(f"  {a - 12:7d}..{b - 12:7d} ({b - a:6d}) {w}")


main()
