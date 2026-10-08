"""Tests for kotor_lore against the real GOG install; skipped when it is not there.

  python -m unittest discover -s tools -p test_kotor_lore.py
"""
import unittest

import kotor_lore as lore


@unittest.skipUnless(lore.game_dir_present(), "KOTOR install not found")
class Lore(unittest.TestCase):
    def test_the_first_quest_resolves(self):
        self.assertEqual(lore.quest_name("end_attack"), "Attack on the Endar Spire")
        self.assertIn("Carth Onasi", lore.journal_text("END_ATTACK", 20))

    def test_an_unlisted_state_uses_the_closest_earlier_entry(self):
        self.assertEqual(lore.journal_text("end_attack", 25), lore.journal_text("end_attack", 20))
        self.assertEqual(lore.journal_text("no_such_quest", 10), "")

    def test_area_and_character_names(self):
        self.assertIn("Endar Spire", lore.area_name("end_m01aa"))
        self.assertEqual(lore.creature_name("end_m01aa", "end_trask"), "Trask")
        self.assertEqual(lore.creature_name("end_m01aa", "Carth"), "Carth")
        self.assertEqual(lore.creature_names("not_a_module"), {})


if __name__ == "__main__":
    unittest.main()
