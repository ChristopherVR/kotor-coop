namespace KotorCoop.Plugin;

// The arena boss (docs/research/chungus-boss.md): the host spawns him, and every game plays his
// powers out for itself when the host says he used one. The agent keeps the time (wind-up now,
// landing a moment later) and asks for each step through this one cheat kind.
internal sealed unsafe partial class CheatController
{
    /// <summary>The boss this game's own engine spawned (host only), or 0.</summary>
    private static uint _chungusId;

    private void Chungus(nint player, uint playerId)
    {
        uint word = block.CheatTarget;
        int power = ChungusRule.PowerOf(word);

        switch (block.CheatAmount)
        {
            case ChungusRule.OpSpawn:
                ChungusSpawn();
                break;

            case ChungusRule.OpWindUp:
            {
                nint boss = ResolveChungus(out uint bossId);
                ushort animation = ChungusRule.WindUpAnimation(power);
                bool played = boss != 0 && animation != 0 && Odyssey.PlayOneShotAnimation(boss, animation);
                bool heard = Odyssey.PlaySound(ChungusRule.WindUpSound(power));
                Answer(
                    SharedBlock.CheatStatusApplied,
                    $"chungus: power {power} begins (boss 0x{bossId:X}, animation {animation} {(played ? "played" : "not played")}, "
                    + $"sound {(heard ? "played" : "none")})");
                break;
            }

            case ChungusRule.OpLand:
                ChungusLand(player, playerId, word, power);
                break;

            case ChungusRule.OpDespawn:
            {
                uint id = _chungusId;
                _chungusId = 0;
                bool gone = id != 0 && Odyssey.ResolveServerObject(id) != 0 && Odyssey.DestroyObjectById(id, out _);
                Answer(SharedBlock.CheatStatusApplied, gone ? $"chungus: 0x{id:X} removed" : "chungus: nothing to remove");
                break;
            }

            case ChungusRule.OpAnimation:
            {
                // Staging: play any animation id on him, to see what a row of animations.2da does.
                nint boss = ResolveChungus(out uint bossId);
                bool played = boss != 0 && Odyssey.PlayOneShotAnimation(boss, (ushort)word);
                Answer(
                    played ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
                    $"chungus: animation {word} on 0x{bossId:X} {(played ? "played" : "not played")}");
                break;
            }

            case ChungusRule.OpSound:
            {
                string name = ReadCheatText();
                bool heard = Odyssey.PlaySound(name);
                Answer(
                    heard ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
                    $"chungus: sound '{name}' {(heard ? "played" : "not played")}");
                break;
            }

            default:
                Answer(SharedBlock.CheatStatusRefused, $"chungus: unknown request {block.CheatAmount}");
                break;
        }
    }

    /// <summary>Host: puts him in the ring. Spawned the way a script would (through the routine
    /// table), so the host-spawns-only hook names his blueprint to every joiner and their copy is
    /// built from the same file.</summary>
    private void ChungusSpawn()
    {
        if (_chungusId != 0 && Odyssey.ResolveServerObject(_chungusId) != 0)
        {
            Answer(SharedBlock.CheatStatusApplied, $"boss 0x{_chungusId:X} (already here)");
            return;
        }

        Vec3 at = block.CheatPosition;
        Vec3 facingEast = new() { X = 1f, Y = 0f, Z = 0f };
        uint id = Odyssey.SpawnCreature(ChungusRule.Template, at, facingEast, viaScriptTable: true, out int failure);
        if (id is 0 or Odyssey.ObjectInvalid)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"chungus: could not spawn '{ChungusRule.Template}' (failure {failure}; 9 = the blueprint is not installed)");
            return;
        }

        _chungusId = id;
        bool hostile = Odyssey.SetCreatureFaction(id, Odyssey.FactionHostile1);
        nint boss = Odyssey.ResolveServerObject(id);
        int hitPoints = boss != 0 ? Odyssey.GetCurrentHitPoints(boss) : 0;
        PluginHost.Log(
            $"chungus: spawned 0x{id:X} at ({at.X:F1}, {at.Y:F1}), {hitPoints} hp, hostile {(hostile ? "yes" : "NO")}");
        Answer(SharedBlock.CheatStatusApplied, $"boss 0x{id:X}");
    }

    /// <summary>A power lands: its sound, and what it does to this game's own player if it was
    /// aimed at them. The host also gives the boss his heal for the soul stone.</summary>
    private void ChungusLand(nint player, uint playerId, uint word, int power)
    {
        Odyssey.PlaySound(ChungusRule.LandSound(power));

        int strength = ChungusRule.StrengthOf(word);
        string result = "not aimed at this player";

        if ((word & ChungusRule.FlagHitsLocalPlayer) != 0 && player != 0 && !Odyssey.IsCreatureDead(player))
        {
            BossLanding landing = ChungusRule.Landing(
                power, strength, Odyssey.GetCurrentHitPoints(player), Odyssey.GetEffectiveMaxHitPoints(player));

            int damageFailure = 0;
            bool hurt = landing.Damage > 0 && Odyssey.ApplyDamageEffect(playerId, landing.Damage, out damageFailure);
            int effectFailure = 0;
            bool applied = landing.Effect != 0
                && OdysseyEffects.ApplyEffect(playerId, landing.Effect, landing.DurationMs, landing.Magnitude, out effectFailure);
            int secondFailure = 0;
            bool second = landing.SecondEffect != 0
                && OdysseyEffects.ApplyEffect(
                    playerId, landing.SecondEffect, landing.SecondDurationMs, landing.SecondMagnitude, out secondFailure);

            result = $"damage {landing.Damage} {(hurt ? "applied" : $"not applied ({damageFailure})")}, "
                + $"effect {landing.Effect} {(applied ? "applied" : $"not applied ({effectFailure})")}"
                + (landing.SecondEffect != 0 ? $", effect {landing.SecondEffect} {(second ? "applied" : $"not applied ({secondFailure})")}" : string.Empty);
        }

        if (power == ChungusRule.Drain && (word & ChungusRule.FlagHost) != 0)
        {
            nint boss = ResolveChungus(out uint bossId);
            if (boss != 0 && !Odyssey.IsCreatureDead(boss))
            {
                int heal = ChungusRule.DrainHeal(Odyssey.GetEffectiveMaxHitPoints(boss));
                bool healed = OdysseyEffects.ApplyEffect(bossId, PeerEffectKindValues.Heal, 0, (uint)heal, out int healFailure);
                result += $"; boss healed {heal} {(healed ? "ok" : $"refused ({healFailure})")}";
            }
        }

        Answer(SharedBlock.CheatStatusApplied, $"chungus: power {power} lands - {result}");
    }

    /// <summary>
    /// This game's copy of the boss: the one it spawned (host), or the stand-in built for the
    /// host's creature whose blueprint is his (joiner).
    /// </summary>
    private static nint ResolveChungus(out uint objectId)
    {
        objectId = _chungusId;
        if (objectId != 0)
        {
            nint own = Odyssey.ResolveServerObject(objectId);
            if (own != 0)
            {
                return own;
            }
        }

        ReadOnlySpan<uint> owners = MaterialisedEnemies.OwnerIdsSnapshot;
        ReadOnlySpan<uint> locals = MaterialisedEnemies.LocalIdsSnapshot;
        for (int i = 0; i < owners.Length && i < locals.Length; i++)
        {
            if (owners[i] == 0 || locals[i] == 0
                || !ScriptCreateObjectHook.TryGetHostTemplate(owners[i], out string template)
                || !template.Equals(ChungusRule.Template, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nint copy = Odyssey.ResolveServerObject(locals[i]);
            if (copy != 0)
            {
                objectId = locals[i];
                return copy;
            }
        }

        objectId = 0;
        return 0;
    }
}
