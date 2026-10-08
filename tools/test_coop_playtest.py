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

    def test_a_container_is_offered_by_its_in_game_name(self):
        # A raw tag ('end_locker01') lost to a dozen door options; the footlocker held the gear.
        class Lore:
            @staticmethod
            def placeable_name(module, tag):
                return ("Footlocker", True) if tag == "end_locker01" else ("", False)
        saved, h.kotor_lore = h.kotor_lore, Lore
        try:
            locker = {"id": 198, "kind": "placeable", "tag": "end_locker01", "distance": 9.5,
                      "position": [9.5, 0, 0], "open": False, "locked": False}
            acts = h.actions_for("host", player(objects=[locker]), player(), {})
            self.assertIn("Footlocker (end_locker01), holds items", acts["search_198"]["description"])
            self.assertEqual(h.what_is("end_m01aa", "chair2"), "chair2")
        finally:
            h.kotor_lore = saved

    def test_a_menu_over_a_conversation_is_closed_before_space_is_pressed(self):
        # 2026-10-08: a character sheet over a bark; Space under it toggled pause instead.
        me = player(conversation=True, menu_open=True, panels=["character", "menu"])
        self.assertEqual(list(h.actions_for("joiner", me, player(), {})), ["close_menu"])

    def test_a_paused_game_is_unpaused_with_the_pause_key(self):
        me = player(conversation=True, menu_open=True, panels=["pause", "main_interface"])
        acts = h.actions_for("joiner", me, player(), {})
        self.assertEqual(list(acts), ["unpause"])
        self.assertEqual(acts["unpause"]["commands"], [f"input key {h.DIK_SPACE} down", f"input key {h.DIK_SPACE} up"])

    def test_an_open_menu_is_handled_before_anything_else(self):
        container = player(menu_open=True, panels=["main_interface", "container"], objects=[creature(5, "n_sith", 1, 4)])
        self.assertEqual(list(h.actions_for("joiner", container, player(), {})), ["take_items"])
        self.assertEqual(list(h.actions_for("joiner", container, player(), {"took": True})), ["close_menu"])
        equip = player(menu_open=True, panels=["equip", "menu"])
        self.assertEqual(h.actions_for("host", equip, player(), {})["close_menu"]["commands"], ["closegui"])

    def test_phantoms_and_party_members_are_never_targets(self):
        me = player(objects=[creature(1, "coop_phantom", 1, 3), creature(2, "end_trask", 1, 3, party=True),
                             creature(3, "n_sith", 1, 2)])
        acts = h.actions_for("host", me, player(), {})
        self.assertEqual([k for k in acts if k.startswith("fight_")], ["fight_3"])

    def test_a_far_enemy_is_walked_to_and_an_unreachable_one_is_not_offered(self):
        # The attack verb's melee snap is a teleport; it once dropped the host into a sealed
        # cutscene room behind a window. Fights walk the walkmesh instead.
        class Nav:
            rooms = {}
            def path(self, start, goal, blocked=()):
                return [start, (5.0, 0.0), goal] if goal[0] < 20 else None
        me = player(objects=[creature(3, "n_sith", 1, 10), creature(4, "end_cut2_sith2", 1, 26)])
        acts = h.actions_for("host", me, player(), {}, nav=Nav())
        self.assertNotIn("fight_4", acts)
        walk = acts["fight_3"]["walk"]
        self.assertEqual(walk[:2], [(0, 0), (5.0, 0.0)])
        self.assertAlmostEqual(walk[-1][0], 8.8, places=3)
        self.assertEqual(acts["fight_3"]["commands"], ["attack 0x3"])
        self.assertNotIn("fight_3", h.actions_for("host", me, player(), {}))  # no walkmesh, no fight

    def test_the_joiner_is_offered_the_partners_conversation_and_never_doors(self):
        door = {"id": 7, "kind": "door", "tag": "door", "position": [10, 0, 0], "distance": 10, "open": 0, "destination": "end_m01ab"}
        me = player(peer_partner_tag="end_trask", objects=[door, creature(4, "end_trask", 2, 3)])
        acts = h.actions_for("joiner", me, player(conversation=True), {})
        self.assertIn("join_end_trask", acts)
        self.assertNotIn("walk_7", acts)
        self.assertNotIn("join_end_trask", h.actions_for("joiner", me, player(conversation=True), {"join:end_trask": 2}))

    def test_a_cutscene_on_an_invisible_scene_object_is_not_joined_by_clicking(self):
        # end_room5 runs on end01_sceneobj02, an invisible placeable: nobody to click, and a late
        # click restarted the cutscene as a second run that stalled (2026-10-09).
        scene = {"id": 9, "kind": "placeable", "tag": "end01_sceneobj02", "distance": 3,
                 "position": [3, 0, 0], "open": False, "locked": False}
        me = player(peer_partner_tag="end01_sceneobj02", objects=[scene])
        self.assertNotIn("join_end01_sceneobj02", h.actions_for("joiner", me, player(conversation=True), {}))

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

    def test_level_up_is_offered_until_tried_and_never_in_a_fight(self):
        acts = h.actions_for("host", player(xp=100), player(), {})
        self.assertEqual(acts["level_up"]["commands"], [h.HUD_CHARACTER, h.CHARACTER_AUTO_LEVEL])
        self.assertNotIn("level_up", h.actions_for("host", player(xp=100), player(), {"level:pc": 1000}))
        self.assertNotIn("level_up", h.actions_for("host", player(objects=[creature(3, "n_sith", 1, 5)]), player(), {}))

    def test_refresh_forgets_doors_but_not_talks_containers_or_level_gates(self):
        tried = {"end_m01aa:7:pc": 2, "end_m01aa:7:pc:unlock": 2, "level:pc": 3000, "swaps": 3,
                 "talk:end_m01aa:4": 3, "took_any": True, "end_m01aa:198": 1}
        h.refresh_doors(tried, player())
        self.assertEqual(tried, {"talk:end_m01aa:4": 3, "took_any": True, "end_m01aa:198": 1, "level:pc": 3000})

    def test_level_up_waits_for_the_next_threshold(self):
        self.assertEqual(h.next_level_xp(1825), 3000)
        self.assertEqual(h.next_level_xp(3000), 6000)
        # A veteran past level 20 never levels again; the gate must stay shut, not reopen at 190001.
        self.assertGreater(h.next_level_xp(7320369), 7320369)
        self.assertNotIn("level_up", h.actions_for("host", player(xp=2999), player(), {"level:pc": 3000}))
        self.assertIn("level_up", h.actions_for("host", player(xp=3000), player(), {"level:pc": 3000}))

    def test_a_popup_ok_that_misses_moves_down_the_rows(self):
        first = h.actions_for("host", player(menu_open=True, panels=["status_summary"]), player(), {})
        again = h.actions_for("host", player(menu_open=True, panels=["status_summary"]), player(), {"ok:status_summary": 1})
        self.assertEqual(first["ok_summary"]["commands"], ["input click 399 316"])
        self.assertEqual(again["ok_summary"]["commands"], ["input click 399 333"])

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


class StuckScene(unittest.TestCase):
    def test_a_conversation_frozen_on_one_node_in_both_games_is_a_finding(self):
        ref = object.__new__(h.Referee)
        ref.since, ref.reported, ref.findings, ref.join_wanted = {}, set(), [], {}
        ref.games, ref.log_offsets = [], {}
        flagged = []
        ref.flag = lambda key, detail, states: flagged.append(key)
        talking = dict(conversation=True, dialogue_node=7, partner_tag="saul")
        import unittest.mock as m
        with m.patch.object(h.time, "monotonic", side_effect=[0.0, h.SETTLE["stuckdialogue"] + 1]):
            ref.observe(player(**talking), player(**talking))
            ref.observe(player(**talking), player(**talking))
        self.assertTrue(any(k.startswith("stuckdialogue:") for k in flagged))


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


class FakeLore:
    @staticmethod
    def journal_entry(tag, state):
        return {"end_attack": ("Carth ordered all hands to the bridge.", False), "old_done": ("All over.", True)}.get(tag)

    @staticmethod
    def quest_name(tag):
        return {"end_attack": "Attack on the Endar Spire"}.get(tag, "")

    @staticmethod
    def area_name(module):
        return "Endar Spire"

    @staticmethod
    def creature_name(module, tag):
        return {"end_trask": "Trask Ulgo"}.get(tag, "")


def a_choice():
    """A host with two real options: a door out and a person to talk to."""
    door = {"id": 7, "kind": "door", "tag": "end_door10", "position": [10, 0, 0], "distance": 10, "open": 1,
            "destination": "end_m01ab", "locked": False}
    me = player(journal={"end_attack": 20, "old_done": 10}, partner_tag="", dialogue_line="",
                objects=[door, creature(4, "end_trask", 2, 5)])
    return me, h.actions_for("host", me, player(), {})


class ClaudeBrain(unittest.TestCase):
    def setUp(self):
        import json
        import types
        import unittest.mock as m
        self.calls = []
        self.reply = {"result": 'Sure.\n{"choice": "talk_4", "plan": "Meet Trask", "why": "quest giver"}', "total_cost_usd": 0.01}

        def fake_run(cmd, **kw):
            self.calls.append((cmd, kw))
            return types.SimpleNamespace(returncode=0, stdout=json.dumps(self.reply), stderr="")
        for p in (m.patch.object(h.subprocess, "run", fake_run), m.patch.object(h, "kotor_lore", FakeLore)):
            p.start()
            self.addCleanup(p.stop)

    def policy(self, **kw):
        return h.ClaudePolicy(h.RulesPolicy(1), claude_cmd="claude", **kw)

    def choose(self, policy, me, acts, role="host"):
        import asyncio
        return asyncio.run(policy.choose(role, "text", acts, ctx={"me": me, "peer": player()}))

    def test_the_prompt_has_journal_place_options_and_no_tools(self):
        me, acts = a_choice()
        key, meta = self.choose(self.policy(), me, acts)
        self.assertEqual((key, meta["source"]), ("talk_4", "claude"))
        cmd, kw = self.calls[0]
        prompt = kw["input"]
        self.assertIn("Attack on the Endar Spire", prompt)
        self.assertIn("Carth ordered all hands to the bridge.", prompt)
        self.assertNotIn("All over.", prompt)  # a finished quest is not active
        self.assertIn("Endar Spire (module end_m01aa)", prompt)
        self.assertIn("Talk to Trask Ulgo (end_trask)", prompt)
        self.assertIn("exit_7:", prompt)
        self.assertLess(len(prompt), 6000)
        self.assertEqual(cmd[cmd.index("--tools") + 1], "")
        self.assertNotIn("--dangerously-skip-permissions", cmd)
        self.assertIn("sonnet", cmd)

    def test_a_screenshot_allows_only_read(self):
        me, acts = a_choice()
        policy = self.policy(screenshots=True)
        policy.shot_provider = lambda role: h.Path("C:/evidence/shot.png")
        self.choose(policy, me, acts)
        cmd, kw = self.calls[0]
        self.assertEqual(cmd[cmd.index("--tools") + 1], "Read")
        self.assertIn("shot.png", kw["input"])

    def test_routine_and_forced_situations_never_call_claude(self):
        policy = self.policy()
        me, acts = a_choice()
        fight = {"fight_3": {"description": "Attack", "commands": []}} | acts
        self.choose(policy, me, fight)
        self.choose(policy, me, {"continue": {"description": "c", "commands": []}, "wait": {"description": "w", "commands": []}})
        self.choose(policy, me, {k: v for k, v in acts.items() if k.startswith("talk_")})
        self.assertEqual(self.calls, [])

    def test_the_same_situation_reuses_the_answer_then_moves_to_an_untried_option(self):
        me, acts = a_choice()
        policy = self.policy()
        picks = [self.choose(policy, me, acts)[0] for _ in range(5)]
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(picks[:3], ["talk_4"] * 3)
        self.assertEqual(picks[3], "exit_7")
        me["journal"]["end_attack"] = 30  # the story moved: ask again
        self.choose(policy, me, acts)
        self.assertEqual(len(self.calls), 2)

    def test_the_budget_is_enforced_and_reported(self):
        me, acts = a_choice()
        policy = self.policy(budget=1)
        self.choose(policy, me, acts)
        me["journal"]["end_attack"] = 30
        key, meta = self.choose(policy, me, acts)
        self.assertEqual(len(self.calls), 1)
        self.assertIn("budget spent", meta["note"])
        report = policy.report()
        self.assertTrue(report["budget_spent"])
        self.assertEqual((report["calls"], report["answered"]), (1, 1))
        self.assertEqual(report["log"][0]["choice"], "talk_4")
        self.assertGreater(report["log"][0]["prompt_chars"], 100)

    def test_bad_replies_fall_back_to_rules(self):
        me, acts = a_choice()
        for reply in ({"result": "I think we should talk to Trask."}, {"result": '{"choice": "warp_to_bridge"}'}):
            self.reply = reply
            policy = self.policy()
            key, meta = self.choose(policy, me, acts)
            self.assertIn(key, acts)
            self.assertEqual(meta["source"], "rules")
            self.assertIn("error", policy.report()["log"][0])
        self.reply = {"result": "Not JSON at all {oops", "is_error": True}
        self.assertEqual(self.choose(self.policy(), me, acts)[1]["source"], "rules")

    def test_the_plan_and_history_are_carried_into_the_next_prompt(self):
        me, acts = a_choice()
        policy = self.policy()
        self.choose(policy, me, acts)
        policy.record("host", "talk_4", "Talk to Trask Ulgo", "conversation started")
        policy.note("host", "conversation with Trask Ulgo (end_trask) ended")
        me["journal"]["end_attack"] = 30
        self.choose(policy, me, acts)
        prompt = self.calls[1][1]["input"]
        self.assertIn("Meet Trask", prompt)
        self.assertIn("talk_4 (Talk to Trask Ulgo) -> conversation started", prompt)
        self.assertIn("ended", prompt)

    def test_the_prompt_stays_small_with_a_long_journal(self):
        me, acts = a_choice()
        me["journal"] = {f"quest{i}": 10 for i in range(20)}
        import unittest.mock as m
        with m.patch.object(FakeLore, "journal_entry", staticmethod(lambda tag, state: ("word " * 400, False))):
            self.assertLess(len(h.build_prompt("host", me, player(), acts)), 6000)

    def test_a_far_character_gets_a_walk_and_talk_option(self):
        class Nav:
            rooms = {}

            def path(self, a, b, blocked=()):
                return [a, (b[0] - 5, b[1]), b]
        me = player(position=[0, 0, 0], objects=[creature(9, "end_trask", 2, 30)])
        acts = h.actions_for("host", me, player(), {}, Nav())
        self.assertIn("gotalk_9", acts)
        self.assertEqual(acts["gotalk_9"]["commands"], ["talktag end_trask"])
        self.assertTrue(acts["gotalk_9"]["walk"])
        self.assertNotIn("gotalk_9", h.actions_for("host", me, player(), {"gotalk:end_m01aa:9": 2}, Nav()))
        self.assertNotIn("gotalk_9", h.actions_for("joiner", me, player(), {}, Nav()))


if __name__ == "__main__":
    unittest.main()


class SnapshotRetry(unittest.TestCase):
    class Flaky:
        def __init__(self, failures, exited=False):
            self.failures, self._exited, self.calls = failures, exited, 0

        def snapshot(self):
            self.calls += 1
            if self.calls <= self.failures:
                raise h.MemoryError("Unreadable game address 0x44460ff0")
            return {"module": "end_m01aa"}

        def exited(self):
            return self._exited

    def test_a_freed_object_mid_read_is_retried(self):
        # Under full page heap a creature freed during the read is unreadable at once.
        game = self.Flaky(failures=2)
        self.assertEqual(h.snapshot_retrying(game, pause=0)["module"], "end_m01aa")

    def test_an_exited_game_is_not_retried(self):
        with self.assertRaises(h.MemoryError):
            h.snapshot_retrying(self.Flaky(failures=1, exited=True), pause=0)
