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
        me = player(conversation=True, reply_count=2, partner_tag="end_trask", dialogue_node=12,
                    reply_texts=["Okay.", "How do you use security on a door?"])
        tried = {"said:end_trask:12:How do you use security on a door?": 2}
        self.assertEqual(list(h.actions_for("host", me, player(), tried)), ["reply_1"])
        tried["said:end_trask:12:Okay."] = 2
        self.assertEqual(list(h.actions_for("host", me, player(), tried)), ["wait"])
        # The same words on another node are a fresh answer.
        self.assertIn("reply_1", h.actions_for("host", dict(me, dialogue_node=13), player(), tried))

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


class StubNav:
    rooms = {"r1": (0.0, 0.0), "r2": (20.0, 0.0)}

    def __init__(self):
        self.calls = []

    def path(self, start, goal, blocked=()):
        self.calls.append((goal, tuple(blocked)))
        return [tuple(start), ((start[0] + goal[0]) / 2, start[1]), tuple(goal)]


class Walking(unittest.TestCase):
    def test_a_far_closed_door_gets_a_walk_that_stops_short_of_it(self):
        door = {"id": 5, "kind": "door", "tag": "d", "position": [10, 0, 0], "distance": 10, "open": 0, "destination": ""}
        acts = h.walk_actions(player(position=[0, 0, 0]), StubNav(), {}, [door])
        walk = acts["goto_5"]["walk"]
        self.assertAlmostEqual(walk[-1][0], 8.0)
        self.assertEqual(acts["goto_5"]["commands"], [])

    def test_routes_avoid_other_closed_doors_and_skip_visited_rooms(self):
        nav = StubNav()
        a = {"id": 5, "kind": "door", "tag": "a", "position": [10, 0, 0], "distance": 10, "open": 0, "destination": ""}
        b = {"id": 6, "kind": "door", "tag": "b", "position": [0, 9, 0], "distance": 9, "open": 0, "destination": ""}
        acts = h.walk_actions(player(position=[0, 0, 0]), nav, {"visited": {"r1"}}, [a, b])
        self.assertIn(((10, 0), ((0, 9),)), nav.calls)
        self.assertIn("explore_r2", acts)
        self.assertNotIn("explore_r1", acts)

    def test_no_walkmesh_means_no_walking(self):
        self.assertEqual(h.walk_actions(player(), None, {}, []), {})


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


def door(tag, x, opened, locked, oid=1):
    return {"id": oid, "kind": "door", "tag": tag, "position": [x, 0, 0], "distance": x, "open": opened,
            "locked": locked, "destination": ""}


def box(tag, x, opened, locked, container=True):
    return {"id": 50, "kind": "placeable", "tag": tag, "position": [x, 0, 0], "distance": x, "open": opened,
            "locked": locked, "container": container}


class SharedState(unittest.TestCase):
    def test_a_door_that_agrees_is_fine_and_open_poses_are_equivalent(self):
        host = player(objects=[door("d1", 3.04, 1, False)])
        joiner = player(objects=[door("d1", 3.04, 2, False)])
        self.assertEqual(h.door_differences(host, joiner), {})

    def test_a_door_open_on_one_side_or_locked_on_one_side_is_a_difference(self):
        host = player(objects=[door("d1", 3, 1, False), door("d2", 9, 0, True)])
        joiner = player(objects=[door("d1", 3, 0, False), door("d2", 9, 0, False)])
        diffs = h.door_differences(host, joiner)
        self.assertEqual(sorted(diffs), ["d1@3,0", "d2@9,0"])
        self.assertIn("host open/unlocked, joiner closed/unlocked", diffs["d1@3,0"])

    def test_ambiguous_or_unmatched_doors_are_not_guessed(self):
        host = player(objects=[door("d", 3, 1, False), door("d", 3, 1, False, oid=2), door("only_host", 5, 1, False)])
        joiner = player(objects=[door("d", 3, 0, False), door("d", 3, 0, False, oid=2)])
        self.assertEqual(h.door_differences(host, joiner), {})

    def test_only_containers_are_compared_and_open_or_lock_counts(self):
        host = player(objects=[box("chest", 4, True, False), box("lever", 8, True, False, container=False)])
        joiner = player(objects=[box("chest", 4, False, False), box("lever", 8, False, False, container=False)])
        self.assertEqual(list(h.container_differences(host, joiner)), ["chest@4,0"])

    def test_enemy_health_within_tolerance_agrees_and_beyond_it_differs(self):
        a = dict(creature(7, "n_sith", 1, 5, hp=40), max_hp=50)
        close = dict(a, hp=44)
        far = dict(a, hp=20)
        self.assertEqual(h.enemy_hp_differences(player(objects=[a]), player(objects=[close])), {})
        self.assertEqual(list(h.enemy_hp_differences(player(objects=[a]), player(objects=[far]))), ["n_sith#7"])

    def test_an_enemy_dead_on_one_side_only_differs_even_at_low_health(self):
        a = dict(creature(7, "n_sith", 1, 5, hp=2), max_hp=50)
        dead = dict(a, hp=0)
        diffs = h.enemy_hp_differences(player(objects=[a]), player(objects=[dead]))
        self.assertIn("dead on one side", diffs["n_sith#7"])
        both_dead = h.enemy_hp_differences(player(objects=[dead]), player(objects=[dead]))
        self.assertEqual(both_dead, {})

    def test_stand_ins_party_members_and_unmatched_enemies_are_not_compared(self):
        host = player(objects=[creature(1, "coop_phantom", 1, 3, hp=10), creature(2, "end_trask", 1, 3, hp=10, party=True),
                               creature(3, "only_host", 1, 3, hp=10), creature(4, "n_sith", 1, 3, hp=10)])
        joiner = player(objects=[creature(1, "coop_phantom", 1, 3, hp=1), creature(2, "end_trask", 1, 3, hp=1, party=True),
                                 creature(4, "other_tag", 1, 3, hp=1)])
        self.assertEqual(h.enemy_hp_differences(host, joiner), {})

    def test_the_hosts_companions_must_exist_as_party_members_on_the_joiner(self):
        trask = creature(4, "end_trask", 5, 2, party=True)
        host = player(objects=[trask, creature(1, "coop_phantom", 2, 1), creature(2, "", 0, 0, party=True)])
        self.assertEqual(h.party_differences(host, player(objects=[dict(trask, id=9), creature(3, "", 0, 0, party=True)])), {})
        missing = h.party_differences(host, player(objects=[]))
        self.assertEqual(list(missing), ["missing_on_joiner:end_trask"])
        extra = h.party_differences(player(objects=[]), player(objects=[trask]))
        self.assertEqual(list(extra), ["extra_on_joiner:end_trask"])

    def peers(self, slot_health=20, peer_hp=20, phantom_hp=20, **peer_extra):
        slot = {"peer": 2, "position": [1.0, 0, 0], "health": slot_health, "max_health": 40}
        host = player(remote_slots=[slot], objects=[creature(9, "coop_phantom", 2, 1, hp=phantom_hp)])
        joiner = player(hp=peer_hp, max_hp=40, remote_slots=[dict(slot, health=15)],
                        objects=[creature(8, "coop_phantom", 2, 1, hp=15)], **peer_extra)
        host["hp"], host["max_hp"] = 15, 40
        return host, joiner

    def test_the_peer_mirror_agrees_within_tolerance(self):
        host, joiner = self.peers(slot_health=20, peer_hp=21, phantom_hp=20)
        self.assertEqual(h.peer_health_differences(host, joiner), {})

    def test_a_stale_slot_or_a_stand_in_with_the_wrong_health_is_a_difference(self):
        host, joiner = self.peers(slot_health=30, peer_hp=20, phantom_hp=30)
        self.assertEqual(list(h.peer_health_differences(host, joiner)), ["host:slot"])
        host, joiner = self.peers(slot_health=20, peer_hp=20, phantom_hp=35)
        self.assertEqual(list(h.peer_health_differences(host, joiner)), ["host:phantom"])

    def test_a_downed_or_companion_controlling_peer_is_skipped(self):
        host, joiner = self.peers(slot_health=1, peer_hp=0, phantom_hp=1)
        self.assertEqual(h.peer_health_differences(host, joiner), {})
        host, joiner = self.peers(slot_health=30, peer_hp=20, phantom_hp=30, controlled_tag="end_trask")
        self.assertEqual(h.peer_health_differences(host, joiner), {})
        host, joiner = self.peers()
        host["remote_slots"] = None
        self.assertEqual(h.peer_health_differences(host, joiner), {})

    def test_journal_entries_must_converge_and_a_missing_entry_is_a_difference(self):
        host = player(journal={"end_attack": 20, "tar_quest": 10})
        self.assertEqual(h.journal_differences(host, player(journal={"end_attack": 20, "tar_quest": 10})), {})
        diffs = h.journal_differences(host, player(journal={"end_attack": 10}))
        self.assertEqual(sorted(diffs), ["end_attack", "tar_quest"])
        self.assertIn("joiner absent", diffs["tar_quest"])

    def test_globals_treat_absent_as_zero_and_name_what_differs(self):
        host = player(globals={"END_ROOM7_DEAD": 1, "K_SWG_FUEL": 0})
        joiner = player(globals={"END_BRIDGE_SEALED": 1})
        self.assertEqual(sorted(h.globals_differences(host, joiner)), ["END_BRIDGE_SEALED", "END_ROOM7_DEAD"])
        self.assertEqual(h.globals_differences(player(globals={"A": 0}), player(globals={})), {})

    def test_an_unreadable_journal_global_or_xp_skips_the_check(self):
        for key, check in (("journal", h.journal_differences), ("globals", h.globals_differences), ("xp", h.xp_differences)):
            self.assertEqual(check(player(**{key: None}), player(**{key: {} if key != "xp" else 5})), {})
            self.assertEqual(check(player(), player()), {})

    def test_experience_must_stay_level_only_for_players_who_started_level(self):
        level = {"xp_started_equal": True}
        self.assertEqual(h.xp_differences(player(xp=300, **level), player(xp=300)), {})
        self.assertEqual(list(h.xp_differences(player(xp=300, **level), player(xp=150))), ["pc"])
        self.assertEqual(h.xp_differences(player(xp=300, controlled_tag="end_trask", **level), player(xp=150)), {})
        # A veteran joining ahead keeps its lead (progression-sync.md, 2026-09-18).
        self.assertEqual(h.xp_differences(player(xp=0, xp_started_equal=False), player(xp=1825)), {})


class RefereeSharedState(unittest.TestCase):
    def setUp(self):
        self.ref = object.__new__(h.Referee)
        self.ref.since, self.ref.reported, self.ref.findings = {}, set(), []
        self.flagged = []
        self.ref.flag = lambda key, detail, states: (self.ref.reported.add(key), self.flagged.append((key, detail)))

    def test_each_item_has_its_own_settle_window_and_a_recovery_resets_it(self):
        diffs = {"d1": "door d1 differs"}
        self.ref.persist_each("door", diffs, [], 0.0)
        self.ref.persist_each("door", diffs, [], h.SETTLE["door"] - 1)
        self.assertEqual(self.flagged, [])
        self.ref.persist_each("door", {}, [], h.SETTLE["door"])          # agreed again
        self.assertEqual(self.ref.since, {})
        self.ref.persist_each("door", diffs, [], h.SETTLE["door"] + 1)   # window restarts
        self.ref.persist_each("door", diffs, [], h.SETTLE["door"] + 2)
        self.assertEqual(self.flagged, [])
        self.ref.persist_each("door", diffs, [], 2 * h.SETTLE["door"] + 1)
        self.assertEqual(self.flagged, [("door:d1", "door d1 differs")])

    def test_one_kind_stops_reporting_after_its_cap(self):
        diffs = {f"g{i}": "x" for i in range(h.MAX_PER_KIND + 3)}
        self.ref.persist_each("globals", diffs, [], 0.0)
        self.ref.persist_each("globals", diffs, [], h.SETTLE["globals"])
        self.assertEqual(len(self.flagged), h.MAX_PER_KIND)

    def test_every_check_has_a_settle_window(self):
        for kind, _ in h.SHARED_STATE_CHECKS:
            self.assertIn(kind, h.SETTLE)


if __name__ == "__main__":
    unittest.main()
