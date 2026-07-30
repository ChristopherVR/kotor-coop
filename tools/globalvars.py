#!/usr/bin/env python3
"""Reader for KOTOR 1 save-game GFF files, specialised for GLOBALVARS.res.

GLOBALVARS.res is the on-disk image of the engine's CSWGlobalVariableTable
(in memory at CServerExoAppInternal+0x100FC).  It is a plain GFF V3.2 file
with the 4CC "GVT ".  PARTYTABLE.res ("PT  ") and savenfo.res ("NFO ") use
the same container, so the generic `gff` sub-command reads those too.

Layout of GLOBALVARS.res (verified against 000002 - Game1):

    root struct
      CatBoolean  : List of { Name : CExoString }   <- ordered name catalogue
      ValBoolean  : VOID  (ceil(n/8) bytes, MSB-first bit per catalogue index)
      CatNumber   : List of { Name : CExoString }
      ValNumber   : VOID  (one unsigned byte per catalogue index)
      CatLocation : List of { Name : CExoString }
      ValLocation : VOID  (24 bytes per entry: 6 little-endian floats)
      CatString   : List of { Name : CExoString }
      ValString   : List of { String : CExoString }

The catalogue index *is* the value index; the engine's in-memory hash table
only exists to map a name to that index quickly.

CLI
    globalvars.py dump   [FILE]                  every variable, grouped by type
    globalvars.py list   [FILE] [--type T] [--prefix P] [--nonzero] [--csv]
    globalvars.py get    [FILE] NAME
    globalvars.py stats  [FILE]
    globalvars.py hash   NAME [NAME...]          engine slot + proposed coop id
    globalvars.py ids    [FILE] [--bits 32]      coop id map + collision report
    globalvars.py gff    [FILE] [--max-list N]   generic GFF tree dump

FILE defaults to the save under the standard GOG install; override with
--save DIR or by passing a path.
"""

import argparse
import os
import struct
import sys

DEFAULT_SAVE = r"C:\GOG Games\Star Wars KOTOR (GOG)\saves\000002 - Game1"

ENGINE_NOTES = r"""
CSWGlobalVariableTable, swkotor.exe GOG 1.03.  All VERIFIED from disassembly.

POINTER CHAIN
    serverApp  = *(u32*)(*(u32*)0x007A39FC + 0x08)      ; CServerExoApp*
    internal   = *(u32*)(serverApp + 0x04)              ; CServerExoAppInternal*
    gvt        = internal + 0x000100FC                  ; CSWGlobalVariableTable (embedded)
    partyTable = internal + 0x0001B770                  ; CSWPartyTable (embedded)
    journal    = *(u32*)(partyTable + 0x11C)            ; CSWSJournal*
  CServerExoApp::GetGlobalVariableTable 0x004AEE60 = { return *(this+4) + 0x100FC; }
  CServerExoApp::GetPartyTable          0x004AEE70 = { return *(this+4) + 0x1B770; }

LAYOUT (size 0xB674)
    +0x00000  GlobalIndentifier identifiers[1775]   ; 0x18 stride, open-addressed hash
                 +0x00 char name[22] (NUL terminated, empty first byte = free slot)
                 +0x16 u16  (type << 14) | index    ; type 0=bool 1=number 2=loc 3=string
    +0x0A668  u8   booleans[113]     ; bit array, MSB first: bit = (b[i>>3] >> (7-(i&7))) & 1
    +0x0A6D9  i8   numbers[500]      ; signed byte; GetGlobalNumber movsx-es it
    +0x0A8D0  CScriptLocation locations[100]   ; 0x18 = {Vector pos; Vector dir}
    +0x0B230  CExoString strings[5]  ; {char*; u32 len}
    +0x0B258  i32 boolean_count      ; cap 900  (addGlobalIndentifierBoolean 0x00528F70)
    +0x0B25C  i32 number_count       ; cap 500
    +0x0B260  i32 location_count     ; cap 100
    +0x0B264  i32 string_count       ; cap 5
    +0x0B268  u32 crc32_table[256]   ; built in the ctor 0x0052B170, poly 0xEDB88320
    +0x0B668  i32 probe stats (collisions, total probe len, max probe len)

KEYING
    Hash 0x00528E50: c = 0; for ch in tolower(name): c = (c>>8) ^ crc[(c^ch)&0xFF]
                     slot = c % 1775; linear probe (slot+1)%1775 while occupied+mismatch.
    FindIdentifierIndex 0x00529030 returns -1 when the name is not in the catalogue.
    The catalogue is FIXED: SetGlobalBoolean on an unknown name logs
    "Script var BOOLEAN '%s' not in catalogue!" and does nothing.

READ / WRITE  (__thiscall, this = gvt, CExoString is {char* ptr; u32 len} by pointer)
    0x00529110  void GetValueBoolean (CExoString* name, int*  out)
    0x00529570  void SetValueBoolean (CExoString* name, int   value)
    0x00529240  void GetValueNumber  (CExoString* name, char* out)
    0x00529680  void SetValueNumber  (CExoString* name, char  value)
    0x00529350  void GetValueLocation(CExoString* name, CScriptLocation* out)
    0x00529790  void SetValueLocation(CExoString* name, CScriptLocation* value)
    0x00529460  void GetValueString  (CExoString* name, CExoString* out)
    0x005298A0  void SetValueString  (CExoString* name, CExoString* value)
    0x00529030  int  FindIdentifierIndex(CExoString* name)
    0x00528E50  int  Hash(CExoString* name)
  These are pure table pokes: no script fires, no client message is sent.

NWSCRIPT ROUTE (alternative, goes through the VM like CreateObject already does)
    578 GetGlobalBoolean   -> 0x00538DC0   579 SetGlobalBoolean -> 0x00542B60
    580 GetGlobalNumber    -> 0x00538DC0   581 SetGlobalNumber  -> 0x00542B60
    194 GetGlobalString    -> 0x00538ED0   160 SetGlobalString  -> 0x00542C60
    692 GetGlobalLocation  -> 0x00538CD0   693 SetGlobalLocation-> 0x00542A50
    679 GetLocalBoolean    -> 0x0053B760   680 SetLocalBoolean  -> 0x00543130
    681 GetLocalNumber     -> 0x0053B760   682 SetLocalNumber   -> 0x00543130
    367 AddJournalQuestEntry -> 0x005483F0 368 RemoveJournalQuestEntry -> 0x0054AF60
    369 GetJournalEntry      -> 0x00549F30

PER-OBJECT LOCALS (CSWVarTable, 0x14 bytes: u32 bits[3] then i8 nums[8])
    ExecuteCommandGetLocalValue 0x0053B760 resolves the table as:
        type==4 (area)   -> obj->vtable[0x28]() + 0x1F4
        type==3 (module) -> obj->vtable[0x20]() + 0x09C
        otherwise        -> obj + 0x110
    0x0059B000 GetLocalBoolean(int idx)          0x0059B040 SetLocalBoolean(int idx,int v)
    0x0059B0B0 GetLocalNumber (int idx)          0x0059B0D0 SetLocalNumber (int idx,char v)
    Only 96 booleans and 8 numbers per object; SetLocalNumber rejects |v| > 127.

JOURNAL (CSWSJournal, 0x0C bytes: {SJournalEntry* data; i32 size; i32 capacity})
    SJournalEntry stride 0x3C: +0x10 date, +0x14 time, +0x18 CExoString plotId,
                               +0x20 state, +0x28 picture(u16), +0x34 planet id
    0x005C58A0 GetState(CExoString* plotId)  -> state, 0 when absent
    0x005C5A40 SetState(CExoString* plotId, int state, int bAllPartyMembers)
               creates the entry if new AND sends SendServerToPlayerJournalAddQuest,
               i.e. it is the one call that also refreshes the journal GUI.
    0x005C63F0 SetDate  0x005C6650 SetTime  0x005C68B0 Destroy(plotId)
    0x004EDB00 CSWSCreature::GetJournal() == GetPartyTable()->GetPartyJournal()
"""

# ---------------------------------------------------------------- GFF reader

BYTE, CHAR, WORD, SHORT, DWORD, INT, DWORD64, INT64, FLOAT, DOUBLE = range(10)
CEXOSTRING, RESREF, CEXOLOCSTRING, VOID, STRUCT, LIST, ORIENTATION, VECTOR, STRREF = range(10, 19)

TYPE_NAMES = {
    BYTE: "BYTE", CHAR: "CHAR", WORD: "WORD", SHORT: "SHORT", DWORD: "DWORD",
    INT: "INT", DWORD64: "DWORD64", INT64: "INT64", FLOAT: "FLOAT",
    DOUBLE: "DOUBLE", CEXOSTRING: "CExoString", RESREF: "ResRef",
    CEXOLOCSTRING: "CExoLocString", VOID: "VOID", STRUCT: "Struct",
    LIST: "List", ORIENTATION: "Orientation", VECTOR: "Vector", STRREF: "StrRef",
}

# types whose payload fits in the field's DataOrDataOffset word
_INLINE = {BYTE, CHAR, WORD, SHORT, DWORD, INT, FLOAT}


class GffStruct(dict):
    """A GFF struct: label -> (field_type, value).  Also behaves as a dict."""

    def __init__(self, struct_type=0):
        super().__init__()
        self.struct_type = struct_type

    def val(self, label, default=None):
        v = self.get(label)
        return default if v is None else v[1]


class Gff:
    def __init__(self, data, path=None):
        self.path = path
        self.raw = data
        if len(data) < 56:
            raise ValueError("too short to be a GFF")
        self.file_type = data[0:4].decode("latin-1")
        self.file_version = data[4:8].decode("latin-1")
        if self.file_version != "V3.2":
            raise ValueError("unsupported GFF version %r" % self.file_version)
        (self.struct_off, self.struct_cnt,
         self.field_off, self.field_cnt,
         self.label_off, self.label_cnt,
         self.fdata_off, self.fdata_len,
         self.findex_off, self.findex_len,
         self.lindex_off, self.lindex_len) = struct.unpack_from("<12I", data, 8)

        self.labels = [
            data[self.label_off + 16 * i: self.label_off + 16 * i + 16].split(b"\0")[0].decode("latin-1")
            for i in range(self.label_cnt)
        ]
        self._struct_cache = {}
        self.root = self.struct_at(0)

    # -- raw accessors -----------------------------------------------------

    def _struct_rec(self, i):
        return struct.unpack_from("<3I", self.raw, self.struct_off + 12 * i)

    def _field_rec(self, i):
        return struct.unpack_from("<3I", self.raw, self.field_off + 12 * i)

    def _field_indices(self, byte_off, count):
        return struct.unpack_from("<%dI" % count, self.raw, self.findex_off + byte_off)

    # -- decoding ----------------------------------------------------------

    def struct_at(self, idx):
        cached = self._struct_cache.get(idx)
        if cached is not None:
            return cached
        stype, data_or_off, nfields = self._struct_rec(idx)
        out = GffStruct(stype)
        self._struct_cache[idx] = out
        if nfields == 1:
            field_ids = (data_or_off,)
        elif nfields == 0:
            field_ids = ()
        else:
            field_ids = self._field_indices(data_or_off, nfields)
        for fid in field_ids:
            label, ftype, value = self.field(fid)
            out[label] = (ftype, value)
        return out

    def field(self, fid):
        ftype, label_idx, dod = self._field_rec(fid)
        label = self.labels[label_idx] if label_idx < len(self.labels) else "<label%d>" % label_idx
        return label, ftype, self._value(ftype, dod)

    def _value(self, ftype, dod):
        d = self.raw
        if ftype in _INLINE:
            if ftype == BYTE:
                return dod & 0xFF
            if ftype == CHAR:
                return struct.unpack("<b", struct.pack("<I", dod)[:1])[0]
            if ftype == WORD:
                return dod & 0xFFFF
            if ftype == SHORT:
                return struct.unpack("<h", struct.pack("<I", dod)[:2])[0]
            if ftype == DWORD:
                return dod
            if ftype == INT:
                return struct.unpack("<i", struct.pack("<I", dod))[0]
            if ftype == FLOAT:
                return struct.unpack("<f", struct.pack("<I", dod))[0]
        if ftype == STRREF:
            # StrRef is stored in field data as {u32 size; u32 strref}
            return struct.unpack_from("<I", d, self.fdata_off + dod + 4)[0]
        if ftype == STRUCT:
            return self.struct_at(dod)
        if ftype == LIST:
            off = self.lindex_off + dod
            (n,) = struct.unpack_from("<I", d, off)
            ids = struct.unpack_from("<%dI" % n, d, off + 4) if n else ()
            return [self.struct_at(i) for i in ids]

        base = self.fdata_off + dod
        if ftype == DWORD64:
            return struct.unpack_from("<Q", d, base)[0]
        if ftype == INT64:
            return struct.unpack_from("<q", d, base)[0]
        if ftype == DOUBLE:
            return struct.unpack_from("<d", d, base)[0]
        if ftype == CEXOSTRING:
            (n,) = struct.unpack_from("<I", d, base)
            return d[base + 4: base + 4 + n].decode("latin-1")
        if ftype == RESREF:
            n = d[base]
            return d[base + 1: base + 1 + n].decode("latin-1")
        if ftype == CEXOLOCSTRING:
            total, strref, count = struct.unpack_from("<3I", d, base)
            p = base + 12
            subs = []
            for _ in range(count):
                sid, slen = struct.unpack_from("<2I", d, p)
                subs.append((sid, d[p + 8: p + 8 + slen].decode("latin-1")))
                p += 8 + slen
            return {"strref": strref if strref != 0xFFFFFFFF else -1, "strings": subs}
        if ftype == VOID:
            (n,) = struct.unpack_from("<I", d, base)
            return d[base + 4: base + 4 + n]
        if ftype == ORIENTATION:
            return struct.unpack_from("<4f", d, base)
        if ftype == VECTOR:
            return struct.unpack_from("<3f", d, base)
        raise ValueError("unknown GFF field type %d" % ftype)


def load_gff(path):
    with open(path, "rb") as fh:
        return Gff(fh.read(), path)


# ------------------------------------------------------- GLOBALVARS decoding

BOOL, NUMBER, LOCATION, STRING = "boolean", "number", "location", "string"
TYPE_ORDER = (BOOL, NUMBER, LOCATION, STRING)

# engine capacities: CSWGlobalVariableTable::addGlobalIndentifier* bounds
CAPACITY = {BOOL: 900, NUMBER: 500, LOCATION: 100, STRING: 5}


class GlobalVar:
    __slots__ = ("name", "type", "index", "value")

    def __init__(self, name, vtype, index, value):
        self.name, self.type, self.index, self.value = name, vtype, index, value

    def is_default(self):
        if self.type == BOOL:
            return self.value == 0
        if self.type == NUMBER:
            return self.value == 0
        if self.type == LOCATION:
            return all(c == 0.0 for c in self.value)
        return self.value == ""

    def fmt(self):
        if self.type == LOCATION:
            return "(%.3f, %.3f, %.3f) dir (%.3f, %.3f, %.3f)" % self.value
        if self.type == STRING:
            return '"%s"' % self.value
        return str(self.value)

    def __repr__(self):
        return "%-8s [%3d] %-28s = %s" % (self.type, self.index, self.name, self.fmt())


def _catalogue(gff, label):
    entries = gff.root.val(label, [])
    return [s.val("Name", "") for s in entries]


class GlobalVars:
    """Decoded GLOBALVARS.res."""

    def __init__(self, gff):
        self.gff = gff
        self.vars = []
        self.by_name = {}
        self.by_type = {t: [] for t in TYPE_ORDER}

        names = _catalogue(gff, "CatBoolean")
        blob = gff.root.val("ValBoolean", b"")
        for i, nm in enumerate(names):
            byte = blob[i >> 3] if (i >> 3) < len(blob) else 0
            self._add(nm, BOOL, i, (byte >> (7 - (i & 7))) & 1)

        names = _catalogue(gff, "CatNumber")
        blob = gff.root.val("ValNumber", b"")
        for i, nm in enumerate(names):
            # GetGlobalNumber sign-extends the stored byte (movsx at 0x00538E84),
            # so a global number is an int8, not a uint8.
            raw = blob[i] if i < len(blob) else 0
            self._add(nm, NUMBER, i, raw - 256 if raw > 127 else raw)

        names = _catalogue(gff, "CatLocation")
        blob = gff.root.val("ValLocation", b"")
        for i, nm in enumerate(names):
            off = i * 24
            v = struct.unpack_from("<6f", blob, off) if off + 24 <= len(blob) else (0.0,) * 6
            self._add(nm, LOCATION, i, v)

        names = _catalogue(gff, "CatString")
        vals = gff.root.val("ValString", [])
        for i, nm in enumerate(names):
            s = vals[i].val("String", "") if i < len(vals) else ""
            self._add(nm, STRING, i, s)

        self.raw_blobs = {
            BOOL: gff.root.val("ValBoolean", b""),
            NUMBER: gff.root.val("ValNumber", b""),
            LOCATION: gff.root.val("ValLocation", b""),
        }

    def _add(self, name, vtype, index, value):
        v = GlobalVar(name, vtype, index, value)
        self.vars.append(v)
        self.by_type[vtype].append(v)
        self.by_name.setdefault(name.lower(), v)

    def get(self, name):
        return self.by_name.get(name.lower())


# ------------------------------------------------------------------ hashing

def _crc_table():
    tab = []
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ 0xEDB88320 if c & 1 else c >> 1
        tab.append(c)
    return tab


_CRC = _crc_table()


def engine_crc(name):
    """CSWGlobalVariableTable::Hash inner CRC (0x00528E50).

    Standard reflected CRC-32 (poly 0xEDB88320) with init 0 and no final
    xor, folded over tolower(name).  Verified byte-for-byte against the
    disassembly; the table is built in the constructor at 0x0052B170.
    """
    c = 0
    for ch in name.encode("latin-1", "replace"):
        if 0x41 <= ch <= 0x5A:
            ch += 0x20
        c = (c >> 8) ^ _CRC[(c ^ ch) & 0xFF]
    return c & 0xFFFFFFFF


def engine_slot(name):
    """Initial probe slot in the 1775-entry identifier table."""
    return engine_crc(name) % 0x6EF


def coop_flag_id(name, vtype=None):
    """Proposed EventFlagStore key: FNV-1a 32 over "type:lowername".

    Deliberately NOT the engine hash: the engine's is mod-1775 and collides
    constantly by design (it linear-probes).  Namespacing by type keeps a
    boolean and a number of the same name distinct.
    """
    s = ("%s:%s" % (vtype, name.lower())) if vtype else name.lower()
    h = 0x811C9DC5
    for ch in s.encode("utf-8"):
        h = ((h ^ ch) * 0x01000193) & 0xFFFFFFFF
    return h


# ---------------------------------------------------------------------- CLI

def _resolve(path_or_none, filename, save_dir):
    if path_or_none:
        p = path_or_none
        if os.path.isdir(p):
            return os.path.join(p, filename)
        if not os.path.exists(p) and not os.path.isabs(p):
            alt = os.path.join(save_dir, p)
            if os.path.exists(alt):
                return alt
        return p
    return os.path.join(save_dir, filename)


def _load_globals(args):
    p = _resolve(args.file, "GLOBALVARS.res", args.save)
    if not os.path.exists(p):
        sys.exit("no such file: %s" % p)
    return p, GlobalVars(load_gff(p))


def cmd_dump(args):
    p, gv = _load_globals(args)
    print("# %s" % p)
    for t in TYPE_ORDER:
        rows = gv.by_type[t]
        n_set = sum(0 if v.is_default() else 1 for v in rows)
        print("\n== %s: %d declared / %d capacity, %d non-default ==" % (t, len(rows), CAPACITY[t], n_set))
        for v in rows:
            print("  [%3d] %-30s %s" % (v.index, v.name, v.fmt()))
    return 0


def cmd_list(args):
    p, gv = _load_globals(args)
    rows = gv.vars if not args.type else gv.by_type[args.type]
    if args.prefix:
        pfx = args.prefix.lower()
        rows = [v for v in rows if v.name.lower().startswith(pfx)]
    if args.nonzero:
        rows = [v for v in rows if not v.is_default()]
    if args.csv:
        print("type,index,name,value")
        for v in rows:
            print("%s,%d,%s,%s" % (v.type, v.index, v.name, v.fmt().replace(",", ";")))
    else:
        for v in rows:
            print(repr(v))
    print("# %d row(s)" % len(rows), file=sys.stderr)
    return 0


def cmd_get(args):
    p, gv = _load_globals(args)
    v = gv.get(args.name)
    if v is None:
        print("not declared: %s" % args.name)
        return 1
    print("%s  type=%s index=%d value=%s" % (v.name, v.type, v.index, v.fmt()))
    print("  engine slot = %d (crc 0x%08X)" % (engine_slot(v.name), engine_crc(v.name)))
    print("  coop flag id = 0x%08X" % coop_flag_id(v.name, v.type))
    return 0


def cmd_stats(args):
    p, gv = _load_globals(args)
    print("%s  (%d bytes)" % (p, os.path.getsize(p)))
    total = 0
    for t in TYPE_ORDER:
        rows = gv.by_type[t]
        total += len(rows)
        n_set = sum(0 if v.is_default() else 1 for v in rows)
        print("  %-9s %4d declared  %4d non-default  (engine cap %d)" % (t, len(rows), n_set, CAPACITY[t]))
    print("  %-9s %4d" % ("TOTAL", total))
    pfx = {}
    for v in gv.vars:
        key = v.name.split("_")[0] if "_" in v.name else v.name[:3]
        pfx[key] = pfx.get(key, 0) + 1
    print("\ntop name prefixes:")
    for k, n in sorted(pfx.items(), key=lambda kv: -kv[1])[:25]:
        print("  %-14s %4d" % (k, n))
    return 0


def cmd_hash(args):
    for n in args.names:
        print("%-32s slot=%4d crc=0x%08X  id(bool)=0x%08X id(number)=0x%08X"
              % (n, engine_slot(n), engine_crc(n), coop_flag_id(n, BOOL), coop_flag_id(n, NUMBER)))
    return 0


def cmd_ids(args):
    p, gv = _load_globals(args)
    mask = (1 << args.bits) - 1
    seen = {}
    coll = []
    for v in gv.vars:
        fid = coop_flag_id(v.name, v.type) & mask
        if fid in seen and seen[fid] != (v.type, v.name):
            coll.append((fid, seen[fid], (v.type, v.name)))
        seen[fid] = (v.type, v.name)
    for v in gv.vars:
        print("0x%08X  %-8s %s" % (coop_flag_id(v.name, v.type) & mask, v.type, v.name))
    print("\n# %d names, %d distinct ids at %d bits, %d collision(s)"
          % (len(gv.vars), len(seen), args.bits, len(coll)), file=sys.stderr)
    for fid, a, b in coll:
        print("#   COLLISION 0x%08X: %s vs %s" % (fid, a, b), file=sys.stderr)
    # engine-slot collisions, for reference
    slots = {}
    eng = 0
    for v in gv.vars:
        s = engine_slot(v.name)
        if s in slots:
            eng += 1
        slots.setdefault(s, v.name)
    print("# engine 1775-slot table: %d probe collisions among these names" % eng, file=sys.stderr)
    return 0


def _fmt_value(ftype, value, indent, max_list):
    if ftype == LIST:
        n = len(value)
        head = "List[%d]" % n
        if n == 0:
            return head
        lines = [head]
        for i, s in enumerate(value[:max_list]):
            lines.append("%s  [%d] %s" % (" " * indent, i, _fmt_struct(s, indent + 4, max_list)))
        if n > max_list:
            lines.append("%s  ... %d more" % (" " * indent, n - max_list))
        return "\n".join(lines)
    if ftype == STRUCT:
        return _fmt_struct(value, indent + 2, max_list)
    if ftype == VOID:
        return "VOID[%d] %s%s" % (len(value), value[:32].hex(), "..." if len(value) > 32 else "")
    return repr(value)


def _fmt_struct(s, indent, max_list):
    if not s:
        return "{}"
    lines = ["{ (type %d)" % s.struct_type]
    for label, (ftype, value) in s.items():
        lines.append("%s%-20s %-14s %s" % (" " * indent, label, TYPE_NAMES.get(ftype, ftype),
                                           _fmt_value(ftype, value, indent, max_list)))
    lines.append("%s}" % (" " * max(0, indent - 2)))
    return "\n".join(lines)


def cmd_diff(args):
    a = GlobalVars(load_gff(_resolve(args.a, "GLOBALVARS.res", args.save)))
    b = GlobalVars(load_gff(_resolve(args.b, "GLOBALVARS.res", args.save)))
    an = {(v.type, v.name.lower()): v for v in a.vars}
    bn = {(v.type, v.name.lower()): v for v in b.vars}
    n = 0
    for k in sorted(set(an) | set(bn)):
        va, vb = an.get(k), bn.get(k)
        if va is None:
            print("+ %-8s %-30s %s" % (k[0], vb.name, vb.fmt()))
            n += 1
        elif vb is None:
            print("- %-8s %-30s %s" % (k[0], va.name, va.fmt()))
            n += 1
        elif va.value != vb.value:
            print("~ %-8s %-30s %s -> %s" % (k[0], va.name, va.fmt(), vb.fmt()))
            n += 1
    print("# %d difference(s)" % n, file=sys.stderr)
    return 0


def cmd_engine(args):
    print(ENGINE_NOTES.strip())
    return 0


def cmd_gff(args):
    p = _resolve(args.file, "GLOBALVARS.res", args.save)
    if not os.path.exists(p):
        sys.exit("no such file: %s" % p)
    g = load_gff(p)
    print("%s  type=%r version=%r  structs=%d fields=%d labels=%d"
          % (p, g.file_type, g.file_version, g.struct_cnt, g.field_cnt, g.label_cnt))
    print("labels: %s" % ", ".join(g.labels))
    print(_fmt_struct(g.root, 2, args.max_list))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--save", default=DEFAULT_SAVE, help="save directory (default: %(default)s)")
    sub = ap.add_subparsers(dest="cmd")

    def add(name, fn, **kw):
        sp = sub.add_parser(name, **kw)
        sp.set_defaults(fn=fn)
        return sp

    sp = add("dump", cmd_dump, help="print every global variable")
    sp.add_argument("file", nargs="?")

    sp = add("list", cmd_list, help="filtered listing")
    sp.add_argument("file", nargs="?")
    sp.add_argument("--type", choices=TYPE_ORDER)
    sp.add_argument("--prefix")
    sp.add_argument("--nonzero", action="store_true", help="only non-default values")
    sp.add_argument("--csv", action="store_true")

    sp = add("get", cmd_get, help="look one variable up by name")
    sp.add_argument("name")
    sp.add_argument("file", nargs="?")

    sp = add("stats", cmd_stats, help="counts per type and name-prefix histogram")
    sp.add_argument("file", nargs="?")

    sp = add("hash", cmd_hash, help="engine hash slot and proposed coop flag id")
    sp.add_argument("names", nargs="+")

    sp = add("ids", cmd_ids, help="coop flag id map plus collision report")
    sp.add_argument("file", nargs="?")
    sp.add_argument("--bits", type=int, default=32)

    sp = add("diff", cmd_diff, help="compare two GLOBALVARS.res files")
    sp.add_argument("a")
    sp.add_argument("b")

    add("engine", cmd_engine, help="print the verified in-memory layout and call recipe")

    sp = add("gff", cmd_gff, help="generic GFF tree dump (works on PARTYTABLE.res, savenfo.res)")
    sp.add_argument("file", nargs="?")
    sp.add_argument("--max-list", type=int, default=8)

    args = ap.parse_args(argv)
    if not getattr(args, "fn", None):
        ap.print_help()
        return 2
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
