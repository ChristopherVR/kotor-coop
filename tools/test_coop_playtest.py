"""Offline tests for the co-op playtest harness; no game, model or Windows APIs required.

  python -m unittest discover -s tools -p test_coop_playtest.py
"""
import unittest

import coop_playtest as h


def player(**extra):
    state = {"loading": False, "hp": 10, "module": "end_m01aa", "position": [0, 0, 0],
             "orientation": [1, 0, 0], "conversation": False, "reply_count": 0, "reply_texts": [],
             "menu_open": False, "peer_partner_tag": "", "remote_count": 1, "objects": []}
    state.update(extra)
    return state


def creature(oid, tag, faction, distance, hp=10, party=False):
    return {"id": oid, "kind": "creature", "tag": tag, "faction": faction, "hp": hp, "party": party,
            "distance": distance, "position": [distance, 0, 0]}


class Actions(unittest.TestCase):
    def test_replies_carry_their_text_and_use_number_keys(self):
        me = player(conversation=True, reply_count=2, reply_texts=["Okay.", "Can you repeat that?"])
        acts = h.actions_for("host", me, player(), {})
        self.assertEqual(list(acts), ["reply_1", "reply_2"])
        self.assertIn('"Okay."', acts["reply_1"]["description"])
        self.assertEqual(acts["reply_2"]["commands"], ["input key 3 tap"])

    def test_an_open_menu_is_handled_before_anything_else(self):
        container = player(menu_open=True, panels=["main_interface", "container"], objects=[creature(5, "n_sith", 1, 4)])
        self.assertEqual(list(h.actions_for("joiner", container, player(), {})), ["take_items", "close_menu"])
        self.assertEqual(list(h.actions_for("joiner", container, player(), {"took": True})), ["close_menu"])
        equip = player(menu_open=True, panels=["equip", "menu"])
        self.assertEqual(h.actions_for("host", equip, player(), {})["close_menu"]["commands"], ["closegui"])

    def test_phantoms_and_party_members_are_never_targets(self):
        me = player(objects=[creature(1, "coop_phantom", 1, 3), creature(2, "end_trask", 1, 3, party=True),
                             creature(3, "n_sith", 1, 5)])
        acts = h.actions_for("host", me, player(), {})
        self.assertEqual([k for k in acts if k.startswith("fight_")], ["fight_3"])

    def test_the_joiner_is_offered_the_partners_conversation_and_never_doors(self):
        door = {"id": 7, "kind": "door", "tag": "door", "position": [10, 0, 0], "distance": 10, "open": 0, "destination": "end_m01ab"}
        me = player(peer_partner_tag="end_trask", objects=[door])
        acts = h.actions_for("joiner", me, player(conversation=True), {})
        self.assertIn("join_end_trask", acts)
        self.assertNotIn("walk_7", acts)
        self.assertNotIn("join_end_trask", h.actions_for("joiner", me, player(conversation=True), {"join:end_trask": 2}))

    def test_a_far_door_is_approached_three_times_then_dropped(self):
        door = {"id": 9, "kind": "door", "tag": "far", "position": [60, 0, 0], "distance": 60, "open": 0, "destination": "x"}
        me = player(objects=[door])
        self.assertEqual(h.actions_for("host", me, player(), {})["approach_9"]["commands"], ["walkto 60.0 0.0"])
        self.assertNotIn("approach_9", h.actions_for("host", me, player(), {"end_m01aa:9:approach": 3}))

    def test_loading_and_downed_players_do_nothing(self):
        self.assertEqual(h.actions_for("host", player(loading=True), player(), {}), {})
        self.assertEqual(h.actions_for("host", player(hp=0), player(), {}), {})


class RefereeWindows(unittest.TestCase):
    def setUp(self):
        self.ref = object.__new__(h.Referee)
        self.ref.since, self.ref.reported, self.ref.findings = {}, set(), []
        self.flagged = []
        self.ref.flag = lambda key, detail, states: self.flagged.append(key)

    def test_a_difference_is_a_finding_only_after_its_settle_window(self):
        self.ref.persist("census", True, "x", [], 100.0)
        self.ref.persist("census", True, "x", [], 100.0 + h.SETTLE["census"] - 1)
        self.assertEqual(self.flagged, [])
        self.ref.persist("census", True, "x", [], 100.0 + h.SETTLE["census"])
        self.assertEqual(self.flagged, ["census"])

    def test_a_difference_that_clears_restarts_its_window(self):
        self.ref.persist("module", True, "x", [], 0.0)
        self.ref.persist("module", False, "x", [], 40.0)
        self.ref.persist("module", True, "x", [], 41.0)
        self.ref.persist("module", True, "x", [], 41.0 + h.SETTLE["module"] - 1)
        self.assertEqual(self.flagged, [])


if __name__ == "__main__":
    unittest.main()
