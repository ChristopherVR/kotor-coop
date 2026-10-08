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
        self.assertEqual(list(h.actions_for("joiner", container, player(), {})), ["take_items"])
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

    def test_door_reach_follows_the_plugin_limits(self):
        far = {"id": 9, "kind": "door", "tag": "far", "position": [120, 0, 0], "distance": 120, "open": 0, "destination": "x"}
        self.assertFalse([k for k in h.actions_for("host", player(objects=[far]), player(), {}) if k.endswith("_9")])
        mid = dict(far, distance=60)
        self.assertEqual([k for k in h.actions_for("host", player(objects=[mid]), player(), {}) if k.endswith("_9")], ["exit_9"])

    def test_popups_are_dismissed_with_their_own_buttons(self):
        self.assertEqual(list(h.actions_for("host", player(menu_open=True, panels=["status_summary"]), player(), {})), ["ok_summary"])
        self.assertEqual(list(h.actions_for("host", player(menu_open=True, panels=["x", "message_box"]), player(), {})), ["ok_message"])
        party = player(menu_open=True, panels=["party_selection"])
        self.assertEqual(list(h.actions_for("host", party, player(), {})), ["party_ok"])
        self.assertEqual(h.actions_for("host", party, player(), {"party_ok": 1})["party_add"]["commands"],
                         [h.PARTY_FIRST, h.PARTY_ADD, h.PARTY_OK])

    def test_equip_is_offered_once_per_character_and_never_in_a_fight(self):
        self.assertNotIn("equip", h.actions_for("host", player(), player(), {}))  # nothing taken yet
        got = {"took_any": True}
        self.assertIn("equip", h.actions_for("host", player(), player(), got))
        self.assertNotIn("equip", h.actions_for("host", player(), player(), got | {"equip:pc": 1}))
        self.assertIn("equip", h.actions_for("host", player(controlled_tag="end_trask"), player(), got | {"equip:pc": 1}))
        fight = player(objects=[creature(3, "n_sith", 1, 5)])
        self.assertNotIn("equip", h.actions_for("host", fight, player(), got))

    def test_only_the_host_swaps_and_a_companion_can_retry_a_door(self):
        trask = creature(4, "end_trask", 5, 2, party=True)
        door = {"id": 7, "kind": "door", "tag": "door", "position": [3, 0, 0], "distance": 3, "open": 0, "destination": ""}
        me = player(player_id=1, objects=[trask, door])
        tried = {"end_m01aa:7:pc": 2}
        acts = h.actions_for("host", me, player(), tried)
        self.assertNotIn("open_7", acts)
        self.assertEqual(acts["control_end_trask"]["commands"], [h.HUD_SECOND_PORTRAIT])
        self.assertNotIn("control_end_trask", h.actions_for("joiner", me, player(), tried))
        as_trask = h.actions_for("host", player(player_id=4, controlled_tag="end_trask", objects=[door]), player(), tried)
        self.assertEqual(list(as_trask), ["open_7"])
        later = h.actions_for("host", player(player_id=4, controlled_tag="end_trask", objects=[door]), player(),
                              tried | {"acted_as_companion": 2})
        self.assertIn("control_pc", later)
        self.assertNotIn("control_end_trask", h.actions_for("host", me, player(), {}))

    def test_a_full_container_must_be_emptied_before_it_closes(self):
        box = player(menu_open=True, panels=["main_interface", "container"])
        self.assertEqual(list(h.actions_for("host", box, player(), {})), ["take_items"])
        self.assertEqual(list(h.actions_for("host", box, player(), {"took": True})), ["close_menu"])

    def test_the_joiner_never_starts_a_conversation(self):
        me = player(objects=[creature(4, "end_trask", 5, 2)])
        self.assertIn("talk_4", h.actions_for("host", me, player(), {}))
        self.assertNotIn("talk_4", h.actions_for("joiner", me, player(), {}))

    def test_a_reply_picked_twice_is_not_offered_again(self):
        me = player(conversation=True, reply_count=2, partner_tag="end_trask",
                    reply_texts=["Okay.", "How do you use security on a door?"])
        tried = {"said:end_trask:How do you use security on a door?": 2}
        self.assertEqual(list(h.actions_for("host", me, player(), tried)), ["reply_1"])
        tried["said:end_trask:Okay."] = 2
        self.assertEqual(list(h.actions_for("host", me, player(), tried)), ["wait"])

    def test_a_notice_is_dismissed_even_during_a_conversation(self):
        me = player(conversation=True, reply_count=0, panels=["x", "status_summary"])
        self.assertEqual(list(h.actions_for("joiner", me, player(), {})), ["ok_summary"])

    def test_a_locked_door_offers_security_and_a_plain_try(self):
        door = {"id": 0x46, "kind": "door", "tag": "end_door02", "position": [5, 0, 0], "distance": 5, "open": 0,
                "destination": "", "locked": True}
        acts = h.actions_for("host", player(objects=[door]), player(), {})
        self.assertEqual([k for k in acts if k.endswith("_70")], ["open_70", "unlock_70"])
        self.assertEqual(acts["unlock_70"]["commands"], ["unlockdoor 0x46"])
        self.assertNotIn("unlock_70", h.actions_for("host", player(objects=[door]), player(), {"end_m01aa:70:pc:unlock": 2}))

    def test_level_up_is_offered_once_until_new_experience(self):
        acts = h.actions_for("host", player(), player(), {})
        self.assertEqual(acts["level_up"]["commands"], [h.HUD_CHARACTER, h.CHARACTER_AUTO_LEVEL])
        self.assertNotIn("level_up", h.actions_for("host", player(), player(), {"level:pc": 1}))
        self.assertNotIn("level_up", h.actions_for("host", player(objects=[creature(3, "n_sith", 1, 5)]), player(), {}))

    def test_refresh_forgets_doors_and_levels_but_not_talks(self):
        tried = {"end_m01aa:7:pc": 2, "end_m01aa:7:pc:unlock": 2, "level:pc": 1, "swaps": 3,
                 "talk:end_m01aa:4": 3, "took_any": True, "end_m01aa:198": 1}
        h.refresh_doors(tried, player())
        self.assertEqual(tried, {"talk:end_m01aa:4": 3, "took_any": True, "end_m01aa:198": 1})

    def test_an_open_transition_is_offered_ahead_of_nearer_dead_ends(self):
        doors = [{"id": i, "kind": "door", "tag": f"d{i}", "position": [i, 0, 0], "distance": i, "open": 0,
                  "destination": "", "locked": True} for i in range(1, 6)]
        doors.append({"id": 74, "kind": "door", "tag": "end_door10", "position": [23, 0, 0], "distance": 23,
                      "open": 2, "destination": "end_m01ab"})
        acts = h.actions_for("host", player(objects=doors), player(), {})
        self.assertEqual([k for k in acts if k.startswith(("open_", "exit_"))][0], "exit_74")

    def test_loading_and_downed_players_do_nothing(self):
        self.assertEqual(h.actions_for("host", player(loading=True), player(), {}), {})
        self.assertEqual(h.actions_for("host", player(hp=0), player(), {}), {})


class Census(unittest.TestCase):
    def test_a_host_spawn_matched_by_a_joiner_stand_in_is_not_a_difference(self):
        host = player(objects=[creature(1, "end_reinforce3", 1, 5), creature(9, "coop_phantom", 2, 1)])
        joiner = player(objects=[creature(7, "coop_phantom", 2, 5), creature(8, "coop_phantom", 2, 1)])
        self.assertFalse(h.census_difference(host, joiner)[0])

    def test_a_host_enemy_with_nothing_on_the_joiner_is_a_difference(self):
        host = player(objects=[creature(1, "end_reinforce3", 1, 5), creature(9, "coop_phantom", 2, 1)])
        joiner = player(objects=[creature(8, "coop_phantom", 2, 1)])
        self.assertTrue(h.census_difference(host, joiner)[0])


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
