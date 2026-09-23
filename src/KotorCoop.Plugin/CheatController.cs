using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Runs the staging cheats the agent publishes on the block's cheat channel, on the game's
/// own thread, through controllers and engine calls that already exist.
///
/// Why this is not "the retail cheats again": KOTOR's own console already ships
/// <c>giveitem</c>, <c>givecredits</c>, <c>addexp</c>, <c>addlevel</c>, <c>revealmap</c> and
/// <c>heal</c>, every one of which touches only the typist's own single-player state and is
/// therefore already correct in co-op (see docs/research/console-cheats.md for the table).
/// What retail has no idea about is AUTHORITY: an enemy belongs to one instance, a phantom
/// belongs to a peer, a companion belongs to the host. So the verbs here are the ones whose
/// answer depends on who typed them, plus the two that stage a co-op scenario nothing else
/// can (put me next to my partner; put a shared item on the floor).
///
/// Every path either does the thing and says so, or refuses and says exactly what it saw.
/// A guard that silently does nothing is a bug: this channel is meant to be driven by an
/// unattended verification run, which can only trust an answer that names its own reason.
/// </summary>
internal sealed unsafe partial class CheatController(SharedBlockView block)
{
    private uint _appliedSerial;

    /// <summary>Set by <see cref="MoviePlay"/> when it defers the actual engine call to
    /// <see cref="PendingMoviePlay"/> (docs/research/cinematics.md §10.9): the serial this
    /// deferred call belongs to, so <see cref="OnFrame"/>'s own <c>finally</c> does not mark it
    /// <see cref="SharedBlockView.CheatApplied"/> before the call has actually run — that would
    /// tell a poller "done" a frame before the movie even started.</summary>
    private uint _deferredMovieSerial;

    /// <summary>
    /// A module change does NOT drop a pending request: its serial is still ahead, and the
    /// next stable frame either runs it against the new world or refuses it with a reason.
    /// The deferred top-up is dropped, because its object id names a stranger now.
    /// </summary>
    public void Invalidate()
    {
        _pendingHitPoints = 0;
        _pendingHitPointTarget = 0;

        // Same reasoning as the hit-point top-up above: a module change drops the deferred
        // min1hp restore rather than firing it against a stranger who now holds this id.
        _pendingMinOneHpRestoreTarget = 0;
        _pendingMinOneHpRestoreFramesLeft = 0;

        // PendingMoviePlay itself is cancelled centrally (PluginHost.InvalidateAll); this just
        // stops OnFrame's finally from waiting on a serial that will never complete now.
        _deferredMovieSerial = 0;

        // A walk armed in the world we just left names a door that no longer exists — and if
        // the module changed BECAUSE the walk worked, the watch has nothing left to report.
        _walkFramesLeft = 0;
        _walkWatchFrames = 0;
        _walkDoorId = 0;

        // Same reasoning for a crossdoor watch: if the module changed because the posted event
        // actually worked, this zeroing is exactly what makes WatchDoorCrossing print nothing
        // on success (see its own doc comment).
        _crossWatchFramesLeft = 0;
        _crossDoorId = 0;
        _crossTransitionSeenPending = false;
    }

    public void OnFrame()
    {
        ApplyPendingHitPoints();
        ApplyPendingMinOneHpRestore();
        RetryPendingDoorWalk();
        WatchDoorWalk();
        WatchDoorCrossing();

        uint serial = block.CheatSerial;
        if (serial == _appliedSerial)
        {
            return;
        }

        _appliedSerial = serial;

        try
        {
            Execute(block.CheatKind);
        }
        catch (Exception ex)
        {
            // A staging cheat must never take the game with it. Report the throw as the
            // answer rather than swallowing it: an unattended run reading "applied" after an
            // exception would be worse than no channel at all.
            Answer(SharedBlock.CheatStatusEngineRefused, $"cheat threw: {ex.GetType().Name}");
        }
        finally
        {
            if (_deferredMovieSerial != serial)
            {
                block.CheatApplied = serial;
            }
        }
    }

    private void Execute(uint kind)
    {
        // Needs no world: the pad is worth inspecting (and the virtual pad worth driving) on
        // the main menu, where the gate below would refuse it.
        if (kind == SharedBlock.CheatKindGamepad)
        {
            Answer(SharedBlock.CheatStatusApplied, PluginHost.OnGamepadConsole(ReadCheatText()));
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        uint playerId = Odyssey.GetPlayerObjectId();

        if (player == 0 || playerId == 0)
        {
            Answer(SharedBlock.CheatStatusNoWorld, "no player loaded (not in a module yet)");
            return;
        }

        switch (kind)
        {
            case SharedBlock.CheatKindGod:
                God(player);
                break;

            case SharedBlock.CheatKindTopUp:
                TopUp(player);
                break;

            case SharedBlock.CheatKindStandUp:
                StandUp(player, playerId);
                break;

            case SharedBlock.CheatKindHurt:
                Hurt(player, playerId);
                break;

            case SharedBlock.CheatKindDown:
                Down(player, playerId);
                break;

            case SharedBlock.CheatKindSlay:
                Slay(playerId);
                break;

            case SharedBlock.CheatKindTeleport:
                Teleport(player);
                break;

            case SharedBlock.CheatKindDropItem:
                DropItem(player);
                break;

            case SharedBlock.CheatKindStageMovie:
                StageMovie();
                break;

            case SharedBlock.CheatKindSetItem:
                SetItem();
                break;

            case SharedBlock.CheatKindResolve:
                Resolve();
                break;

            case SharedBlock.CheatKindUseDoor:
                UseDoor(player, playerId);
                break;

            case SharedBlock.CheatKindCrossDoor:
                CrossDoor(player, playerId);
                break;

            case SharedBlock.CheatKindOpenDoor:
                OpenDoor(player, playerId);
                break;

            case SharedBlock.CheatKindGiveLoot:
                GiveLoot();
                break;

            case SharedBlock.CheatKindFireScript:
                FireScript();
                break;

            case SharedBlock.CheatKindMoveNear:
                MoveNear(player);
                break;

            case SharedBlock.CheatKindApplyEffect:
                ApplyEffect(player);
                break;

            case SharedBlock.CheatKindMoviePlay:
                MoviePlay();
                break;

            case SharedBlock.CheatKindDestroyObject:
                DestroyObject(playerId);
                break;

            case SharedBlock.CheatKindLeader:
                Leader();
                break;

            case SharedBlock.CheatKindRecruit:
                Recruit(player);
                break;

            case SharedBlock.CheatKindUsePlaceable:
                UsePlaceable(player);
                break;

            case SharedBlock.CheatKindClosePlaceable:
                ClosePlaceable(player);
                break;

            case SharedBlock.CheatKindSkipLine:
                SkipLine();
                break;

            case SharedBlock.CheatKindSelectReply:
                SelectReply();
                break;

            case SharedBlock.CheatKindWalkTo:
                WalkTo(playerId);
                break;

            case SharedBlock.CheatKindPartyPoolAdd:
                PartyPoolAdd(playerId);
                break;

            case SharedBlock.CheatKindPartyPoolRemove:
                PartyPoolRemove();
                break;

            case SharedBlock.CheatKindQuitToMenu:
                QuitToMenu();
                break;

            case SharedBlock.CheatKindScriptGive:
                ScriptGive();
                break;

            case SharedBlock.CheatKindLootTake:
                LootTake(player);
                break;

            case SharedBlock.CheatKindStoreInsert:
                StoreInsert(playerId);
                break;

            case SharedBlock.CheatKindStoreRemove:
                StoreRemove();
                break;

            case SharedBlock.CheatKindGrantXp:
                GrantXp(playerId);
                break;

            case SharedBlock.CheatKindEquip:
                Equip();
                break;

            case SharedBlock.CheatKindSetGlobal:
                SetGlobal();
                break;

            case SharedBlock.CheatKindPazaak:
                Pazaak();
                break;

            case SharedBlock.CheatKindOpenStore:
                OpenStore(playerId);
                break;

            case SharedBlock.CheatKindCloseGui:
                CloseGui();
                break;

            case SharedBlock.CheatKindPopModal:
                PopModal();
                break;

            case SharedBlock.CheatKindPartySelectOk:
                PartySelectOk();
                break;

            case SharedBlock.CheatKindFaction:
                Faction(playerId);
                break;

            case SharedBlock.CheatKindPazaakEnd:
                PazaakEnd();
                break;

            case SharedBlock.CheatKindStoreBuy:
                Buy(player);
                break;

            case SharedBlock.CheatKindStoreSell:
                Sell(player);
                break;

            case SharedBlock.CheatKindStartDialog:
                StartDialog(playerId);
                break;

            case SharedBlock.CheatKindRemoveParty:
                RemoveParty();
                break;

            case SharedBlock.CheatKindSetGold:
                SetGold(player);
                break;

            case SharedBlock.CheatKindAddClass:
                AddClass(playerId);
                break;

            case SharedBlock.CheatKindMineAction:
                MineAction(playerId);
                break;

            case SharedBlock.CheatKindFollowKick:
                FollowKick();
                break;

            case SharedBlock.CheatKindFollowStall:
                FollowStall();
                break;

            case SharedBlock.CheatKindSkipKey:
                SkipKey();
                break;

            case SharedBlock.CheatKindQuickSave:
                QuickSave();
                break;

            case SharedBlock.CheatKindGalaxyMapOpen:
                OpenGalaxyMap();
                break;

            case SharedBlock.CheatKindGalaxyMapClose:
                CloseGalaxyMap();
                break;

            case SharedBlock.CheatKindGalaxyMapStats:
                GalaxyMapStats();
                break;

            case SharedBlock.CheatKindSpawnCreature:
                SpawnCreature(player);
                break;

            case SharedBlock.CheatKindGalaxyAccept:
                GalaxyMapAccept();
                break;

            case SharedBlock.CheatKindGalaxyMapSelect:
                SelectGalaxyMapPlanet();
                break;

            case SharedBlock.CheatKindGalaxyMapUnlock:
                UnlockGalaxyMapPlanet();
                break;

            case SharedBlock.CheatKindGalaxyMapDepart:
                DepartGalaxyMap();
                break;

            default:
                Answer(SharedBlock.CheatStatusRefused, $"unknown cheat kind {kind}");
                break;
        }
    }

    /// <summary>True when the id is one of the bodies this instance renders for a peer.</summary>
    private bool IsPhantom(uint objectId)
    {
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (block.GetPhantomId(i) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the id is a party companion, read from the engine's own roster rather than
    /// inferred from faction — a mirrored companion on a client is frozen and neutral, so
    /// faction would not tell them apart from scenery.
    /// </summary>
    private static bool IsCompanion(uint objectId)
    {
        if (!Odyssey.TryReadPartyRoster(out _, out uint partyMask))
        {
            return false;
        }

        for (int slot = 0; slot < 32; slot++)
        {
            if ((partyMask & (1u << slot)) == 0)
            {
                continue;
            }

            if (Odyssey.GetPartyRosterObjectId(slot) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The request's text field as a string, cut at the first null.</summary>
    private string ReadCheatText()
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(text, length);
    }

    /// <summary>Writes the answer where both the agent and the log will find it. Status and
    /// detail together, always — a status with no sentence is the silent guard this channel
    /// exists to avoid.</summary>
    private void Answer(int status, string detail)
    {
        block.SetCheatDetail(detail);
        block.CheatStatus = status;
        PluginHost.Log($"cheat: {detail} (status {status})");
    }
}
