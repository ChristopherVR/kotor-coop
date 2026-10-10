namespace KotorCoop.Plugin;

// The arena boss (docs/research/chungus-boss.md): the host spawns him, and every game plays his
// powers out for itself when the host says he used one. The agent keeps the time (wind-up now,
// landing a moment later) and asks for each step through this one cheat kind.
internal sealed unsafe partial class CheatController
{
    /// <summary>The boss this game's own engine spawned (host only), or 0.</summary>
    private static uint _chungusId;

    /// <summary>The joiner's copy that was last given his name, so it is written once.</summary>
    private static uint _chungusNamedCopy;

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
                nint boss = ResolveChungus(block.CheatItemCharges, out uint bossId);
                string animated = "no animation";
                if (boss != 0 && (word & ChungusRule.FlagHost) != 0)
                {
                    // The host owns him: the script command drops his attack and plays it.
                    (int script, float seconds) = ChungusRule.WindUpScriptAnimation(power);
                    int failure = 0;
                    bool played = script != 0 && Odyssey.PlayAnimationNow(bossId, script, seconds, out failure);
                    animated = $"script animation {script} {(played ? "played" : $"not played ({failure})")}";
                }
                else if (boss != 0)
                {
                    ushort animation = ChungusRule.WindUpAnimation(power);
                    bool played = animation != 0 && Odyssey.PlayOneShotAnimation(boss, animation);
                    animated = $"animation {animation} {(played ? "played" : "not played")}";
                }

                bool heard = Odyssey.PlaySound(ChungusRule.WindUpSound(power));
                uint gathering = ChungusRule.WindUpEffect(power);
                bool seen = boss != 0 && gathering != 0 && OdysseyEffects.ApplyVisualEffect(bossId, gathering, 0, out _);
                Answer(
                    SharedBlock.CheatStatusApplied,
                    $"chungus: power {power} begins (boss 0x{bossId:X}, {animated}, sound {(heard ? "played" : "none")}, "
                    + $"effect {gathering} {(seen ? "shown" : "none")})");
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
                // Staging: play any animation on him. A number below 1000 is a script constant
                // (through the script command, as the host plays his powers; a looping one is
                // held for two seconds); anything else is an engine id, as a joiner replays one.
                nint boss = ResolveChungus(0, out uint bossId);
                int failure = 0;
                bool played = boss != 0 && (word < 1000
                    ? Odyssey.PlayAnimationNow(bossId, (int)word, word < 100 ? 2f : 0f, out failure)
                    : Odyssey.PlayOneShotAnimation(boss, (ushort)word));
                Answer(
                    played ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
                    $"chungus: animation {word} on 0x{bossId:X} {(played ? "played" : $"not played ({failure})")}");
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

            case ChungusRule.OpBlink:
            {
                // Host: he is somewhere else, all at once. The move is the walkmesh-guarded snap,
                // so a spot that is not floor leaves him where he was.
                nint boss = ResolveChungus(block.CheatItemCharges, out uint bossId);
                Vec3 to = block.CheatPosition;
                bool moved = boss != 0 && !Odyssey.IsCreatureDead(boss) && Odyssey.SnapTo(boss, to);
                if (moved)
                {
                    OdysseyEffects.ApplyVisualEffect(bossId, ChungusRule.BlinkEffect, 0, out _);
                }

                Answer(
                    moved ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
                    $"chungus: blink to ({to.X:F1}, {to.Y:F1}, {to.Z:F1}) {(moved ? "done" : "refused")} (boss 0x{bossId:X})");
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

        // What is seen: the game's own effects, on this game's copy of him and on its own player.
        nint source = ResolveChungus(block.CheatItemCharges, out uint sourceId);
        uint burst = ChungusRule.LandEffectOnBoss(power);
        if (source != 0 && burst != 0)
        {
            OdysseyEffects.ApplyVisualEffect(sourceId, burst, 0, out _);
        }

        if ((word & ChungusRule.FlagHitsLocalPlayer) != 0 && player != 0 && !Odyssey.IsCreatureDead(player))
        {
            BossSight sight = ChungusRule.Sight(power);
            int shown = 0;
            if (sight.Impact != 0 && OdysseyEffects.ApplyVisualEffect(playerId, sight.Impact, 0, out _)) shown++;
            if (sight.Shake && OdysseyEffects.ApplyVisualEffect(playerId, ChungusRule.VfxScreenShake, 0, out _)) shown++;
            if (sight.Lasting != 0 && OdysseyEffects.ApplyVisualEffect(playerId, sight.Lasting, sight.LastingMs, out _)) shown++;
            if (sight.Beam != 0 && source != 0 && OdysseyEffects.ApplyBeam(playerId, sight.Beam, sourceId, sight.BeamMs, out _)) shown++;

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

            result = $"{shown} effect(s) shown, damage {landing.Damage} {(hurt ? "applied" : $"not applied ({damageFailure})")}, "
                + $"effect {landing.Effect} {(applied ? "applied" : $"not applied ({effectFailure})")}"
                + (landing.SecondEffect != 0 ? $", effect {landing.SecondEffect} {(second ? "applied" : $"not applied ({secondFailure})")}" : string.Empty);
        }

        if (power == ChungusRule.Drain && (word & ChungusRule.FlagHost) != 0)
        {
            nint boss = ResolveChungus(block.CheatItemCharges, out uint bossId);
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
    /// host's creature (joiner). A joiner's copy is known by the host having named his blueprint
    /// for it, or failing that by wearing <paramref name="appearanceRow"/>, the row of this
    /// install's appearance table that is his and nobody else's (seen live: the copy was built
    /// before the blueprint's name arrived, so the name alone found nothing).
    /// </summary>
    private static nint ResolveChungus(uint appearanceRow, out uint objectId)
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
            if (owners[i] == 0 || locals[i] == 0)
            {
                continue;
            }

            nint copy = Odyssey.ResolveServerObject(locals[i]);
            if (copy == 0)
            {
                continue;
            }

            bool byTemplate = ScriptCreateObjectHook.TryGetHostTemplate(owners[i], out string template)
                && template.Equals(ChungusRule.Template, StringComparison.OrdinalIgnoreCase);
            bool byRow = appearanceRow is > 0 and <= ushort.MaxValue
                && Odyssey.GetServerAppearanceRow(copy) == appearanceRow;
            if (!byTemplate && !byRow)
            {
                continue;
            }

            objectId = locals[i];
            NameChungusCopy(copy, objectId);
            return copy;
        }

        objectId = 0;
        return 0;
    }

    /// <summary>A stand-in built without his blueprint has no name over its head; give it his.</summary>
    private static void NameChungusCopy(nint copy, uint objectId)
    {
        if (_chungusNamedCopy == objectId)
        {
            return;
        }

        _chungusNamedCopy = objectId;
        ReadOnlySpan<byte> name = "Chungus"u8;
        fixed (byte* text = name)
        {
            Odyssey.SetCreatureFirstName(copy, text, name.Length);
        }
    }
}
