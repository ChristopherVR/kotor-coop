"""Offline guard tests; no game, model or Windows APIs required."""
import unittest
from laya_playtest import Game, MemoryError, candidates


class PlaytestGuards(unittest.TestCase):
    def test_writer_cannot_touch_engine_or_acknowledgements(self):
        game = object.__new__(Game)
        game.check_block = lambda: None
        # These must fail before reaching any Windows API, including boundary crossings.
        for offset, size in ((0, 4), (0x9F3, 4), (0xA54, 8), (0x3364, 4),
                             (0x3320, 4), (0x1D90, 8), (0x2260, 4)):
            with self.subTest(offset=offset), self.assertRaises(MemoryError):
                game.write(offset, bytes(size))

    def test_model_cannot_supply_save_warp_or_shell_commands(self):
        game = object.__new__(Game)
        for command in ("save 1", "warp tar_m02aa", "tp 1 2 3", "spawncreature n_sithsoldier", "powershell"):
            with self.subTest(command=command), self.assertRaises(ValueError):
                game.console(command)

    def test_talk_cannot_inject_or_close_dialogue(self):
        game = object.__new__(Game)
        for tag in ("close", "npc;warp", "x" * 32):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                game.console("talktag " + tag)

    def test_loading_and_death_do_not_generate_game_actions(self):
        self.assertEqual(candidates({"loading": True}, {})["wait"]["commands"], [])
        self.assertEqual(candidates({"loading": False, "hp": 0}, {}), {})

    def test_dialogue_prevents_combat_and_movement(self):
        state = {"loading": False, "hp": 5, "conversation": True, "reply_count": 3}
        choices = candidates(state, {})
        self.assertEqual(list(choices), ["reply_1", "reply_2", "reply_3"])
        self.assertEqual(choices["reply_3"]["commands"], ["input key 4 tap"])

    def test_walk_input_is_bounded_and_released(self):
        state = {"loading": False, "hp": 5, "conversation": False, "module": "end_m01aa",
                 "position": [1, 1, 0], "orientation": [1, 0, 0],
                 "objects": [{"id": 3, "kind": "door", "tag": "door", "position": [10, 1, 0],
                              "distance": 9, "open": 0, "destination": ""}]}
        action = candidates(state, {})["walk_3"]
        self.assertEqual(action["commands"], ["input key 17 down", "input key 17 up"])
        self.assertLessEqual(action["hold_seconds"], 1)
        self.assertEqual(candidates(state, {"end_m01aa:3": 3}), {})


if __name__ == "__main__":
    unittest.main()
