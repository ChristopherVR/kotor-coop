#!/usr/bin/env python3
"""
kotorfiles.py -- pure-stdlib readers for the Star Wars: KOTOR 1 (Odyssey engine)
data formats a co-op mod needs.

Implements:
  * KEY/BIF  -- chitin.key + data/*.bif   (the global resource index)
  * RIM      -- modules/*.rim, rims/*.rim (module archives)
  * ERF/MOD  -- patch.erf, *.mod, *.erf   (archives with a localised description)
  * GFF V3.2 -- .are .git .ifo .utc .utd .utp .uti .utt .ute .utw .dlg .res .fac ...
  * 2DA      -- both "2DA V2.0" (ASCII) and "2DA V2.b" (binary)
  * TLK      -- dialog.tlk (enough to resolve a StrRef to text)

Nothing here writes to disk; every open() is 'rb'.

CLI
---
  python tools/kotorfiles.py key            [--game DIR] [--bifs] [--find PATTERN]
  python tools/kotorfiles.py list           ARCHIVE [--type EXT]
  python tools/kotorfiles.py gff            SOURCE [--max-depth N] [--path PATH]
  python tools/kotorfiles.py 2da            SOURCE [--rows A:B] [--cols a,b,c] [--where col=val]
  python tools/kotorfiles.py extract        SOURCE --out FILE
  python tools/kotorfiles.py tlk            STRREF [--game DIR]
  python tools/kotorfiles.py creatures      MODULE [--game DIR]

SOURCE is either a filesystem path, "archive.rim:resref.ext" to read a member of an
archive, or a bare "resref.ext" which is looked up in chitin.key.

Examples
--------
  python tools/kotorfiles.py key  --bifs
  python tools/kotorfiles.py list "C:/GOG Games/Star Wars KOTOR (GOG)/modules/end_m01aa.rim"
  python tools/kotorfiles.py gff  "end_m01aa:m01aa.git" --path "Creature List/7"
  python tools/kotorfiles.py 2da  creaturespeed.2da
  python tools/kotorfiles.py 2da  appearance.2da --rows 110:120 --cols label,walkdist,rundist
  python tools/kotorfiles.py creatures end_m01aa
  python tools/kotorfiles.py objectids end_m01aa --verify path/to/END_M01AA.sav

NOTE: a module's resources are named after the AREA, not the module -- end_m01aa.rim
contains m01aa.are / m01aa.git / module.ifo.  The `creatures` and `objectids`
commands find the .git by resource type so you can just name the module.
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

DEFAULT_GAME_DIR = r"C:\GOG Games\Star Wars KOTOR (GOG)"


# --------------------------------------------------------------------------
# resource types
# --------------------------------------------------------------------------

RES_TYPES: Dict[int, str] = {
    0: "res", 1: "bmp", 2: "mve", 3: "tga", 4: "wav", 6: "plt", 7: "ini",
    8: "mp3", 9: "mpg", 10: "txt", 11: "xml",
    2000: "plh", 2001: "tex", 2002: "mdl", 2003: "thg", 2005: "fnt",
    2007: "lua", 2008: "slt", 2009: "nss", 2010: "ncs", 2011: "mod",
    2012: "are", 2013: "set", 2014: "ifo", 2015: "bic", 2016: "wok",
    2017: "2da", 2018: "tlk", 2022: "txi", 2023: "git", 2024: "bti",
    2025: "uti", 2026: "btc", 2027: "utc", 2029: "dlg", 2030: "itp",
    2031: "btt", 2032: "utt", 2033: "dds", 2034: "bts", 2035: "uts",
    2036: "ltr", 2037: "gff", 2038: "fac", 2039: "bte", 2040: "ute",
    2041: "btd", 2042: "utd", 2043: "btp", 2044: "utp", 2045: "dft",
    2046: "gic", 2047: "gui", 2048: "css", 2049: "ccs", 2050: "btm",
    2051: "utm", 2052: "dwk", 2053: "pwk", 2054: "jrl", 2055: "sav",
    2056: "utw", 2057: "4pc", 2058: "ssf", 2059: "hak", 2060: "nwm",
    2061: "bik", 2062: "ndb", 2063: "ptm", 2064: "ptt", 2065: "bak",
    3000: "lyt", 3001: "vis", 3002: "rim", 3003: "pth", 3004: "lip",
    3005: "bwm", 3006: "txb", 3007: "tpc", 3008: "mdx", 3009: "rsv",
    3010: "sig", 3011: "mab", 3012: "qst", 3013: "npc", 3014: "spn",
    3015: "utx", 3016: "mmd", 3017: "smm", 3018: "uta", 3019: "mde",
    3020: "mdv", 3021: "mdx", 3022: "hqm", 3033: "erf",
}
EXT_TO_TYPE: Dict[str, int] = {}
for _k, _v in RES_TYPES.items():
    EXT_TO_TYPE.setdefault(_v, _k)
EXT_TO_TYPE["mdx"] = 3008  # disambiguate the duplicate


def restype_name(t: int) -> str:
    return RES_TYPES.get(t, "t%d" % t)


def _cstr(b: bytes) -> str:
    i = b.find(b"\x00")
    if i >= 0:
        b = b[:i]
    return b.decode("windows-1252", "replace")


# --------------------------------------------------------------------------
# Archive base
# --------------------------------------------------------------------------

class Resource:
    """One entry in an archive."""

    __slots__ = ("resref", "restype", "offset", "size", "container")

    def __init__(self, resref: str, restype: int, offset: int, size: int, container):
        self.resref = resref
        self.restype = restype
        self.offset = offset
        self.size = size
        self.container = container

    @property
    def ext(self) -> str:
        return restype_name(self.restype)

    @property
    def name(self) -> str:
        return "%s.%s" % (self.resref, self.ext)

    def read(self) -> bytes:
        return self.container.read_resource(self)

    def __repr__(self) -> str:
        return "<Resource %s size=%d off=0x%X>" % (self.name, self.size, self.offset)


class Archive:
    """Common surface: .resources list, .get(name), .read(name)."""

    def __init__(self, path: str):
        self.path = path
        self.resources: List[Resource] = []
        self._index: Dict[Tuple[str, int], Resource] = {}

    def _build_index(self) -> None:
        self._index = {(r.resref.lower(), r.restype): r for r in self.resources}

    def get(self, name: str) -> Optional[Resource]:
        """name is 'resref.ext' or just 'resref' (first match)."""
        if "." in name:
            base, ext = name.rsplit(".", 1)
            t = EXT_TO_TYPE.get(ext.lower())
            if t is not None:
                return self._index.get((base.lower(), t))
        low = name.lower()
        for r in self.resources:
            if r.resref.lower() == low:
                return r
        return None

    def read(self, name: str) -> bytes:
        r = self.get(name)
        if r is None:
            raise KeyError("%s not in %s" % (name, self.path))
        return r.read()

    def read_resource(self, res: Resource) -> bytes:
        with open(self.path, "rb") as f:
            f.seek(res.offset)
            return f.read(res.size)

    def __iter__(self) -> Iterable[Resource]:
        return iter(self.resources)

    def __len__(self) -> int:
        return len(self.resources)


# --------------------------------------------------------------------------
# RIM
# --------------------------------------------------------------------------

class RIM(Archive):
    """
    'RIM V1.0'
      0x00 char[4] 'RIM '
      0x04 char[4] 'V1.0'
      0x08 uint32  reserved
      0x0C uint32  file count
      0x10 uint32  file table offset
      0x14 uint32  isExtension flag (1 for *_s.rim)
      0x18 ..0x78  reserved (100 bytes)
    entry (32 bytes): char[16] resref, uint32 type, uint32 index, uint32 offset, uint32 size
    """

    def __init__(self, path: str):
        Archive.__init__(self, path)
        with open(path, "rb") as f:
            hdr = f.read(0x78)
            if hdr[:4] != b"RIM ":
                raise ValueError("%s: not a RIM (magic %r)" % (path, hdr[:4]))
            self.version = hdr[4:8].decode("ascii")
            (self.reserved, count, table_off, self.is_extension) = struct.unpack_from("<IIII", hdr, 8)
            f.seek(table_off)
            tbl = f.read(32 * count)
        for i in range(count):
            resref, rtype, idx, off, size = struct.unpack_from("<16sIIII", tbl, 32 * i)
            self.resources.append(Resource(_cstr(resref), rtype, off, size, self))
        self._build_index()


# --------------------------------------------------------------------------
# ERF / MOD / SAV
# --------------------------------------------------------------------------

class ERF(Archive):
    """
    'ERF V1.0' / 'MOD V1.0' / 'SAV V1.0' / 'HAK V1.0'
      0x00 char[4] type, 0x04 char[4] version
      0x08 uint32 languageCount
      0x0C uint32 localizedStringSize
      0x10 uint32 entryCount
      0x14 uint32 offsetToLocalizedString
      0x18 uint32 offsetToKeyList
      0x1C uint32 offsetToResourceList
      0x20 uint32 buildYear (since 1900), 0x24 uint32 buildDay
      0x28 uint32 descriptionStrRef, 0x2C 116 bytes reserved   (header = 160 bytes)
    key entry      (24 bytes): char[16] resref, uint32 resId, uint16 resType, uint16 unused
    resource entry (8 bytes) : uint32 offset, uint32 size
    """

    def __init__(self, path: str):
        Archive.__init__(self, path)
        with open(path, "rb") as f:
            hdr = f.read(160)
            self.file_type = hdr[:4].decode("ascii", "replace")
            self.version = hdr[4:8].decode("ascii", "replace")
            if self.version.strip() != "V1.0":
                raise ValueError("%s: unsupported ERF version %r" % (path, self.version))
            (lang_count, locstr_size, entry_count, off_locstr,
             off_keys, off_res, byear, bday, self.description_strref) = struct.unpack_from("<9I", hdr, 8)
            self.build_year = 1900 + byear
            self.build_day = bday
            self.language_count = lang_count
            f.seek(off_keys)
            keys = f.read(24 * entry_count)
            f.seek(off_res)
            vals = f.read(8 * entry_count)
        for i in range(entry_count):
            resref, res_id, rtype, _unused = struct.unpack_from("<16sIHH", keys, 24 * i)
            off, size = struct.unpack_from("<II", vals, 8 * i)
            self.resources.append(Resource(_cstr(resref), rtype, off, size, self))
        self._build_index()


# --------------------------------------------------------------------------
# BIF
# --------------------------------------------------------------------------

class BIF(Archive):
    """
    'BIFFV1  '
      0x00 char[4] 'BIFF', 0x04 char[4] 'V1  '
      0x08 uint32 variableResourceCount
      0x0C uint32 fixedResourceCount
      0x10 uint32 variableTableOffset
    variable entry (16 bytes): uint32 id, uint32 offset, uint32 size, uint32 type
    The low 20 bits of `id` are the index within the BIF (matches the KEY's ResID).
    """

    def __init__(self, path: str):
        Archive.__init__(self, path)
        with open(path, "rb") as f:
            hdr = f.read(20)
            if hdr[:4] != b"BIFF":
                raise ValueError("%s: not a BIF" % path)
            var_count, fix_count, var_off = struct.unpack_from("<III", hdr, 8)
            f.seek(var_off)
            tbl = f.read(16 * var_count)
        self.by_index: Dict[int, Resource] = {}
        for i in range(var_count):
            rid, off, size, rtype = struct.unpack_from("<IIII", tbl, 16 * i)
            r = Resource("", rtype, off, size, self)
            self.resources.append(r)
            self.by_index[rid & 0xFFFFF] = r
        self.fixed_count = fix_count


# --------------------------------------------------------------------------
# KEY
# --------------------------------------------------------------------------

class KeyIndex:
    """
    'KEY V1  '
      0x00 char[4] 'KEY ', 0x04 char[4] 'V1  '
      0x08 uint32 bifCount
      0x0C uint32 keyCount
      0x10 uint32 offsetToFileTable
      0x14 uint32 offsetToKeyTable
      0x18 uint32 buildYear, 0x1C uint32 buildDay, 0x20 32 bytes reserved
    file entry (12 bytes): uint32 fileSize, uint32 filenameOffset, uint16 filenameSize, uint16 drives
    key  entry (22 bytes): char[16] resref, uint16 resType, uint32 resId
        resId >> 20      = index into the BIF file table
        resId & 0xFFFFF  = index of the resource inside that BIF
    """

    def __init__(self, path: str):
        self.path = path
        self.game_dir = os.path.dirname(os.path.abspath(path))
        with open(path, "rb") as f:
            data = f.read()
        if data[:4] != b"KEY ":
            raise ValueError("%s: not a KEY file" % path)
        self.version = data[4:8].decode("ascii")
        bif_count, key_count, off_files, off_keys, byear, bday = struct.unpack_from("<6I", data, 8)
        self.build_year = 1900 + byear
        self.build_day = bday

        self.bif_names: List[str] = []
        self.bif_sizes: List[int] = []
        for i in range(bif_count):
            fsize, name_off, name_len, drives = struct.unpack_from("<IIHH", data, off_files + 12 * i)
            self.bif_names.append(_cstr(data[name_off:name_off + name_len]).replace("\\", "/"))
            self.bif_sizes.append(fsize)

        # (resref_lower, restype) -> (bif_index, res_index)
        self.entries: Dict[Tuple[str, int], Tuple[int, int]] = {}
        self.order: List[Tuple[str, int, int, int]] = []
        for i in range(key_count):
            resref, rtype, rid = struct.unpack_from("<16sHI", data, off_keys + 22 * i)
            name = _cstr(resref)
            bif_i = rid >> 20
            res_i = rid & 0xFFFFF
            self.entries[(name.lower(), rtype)] = (bif_i, res_i)
            self.order.append((name, rtype, bif_i, res_i))

        self._bif_cache: Dict[int, BIF] = {}

    def bif(self, index: int) -> BIF:
        b = self._bif_cache.get(index)
        if b is None:
            b = BIF(os.path.join(self.game_dir, self.bif_names[index]))
            self._bif_cache[index] = b
        return b

    def locate(self, name: str) -> Optional[Tuple[str, int, int]]:
        """'resref.ext' -> (bif filename, bif index, resource index)."""
        base, ext = name.rsplit(".", 1) if "." in name else (name, "")
        t = EXT_TO_TYPE.get(ext.lower())
        if t is None:
            return None
        hit = self.entries.get((base.lower(), t))
        if hit is None:
            return None
        return (self.bif_names[hit[0]], hit[0], hit[1])

    def read(self, name: str) -> bytes:
        base, ext = name.rsplit(".", 1) if "." in name else (name, "")
        t = EXT_TO_TYPE.get(ext.lower())
        if t is None:
            raise KeyError("unknown extension in %r" % name)
        hit = self.entries.get((base.lower(), t))
        if hit is None:
            raise KeyError("%s not in %s" % (name, self.path))
        bif_i, res_i = hit
        b = self.bif(bif_i)
        r = b.by_index[res_i]
        return r.read()

    def find(self, pattern: str) -> List[Tuple[str, int, int, int]]:
        p = pattern.lower()
        return [e for e in self.order if p in e[0].lower()]


# --------------------------------------------------------------------------
# GFF V3.2
# --------------------------------------------------------------------------

GFF_BYTE, GFF_CHAR, GFF_WORD, GFF_SHORT, GFF_DWORD, GFF_INT = 0, 1, 2, 3, 4, 5
GFF_DWORD64, GFF_INT64, GFF_FLOAT, GFF_DOUBLE = 6, 7, 8, 9
GFF_CEXOSTRING, GFF_RESREF, GFF_CEXOLOCSTRING, GFF_VOID = 10, 11, 12, 13
GFF_STRUCT, GFF_LIST, GFF_ORIENTATION, GFF_VECTOR, GFF_STRREF = 14, 15, 16, 17, 18

GFF_TYPE_NAMES = {
    0: "BYTE", 1: "CHAR", 2: "WORD", 3: "SHORT", 4: "DWORD", 5: "INT",
    6: "DWORD64", 7: "INT64", 8: "FLOAT", 9: "DOUBLE", 10: "CExoString",
    11: "ResRef", 12: "CExoLocString", 13: "VOID", 14: "Struct", 15: "List",
    16: "Orientation", 17: "Vector", 18: "StrRef",
}


class LocString:
    __slots__ = ("strref", "substrings")

    def __init__(self, strref: int, substrings: List[Tuple[int, str]]):
        self.strref = strref
        self.substrings = substrings

    def text(self) -> str:
        if self.substrings:
            return self.substrings[0][1]
        return ""

    def __repr__(self) -> str:
        if self.substrings:
            return "LocString(strref=%d, %r)" % (self.strref, self.substrings[0][1])
        return "LocString(strref=%d)" % self.strref


class GFFStruct:
    """dict-like, but keeps insertion order and remembers the struct id."""

    __slots__ = ("struct_id", "fields", "types")

    def __init__(self, struct_id: int):
        self.struct_id = struct_id
        self.fields: Dict[str, Any] = {}
        self.types: Dict[str, int] = {}

    def __contains__(self, k: str) -> bool:
        return k in self.fields

    def __getitem__(self, k: str) -> Any:
        return self.fields[k]

    def get(self, k: str, default: Any = None) -> Any:
        return self.fields.get(k, default)

    def keys(self):
        return self.fields.keys()

    def items(self):
        return self.fields.items()

    def __repr__(self) -> str:
        return "<GFFStruct id=%d %d fields>" % (self.struct_id, len(self.fields))


class GFF:
    """
    header (56 bytes):
      char[4] fileType, char[4] fileVersion ('V3.2')
      uint32 structOffset, structCount
      uint32 fieldOffset,  fieldCount
      uint32 labelOffset,  labelCount
      uint32 fieldDataOffset, fieldDataCount   (bytes)
      uint32 fieldIndicesOffset, fieldIndicesCount (bytes)
      uint32 listIndicesOffset,  listIndicesCount  (bytes)
    struct (12): uint32 type, uint32 dataOrDataOffset, uint32 fieldCount
    field  (12): uint32 type, uint32 labelIndex, uint32 dataOrDataOffset
    label  (16): space/NUL padded ASCII
    """

    def __init__(self, data: bytes, name: str = "<gff>"):
        self.name = name
        self.data = data
        if len(data) < 56:
            raise ValueError("%s: too short for GFF" % name)
        self.file_type = data[:4].decode("ascii", "replace")
        self.version = data[4:8].decode("ascii", "replace")
        if self.version != "V3.2":
            raise ValueError("%s: unsupported GFF version %r" % (name, self.version))
        (self.struct_off, self.struct_count,
         self.field_off, self.field_count,
         self.label_off, self.label_count,
         self.fielddata_off, self.fielddata_count,
         self.fieldind_off, self.fieldind_count,
         self.listind_off, self.listind_count) = struct.unpack_from("<12I", data, 8)

        self.labels = [_cstr(data[self.label_off + 16 * i:self.label_off + 16 * i + 16]).rstrip()
                       for i in range(self.label_count)]
        self._struct_cache: Dict[int, GFFStruct] = {}
        self.root = self.struct_at(0)

    # -- raw accessors ------------------------------------------------
    def _struct_raw(self, i: int) -> Tuple[int, int, int]:
        return struct.unpack_from("<III", self.data, self.struct_off + 12 * i)

    def _field_raw(self, i: int) -> Tuple[int, int, int]:
        return struct.unpack_from("<III", self.data, self.field_off + 12 * i)

    def _field_indices(self, byte_off: int, count: int) -> List[int]:
        return list(struct.unpack_from("<%dI" % count, self.data, self.fieldind_off + byte_off))

    # -- decoding -----------------------------------------------------
    def struct_at(self, index: int) -> GFFStruct:
        cached = self._struct_cache.get(index)
        if cached is not None:
            return cached
        stype, data_or_off, fcount = self._struct_raw(index)
        s = GFFStruct(stype)
        self._struct_cache[index] = s
        if fcount == 1:
            idxs = [data_or_off]
        elif fcount == 0:
            idxs = []
        else:
            idxs = self._field_indices(data_or_off, fcount)
        for fi in idxs:
            ftype, label_i, dod = self._field_raw(fi)
            label = self.labels[label_i] if label_i < len(self.labels) else "?%d" % label_i
            s.fields[label] = self._field_value(ftype, dod)
            s.types[label] = ftype
        return s

    def _field_value(self, ftype: int, dod: int) -> Any:
        d = self.data
        if ftype == GFF_BYTE:
            return dod & 0xFF
        if ftype == GFF_CHAR:
            v = dod & 0xFF
            return v - 256 if v > 127 else v
        if ftype == GFF_WORD:
            return dod & 0xFFFF
        if ftype == GFF_SHORT:
            v = dod & 0xFFFF
            return v - 65536 if v > 32767 else v
        if ftype == GFF_DWORD:
            return dod
        if ftype == GFF_INT:
            return dod - 0x100000000 if dod > 0x7FFFFFFF else dod
        if ftype == GFF_FLOAT:
            return struct.unpack("<f", struct.pack("<I", dod))[0]

        base = self.fielddata_off + dod
        if ftype == GFF_DWORD64:
            return struct.unpack_from("<Q", d, base)[0]
        if ftype == GFF_INT64:
            return struct.unpack_from("<q", d, base)[0]
        if ftype == GFF_DOUBLE:
            return struct.unpack_from("<d", d, base)[0]
        if ftype == GFF_CEXOSTRING:
            n = struct.unpack_from("<I", d, base)[0]
            return d[base + 4:base + 4 + n].decode("windows-1252", "replace")
        if ftype == GFF_RESREF:
            n = d[base]
            return d[base + 1:base + 1 + n].decode("windows-1252", "replace")
        if ftype == GFF_CEXOLOCSTRING:
            _total, strref, count = struct.unpack_from("<III", d, base)
            p = base + 12
            subs: List[Tuple[int, str]] = []
            for _ in range(count):
                sid, slen = struct.unpack_from("<II", d, p)
                subs.append((sid, d[p + 8:p + 8 + slen].decode("windows-1252", "replace")))
                p += 8 + slen
            if strref == 0xFFFFFFFF:
                strref = -1
            return LocString(strref, subs)
        if ftype == GFF_VOID:
            n = struct.unpack_from("<I", d, base)[0]
            return d[base + 4:base + 4 + n]
        if ftype == GFF_ORIENTATION:
            return tuple(struct.unpack_from("<4f", d, base))
        if ftype == GFF_VECTOR:
            return tuple(struct.unpack_from("<3f", d, base))
        if ftype == GFF_STRREF:
            _size, ref = struct.unpack_from("<II", d, base)
            return ref

        if ftype == GFF_STRUCT:
            return self.struct_at(dod)
        if ftype == GFF_LIST:
            off = self.listind_off + dod
            n = struct.unpack_from("<I", d, off)[0]
            idxs = struct.unpack_from("<%dI" % n, d, off + 4) if n else ()
            return [self.struct_at(i) for i in idxs]
        raise ValueError("unknown GFF field type %d" % ftype)

    # -- convenience --------------------------------------------------
    def path(self, spec: str) -> Any:
        """Navigate 'A/B/3/C' where a number indexes into a list."""
        cur: Any = self.root
        if not spec:
            return cur
        for part in spec.replace("\\", "/").split("/"):
            if part == "":
                continue
            if isinstance(cur, list):
                cur = cur[int(part)]
            elif isinstance(cur, GFFStruct):
                cur = cur[part]
            else:
                raise KeyError("cannot descend into %r with %r" % (type(cur), part))
        return cur


def gff_to_text(node: Any, indent: int = 0, max_depth: int = 99,
                types: Optional[Dict[str, int]] = None, label: str = "") -> List[str]:
    """Human-readable recursive dump."""
    pad = "  " * indent
    out: List[str] = []
    if isinstance(node, GFFStruct):
        out.append("%s%s[struct id=%d, %d fields]" % (pad, (label + " ") if label else "", node.struct_id, len(node.fields)))
        if indent >= max_depth:
            return out
        for k, v in node.fields.items():
            t = GFF_TYPE_NAMES.get(node.types.get(k, -1), "?")
            if isinstance(v, (GFFStruct, list)):
                out.extend(gff_to_text(v, indent + 1, max_depth, label="%s (%s)" % (k, t)))
            else:
                out.append("%s  %-24s %-14s %s" % (pad, k, t, _fmt_scalar(v)))
    elif isinstance(node, list):
        out.append("%s%s%d entries" % (pad, (label + " = ") if label else "list = ", len(node)))
        if indent >= max_depth:
            return out
        for i, item in enumerate(node):
            out.extend(gff_to_text(item, indent + 1, max_depth, label="[%d]" % i))
    else:
        out.append("%s%s%s" % (pad, (label + " = ") if label else "", _fmt_scalar(node)))
    return out


def _fmt_scalar(v: Any) -> str:
    if isinstance(v, float):
        return "%.6g" % v
    if isinstance(v, bytes):
        return "<%d bytes> %s" % (len(v), v[:24].hex())
    if isinstance(v, LocString):
        return repr(v)
    if isinstance(v, tuple):
        return "(" + ", ".join("%.6g" % x for x in v) + ")"
    return str(v)


# --------------------------------------------------------------------------
# 2DA
# --------------------------------------------------------------------------

class TwoDA:
    """
    ASCII  '2DA V2.0\\n' : blank line, TAB-separated header row, then data rows whose
                          first token is the row label. Quoted values may contain spaces.
    Binary '2DA V2.b\\n' : TAB-terminated column labels then NUL;
                          uint32 rowCount;
                          TAB-terminated row labels;
                          rowCount*colCount uint16 offsets into the data block;
                          uint16 dataBlockSize;
                          NUL-terminated strings.
    Empty cells are the literal '****' in ASCII and an empty string in binary.
    """

    def __init__(self, data: bytes, name: str = "<2da>"):
        self.name = name
        self.columns: List[str] = []
        self.row_labels: List[str] = []
        self.rows: List[List[str]] = []
        magic = data[:8]
        if magic == b"2DA V2.b":
            self._parse_binary(data)
        elif magic == b"2DA V2.0":
            self._parse_ascii(data)
        else:
            raise ValueError("%s: not a 2DA (magic %r)" % (name, magic))
        self._colidx = {c.lower(): i for i, c in enumerate(self.columns)}

    # -- parsing ------------------------------------------------------
    def _parse_binary(self, data: bytes) -> None:
        p = 9  # past '2DA V2.b\n'
        end = data.index(b"\x00", p)
        hdr = data[p:end].decode("windows-1252", "replace")
        self.columns = [c for c in hdr.split("\t") if c != ""]
        p = end + 1
        row_count = struct.unpack_from("<I", data, p)[0]
        p += 4
        for _ in range(row_count):
            e = data.index(b"\t", p)
            self.row_labels.append(data[p:e].decode("windows-1252", "replace"))
            p = e + 1
        ncols = len(self.columns)
        offsets = struct.unpack_from("<%dH" % (row_count * ncols), data, p)
        p += 2 * row_count * ncols
        _blocksize = struct.unpack_from("<H", data, p)[0]
        p += 2
        block = p
        for r in range(row_count):
            row = []
            for c in range(ncols):
                o = block + offsets[r * ncols + c]
                e = data.index(b"\x00", o)
                row.append(data[o:e].decode("windows-1252", "replace"))
            self.rows.append(row)

    def _parse_ascii(self, data: bytes) -> None:
        text = data.decode("windows-1252", "replace").replace("\r\n", "\n")
        lines = text.split("\n")
        # line 0 = magic, line 1 = usually blank / DEFAULT:
        i = 1
        while i < len(lines) and lines[i].strip() == "":
            i += 1
        if lines[i].upper().startswith("DEFAULT:"):
            i += 1
            while i < len(lines) and lines[i].strip() == "":
                i += 1
        self.columns = _split_2da(lines[i])
        for ln in lines[i + 1:]:
            if ln.strip() == "":
                continue
            toks = _split_2da(ln)
            if not toks:
                continue
            self.row_labels.append(toks[0])
            vals = toks[1:]
            vals += [""] * (len(self.columns) - len(vals))
            self.rows.append(["" if v == "****" else v for v in vals[:len(self.columns)]])

    # -- access -------------------------------------------------------
    @property
    def row_count(self) -> int:
        return len(self.rows)

    @property
    def col_count(self) -> int:
        return len(self.columns)

    def col_index(self, col: str) -> int:
        i = self._colidx.get(col.lower())
        if i is None:
            raise KeyError("%s: no column %r (have %s)" % (self.name, col, ", ".join(self.columns)))
        return i

    def get(self, row: int, col: str) -> str:
        return self.rows[row][self.col_index(col)]

    def row_dict(self, row: int) -> Dict[str, str]:
        return dict(zip(self.columns, self.rows[row]))

    def find_rows(self, col: str, value: str) -> List[int]:
        ci = self.col_index(col)
        v = value.lower()
        return [i for i, r in enumerate(self.rows) if r[ci].lower() == v]


def _split_2da(line: str) -> List[str]:
    """Whitespace-split honouring double quotes (ASCII 2DA allows quoted cells)."""
    out: List[str] = []
    cur = ""
    inq = False
    for ch in line:
        if ch == '"':
            inq = not inq
        elif ch in " \t" and not inq:
            if cur != "":
                out.append(cur)
                cur = ""
        else:
            cur += ch
    if cur != "":
        out.append(cur)
    return out


# --------------------------------------------------------------------------
# TLK
# --------------------------------------------------------------------------

class TLK:
    """
    'TLK V3.0': uint32 languageId, uint32 stringCount, uint32 stringEntriesOffset,
    then stringCount * 40-byte entries:
      uint32 flags, char[16] soundResRef, uint32 volumeVariance, uint32 pitchVariance,
      uint32 offsetToString, uint32 stringSize, float soundLength
    """

    def __init__(self, path: str):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()
        if self.data[:4] != b"TLK ":
            raise ValueError("%s: not a TLK" % path)
        self.language_id, self.string_count, self.entries_off = struct.unpack_from("<III", self.data, 8)

    def get(self, strref: int) -> str:
        if strref < 0 or strref >= self.string_count:
            return ""
        base = 20 + 40 * strref
        flags, _snd, _vv, _pv, off, size, _slen = struct.unpack_from("<I16sIIIIf", self.data, base)
        if not (flags & 1):
            return ""
        s = self.entries_off + off
        return self.data[s:s + size].decode("windows-1252", "replace")


# --------------------------------------------------------------------------
# Game -- convenience resolver across Override / modules / KEY
# --------------------------------------------------------------------------

class Game:
    def __init__(self, game_dir: str = DEFAULT_GAME_DIR):
        self.dir = game_dir
        self._key: Optional[KeyIndex] = None
        self._tlk: Optional[TLK] = None
        self._mods: Dict[str, List[Archive]] = {}

    @property
    def key(self) -> KeyIndex:
        if self._key is None:
            self._key = KeyIndex(os.path.join(self.dir, "chitin.key"))
        return self._key

    @property
    def tlk(self) -> TLK:
        if self._tlk is None:
            self._tlk = TLK(os.path.join(self.dir, "dialog.tlk"))
        return self._tlk

    def module(self, name: str) -> List[Archive]:
        """Every archive that makes up a module, in search order (.rim then _s.rim)."""
        name = name.lower()
        if name in self._mods:
            return self._mods[name]
        out: List[Archive] = []
        for suffix, cls in ((".rim", RIM), ("_s.rim", RIM), (".mod", ERF), ("_dlg.erf", ERF)):
            p = os.path.join(self.dir, "modules", name + suffix)
            if os.path.exists(p):
                out.append(cls(p))
        self._mods[name] = out
        return out

    def module_read(self, name: str, resname: str) -> bytes:
        for a in self.module(name):
            r = a.get(resname)
            if r is not None:
                return r.read()
        raise KeyError("%s not found in module %s" % (resname, name))

    def read(self, resname: str) -> bytes:
        """Override -> chitin.key (matching the engine's own precedence, roughly)."""
        p = os.path.join(self.dir, "Override", resname)
        if os.path.exists(p):
            with open(p, "rb") as f:
                return f.read()
        return self.key.read(resname)

    def twoda(self, name: str) -> TwoDA:
        if not name.endswith(".2da"):
            name += ".2da"
        return TwoDA(self.read(name), name)

    def gff(self, resname: str) -> GFF:
        return GFF(self.read(resname), resname)

    def strref(self, ref: int) -> str:
        return self.tlk.get(ref)


# --------------------------------------------------------------------------
# object-id prediction
# --------------------------------------------------------------------------

# The order in which CSWSArea::LoadGIT (0x0050DD80) calls its per-list loaders.
# Read straight out of swkotor.exe: LoadCreatures, LoadItems, LoadDoors,
# LoadTriggers, LoadEncounters, LoadWaypoints, LoadSounds, LoadPlaceables,
# LoadStores, LoadAreaEffects, LoadProperties, LoadMaps, LoadPlaceableCameras.
# Only the ones that create objects with ids are listed, paired with the
# blueprint extension whose Equip_ItemList/ItemList decide how many extra ids
# the object's contents consume.
AREA_LOAD_ORDER: List[Tuple[str, str]] = [
    ("Creature List", "utc"),
    ("List", "uti"),            # loose items lying in the area
    ("Door List", "utd"),
    ("TriggerList", "utt"),
    ("Encounter List", "ute"),
    ("WaypointList", "utw"),
    ("SoundList", "uts"),
    ("Placeable List", "utp"),
    ("StoreList", "utm"),
]

# Object 0 is the module and object 1 is the area, so the first creature in the
# GIT gets id 2. (Verified on end_m01aa: creature[0] == 0x2, and every saved
# creature reports AreaId == 1.)
FIRST_GIT_OBJECT_ID = 2


def predict_object_ids(game: "Game", module: str,
                       first_id: int = FIRST_GIT_OBJECT_ID) -> List[Dict[str, Any]]:
    """
    Reproduce, offline, the object ids the engine hands out when it loads a
    module's GIT for the first time.

    The rule, verified against a real savegame for end_m01aa (217/217 exact):
      * lists are visited in AREA_LOAD_ORDER, entries in GIT file order;
      * each entry takes the next id, then its blueprint's owned items
        (Equip_ItemList + ItemList) take the ids immediately after it.

    Returns one dict per GIT entry: list, index, resref, object_id, item_ids.
    """
    git = None
    for a in game.module(module):
        for r in a:
            if r.ext == "git":
                git = GFF(r.read(), r.name)
                break
        if git is not None:
            break
    if git is None:
        raise KeyError("no .git in module %s" % module)

    def owned_items(resref: str, ext: str) -> int:
        if not resref:
            return 0
        for src in (lambda: game.module_read(module, resref + "." + ext),
                    lambda: game.read(resref + "." + ext)):
            try:
                t = GFF(src(), resref)
            except Exception:
                continue
            return len(t.root.get("Equip_ItemList", [])) + len(t.root.get("ItemList", []))
        return 0

    out: List[Dict[str, Any]] = []
    nid = first_id
    for list_name, ext in AREA_LOAD_ORDER:
        for i, s in enumerate(git.root.get(list_name, [])):
            rr = s.get("TemplateResRef", "")
            n = owned_items(rr, ext)
            out.append({
                "list": list_name, "index": i, "resref": rr,
                "object_id": nid, "item_ids": list(range(nid + 1, nid + 1 + n)),
                "x": s.get("XPosition", s.get("X", 0.0)),
                "y": s.get("YPosition", s.get("Y", 0.0)),
                "tag": s.get("Tag", ""),
            })
            nid += 1 + n
    return out


# --------------------------------------------------------------------------
# generic "source" resolution for the CLI
# --------------------------------------------------------------------------

def open_archive(path: str) -> Archive:
    low = path.lower()
    if low.endswith(".rim"):
        return RIM(path)
    with open(path, "rb") as f:
        magic = f.read(4)
    if magic == b"RIM ":
        return RIM(path)
    return ERF(path)


def load_source(source: str, game: Game) -> Tuple[bytes, str]:
    """
    'path/to/file'                 -> raw file
    'path/to/archive.rim:res.ext'  -> archive member
    'module_name:res.ext'          -> member of a module in modules/
    'res.ext'                      -> looked up via Override then chitin.key
    """
    if ":" in source:
        head, tail = source.rsplit(":", 1)
        if len(head) > 1 and not (len(head) == 1 or head[1] == ":" and "/" not in head and "\\" not in head):
            pass
        # Windows drive letters: 'C:/x' -- only treat as archive:member if the tail
        # looks like a resref and the head is longer than a drive letter.
        if len(head) > 2 or "/" in head or "\\" in head:
            if os.path.exists(head):
                return open_archive(head).read(tail), tail
            mods = game.module(head)
            if mods:
                return game.module_read(head, tail), tail
    if os.path.exists(source):
        with open(source, "rb") as f:
            return f.read(), os.path.basename(source)
    return game.read(source), source


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def _cmd_key(args) -> int:
    g = Game(args.game)
    k = g.key
    print("%s  version=%s  built %d day %d" % (k.path, k.version, k.build_year, k.build_day))
    print("%d BIFs, %d key entries" % (len(k.bif_names), len(k.order)))
    if args.bifs:
        counts: Dict[int, int] = {}
        for _n, _t, bi, _ri in k.order:
            counts[bi] = counts.get(bi, 0) + 1
        print()
        print("%-4s %-28s %12s %8s" % ("idx", "bif", "bytes", "entries"))
        for i, n in enumerate(k.bif_names):
            print("%-4d %-28s %12d %8d" % (i, n, k.bif_sizes[i], counts.get(i, 0)))
    if args.find:
        hits = k.find(args.find)
        print("\n%d match(es) for %r" % (len(hits), args.find))
        for name, t, bi, ri in hits[:args.limit]:
            print("  %-18s %-6s bif=%-24s index=%d" % (name, restype_name(t), k.bif_names[bi], ri))
    return 0


def _cmd_list(args) -> int:
    a = open_archive(args.archive)
    kind = type(a).__name__
    print("%s  (%s, %d resources)" % (args.archive, kind, len(a)))
    if isinstance(a, RIM):
        print("  isExtension=%d" % a.is_extension)
    if isinstance(a, ERF):
        print("  type=%s built %d day %d descStrRef=%d" % (a.file_type, a.build_year, a.build_day, a.description_strref))
    print()
    print("%-5s %-18s %-8s %10s %12s" % ("#", "resref", "type", "size", "offset"))
    n = 0
    for i, r in enumerate(a):
        if args.type and r.ext.lower() != args.type.lower().lstrip("."):
            continue
        print("%-5d %-18s %-8s %10d 0x%010X" % (i, r.resref, r.ext, r.size, r.offset))
        n += 1
    print("\n%d shown" % n)
    return 0


def _cmd_gff(args) -> int:
    g = Game(args.game)
    data, name = load_source(args.source, g)
    gf = GFF(data, name)
    print("%s  type=%r version=%s  structs=%d fields=%d labels=%d" %
          (name, gf.file_type, gf.version, gf.struct_count, gf.field_count, gf.label_count))
    node = gf.path(args.path) if args.path else gf.root
    print()
    for line in gff_to_text(node, 0, args.max_depth, label=args.path or ""):
        print(line)
    return 0


def _cmd_2da(args) -> int:
    g = Game(args.game)
    data, name = load_source(args.source, g)
    t = TwoDA(data, name)
    print("%s  %d rows x %d columns" % (name, t.row_count, t.col_count))
    cols = [c.strip() for c in args.cols.split(",")] if args.cols else t.columns
    lo, hi = 0, t.row_count
    if args.rows:
        a, _, b = args.rows.partition(":")
        lo = int(a) if a else 0
        hi = int(b) if b else (lo + 1 if not _ else t.row_count)
    wcol = wval = None
    if args.where:
        wcol, _, wval = args.where.partition("=")
    print()
    print("columns: " + ", ".join(t.columns))
    print()
    widths = [max(len(c), 10) for c in cols]
    print("%-6s " % "row" + "  ".join(c.ljust(w) for c, w in zip(cols, widths)))
    for i in range(max(0, lo), min(hi, t.row_count)):
        if wcol and t.get(i, wcol).lower() != wval.lower():
            continue
        vals = [t.get(i, c) for c in cols]
        print("%-6s " % t.row_labels[i] + "  ".join(v.ljust(w) for v, w in zip(vals, widths)))
    return 0


def _cmd_extract(args) -> int:
    g = Game(args.game)
    data, _name = load_source(args.source, g)
    with open(args.out, "wb") as f:
        f.write(data)
    print("wrote %d bytes to %s" % (len(data), args.out))
    return 0


def _cmd_tlk(args) -> int:
    g = Game(args.game)
    print(repr(g.strref(args.strref)))
    return 0


def _cmd_creatures(args) -> int:
    """Dump a module GIT's Creature List in file order -- the ordering that decides
    the engine's object-id assignment."""
    g = Game(args.game)
    data = name = None
    if os.path.exists(args.module):
        arcs: List[Archive] = [open_archive(args.module)]
    else:
        arcs = g.module(args.module)
    for a in arcs:
        for r in a:
            if r.ext == "git":
                data, name = r.read(), r.name
                break
        if data is not None:
            break
    if data is None:
        raise SystemExit("no .git resource in module %s" % args.module)
    gf = GFF(data, name)
    lst = gf.root.get("Creature List", [])
    print("%s  Creature List: %d entries" % (name, len(lst)))
    print("%-4s %-18s %-10s %-10s %-10s %-8s" % ("idx", "TemplateResRef", "X", "Y", "Z", "Bearing"))
    for i, c in enumerate(lst):
        print("%-4d %-18s %-10.4f %-10.4f %-10.4f %-8.4f" % (
            i, c.get("TemplateResRef", ""), c.get("XPosition", 0.0),
            c.get("YPosition", 0.0), c.get("ZPosition", 0.0), c.get("XOrientation", 0.0)))
    for key in ("Door List", "Placeable List", "TriggerList", "Encounter List",
                "WaypointList", "SoundList", "StoreList", "List"):
        v = gf.root.get(key)
        if isinstance(v, list):
            print("%-16s %d" % (key, len(v)))
    return 0


def _cmd_objectids(args) -> int:
    g = Game(args.game)
    rows = predict_object_ids(g, args.module, args.first_id)
    actual: Dict[Tuple[str, Tuple[float, float]], List[int]] = {}
    if args.verify:
        e = ERF(args.verify)
        sg = None
        for r in e:
            if r.ext == "git":
                sg = GFF(r.read(), r.name)
        if sg is None:
            raise SystemExit("%s has no .git" % args.verify)
        for L, _ in AREA_LOAD_ORDER:
            for s in sg.root.get(L, []):
                k = (L, (round(s.get("XPosition", s.get("X", 0.0)), 3),
                         round(s.get("YPosition", s.get("Y", 0.0)), 3)))
                actual.setdefault(k, []).append(s.get("ObjectId", -1))
    print("%-16s %-4s %-18s %-8s %-6s %s" % ("list", "idx", "resref", "id", "items", "saved id" if actual else ""))
    ok = bad = 0
    for r in rows:
        note = ""
        if actual:
            a = actual.get((r["list"], (round(r["x"], 3), round(r["y"], 3))))
            if a:
                v = a.pop(0)
                note = "0x%X %s" % (v, "OK" if v == r["object_id"] else "MISMATCH")
                ok += v == r["object_id"]
                bad += v != r["object_id"]
            else:
                note = "(not in save)"
        print("%-16s %-4d %-18s 0x%-6X %-6d %s" % (r["list"], r["index"], r["resref"],
                                                   r["object_id"], len(r["item_ids"]), note))
    if actual:
        print("\nmatched=%d mismatched=%d" % (ok, bad))
    return 0


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(prog="kotorfiles.py", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=DEFAULT_GAME_DIR, help="game install directory")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("key", help="summarise chitin.key")
    p.add_argument("--bifs", action="store_true", help="list the BIF file table")
    p.add_argument("--find", help="substring match on resref")
    p.add_argument("--limit", type=int, default=60)
    p.set_defaults(func=_cmd_key)

    p = sub.add_parser("list", help="list an archive (.rim/.erf/.mod)")
    p.add_argument("archive")
    p.add_argument("--type", help="filter by extension")
    p.set_defaults(func=_cmd_list)

    p = sub.add_parser("gff", help="dump a GFF as text")
    p.add_argument("source")
    p.add_argument("--path", default="", help="'Creature List/0' style sub-path")
    p.add_argument("--max-depth", type=int, default=99)
    p.set_defaults(func=_cmd_gff)

    p = sub.add_parser("2da", help="dump a 2DA")
    p.add_argument("source")
    p.add_argument("--rows", help="LO:HI slice")
    p.add_argument("--cols", help="comma-separated column subset")
    p.add_argument("--where", help="col=value filter")
    p.set_defaults(func=_cmd_2da)

    p = sub.add_parser("extract", help="write a resource to a file")
    p.add_argument("source")
    p.add_argument("--out", required=True)
    p.set_defaults(func=_cmd_extract)

    p = sub.add_parser("tlk", help="resolve a StrRef from dialog.tlk")
    p.add_argument("strref", type=int)
    p.set_defaults(func=_cmd_tlk)

    p = sub.add_parser("creatures", help="dump a module GIT Creature List in file order")
    p.add_argument("module")
    p.set_defaults(func=_cmd_creatures)

    p = sub.add_parser("objectids", help="predict engine object ids for a module's GIT, offline")
    p.add_argument("module")
    p.add_argument("--first-id", type=int, default=FIRST_GIT_OBJECT_ID)
    p.add_argument("--verify", help="path to a savegame module .sav (ERF) to check against")
    p.set_defaults(func=_cmd_objectids)

    args = ap.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
