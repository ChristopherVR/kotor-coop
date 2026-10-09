"""Windows KOTOR observer and bounded Laya-controlled playtest. All actions are logged.

Run with the Laya pilot venv. Uses Codex's laya-kotor-pilot MCP registration.
Only supports the current shared-block layout; refuses stale installations.
No arbitrary model-generated commands, teleports, warps, or save edits in play mode.
"""
from __future__ import annotations

import argparse
import asyncio
import ctypes as C
import json
import math
import os
import re
from pathlib import Path
import struct
import subprocess
import time
import tomllib

REPO = Path(__file__).resolve().parent.parent
SIGNATURE = bytes.fromhex("4B4F434F4F5030316500000001EEFFC0")
PANELS = {0x7559E0, 0x755800, 0x755888, 0x755958}
# CGuiInGame panel fields (symbol database, `tools/q.py offsets CGuiInGame`).
PANEL_FIELDS = {0x08: "menu", 0x0C: "equip", 0x10: "inventory", 0x14: "character", 0x18: "abilities",
                0x1C: "messages", 0x20: "journal", 0x24: "map", 0x28: "options", 0x3C: "dialog",
                0x40: "cinematic", 0x44: "computer", 0x50: "examine", 0x54: "container",
                0x58: "create_item", 0x78: "party_selection", 0x7C: "pause", 0x80: "galaxy_map",
                0x84: "store", 0x90: "main_interface", 0x94: "area_transition", 0x98: "message_box",
                0x9C: "skill_info", 0xA0: "tutorial_box", 0xA8: "status_summary"}
POPUPS = {"container", "store", "message_box", "tutorial_box", "examine", "party_selection",
          "skill_info", "status_summary", "create_item"}

class MemoryError(RuntimeError):
    pass

class MBI(C.Structure):
    _fields_ = [("base", C.c_void_p), ("allocation", C.c_void_p), ("protect0", C.c_uint32),
                ("padding0", C.c_uint32), ("size", C.c_size_t), ("state", C.c_uint32),
                ("protect", C.c_uint32), ("kind", C.c_uint32), ("padding1", C.c_uint32)]

class Game:
    def __init__(self, pid):
        self.pid = pid
        self.k = C.WinDLL("kernel32", use_last_error=True)
        self.k.OpenProcess.restype = C.c_void_p
        self.k.OpenProcess.argtypes = [C.c_uint32, C.c_int, C.c_uint32]
        self.k.ReadProcessMemory.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
        self.k.WriteProcessMemory.argtypes = self.k.ReadProcessMemory.argtypes
        self.k.VirtualQueryEx.argtypes = [C.c_void_p, C.c_void_p, C.POINTER(MBI), C.c_size_t]
        self.k.VirtualQueryEx.restype = C.c_size_t
        self.k.CloseHandle.argtypes = [C.c_void_p]
        self.handle = self.k.OpenProcess(0x438, 0, pid)
        if not self.handle:
            raise MemoryError(f"Cannot open game {pid}")
        self.block = self.find_block()
        self.game_dir = self.process_path().parent

    def process_path(self):
        self.k.QueryFullProcessImageNameW.argtypes = [C.c_void_p, C.c_uint32, C.c_wchar_p, C.POINTER(C.c_uint32)]
        buf, length = C.create_unicode_buffer(32768), C.c_uint32(32768)
        if not self.k.QueryFullProcessImageNameW(self.handle, 0, buf, C.byref(length)):
            raise MemoryError("Cannot identify game executable")
        if Path(buf.value).name.lower() != "swkotor.exe":
            raise MemoryError("Target is not swkotor.exe")
        return Path(buf.value)

    def read(self, address, size):
        if not 0x10000 <= address < 0x80000000 or not 0 < size <= 64 * 1024 * 1024:
            raise MemoryError(f"Invalid read address/size {address:x}/{size}")
        data, count = C.create_string_buffer(size), C.c_size_t()
        if not self.k.ReadProcessMemory(self.handle, address, data, size, C.byref(count)) or count.value != size:
            raise MemoryError(f"Unreadable game address 0x{address:x}")
        return data.raw

    def unpack(self, address, fmt):
        return struct.unpack(fmt, self.read(address, struct.calcsize(fmt)))[0]

    def u32(self, address):
        return self.unpack(address, "<I")

    def string(self, pointer, length):
        if not pointer or not 0 < length <= 4096:
            return ""
        return self.read(pointer, length).split(b"\0", 1)[0].decode("cp1252", errors="replace")

    def exo(self, address):
        return self.string(self.u32(address), self.u32(address + 4))

    def find_block(self):
        address = 0x10000
        while address < 0x80000000:
            m = MBI()
            if not self.k.VirtualQueryEx(self.handle, address, C.byref(m), C.sizeof(m)):
                break
            if m.state == 0x1000 and m.kind == 0x20000 and not m.protect & 0x101 and m.size <= 64 * 1024 * 1024:
                try:
                    hit = self.read(m.base, m.size).find(SIGNATURE)
                    if hit >= 0 and hit % 4 == 0:
                        return m.base + hit
                except MemoryError:
                    pass
            following = m.base + m.size
            if following <= address:
                break
            address = following
        raise MemoryError("Matching version-100 plugin shared block not found")

    def check_block(self):
        if self.read(self.block, 16) != SIGNATURE:
            raise MemoryError("Plugin identity changed; refusing stale reads/writes")

    def write(self, offset, data):
        self.check_block()
        # Only existing request mailboxes; never engine objects or acknowledgement fields.
        if not any(lo <= offset and offset + len(data) <= hi for lo, hi in
                   ((0x9F4, 0xA58), (0x3328, 0x3364), (0x1D70, 0x1D94))):
            raise MemoryError("Write outside console mailbox")
        count, buffer = C.c_size_t(), C.create_string_buffer(data)
        if not self.k.WriteProcessMemory(self.handle, self.block + offset, buffer, len(data), C.byref(count)) or count.value != len(data):
            raise MemoryError("Console mailbox write failed")

    def console(self, command, timeout=12):
        verb = command.split()[0]
        if verb not in {"input", "opendoor", "usedoor", "walkto", "attack", "screenshot", "talktag",
                        "useplaceable", "autofollow", "closegui", "unlockdoor"}:
            raise ValueError("Command is outside the playtest allowlist")
        if verb in {"useplaceable", "autofollow", "unlockdoor"} and not re.fullmatch(
                r"((useplaceable|unlockdoor) 0x[0-9A-F]{1,8}|autofollow( off)?)", command):
            raise ValueError("Invalid argument for " + verb)
        if verb == "talktag":
            tag = command.split()[1]
            if not re.fullmatch(r"[A-Za-z0-9_]{1,31}", tag) or tag == "close":
                raise ValueError("Invalid NPC tag")
            self.write(0x1D70, tag.encode("ascii").ljust(32, b"\0"))
            self.write(0x1D90, struct.pack("<I", (self.u32(self.block + 0x1D90) + 1) & 0xFFFFFFFF))
            return {"applied": None, "reply": "Native dialogue request posted; verify conversation in next observation"}
        encoded = command.encode("ascii")
        if len(encoded) > 95:
            raise ValueError("Console command exceeds mailbox")
        self.check_block()
        before, native = self.u32(self.block + 0xA58), self.u32(self.block + 0x3328)
        if verb in {"input", "screenshot"}:
            # These local input/capture requests also work before an agent finishes attaching.
            text = command.split(" ", 1)[1].encode("ascii")
            if len(text) > 31 or self.u32(self.block + 0x3364) != native:
                return {"applied": False, "reply": "Native mailbox busy or argument too long"}
            self.write(0x332C, struct.pack("<IIi3f", 103 if verb == "input" else 105, 0, 0, 0, 0, 0) + text.ljust(32, b"\0"))
            self.write(0x3328, struct.pack("<I", (native + 1) & 0xFFFFFFFF))
        else:
            self.write(0x9F8, encoded.ljust(96, b"\0"))
            serial = (self.u32(self.block + 0x9F4) + 1) & 0xFFFFFFFF
            self.write(0x9F4, struct.pack("<I", serial))
        end = time.monotonic() + timeout
        kinds = {"input": 103, "opendoor": 17, "usedoor": 10, "walkto": 24, "attack": 98, "screenshot": 105}
        # useplaceable / autofollow answer through the agent reply line (no native kind here).
        kind = kinds.get(command.split()[0])
        while time.monotonic() < end:
            if kind:
                request = self.u32(self.block + 0x3328)
                if request != native and self.u32(self.block + 0x332C) == kind and self.u32(self.block + 0x3364) == request:
                    status = self.unpack(self.block + 0x3368, "<i")
                    return {"applied": status == 1, "status": status, "reply": self.string(self.block + 0x336C, 64)}
            elif self.u32(self.block + 0xA58) != before:
                return {"applied": None, "reply": self.string(self.block + 0xA5C, 96)}
            time.sleep(0.05)
        return {"applied": False, "reply": "TIMEOUT: no matching native acknowledgement"}

    def snapshot(self):
        self.check_block()
        manager = self.u32(0x7A39FC)
        internal = self.u32(self.u32(manager + 8) + 4)
        if self.u32(internal + 0x100B4) or self.unpack(internal + 0x10008, "<H") != 2:
            return {"pid": self.pid, "loading": True}
        info = self.u32(internal + 0x10004)
        module = self.exo(info + 0xFC).lower()
        player = self.u32(self.block + 0x2260)
        player_id = self.u32(self.block + 0x2264)
        if not player or self.u32(player) != 0x7470C8 or self.u32(player + 4) != player_id:
            return {"pid": self.pid, "module": module, "loading": True}
        xyz = struct.unpack("<fff", self.read(player + 0x90, 12))
        if not all(math.isfinite(v) for v in xyz):
            raise MemoryError("Invalid player position")
        tree = self.u32(self.u32(internal + 0x1005C))
        buckets = struct.unpack("<8192I", self.read(tree, 32768))
        objects, seen = [], set()
        area = self.u32(player + 0x8C)
        for entry in buckets:
            for _ in range(64):
                if not entry or entry in seen:
                    break
                seen.add(entry)
                data = struct.unpack("<III", self.read(entry, 12))
                entry, body = data[2], data[1]
                if not body:
                    continue
                vt = self.u32(body)
                kind = {0x7470C8: "creature", 0x7498B0: "door", 0x7494D0: "placeable"}.get(vt)
                if not kind or self.u32(body + 0x8C) != area:
                    continue
                pos = struct.unpack("<fff", self.read(body + 0x90, 12))
                oid = self.u32(body + 4)
                item = {"id": oid, "kind": kind, "tag": self.exo(body + 0x18), "position": pos,
                        "distance": round(math.dist(xyz[:2], pos[:2]), 2)}
                if kind == "creature":
                    stats = self.u32(body + 0xA74)
                    # +0xDC current / +0xE0 max hit points (Odyssey.ObjCurrentHitPoints/ObjMaxHitPoints).
                    hp, _, max_hp = struct.unpack("<hhh", self.read(body + 0xDC, 6))
                    item.update(hp=hp, max_hp=max_hp, faction=self.u32(stats + 0x78) if stats else None,
                                combat=bool(self.u32(body + 0x138)), party=bool(self.u32(body + 0xA88)))
                if kind == "placeable":
                    item.update(self.placeable_state(body))
                if kind == "door":
                    item.update(open=self.unpack(body + 0x2CC, "<B"), pending=self.unpack(body + 0x2CD, "<B"),
                                destination=self.exo(body + 0x390),
                                static=bool(self.u32(body + 0x3C0)),  # CSWSDoor::LoadDoor's Static: scenery
                                locked=bool(self.unpack(body + 0x2C4, "<B")))
                objects.append(item)
        client = self.u32(self.u32(manager + 4) + 4)
        gui = self.u32(client + 0x40)
        conversation, replies, reply_texts = False, 0, []
        panels = self.active_panels(gui) if gui else []
        # The HUD leaves the manager's active list while a full-screen menu is up (equipment,
        # options...); popups (container, store, message box...) join it beside the HUD. The
        # CGuiInGame+0x30 "input class GUI" flag misses popups, so it is not used here.
        menu_open = bool(panels) and ("main_interface" not in panels or any(p in POPUPS for p in panels))
        if gui:
            conversation = bool(self.u32(gui + 0xB4))
            if conversation:
                for off in (0x3C, 0x40, 0x44, 0x48):
                    panel = self.u32(gui + off)
                    if panel and self.u32(panel) in PANELS:
                        count = self.u32(panel + 0x6C)
                        if 0 < count <= 9:
                            replies = count
                            reply_texts = self.reply_texts(panel, count)
                            break
        return {"pid": self.pid, "loading": False, "module": module, "position": xyz,
                "player_id": player_id, "hp": self.unpack(player + 0xDC, "<h"), "frame": self.u32(self.block + 0x250),
                "max_hp": self.unpack(player + 0xE0, "<h"),
                # Cross-game referee inputs; each is None when its read fails, and the check skips.
                "xp": self.experience(player), "journal": self.journal(internal),
                "globals": self.globals(internal, module), "remote_slots": self.remote_slots(),
                # The creature under control: "" for the player character, a companion's tag
                # (end_trask) after a portrait swap.
                "controlled_tag": self.exo(player + 0x18),
                "orientation": struct.unpack("<fff", self.read(player + 0x9C, 12)),
                "remote_count": self.u32(self.block + 0x48), "conversation": conversation, "reply_count": replies,
                "reply_texts": reply_texts, "menu_open": menu_open, "panels": panels,
                # SharedBlock.OffsetLocalDialogueNode: the entry the local conversation is on.
                "dialogue_node": self.unpack(self.block + 0x1320, "<i"),
                # SharedBlock.Dialogue: our own partner tag, and the peer's while it talks.
                "partner_tag": self.read(self.block + 0x1200, 32).split(b"\0")[0].decode("ascii", "replace"),
                "peer_partner_tag": self.read(self.block + 0x1240, 32).split(b"\0")[0].decode("ascii", "replace")
                    if self.u32(self.block + 0x1228) else "",
                "dialogue_line": self.string(self.block + 0x1100, 96), "objects": sorted(objects, key=lambda x: x["distance"])}

    # ---- state the two games are supposed to agree on (read by the co-op referee) ------------
    # Each reader returns None (or omits its keys) rather than raising, so a bad read costs a check,
    # never a run.

    def placeable_state(self, body):
        """CSWSPlaceable locked +0x260, has_inventory +0x334, open +0x338 (Odyssey.Doors.cs,
        docs/research/placeables-containers.md): one 0xDC-byte read."""
        try:
            blob = self.read(body + 0x260, 0xDC)
            return {"locked": bool(struct.unpack_from("<I", blob, 0)[0]),
                    "container": bool(struct.unpack_from("<I", blob, 0xD4)[0]),
                    "open": bool(struct.unpack_from("<I", blob, 0xD8)[0])}
        except (MemoryError, struct.error):
            return {}

    def experience(self, player):
        """CSWSCreatureStats::experience: creature +0xA74 -> stats, +0x068 (Odyssey.Progression.cs)."""
        try:
            stats = self.u32(player + 0xA74)
            return self.u32(stats + 0x68) if stats else None
        except (MemoryError, struct.error):
            return None

    def journal(self, internal):
        """{lowercase quest tag: state}: the party journal (CSWPartyTable +0x11C, the table is
        server-internal +0x1B770) is a CExoArrayList {data, size, capacity} of 0x3C-byte entries
        with the tag CExoString at +0x18 and the state int at +0x20 (KotorGameBridge.Journal.cs)."""
        try:
            journal = self.u32(internal + 0x1B770 + 0x11C)
            if not journal:
                return None
            data, count = struct.unpack("<II", self.read(journal, 8))
            if count > 512 or (count and not data):
                return None
            entries = {}
            raw = self.read(data, count * 0x3C) if count else b""
            for i in range(count):
                pointer, length = struct.unpack_from("<II", raw, i * 0x3C + 0x18)
                tag = self.string(pointer, length).lower()
                if tag:
                    entries[tag] = struct.unpack_from("<i", raw, i * 0x3C + 0x20)[0]
            return entries
        except (MemoryError, struct.error):
            return None

    GLOBALS_AT, GLOBAL_NAMES, GLOBAL_STRIDE = 0x100FC, 1775, 0x18
    # Per-player scratch the bridge deliberately does not replicate (KotorGameBridge.WorldState.cs).
    NOT_WORLD_TRUTH = {"k_current_planet", "k_last_module", "k_last_location", "k_stunt_module"}

    def globals(self, internal, module):
        """{name: value} of the non-zero synced global booleans (bit array at table +0xA668,
        113 bytes, MSB first) and numbers (signed bytes at +0xA6D9, 500 of them). The name catalogue
        (1775 x 0x18 bytes at the table start; u16 at +22 packs type<<14 | index) is read once per
        module (GlobalVariableTable.cs). Excludes the bridge's own per-player names and k_vis_*."""
        try:
            table = internal + self.GLOBALS_AT
            if getattr(self, "_catalogue_module", None) != module:
                raw = self.read(table, self.GLOBAL_NAMES * self.GLOBAL_STRIDE)
                names = {0: {}, 1: {}}
                for slot in range(self.GLOBAL_NAMES):
                    at = slot * self.GLOBAL_STRIDE
                    if raw[at] == 0:
                        continue
                    name = raw[at:at + 22].split(b"\0", 1)[0].decode("ascii", "replace").lower()
                    packed = struct.unpack_from("<H", raw, at + 22)[0]
                    if packed >> 14 in names and name not in self.NOT_WORLD_TRUTH and not name.startswith("k_vis_"):
                        names[packed >> 14][packed & 0x3FFF] = name
                self._catalogue, self._catalogue_module = names, module
            values = self.read(table + 0xA668, 113 + 500)
            found = {}
            for index, name in self._catalogue[0].items():
                if index < 113 * 8 and (values[index >> 3] >> (7 - (index & 7))) & 1:
                    found[name] = 1
            for index, name in self._catalogue[1].items():
                if index < 500 and values[113 + index]:
                    found[name] = struct.unpack_from("<b", values, 113 + index)[0]
            return found
        except (MemoryError, struct.error):
            return None

    def remote_slots(self):
        """The plugin's RemoteSlot[8] at block +0x50 (0x40 each): active u32 @0, peer id @4,
        position 3 floats @8, health int @0x18, flags @0x3C (high 16 bits = the peer's max hp)."""
        try:
            raw = self.read(self.block + 0x50, 8 * 0x40)
            slots = []
            for i in range(8):
                at = i * 0x40
                if struct.unpack_from("<I", raw, at)[0]:
                    slots.append({"peer": struct.unpack_from("<I", raw, at + 4)[0],
                                  "position": struct.unpack_from("<fff", raw, at + 8),
                                  "health": struct.unpack_from("<i", raw, at + 0x18)[0],
                                  "max_health": struct.unpack_from("<I", raw, at + 0x3C)[0] >> 16})
            return slots
        except (MemoryError, struct.error):
            return None

    # CSWGuiDialog reply items are inline: item i's text is a CExoString at panel + 0x158 +
    # i * 0x140 (found live 2026-10-08 on Trask's tutorial lines). The engine numbers them
    # "1. ...", "2. ...", so a read that does not carry its own slot number is rejected.
    REPLY_TEXT = 0x158
    REPLY_STRIDE = 0x140

    def reply_texts(self, panel, count):
        texts = []
        for i in range(count):
            try:
                text = self.exo(panel + self.REPLY_TEXT + i * self.REPLY_STRIDE)
            except MemoryError:
                return []
            if not text.startswith(f"{i + 1}."):
                return []
            texts.append(text.split(".", 1)[1].strip())
        return texts

    def active_panels(self, gui):
        """Names of CSWGuiManager's active panels (manager +0x88, a CExoArrayList), resolved
        against CGuiInGame's panel fields; an unknown panel is reported by address."""
        known = {self.u32(gui + off): name for off, name in PANEL_FIELDS.items()}
        manager = self.u32(gui + 0x38)
        data, count = self.u32(manager + 0x88), self.u32(manager + 0x8C)
        if not data or count > 64:
            return []
        return [known.get(p, f"0x{p:X}") for p in struct.unpack(f"<{count}I", self.read(data, 4 * count))]

    def exited(self):
        code = C.c_uint32()
        self.k.GetExitCodeProcess.argtypes = [C.c_void_p, C.POINTER(C.c_uint32)]
        return bool(self.k.GetExitCodeProcess(self.handle, C.byref(code))) and code.value != 259

    def close(self):
        self.k.CloseHandle(self.handle)

def candidates(state, attempts):
    if state.get("loading"):
        return {"wait": {"description": "Wait for module loading", "commands": []}}
    if state["hp"] <= 0:
        return {}
    if state["conversation"]:
        if state["reply_count"]:
            return {f"reply_{i+1}": {"description": f"Select displayed reply {i+1}; reply text unavailable", "commands": [f"input key {i+2} tap"]}
                    for i in range(state["reply_count"])}
        return {"continue_dialogue": {"description": "Continue the voiced dialogue using Space", "commands": ["input key 57 tap"]}}
    choices = {}
    for obj in state["objects"]:
        key = f"{state['module']}:{obj['id']}"
        if attempts.get(key, 0) >= 3:
            continue
        if obj["kind"] == "creature" and obj["hp"] > 0 and obj["faction"] == 1 and not obj["party"] and obj["distance"] < 20:
            choices[f"fight_{obj['id']}"] = {"description": f"Attack hostile {obj['tag']} at {obj['distance']} metres", "commands": [f"attack ranged 0x{obj['id']:X}"], "target": key}
            break
    for obj in state["objects"]:
        key = f"{state['module']}:{obj['id']}"
        if obj["kind"] == "creature" and obj["hp"] > 0 and obj["faction"] != 1 and obj["distance"] < 8 and re.fullmatch(r"end_trask|end_carth|carth", obj["tag"]) and attempts.get(key, 0) < 2:
            choices[f"talk_{obj['id']}"] = {"description": f"Talk to story companion {obj['tag']} for the next objective", "commands": [f"talktag {obj['tag']}"], "target": key}
    for obj in state["objects"]:
        key = f"{state['module']}:{obj['id']}"
        if attempts.get(key, 0) >= 3 or obj["kind"] != "door" or (obj["open"] and not obj["destination"]):
            continue
        x, y, z = obj["position"]
        if obj["distance"] > 3:
            action, description = "walk", f"Walk toward door {obj['tag']} at {obj['distance']} metres, destination {obj['destination'] or 'local room'}"
            # Player locomotion needs drive input: AI WalkTo can acknowledge without moving.
            dx, dy = x-state["position"][0], y-state["position"][1]
            ox, oy = state["orientation"][:2]
            angle = math.atan2(ox*dy-oy*dx, ox*dx+oy*dy)
            if abs(angle) > .35:
                keycode = 30 if angle > 0 else 32
                commands = [f"input key {keycode} down", f"input key {keycode} up"]
                duration = min(.8, abs(angle)/2.5)
            else:
                commands = ["input key 17 down", "input key 17 up"]
                duration = min(1.0, obj["distance"]/6)
        elif obj["destination"]:
            action, description, commands = "exit", f"Use transition door to {obj['destination']}", [f"usedoor 0x{obj['id']:X}"]
        else:
            action, description, commands = "open", f"Open local door {obj['tag']} through native engine action", [f"opendoor 0x{obj['id']:X}"]
        choices[f"{action}_{obj['id']}"] = {"description": description, "commands": commands, "target": key,
                                               "hold_seconds": duration if action == "walk" else 0}
        if len(choices) >= 3:
            break
    return choices

async def play(args):
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    config = tomllib.loads((Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "config.toml").read_text())["mcp_servers"][args.server]
    params = StdioServerParameters(command=config["command"], args=config["args"], env=dict(os.environ, **config.get("env", {})))
    games = [Game(args.host), Game(args.client)]
    report = {"steps": [], "issues": [], "modules": [], "laya_actions": 0, "fallback_actions": 0, "reply_text_available": False}
    attempts, started, previous = {}, time.monotonic(), None
    try:
        with (out / "mcp-stderr.log").open("w", encoding="utf-8") as errors:
            async with stdio_client(params, errlog=errors) as (reader, writer):
                async with ClientSession(reader, writer) as session:
                    await session.initialize()
                    for step in range(args.steps):
                        if time.monotonic() - started > args.seconds:
                            report["stop_reason"] = "time budget"
                            break
                        states = [g.snapshot() for g in games]
                        state = states[0]
                        for s in states:
                            if s.get("module") and s["module"] not in report["modules"]:
                                report["modules"].append(s["module"])
                        legal = candidates(state, attempts)
                        if not legal:
                            report["stop_reason"] = "player downed" if state.get("hp", 1) <= 0 else "no untried actions"
                            break
                        summary = {k: v for k, v in state.items() if k != "objects"}
                        result = await session.call_tool("laya_predict", {"state": summary, "model": "english", "min_confidence": args.confidence,
                            "questions": {"action": {"type": "choice", "instructions": "Choose the next action to make story progress in KOTOR. Finish dialogue, defeat hostile enemies, then explore doors. Prefer a route into a new module. Choose only an available action.", "criteria": {k: a["description"] for k, a in legal.items()}}}})
                        data = [json.loads(c.text) for c in result.content if c.type == "text"]
                        answer = data[0].get("answers", {}).get("action", {})
                        chosen = answer.get("choice")
                        accepted = answer.get("abstention") == "passed" and chosen in legal
                        if not accepted:
                            chosen = next(iter(legal))
                        action = legal[chosen]
                        # Re-observe identity/module before executing a model's delayed choice.
                        now = games[0].snapshot()
                        if now.get("loading") != state.get("loading") or now.get("module") != state.get("module") or now.get("player_id") != state.get("player_id"):
                            continue
                        if chosen.startswith("reply_") and (not now.get("conversation") or now.get("reply_count") != state.get("reply_count")):
                            continue
                        if action.get("target") and not any(f"{now['module']}:{o['id']}" == action["target"] for o in now["objects"]):
                            continue
                        report["laya_actions" if accepted else "fallback_actions"] += 1
                        results = []
                        for index, command in enumerate(action["commands"]):
                            results.append(games[0].console(command))
                            if index == 0 and action.get("hold_seconds"):
                                await asyncio.sleep(action["hold_seconds"])
                        row = {"step": step, "before": states, "action": chosen, "source": "laya" if accepted else "fallback", "decision": data, "commands": action["commands"], "results": results}
                        if action.get("target") and not chosen.startswith("walk_"):
                            attempts[action["target"]] = attempts.get(action["target"], 0) + 1
                        await asyncio.sleep(3 if state.get("conversation") else 6)
                        after = [g.snapshot() for g in games]
                        row["after"] = after
                        if chosen.startswith("walk_") and "17 down" in action["commands"][0] and not after[0].get("loading") and after[0].get("module") == state.get("module") and math.dist(state["position"], after[0]["position"]) < .05:
                            report["issues"].append({"step": step, "kind": "no_position_progress", "action": chosen, "module": state["module"]})
                            attempts[action["target"]] = attempts.get(action["target"], 0) + 1
                        if step % 5 == 0:
                            for index, game in enumerate(games):
                                shotname = f"laya_{step}_{index}"
                                shot = game.console(f"screenshot {shotname}")
                                path = game.game_dir / "kotorcoop-shots" / (shotname + ".bmp")
                                if shot.get("applied") and path.exists():
                                    import shutil
                                    shutil.copy2(path, out / path.name)
                        report["steps"].append(row)
                        previous = state
                        (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                        print(json.dumps({"step": step, "module": state.get("module"), "action": chosen, "source": row["source"], "hp": state.get("hp"), "results": results}), flush=True)
                    else:
                        report["stop_reason"] = "step budget"
    except Exception as exc:
        report["stop_reason"] = "runtime error"
        report["issues"].append({"kind": type(exc).__name__, "detail": str(exc)})
        raise
    finally:
        for game in games:
            try:
                for key in (17, 30, 32):
                    game.console(f"input key {key} up")
                game.console("attack stop")
            except Exception as exc:
                report["issues"].append({"kind": "cleanup", "detail": str(exc)})
            finally:
                game.close()
        report["seconds"] = round(time.monotonic() - started, 2)
        (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("FINAL", json.dumps({k: v for k, v in report.items() if k != "steps"}), flush=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=int, required=True)
    parser.add_argument("--client", type=int)
    parser.add_argument("--snapshot", action="store_true")
    parser.add_argument("--steps", type=int, default=40)
    parser.add_argument("--seconds", type=int, default=600)
    parser.add_argument("--confidence", type=float, default=.7)
    parser.add_argument("--server", default="laya-kotor-pilot")
    parser.add_argument("--output", default=str(REPO / "artifacts" / "laya-playthrough"))
    args = parser.parse_args()
    if args.snapshot:
        game = Game(args.host)
        try:
            print(json.dumps(game.snapshot(), indent=2))
        finally:
            game.close()
    else:
        if not args.client or not 1 <= args.steps <= 1000 or not 1 <= args.seconds <= 7200 or not 0 <= args.confidence <= 1:
            parser.error("Provide --client, steps 1..1000, seconds 1..7200, confidence 0..1")
        asyncio.run(play(args))

if __name__ == "__main__":
    main()
