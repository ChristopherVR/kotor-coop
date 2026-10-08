"""Read-only story text from the game files, for the playtest policy that reads the journal.

  journal_text(tag, state)  the journal entry text a quest shows at that state
  quest_name(tag)           the quest's display name
  area_name(module)         the module's area name ("Endar Spire")
  creature_names(module)    {lowercase creature tag: display name} from the module's creature templates

Everything is cached and returns "" / {} / None when the files cannot be read, so a missing
install costs the prompt some context, never a run. The install is C:/GOG Games/Star Wars KOTOR
(GOG) unless KOTOR_GAME_DIR or set_game_dir() says otherwise.
"""
from __future__ import annotations

import os

import kotorfiles

_game = None
_journal = None
_areas, _creatures = {}, {}
JRL_TYPE = 2056  # kotorfiles' table names 2056 "utw"; the key file files global.jrl under it


def set_game_dir(path):
    """Point at another install and drop every cache."""
    global _game, _journal
    _game, _journal = kotorfiles.Game(path) if path else None, None
    _areas.clear()
    _creatures.clear()


def game_dir_present():
    return os.path.isdir(os.environ.get("KOTOR_GAME_DIR", kotorfiles.DEFAULT_GAME_DIR))


def _g():
    global _game
    if _game is None:
        _game = kotorfiles.Game(os.environ.get("KOTOR_GAME_DIR", kotorfiles.DEFAULT_GAME_DIR))
    return _game


def _text(loc):
    """A LocString's text: its own substring when it carries one, else the dialog.tlk entry."""
    if loc is None:
        return ""
    own = loc.text()
    if own:
        return own.strip()
    return _g().strref(loc.strref).strip() if loc.strref >= 0 else ""


def _quests():
    """{lowercase tag: (name, {entry id: (text, ends_quest)})} from global.jrl."""
    global _journal
    if _journal is None:
        _journal = {}
        try:
            g = _g()
            bif, index = g.key.entries[("global", JRL_TYPE)]
            root = kotorfiles.GFF(g.key.bif(bif).by_index[index].read(), "global.jrl").root
            for cat in root.get("Categories") or []:
                entries = {}
                for e in cat.get("EntryList") or []:
                    entries[int(e.get("ID"))] = (_text(e.get("Text")), bool(e.get("End")))
                _journal[(cat.get("Tag") or "").lower()] = (_text(cat.get("Name")), entries)
        except Exception:
            pass
    return _journal


def quest_name(tag):
    return _quests().get((tag or "").lower(), ("", {}))[0]


def journal_entry(tag, state):
    """(text, ends_quest) of the entry with this id, or the closest earlier one; None if unknown."""
    entries = _quests().get((tag or "").lower(), ("", {}))[1]
    if state in entries:
        return entries[state]
    earlier = [i for i in entries if i <= state]
    return entries[max(earlier)] if earlier else None


def journal_text(tag, state):
    entry = journal_entry(tag, state)
    return entry[0] if entry else ""


def _module_resources(module):
    return [r for archive in _g().module(module) for r in archive]


def area_name(module):
    if not module:
        return ""
    module = module.lower()
    if module not in _areas:
        _areas[module] = ""
        try:
            for r in _module_resources(module):
                if r.ext == "are":
                    _areas[module] = _text(kotorfiles.GFF(r.read(), r.name).root.get("Name"))
                    break
        except Exception:
            pass
    return _areas[module]


def creature_names(module):
    """{lowercase tag: display name} for the creatures in the module's GIT. A template's Tag is
    what the live object reports; the name is its first and last name strings."""
    if not module:
        return {}
    module = module.lower()
    if module not in _creatures:
        names = _creatures[module] = {}
        try:
            g = _g()
            resources = _module_resources(module)
            by_name = {r.name.lower(): r for r in resources}
            git = next((r for r in resources if r.ext == "git"), None)
            if git is not None:
                for c in kotorfiles.GFF(git.read(), git.name).root.get("Creature List") or []:
                    template = by_name.get((c.get("TemplateResRef") or "").lower() + ".utc")
                    if template is None:
                        continue
                    root = kotorfiles.GFF(template.read(), template.name).root
                    tag = (root.get("Tag") or "").lower()
                    name = " ".join(t for t in (_text(root.get("FirstName")), _text(root.get("LastName"))) if t)
                    if tag and name:
                        names.setdefault(tag, name)
        except Exception:
            pass
    return _creatures[module]


def creature_name(module, tag):
    return creature_names(module).get((tag or "").lower(), "")
