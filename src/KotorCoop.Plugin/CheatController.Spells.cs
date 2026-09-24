namespace KotorCoop.Plugin;

// The `cast`/`grantpower` live-test primitives (docs/research/effect-sync.md §14): makes the
// LOCAL PLAYER cast a spells.2da row at a target through the engine's own action queue
// (Odyssey.CastSpellAtObject - the verified ActionCastSpellAtObject VM routine, NOT a
// synthetic effect apply), so partner_buffs/enemy_effect_sync's capture hooks - which require
// the effect's creator to be the local player - can finally be exercised by a REAL cast
// instead of the `applyeffect` staging verb's creator 0x7F000000. grantpower is the companion
// verb, for a save whose class doesn't already know the power under test.
internal sealed unsafe partial class CheatController
{
    private void CastSpell(uint playerId)
    {
        int spellId = block.CheatAmount;
        uint targetId = block.CheatTarget;
        int metaMagic = (int)block.CheatItemCharges;

        if (spellId < 0)
        {
            Answer(SharedBlock.CheatStatusRefused, $"cast: {spellId} is not a spells.2da row");
            return;
        }

        if (targetId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "cast needs a target - an object id or 'me'");
            return;
        }

        bool applied = Odyssey.CastSpellAtObject(playerId, spellId, targetId, metaMagic, out int failure);
        if (!applied)
        {
            string reason = failure switch
            {
                1 => "no VM available",
                2 => "no routine table",
                3 => "routine table entry does not match the verified cast handler (build mismatch?)",
                4 => "cast handler bytes do not match the verified build",
                5 => "a script currently owns the VM stack - try again next frame",
                6 => "VM stack push refused",
                7 => "the engine refused the cast (unknown/unlearned power, out of Force points, "
                    + "out of range, or no line of sight - see 'grantpower' if the power isn't known yet)",
                _ => $"engine call refused (failure {failure})",
            };

            PluginHost.Log($"cast: spell {spellId} at 0x{targetId:X} (metamagic {metaMagic}) refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"spell {spellId} at 0x{targetId:X} refused - {reason}");
            return;
        }

        PluginHost.Log($"cast: queued spell {spellId} at 0x{targetId:X} (metamagic {metaMagic}) for the local player");
        Answer(SharedBlock.CheatStatusApplied, $"queued spell {spellId} at 0x{targetId:X} (metamagic {metaMagic})");
    }

    private void GrantPower(uint playerId)
    {
        int spellId = block.CheatAmount;
        int classIndex = (int)block.CheatItemCharges;

        if (spellId < 0)
        {
            Answer(SharedBlock.CheatStatusRefused, $"grantpower: {spellId} is not a spells.2da row");
            return;
        }

        bool granted = Odyssey.TryGrantKnownSpell(playerId, classIndex, spellId, out int failure);
        if (!granted)
        {
            string reason = failure switch
            {
                1 => "local player does not resolve",
                2 => "local player has no stats block",
                3 => $"class slot {classIndex} does not exist (see 'addclass', or the class count 'cheats' prints)",
                4 => "grant-spell handler bytes do not match the verified build",
                _ => $"engine call refused (failure {failure})",
            };

            PluginHost.Log($"grantpower: spell {spellId} class {classIndex} refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"spell {spellId} class {classIndex} refused - {reason}");
            return;
        }

        PluginHost.Log($"grantpower: spell {spellId} added to the local player's class {classIndex} known-spell list");
        Answer(SharedBlock.CheatStatusApplied, $"spell {spellId} added to class {classIndex}'s known powers");
    }
}
