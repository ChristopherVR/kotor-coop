#!/usr/bin/env python3
"""
localvaraudit.py -- content audit for per-object local variables (SetLocalBoolean/SetLocalNumber).

  python tools/localvaraudit.py                      # summary + candidate table
  python tools/localvaraudit.py --module danm13      # one module only
  python tools/localvaraudit.py --json out.json      # every access, machine-readable
  python tools/localvaraudit.py --script k_pdan_...  # dump one script's recovered accesses

Why a symbolic interpreter and not the byte scan quest-sync.md §2 used: a local slot has no
name. `SetLocalBoolean(oTarget, 3, TRUE)` is only meaningful once you know WHICH object oTarget
is and that 3 is a constant, and the stock scripts almost never spell either inline -- they go
through k_inc_utility's UT_SetPlotBooleanFlag(oTarget, nIndex, nState) wrapper, so the
engine call sits inside a subroutine whose arguments were pushed by the caller. A byte scan
sees routine 680 and nothing else.

So this walks every compiled script with a small abstract interpreter over the NCS stack:
constants, OBJECT_SELF, GetObjectByTag("tag") results and copies of them are tracked through
CPTOPSP/CPDOWNSP, subroutine calls (JSR, analysed with the caller's actual stack), globals
(SAVEBP/CPTOPBP) and deferred actions (STORE_STATE closures, e.g. DelayCommand(...)); anything
else joins to "unknown". Stack effects of every ACTION come from the game's own nwscript.nss
(scripts.bif), so the stack stays aligned across engine calls. It is still a static
approximation: a value computed at run time (a loop counter, a random roll) is reported as
unknown, never guessed.

Owners (what OBJECT_SELF is when a script runs) come from the module's own data: the event
fields of every GIT object's blueprint (utc/utd/utp/utt/utw/uts/ute/utm), the conversation each
of them names (a .dlg node's Script/Active runs with OBJECT_SELF = the conversation owner), the
area (.are) and module (.ifo) event fields.

Read-only; nothing is written to the game directory. Depends only on tools/kotorfiles.py.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys
from collections import defaultdict
from typing import Any, Dict, List, Optional, Set, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kotorfiles as kf  # noqa: E402

GET_LOCAL_BOOLEAN = 679
SET_LOCAL_BOOLEAN = 680
GET_LOCAL_NUMBER = 681
SET_LOCAL_NUMBER = 682
LOCAL_ROUTINES = {GET_LOCAL_BOOLEAN, SET_LOCAL_BOOLEAN, GET_LOCAL_NUMBER, SET_LOCAL_NUMBER}

# k_inc_utility.nss / k_inc_generic.nss: boolean slots 0..9 are SW_PLOT_BOOLEAN_01..10, 10 is
# SW_PLOT_HAS_TALKED_TO, 11..19 the SW_PLOT_COMPUTER_*/REPAIR_* plot slots; 20 and up are the
# generic AI's own SW_FLAG_* bookkeeping (event masks, ambient/formation state, AI off, boss AI,
# shield used, ...). The AI range is per-engine scratch by construction.
AI_FLAG_FIRST = 20


# --------------------------------------------------------------------------
# nwscript.nss -> per-routine stack effect
# --------------------------------------------------------------------------

_TYPE_SLOTS = {"void": 0, "vector": 3, "action": 0}
_PROTO = re.compile(r"^\s*(\w+)\s+(\w+)\s*\(([^)]*)\)\s*;", re.M)


def load_signatures(game: kf.Game) -> List[Tuple[str, str, List[str]]]:
    """(return type, name, [param types]) in declaration order == routine number."""
    text = game.read("nwscript.nss").decode("windows-1252", "replace")
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    out = []
    for m in _PROTO.finditer(text):
        ret, name, params = m.group(1), m.group(2), m.group(3).strip()
        if ret in ("return", "if", "while", "else"):
            continue
        ptypes = []
        if params and params != "void":
            for p in params.split(","):
                p = p.strip().split("=")[0].strip()
                ptypes.append(p.split()[0] if p else "int")
        out.append((ret, name, ptypes))
    return out


# --------------------------------------------------------------------------
# NCS decoding (mirrors src/KotorCoop.Memory/GameFiles/NcsReader.cs)
# --------------------------------------------------------------------------

class Insn:
    __slots__ = ("pc", "op", "t", "n", "a", "b", "c", "s")

    def __init__(self, pc, op, t, n, a=None, b=None, c=None, s=None):
        self.pc, self.op, self.t, self.n, self.a, self.b, self.c, self.s = pc, op, t, n, a, b, c, s


def decode(data: bytes) -> Dict[int, Insn]:
    if data[:8] != b"NCS V1.0" or data[8] != 0x42:
        raise ValueError("not NCS")
    out: Dict[int, Insn] = {}
    i = 13
    while i < len(data):
        pc = i
        op = data[i]
        t = data[i + 1] if i + 1 < len(data) else 0
        i += 2
        if op in (0x1D, 0x1E, 0x1F, 0x25):
            (off,) = struct.unpack_from(">i", data, i); i += 4
            out[pc] = Insn(pc, op, t, i - pc, a=pc + off)
        elif op in (0x1B, 0x23, 0x24, 0x28, 0x29):
            (off,) = struct.unpack_from(">i", data, i); i += 4
            out[pc] = Insn(pc, op, t, i - pc, a=off)
        elif op in (0x01, 0x03, 0x26, 0x27):
            off, size = struct.unpack_from(">iH", data, i); i += 6
            out[pc] = Insn(pc, op, t, i - pc, a=off, b=size)
        elif op == 0x21:
            size, keep_off, keep_size = struct.unpack_from(">hhh", data, i); i += 6
            out[pc] = Insn(pc, op, t, i - pc, a=size, b=keep_off, c=keep_size)
        elif op == 0x04:
            if t == 0x05:
                (ln,) = struct.unpack_from(">H", data, i); i += 2
                s = data[i:i + ln].decode("windows-1252", "replace"); i += ln
                out[pc] = Insn(pc, op, t, i - pc, s=s)
            elif t == 0x04:
                (f,) = struct.unpack_from(">f", data, i); i += 4
                out[pc] = Insn(pc, op, t, i - pc, a=f)
            else:
                (v,) = struct.unpack_from(">i", data, i); i += 4
                out[pc] = Insn(pc, op, t, i - pc, a=v)
        elif op == 0x05:
            routine, argc = struct.unpack_from(">HB", data, i); i += 3
            out[pc] = Insn(pc, op, t, i - pc, a=routine, b=argc)
        elif op in (0x0B, 0x0C) and t == 0x24:
            (size,) = struct.unpack_from(">H", data, i); i += 2
            out[pc] = Insn(pc, op, t, i - pc, a=size)
        elif op == 0x2C:
            bpsz, spsz = struct.unpack_from(">ii", data, i); i += 8
            out[pc] = Insn(pc, op, t, i - pc, a=bpsz, b=spsz)
        else:
            out[pc] = Insn(pc, op, t, i - pc)
    return out


# --------------------------------------------------------------------------
# abstract interpretation
# --------------------------------------------------------------------------

UNK = None
SELF = ("self",)
INVALID = ("invalid",)

# Object-returning routines whose result identity matters for the audit.
_TAG_LOOKUPS = {"GetObjectByTag", "GetNearestObjectByTag"}
_PC_LIKE = {"GetFirstPC", "GetNextPC", "GetPCSpeaker", "GetEnteringObject", "GetExitingObject",
            "GetLastOpenedBy", "GetLastUsedBy", "GetLastClosedBy", "GetLastSpeaker",
            "GetPartyMemberByIndex", "GetLastPerceived", "GetLastAttacker", "GetLastHostileActor",
            "GetLastKiller", "GetLastDisturbed", "GetLastLocked", "GetLastUnlocked",
            "GetItemActivator", "GetClickingObject", "GetLastItemActivated"}


def join_val(a, b, pc, i):
    # Deterministic per (join point, slot) so the fixpoint converges.
    return a if a == b else ("u", "join", pc, i)


def is_unknown(v) -> bool:
    return v is None or v[0] in ("u", "nz")


class Budget(Exception):
    pass


class Interp:
    def __init__(self, insns: Dict[int, Insn], sigs, script: str, sink: List[dict]):
        self.insns = insns
        self.sigs = sigs
        self.script = script
        self.sink = sink
        self.steps = 0
        self.memo: Dict[Tuple, Optional[Tuple]] = {}
        self.seen_access: Set[Tuple] = set()
        self.closures_done: Set[int] = set()
        self.depth_conflicts = 0

    # returns the joined stack/bp at RETN, or None if the function never returns
    def run_function(self, entry: int, stack: Tuple, bp: int, depth: int) -> Optional[Tuple[Tuple, int]]:
        key = (entry, stack, bp)
        if key in self.memo:
            return self.memo[key]
        if depth > 24:
            raise Budget("depth")
        self.memo[key] = None  # recursion guard
        states: Dict[int, Tuple[Tuple, int]] = {entry: (stack, bp)}
        work = [entry]
        exit_state: Optional[Tuple[Tuple, int]] = None
        while work:
            pc = work.pop()
            st = states[pc]
            for nxt, nst in self.step(pc, st, depth):
                if nxt == -1:  # RETN
                    exit_state = nst if exit_state is None else self.join(exit_state, nst, -1)
                    continue
                old = states.get(nxt)
                if old is None:
                    states[nxt] = nst
                    work.append(nxt)
                else:
                    j = self.join(old, nst, nxt)
                    if j != old:
                        states[nxt] = j
                        work.append(nxt)
        self.memo[key] = exit_state
        return exit_state

    def join(self, a, b, pc):
        (sa, ba), (sb, bb) = a, b
        if len(sa) != len(sb):
            # Only an infeasible path can arrive with a different depth (compiled NWScript
            # has one stack depth per instruction); keep what was already there.
            self.depth_conflicts += 1
            return a
        return (tuple(join_val(x, y, pc, i) for i, (x, y) in enumerate(zip(sa, sb))), ba)

    def step(self, pc: int, st, depth: int):
        self.steps += 1
        if self.steps > 400000:
            raise Budget("steps")
        ins = self.insns.get(pc)
        if ins is None:
            return []
        stack, bp = list(st[0]), st[1]
        op, t = ins.op, ins.t
        nxt = pc + ins.n

        def pop(k):
            nonlocal stack
            if k <= 0:
                return []
            if k > len(stack):
                raise Budget("underflow")
            v = stack[-k:]
            del stack[-k:]
            return v

        if op == 0x01:  # CPDOWNSP
            n = ins.b // 4
            start = len(stack) + ins.a // 4
            src = stack[-n:]
            if 0 <= start and start + n <= len(stack):
                stack[start:start + n] = src
        elif op == 0x02:  # RSADD
            stack.append(("int", 0) if t == 3 else ("u", pc, 0))
        elif op == 0x03:  # CPTOPSP
            n = ins.b // 4
            start = len(stack) + ins.a // 4
            stack.extend(stack[start:start + n] if start >= 0 else [("u", pc, k) for k in range(n)])
        elif op == 0x04:
            if t == 0x03:
                stack.append(("int", ins.a))
            elif t == 0x05:
                stack.append(("str", ins.s))
            elif t == 0x06:
                stack.append(SELF if ins.a == 0 else INVALID)
            else:
                stack.append(("u", pc, 0))
        elif op == 0x05:  # ACTION
            routine, argc = ins.a, ins.b
            ret, name, ptypes = self.sigs[routine]
            sizes = [_TYPE_SLOTS.get(p, 1) for p in ptypes[:argc]]
            # args: first parameter is on top
            args = []
            for sz in sizes:
                v = pop(sz)
                args.append(v[-1] if sz == 1 else None)
            if routine in LOCAL_ROUTINES:
                self.record(pc, routine, args)
            rs = _TYPE_SLOTS.get(ret, 1)
            if rs == 1:
                if name in _TAG_LOOKUPS and args and args[0] is not None and args[0][0] == "str":
                    stack.append(("tag", args[0][1].lower()))
                elif name in _PC_LIKE:
                    stack.append(("pc", name))
                elif name == "GetModule":
                    stack.append(("module",))
                elif name == "GetArea":
                    stack.append(("area",))
                elif ret == "object":
                    stack.append(("obj?", name))
                else:
                    stack.append(("u", pc, 0))
            else:
                stack.extend([("u", pc, k) for k in range(rs)])
        elif op in (0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12,
                    0x13, 0x14, 0x15, 0x16, 0x17, 0x18):
            if t == 0x24:
                pop(2 * (ins.a // 4)); stack.append(("u", pc, 0))
            elif t == 0x3A:
                pop(6); stack.extend([("u", pc, k) for k in range(1 if op in (0x0B, 0x0C) else 3)])
            elif t in (0x3B, 0x3C):
                pop(4); stack.extend([("u", pc, k) for k in range(3)])
            else:
                pop(2); stack.append(("u", pc, 0))
        elif op in (0x19, 0x1A, 0x22):
            v = pop(1)[0]
            if op == 0x22 and v is not None and v[0] == "int":
                stack.append(("int", 0 if v[1] else 1))
            elif op == 0x19 and v is not None and v[0] == "int":
                stack.append(("int", -v[1]))
            else:
                stack.append(("u", pc, 0))
        elif op == 0x1B:  # MOVSP
            pop(-ins.a // 4)
        elif op == 0x1D:  # JMP
            return [(ins.a, (tuple(stack), bp))]
        elif op == 0x1E:  # JSR
            res = self.run_function(ins.a, tuple(stack), bp, depth + 1)
            if res is None:
                return []
            return [(nxt, res)]
        elif op in (0x1F, 0x25):  # JZ / JNZ
            v = pop(1)[0]
            zero_to = ins.a if op == 0x1F else nxt
            nonzero_to = nxt if op == 0x1F else ins.a
            if v is not None and v[0] == "int":
                return [(zero_to if v[1] == 0 else nonzero_to, (tuple(stack), bp))]
            if v is not None and v[0] == "nz":
                return [(nonzero_to, (tuple(stack), bp))]
            # Refine every copy of the tested value on each edge, so the compiler's
            # duplicate-then-test idiom for && / || does not explore an impossible edge.
            zs = tuple(("int", 0) if x == v else x for x in stack)
            nzs = tuple(("nz", v) if x == v else x for x in stack)
            return [(zero_to, (zs, bp)), (nonzero_to, (nzs, bp))]
        elif op == 0x20:  # RETN
            return [(-1, (tuple(stack), bp))]
        elif op == 0x21:  # DESTRUCT
            n = ins.a // 4
            block = pop(n)
            k0, k1 = ins.b // 4, ins.c // 4
            stack.extend(block[k0:k0 + k1])
        elif op in (0x23, 0x24):  # DECISP/INCISP
            idx = len(stack) + ins.a // 4
            if 0 <= idx < len(stack):
                stack[idx] = ("u", pc, 0)
        elif op in (0x28, 0x29):  # DECIBP/INCIBP
            idx = bp + ins.a // 4
            if 0 <= idx < len(stack):
                stack[idx] = ("u", pc, 0)
        elif op == 0x26:  # CPDOWNBP
            n = ins.b // 4
            start = bp + ins.a // 4
            src = stack[-n:]
            if 0 <= start and start + n <= len(stack):
                stack[start:start + n] = src
        elif op == 0x27:  # CPTOPBP
            n = ins.b // 4
            start = bp + ins.a // 4
            stack.extend(stack[start:start + n] if start >= 0 else [("u", pc, k) for k in range(n)])
        elif op == 0x2A:  # SAVEBP
            bp_new = len(stack)
            stack.append(("bp", bp))
            bp = bp_new
        elif op == 0x2B:  # RESTOREBP
            v = pop(1)[0]
            bp = v[1] if v is not None and v[0] == "bp" else bp
        elif op == 0x2C:  # STORE_STATE: closure body starts after the JMP that follows
            jmp = self.insns.get(nxt)
            if jmp is not None and jmp.op == 0x1D:
                body = nxt + jmp.n
                if body not in self.closures_done:
                    self.closures_done.add(body)
                    try:
                        self.run_function(body, tuple(stack), bp, depth + 1)
                    except Budget:
                        pass
        # everything else (NOP, STORESTATEALL, ...) has no stack effect
        return [(nxt, (tuple(stack), bp))]

    def record(self, pc: int, routine: int, args: List[Any]):
        obj = args[0] if args else UNK
        idx = args[1] if len(args) > 1 else UNK
        val = args[2] if len(args) > 2 else UNK
        key = (pc, obj, idx, val)
        if key in self.seen_access:
            return
        self.seen_access.add(key)
        self.sink.append({
            "script": self.script, "pc": pc,
            "op": "set" if routine in (SET_LOCAL_BOOLEAN, SET_LOCAL_NUMBER) else "get",
            "kind": "b" if routine in (GET_LOCAL_BOOLEAN, SET_LOCAL_BOOLEAN) else "n",
            "obj": describe(obj),
            "index": idx[1] if idx is not None and idx[0] == "int" else None,
            "value": (val[1] if val is not None and val[0] == "int" else None) if routine in (
                SET_LOCAL_BOOLEAN, SET_LOCAL_NUMBER) else None,
        })


def describe(v) -> str:
    if is_unknown(v):
        return "?"
    if v == SELF:
        return "SELF"
    if v == INVALID:
        return "INVALID"
    if v[0] == "tag":
        return "tag:" + v[1]
    if v[0] == "pc":
        return "pc:" + v[1]
    if v[0] in ("module", "area"):
        return v[0]
    if v[0] == "obj?":
        return "call:" + v[1]
    return "?"


def analyse_script(data: bytes, sigs, script: str) -> Tuple[List[dict], Optional[str]]:
    sink: List[dict] = []
    try:
        insns = decode(data)
    except Exception as e:  # noqa: BLE001
        return sink, "decode: %s" % e
    it = Interp(insns, sigs, script, sink)
    try:
        it.run_function(13, (), 0, 0)
    except Budget as e:
        return sink, "budget: %s" % e
    except (IndexError, KeyError, TypeError) as e:
        return sink, "interp: %r" % e
    return sink, None


# --------------------------------------------------------------------------
# module ownership
# --------------------------------------------------------------------------

GIT_LISTS = [("Creature List", "utc"), ("Door List", "utd"), ("TriggerList", "utt"),
             ("Encounter List", "ute"), ("WaypointList", "utw"), ("SoundList", "uts"),
             ("Placeable List", "utp"), ("StoreList", "utm")]


def _script_fields(s: kf.GFFStruct) -> Dict[str, str]:
    out = {}
    for k, v in s.items():
        if isinstance(v, str) and v and (k.startswith("Script") or k.startswith("On")
                                         or k.startswith("Mod_On")):
            out[k] = v.lower()
    return out


class Module:
    def __init__(self, game: kf.Game, name: str):
        self.game = game
        self.name = name
        self.archives = game.module(name)
        self._res: Dict[str, bytes] = {}

    def read(self, resname: str) -> Optional[bytes]:
        low = resname.lower()
        if low in self._res:
            return self._res[low]
        data = None
        for a in self.archives:
            r = a.get(low)
            if r is not None:
                data = r.read()
                break
        if data is None:
            try:
                data = self.game.read(low)
            except Exception:  # noqa: BLE001
                data = None
        self._res[low] = data
        return data

    def resources(self, ext: str) -> List[str]:
        out = []
        for a in self.archives:
            for r in a:
                if r.ext == ext:
                    out.append(r.resref.lower())
        return sorted(set(out))


def module_owners(mod: Module):
    """
    Returns (objects, script_owners):
      objects: list of dicts {list, index, tag, resref, object_id, scripts{event: script}, dlg}
      script_owners: script -> list of (context, owner descriptor)
    """
    git_name = next((r for r in mod.resources("git")), None)
    objects: List[Dict[str, Any]] = []
    owners: Dict[str, List[Tuple[str, str]]] = defaultdict(list)
    if git_name is None:
        return objects, owners
    git = kf.GFF(mod.read(git_name + ".git"), git_name)
    ids = {(o["list"], o["index"]): o["object_id"] for o in kf.predict_object_ids(mod.game, mod.name)}
    for list_name, ext in GIT_LISTS:
        for i, s in enumerate(git.root.get(list_name, [])):
            rr = (s.get("TemplateResRef", "") or "").lower()
            tmpl = None
            if rr:
                raw = mod.read(rr + "." + ext)
                if raw:
                    try:
                        tmpl = kf.GFF(raw, rr + "." + ext).root
                    except Exception:  # noqa: BLE001
                        tmpl = None
            tag = (s.get("Tag") or (tmpl.get("Tag") if tmpl else "") or "").lower()
            scripts = _script_fields(tmpl) if tmpl else {}
            dlg = ((tmpl.get("Conversation") if tmpl else "") or "").lower()
            o = {"list": list_name, "index": i, "tag": tag, "resref": rr, "ext": ext,
                 "object_id": ids.get((list_name, i)), "scripts": scripts, "dlg": dlg}
            objects.append(o)
            desc = "%s:%s" % (ext, tag)
            for ev, sc in scripts.items():
                owners[sc].append(("%s.%s" % (ext, ev), desc))
            if dlg:
                d = mod.read(dlg + ".dlg")
                if d:
                    for ctx, sc in dlg_scripts(d, dlg):
                        owners[sc].append(("dlg:%s.%s" % (dlg, ctx), desc))
    for are in mod.resources("are"):
        raw = mod.read(are + ".are")
        if raw:
            for ev, sc in _script_fields(kf.GFF(raw, are).root).items():
                owners[sc].append(("are.%s" % ev, "area"))
    raw = mod.read("module.ifo")
    if raw:
        for ev, sc in _script_fields(kf.GFF(raw, "module.ifo").root).items():
            owners[sc].append(("ifo.%s" % ev, "module"))
    # Conversations not named by any GIT object (started by a script) still run their node
    # scripts with OBJECT_SELF = whoever the script made the owner; record them as such.
    named = {o["dlg"] for o in objects if o["dlg"]}
    for dlg in mod.resources("dlg"):
        if dlg in named:
            continue
        d = mod.read(dlg + ".dlg")
        if d:
            for ctx, sc in dlg_scripts(d, dlg):
                owners[sc].append(("dlg:%s.%s" % (dlg, ctx), "script-started:" + dlg))
    return objects, owners


def dlg_scripts(data: bytes, name: str):
    try:
        g = kf.GFF(data, name).root
    except Exception:  # noqa: BLE001
        return
    for k in ("EndConversation", "EndConverAbort"):
        v = (g.get(k) or "").lower()
        if v:
            yield k, v
    for lst, links in (("EntryList", "RepliesList"), ("ReplyList", "EntriesList")):
        for n in g.get(lst, []) or []:
            v = (n.get("Script") or "").lower()
            if v:
                yield "action", v
            for l in n.get(links, []) or []:
                c = (l.get("Active") or "").lower()
                if c:
                    yield "cond", c
    for l in g.get("StartingList", []) or []:
        c = (l.get("Active") or "").lower()
        if c:
            yield "cond", c


def module_names(game: kf.Game) -> List[str]:
    names = set()
    for f in os.listdir(os.path.join(game.dir, "modules")):
        low = f.lower()
        if low.endswith("_s.rim"):
            names.add(low[:-6])
        elif low.endswith(".rim"):
            names.add(low[:-4])
        elif low.endswith(".mod"):
            names.add(low[:-4])
    return sorted(names)


# --------------------------------------------------------------------------
# audit
# --------------------------------------------------------------------------

def audit(game: kf.Game, modules: List[str], only_script: Optional[str] = None):
    sigs = load_signatures(game)
    assert sigs[GET_LOCAL_BOOLEAN][1] == "GetLocalBoolean", sigs[GET_LOCAL_BOOLEAN]
    assert sigs[SET_LOCAL_NUMBER][1] == "SetLocalNumber", sigs[SET_LOCAL_NUMBER]
    cache: Dict[Tuple[str, int], Tuple[List[dict], Optional[str]]] = {}
    rows = []
    failures = defaultdict(int)
    for m in modules:
        mod = Module(game, m)
        try:
            objects, owners = module_owners(mod)
        except Exception as e:  # noqa: BLE001
            failures["module:" + m + ":" + repr(e)] += 1
            continue
        scripts = set(owners) | set(mod.resources("ncs"))
        for sc in sorted(scripts):
            if only_script and sc != only_script:
                continue
            raw = mod.read(sc + ".ncs")
            if not raw:
                continue
            key = (sc, hash(raw))
            if key not in cache:
                cache[key] = analyse_script(raw, sigs, sc)
            acc, err = cache[key]
            if err:
                failures[err.split(":")[0]] += 1
            for a in acc:
                rows.append(dict(a, module=m, owners=owners.get(sc, [])))
        yield m, objects, rows
        rows = []
    if failures:
        print("# analysis failures: %s" % dict(failures), file=sys.stderr)


def resolve_targets(access: dict, objects: List[dict]) -> List[Tuple[str, Optional[dict]]]:
    """Every concrete (identity, GIT object) this access can name."""
    obj = access["obj"]
    if obj == "SELF":
        out = []
        for ctx, desc in access["owners"]:
            if desc in ("area", "module") or desc.startswith("script-started:"):
                out.append((desc, None))
                continue
            ext, tag = desc.split(":", 1)
            hits = [o for o in objects if o["tag"] == tag and o["ext"] == ext]
            out.append((desc, hits[0] if len(hits) == 1 else None))
        return out or [("SELF(no owner)", None)]
    if obj.startswith("tag:"):
        tag = obj[4:]
        hits = [o for o in objects if o["tag"] == tag]
        return [("%s:%s" % (hits[0]["ext"], tag) if hits else obj, hits[0] if len(hits) == 1 else None)]
    return [(obj, None)]


def main(argv) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=kf.DEFAULT_GAME_DIR)
    ap.add_argument("--module", action="append")
    ap.add_argument("--script")
    ap.add_argument("--json")
    ap.add_argument("--all", action="store_true", help="print every static-object pair, not just candidates")
    args = ap.parse_args(argv)
    game = kf.Game(args.game)
    modules = args.module or module_names(game)

    all_rows = []
    pairs: Dict[Tuple, Dict[str, Any]] = {}
    stats = defaultdict(int)
    for m, objects, rows in audit(game, modules, args.script):
        for a in rows:
            all_rows.append(a)
            stats["accesses"] += 1
            stats["obj=" + a["obj"].split(":")[0]] += 1
            if args.script:
                print(json.dumps(a))
            for ident, git in resolve_targets(a, objects):
                if a["index"] is None:
                    stats["index unknown"] += 1
                    continue
                k = (m, ident, a["kind"], a["index"])
                p = pairs.setdefault(k, {"module": m, "ident": ident, "kind": a["kind"], "index": a["index"],
                                         "git": git, "writers": set(), "readers": set(), "values": set()})
                ctxs = ",".join(sorted({c for c, _ in a["owners"]})) or "unowned"
                (p["writers"] if a["op"] == "set" else p["readers"]).add("%s[%s]" % (a["script"], ctxs))
                if a["op"] == "set":
                    p["values"].add(a["value"])

    print("# %d accesses; %s" % (stats["accesses"], ", ".join("%s=%d" % kv for kv in sorted(stats.items()))))
    static = [p for p in pairs.values() if p["git"] is not None]
    print("# (module, object, kind, slot) pairs on a uniquely-tagged static GIT object: %d" % len(static))

    def is_candidate(p):
        if not p["writers"] or not p["readers"]:
            return False
        if p["kind"] == "b" and p["index"] >= AI_FLAG_FIRST:
            return False
        return True

    cands = [p for p in static if is_candidate(p)] if not args.all else static
    cands.sort(key=lambda p: (p["module"], p["ident"], p["kind"], p["index"]))
    for p in cands:
        g = p["git"]
        print("%-10s %-28s id=0x%-4X %s%-3d vals=%s\n    W: %s\n    R: %s" % (
            p["module"], p["ident"], g["object_id"] or 0, p["kind"], p["index"],
            sorted(v for v in p["values"] if v is not None) + (["?"] if None in p["values"] else []),
            "; ".join(sorted(p["writers"])) or "-", "; ".join(sorted(p["readers"])) or "-"))
    if args.json:
        with open(args.json, "w") as f:
            json.dump(all_rows, f, indent=1, default=list)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
