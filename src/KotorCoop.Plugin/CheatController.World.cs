using System.Text;
using static KotorCoop.Plugin.Vec3;

namespace KotorCoop.Plugin;

// World/object verbs that don't fit the other groups: FireScript (event-slot diagnostic),
// ApplyEffect (peer-effect apply-side smoke test), DestroyObject, Teleport/WalkTo, and the
// placeable use/close verbs (UsePlaceable/ClosePlaceable), plus FindNearestPhantom.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// DIAGNOSTIC (docs/research/shared-state-audit.md §2, "Strategy 2"). <c>CheatAmount</c> 0
    /// peeks the creature's +0x260 event script slot without firing it (a safe read, confirms
    /// the field looks like a real resref before ever touching it live); nonzero fires it
    /// through <see cref="Odyssey.FireCreatureScriptSlot0x260"/>.
    /// </summary>
    private void FireScript()
    {
        uint objectId = block.CheatTarget;
        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
            return;
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{objectId:X} does not resolve here");
            return;
        }

        // CheatAmount doubles as a slot selector - 0x270 fires the OnNotice candidate
        // (docs/research/shared-state-audit.md round 34), anything else nonzero still means
        // "fire +0x260".
        bool useSlot270 = block.CheatAmount == 0x270;
        int slotOffset = useSlot270 ? 0x270 : 0x260;
        string slot = useSlot270
            ? Odyssey.PeekCreatureScriptSlot0x270(creature)
            : Odyssey.PeekCreatureScriptSlot0x260(creature);

        if (block.CheatAmount == 0)
        {
            PluginHost.Log($"firescript: 0x{objectId:X} +0x{slotOffset:X} = '{slot}' (peek only, not fired)");
            Answer(SharedBlock.CheatStatusApplied, $"0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
            return;
        }

        // Neither slot auto-arms the diagnostic watch any more (production code -
        // EnemyPresenceController.StartLocalFight - now calls FireCreatureScriptSlot0x260 for
        // every real fight, and it must not spam 6000 frames of log per fight). The manual
        // diagnostic path still wants the watch, so arm it explicitly here for both slots.
        if (useSlot270)
        {
            Odyssey.FireCreatureScriptSlot0x270(creature);
            Odyssey.ArmFireScriptWatch(creature);
        }
        else
        {
            Odyssey.FireCreatureScriptSlot0x260(creature);
            Odyssey.ArmFireScriptWatch(creature);
        }

        PluginHost.Log($"firescript: fired 0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
        Answer(SharedBlock.CheatStatusApplied, $"fired 0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
    }

    /// <summary>
    /// STAGING VERB, STAGING ONLY / LOCAL APPLY: runs one of the peer-effect channel's
    /// non-damage constructors through <see cref="OdysseyEffects.ApplyEffect"/> — the EXACT
    /// apply-side path a peer's forwarded effect calls when it lands for real
    /// (<see cref="PeerEffectController.ApplyIncoming"/>). Aimed at a peer's phantom (the
    /// default, when <see cref="SharedBlock.OffsetCheatTarget"/> is 0) this IS the live test
    /// docs/research/effect-sync.md's list has never run: <see cref="PhantomEffectHook"/>
    /// captures the effect landing there just like it would for a real enemy attack, and the
    /// ordinary channel forwards it exactly as if it had come from combat. Aimed at an explicit
    /// id it is a constructor smoke test only — including the local player's own id
    /// (<c>0x7FFFFFFF</c>), which the `applyeffect ... me` console form resolves to, so the
    /// applying half of a constructor can be checked without touching the network at all.
    /// </summary>
    private void ApplyEffect(nint player)
    {
        uint kind = (uint)block.CheatAmount;
        if (kind is PeerEffectKindValues.None || kind > PeerEffectKindValues.Confused)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"unknown effect kind {kind} (1..12 - stun, paralyze, knockdown, slow, "
                + "frightened, sleep, choke, forcepush, whirlwind, heal, temporaryhitpoints, "
                + "confused)");
            return;
        }

        uint targetId = block.CheatTarget;
        nint target;
        string what;

        if (targetId != 0)
        {
            target = Odyssey.ResolveServerObject(targetId);
            what = $"0x{targetId:X}";

            if (target == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no object 0x{targetId:X} in this module");
                return;
            }
        }
        else
        {
            target = FindNearestPhantom(player, out targetId, out int phantomsHeld, out int phantomsResolved);
            what = "the nearest phantom";

            if (target == 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    $"no phantom resolves here ({phantomsHeld} slot(s) held, {phantomsResolved} resolved) "
                    + "- name an object id instead, or 'me' to test locally");
                return;
            }
        }

        uint durationMs = block.CheatItemCharges;
        uint magnitude = block.CheatItemMaxCharges;

        bool applied = OdysseyEffects.ApplyEffect(targetId, kind, durationMs, magnitude, out int failure);

        if (!applied)
        {
            string reason = failure == -1
                ? "no apply-side constructor for this kind yet (see effect-sync.md §4)"
                : $"engine call refused (failure {failure})";

            PluginHost.Log($"applyeffect: kind {kind} on {what} (0x{targetId:X}) refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"kind {kind} on {what} (0x{targetId:X}) refused - {reason}");
            return;
        }

        PluginHost.Log(
            $"applyeffect: kind {kind} applied to {what} (0x{targetId:X}), duration {durationMs}ms, "
            + $"magnitude {magnitude}");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"applied kind {kind} to {what} (0x{targetId:X}), {durationMs}ms, mag {magnitude}");
    }

    /// <summary>
    /// STAGING VERB: destroys a creature outright through
    /// <see cref="Odyssey.DestroyObjectById"/> — the SAME engine call
    /// <see cref="LivenessReconcileController"/>'s own Destroy action already wraps (see
    /// docs/research/creature-liveness.md §5). Built because <c>slay</c> only ever calls
    /// <see cref="Odyssey.KillCreature"/>, and no other verb could put a creature into the
    /// "Gone" state to exercise the Destroy dispatch live.
    ///
    /// Follows <see cref="ApplyEffect"/>'s wiring and <see cref="Slay"/>'s authority rules
    /// (person/companion/plot, same order, same reasons — see <see cref="CheatAuthority.ForDestroy"/>)
    /// but is HOST-ONLY where <see cref="Slay"/> forwards: a mirroring instance is refused
    /// outright (<see cref="DestroyVerdict.RefuseNotOwner"/>), not handed off, because there is
    /// no destroy-ledger to spend a forwarded destroy through.
    /// </summary>
    private void DestroyObject(uint playerId)
    {
        uint targetId = block.CheatTarget;
        if (targetId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "destroyobject needs an object id");
            return;
        }

        nint target = Odyssey.ResolveServerObject(targetId);

        DestroyVerdict verdict = CheatAuthority.ForDestroy(
            targetResolved: target != 0,
            targetIsLocalPlayer: targetId == playerId,
            targetIsPhantom: IsPhantom(targetId),
            targetIsCompanion: IsCompanion(targetId),
            targetIsPlot: target != 0 && Odyssey.IsPlot(target),
            mirrorsEnemies: block.DamageDealtEnabled);

        string explanation = CheatAuthority.Explain(verdict, targetId);

        if (verdict == DestroyVerdict.DestroyHere)
        {
            if (Odyssey.DestroyObjectById(targetId, out int failure))
            {
                PluginHost.Log($"destroyobject: destroyed 0x{targetId:X}");
                Answer(SharedBlock.CheatStatusApplied, explanation);
            }
            else
            {
                Answer(SharedBlock.CheatStatusEngineRefused, $"0x{targetId:X}: destroy refused (failure {failure})");
            }

            return;
        }

        Answer(
            verdict == DestroyVerdict.RefuseMissing ? SharedBlock.CheatStatusNoTarget : SharedBlock.CheatStatusRefused,
            explanation);
    }

    /// <summary>
    /// The closest phantom this instance renders for a peer — the default target for
    /// <see cref="ApplyEffect"/>, because landing the effect ON a phantom is the whole point:
    /// <see cref="PhantomEffectHook"/> only watches ids in the phantom table
    /// (<c>PhantomHitHook.IsWatchedPhantom</c>), so an effect applied to anything else is never
    /// captured or forwarded at all.
    /// </summary>
    private nint FindNearestPhantom(nint player, out uint objectId, out int held, out int resolved)
    {
        Vec3 here = Odyssey.GetPosition(player);

        nint best = 0;
        uint bestId = 0;
        float bestDistance = float.MaxValue;
        held = 0;
        resolved = 0;

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            uint phantomId = block.GetPhantomId(i);
            if (phantomId == 0)
            {
                continue;
            }

            held++;

            nint candidate = Odyssey.ResolveServerObject(phantomId);
            if (candidate == 0)
            {
                continue;
            }

            resolved++;

            float distance = Distance(here, Odyssey.GetPosition(candidate));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = candidate;
            bestId = phantomId;
            bestDistance = distance;
        }

        objectId = bestId;
        return best;
    }

    /// <summary>
    /// Puts the local player at a point in the current area.
    ///
    /// Self only, and that is what makes it safe: your position is yours, published to the
    /// peers every tick, so their phantom of you follows without anything being told about
    /// the teleport. Moving somebody ELSE's character would mean writing a position their own
    /// instance overwrites on its next tick.
    ///
    /// ⚠ NEEDS-LIVE: nothing here consults the walkmesh. The staged form the agent produces
    /// (<c>goto &lt;player&gt;</c>) lands on a point a peer is standing on, which is on the
    /// mesh by construction; hand-typed coordinates are the player's own risk and the answer
    /// says so.
    /// </summary>
    private void Teleport(nint player)
    {
        Vec3 destination = block.CheatPosition;
        Vec3 here = Odyssey.GetPosition(player);

        if (destination.X == 0f && destination.Y == 0f && destination.Z == 0f)
        {
            Answer(SharedBlock.CheatStatusRefused, "refusing to teleport to the area origin");
            return;
        }

        Odyssey.SnapTo(player, destination);

        Vec3 landed = Odyssey.GetPosition(player);
        Answer(
            SharedBlock.CheatStatusApplied,
            $"moved from {here.X:F1},{here.Y:F1} to {landed.X:F1},{landed.Y:F1}");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/minigames.md): the bare-point counterpart to
    /// <see cref="Teleport"/> — a real queued walk instead of a snap, so a subarea trigger's
    /// OnEnter (which only fires for a MOVING creature) actually has a chance to see the
    /// crossing. No retry/watch machinery unlike <see cref="UseDoor"/>'s walk — this is a
    /// one-shot instrument for an unattended live pass, which can just reissue the command if
    /// the VM was mid-call (failure 4).
    /// </summary>
    private void WalkTo(uint playerId)
    {
        Vec3 destination = block.CheatPosition;

        if (destination.X == 0f && destination.Y == 0f && destination.Z == 0f)
        {
            Answer(SharedBlock.CheatStatusRefused, "refusing to walk to the area origin");
            return;
        }

        bool accepted = Odyssey.MoveToLocation(playerId, destination, run: true, out int failure);
        Answer(
            accepted ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
            accepted
                ? $"walking to {destination.X:F1},{destination.Y:F1}"
                : $"the walk was refused (failure {failure})");
    }

    /// <summary>
    /// CONTAINER-SYNC LIVE-TEST PRIMITIVE (docs/research/container-sync.md): makes the local
    /// player USE a placeable through <c>Odyssey.UseObject</c> — the engine's own
    /// client-to-server "I clicked that" input pipe, so the real
    /// <c>AIActionUseObject</c>/<c>CSWSPlaceable::EventHandler</c>/<c>OpenInventory</c> path
    /// runs exactly as a real click would, which is what makes this a meaningful test of
    /// <see cref="ContainerUseHook"/> and <see cref="InteractionLockController"/> rather than a
    /// synthetic poke. Unlike <see cref="UseDoor"/>/<see cref="OpenDoor"/> there is no walk to
    /// queue and no walkmesh coupling — <c>Odyssey.UseObject</c> is a single fire-and-forget
    /// send, so this either reports the send succeeded or explains why it could not be
    /// attempted at all (range, resolution).
    /// </summary>
    private void UsePlaceable(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint placeable;
        string what;
        int seen = 0;

        if (requestedId != 0)
        {
            placeable = Odyssey.ResolvePlaceable(requestedId);
            what = $"0x{requestedId:X}";

            if (placeable == 0)
            {
                // A corpse is not a CSWSPlaceable — clicking it in a real game sends the SAME
                // UseObject input (Odyssey.UseObject is already object-type-agnostic; only
                // THIS cheat handler used to gate it to placeables — docs/research/container-
                // sync.md §6). Explicit-id/tag targets fall through to a raw object use so the
                // live-test rig can exercise a corpse (or anything else an id names) through
                // the identical client-to-server pipe a placeable uses, without adding a
                // second cheat verb. The bare sweep (no id/tag) keeps refusing non-placeables —
                // it exists to find containers, not to guess what a nearby id might be.
                nint rawObject = Odyssey.ResolveServerObject(requestedId);
                if (rawObject == 0)
                {
                    Answer(SharedBlock.CheatStatusNoTarget, $"{what} does not resolve to anything here");
                    return;
                }

                float rawDistance = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(rawObject));
                if (rawDistance > PlaceableUseRange)
                {
                    Answer(
                        SharedBlock.CheatStatusRefused,
                        $"object {what} is {rawDistance:F1}m away, too far to use");
                    return;
                }

                bool rawSent = Odyssey.UseObject(requestedId);
                PluginHost.Log(
                    $"object: use-object sent for {what} (not a placeable) at {rawDistance:F1}m "
                    + $"({(rawSent ? "ok" : "FAILED")})");

                Answer(
                    rawSent ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                    rawSent
                        ? $"object {what}: use sent (not a placeable — a corpse or other creature "
                            + "goes through the same UseObject pipe)"
                        : $"object {what}: UseObject failed (no client or no message object)");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            placeable = OdysseyPlaceables.FindPlaceableByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            // Logged FIRST, unconditionally: a bare `useplaceable` is also the live-test
            // script's way to discover ids, and it should see the whole sweep even when the
            // nearest-container search below comes up empty.
            OdysseyPlaceables.LogPlaceableSweep(player);
            placeable = OdysseyPlaceables.FindNearestContainer(player, out seen);
            what = "the nearest container";
        }

        if (placeable == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, seen == 0
                ? "the sweep found no placeables at all in this module"
                : $"the sweep found {seen} placeable(s) but none close enough / none with inventory");
            return;
        }

        uint placeableId = OdysseyPlaceables.GetPlaceableObjectId(placeable);
        float distance = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(placeable));

        if (distance > PlaceableUseRange)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"placeable 0x{placeableId:X} ({what}) is {distance:F1}m away, too far to use");
            return;
        }

        bool sent = Odyssey.UseObject(placeableId);
        PluginHost.Log(
            $"placeable: use-object sent for 0x{placeableId:X} at {distance:F1}m ({(sent ? "ok" : "FAILED")})");

        Answer(
            sent ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            sent
                ? $"placeable 0x{placeableId:X}: use sent (the container/interaction-lock "
                    + "machinery reacts to it like a real click)"
                : $"placeable 0x{placeableId:X}: UseObject failed (no client or no message object)");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-consequences.md's script-item-sync round,
    /// Controls 2/3 — the sub-case flagged "most likely to fail live" and the one the prior
    /// round could not reach: <c>useplaceable</c> only replicates the network half of a real
    /// click, so it opens the interaction lock but never actually moves an item, and no
    /// injected click can drive the container GUI that would). Same target resolution as
    /// <see cref="UsePlaceable"/> (object id / tag / nearest container), but instead of sending
    /// a UseObject message this removes the container's first item through
    /// <see cref="Odyssey.TryTakeFirstContainerItem"/> — the real
    /// <c>CSWSPlaceable::RemoveItem</c> primitive <see cref="Odyssey.RemoveContainerItem"/>
    /// already uses to apply a PEER's removal — and gives an equivalent copy straight to the
    /// local player via <see cref="Odyssey.GiveItemToInventory"/>. Both halves of a real
    /// container/corpse take (a genuine removal <c>CollectContainerLootRemovals</c>'s next scan
    /// detects, AND a genuine new row <c>ReadPossessedItems</c>'s next sample sees) fire through
    /// two verified engine calls — unlike <c>scriptgive</c>, which fabricates only the second
    /// half with no container involved at all, this exercises the suppression-ledger feed point
    /// at <c>LootPump.PumpContainerLoot</c> the same way a real take would.
    /// </summary>
    private void LootTake(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint placeable;
        string what;
        int seen = 0;

        if (requestedId != 0)
        {
            placeable = Odyssey.ResolvePlaceable(requestedId);
            what = $"0x{requestedId:X}";

            if (placeable == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"{what} does not resolve to a container here");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            placeable = OdysseyPlaceables.FindPlaceableByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            OdysseyPlaceables.LogPlaceableSweep(player);
            placeable = OdysseyPlaceables.FindNearestContainer(player, out seen);
            what = "the nearest container";
        }

        if (placeable == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, seen == 0
                ? "the sweep found no placeables at all in this module"
                : $"the sweep found {seen} placeable(s) but none close enough / none with inventory");
            return;
        }

        uint placeableId = OdysseyPlaceables.GetPlaceableObjectId(placeable);
        float distance = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(placeable));

        if (distance > PlaceableUseRange)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"placeable 0x{placeableId:X} ({what}) is {distance:F1}m away, too far to loot");
            return;
        }

        byte* tagBuffer = stackalloc byte[SharedBlock.CheatTextLength];
        if (!Odyssey.TryTakeFirstContainerItem(
                placeable, tagBuffer, SharedBlock.CheatTextLength - 1, out uint itemId, out int tagLength))
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"placeable 0x{placeableId:X} ({what}) has no takeable item "
                    + "(empty, or its first item's tag is unreadable)");
            return;
        }

        string resref = Encoding.ASCII.GetString(tagBuffer, tagLength);
        uint localId = Odyssey.GetPlayerObjectId();
        uint givenId = Odyssey.GiveItemToInventory(localId, tagBuffer, tagLength, 1, out int giveFailure);
        bool gave = givenId is not (0 or Odyssey.ObjectInvalid);

        PluginHost.Log(
            $"loottake: removed '{resref}' (0x{itemId:X}) from container 0x{placeableId:X} ({what}) "
            + $"and gave a fresh copy ({(gave ? $"0x{givenId:X}" : $"FAILED, failure {giveFailure}")}) "
            + "to the local player - a real removal + a real new inventory row, feeding the SAME "
            + "CollectContainerLootRemovals/ScriptItemGrantPump paths a GUI take would");

        Answer(
            gave ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            gave
                ? $"took '{resref}' (0x{itemId:X}) from 0x{placeableId:X} ({what}) and gave it to "
                    + $"the player as 0x{givenId:X}"
                : $"took '{resref}' (0x{itemId:X}) from 0x{placeableId:X} but could not give it back "
                    + $"to the player (failure {giveFailure}) - the container's copy is GONE regardless");
    }

    /// <summary>
    /// The close-side counterpart to <see cref="UsePlaceable"/>. <c>useplaceable</c> only
    /// replicates the network half of a real click, so the engine's own container GUI never
    /// actually shows on this instance — leaving no panel for an Escape keystroke, or a real
    /// player, to close, which is exactly what the container-sync live-verify pass needed and
    /// did not have. This calls the real <c>CSWSPlaceable::CloseInventory</c> directly
    /// (<see cref="OdysseyPlaceables.CloseInventoryReal"/> — VERIFIED disasm, both stack args 0
    /// is the safe player-agnostic path, see that method's own remarks), so the mirror on every
    /// OTHER instance reacts exactly as it would to a real close: the peer-held container drops
    /// out of <see cref="InteractionLockController"/>'s set, and <see cref="ContainerUseHook"/>
    /// stops refusing it. See docs/research/container-sync.md.
    /// </summary>
    private void ClosePlaceable(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint placeable;
        string what;
        int seen = 0;

        if (requestedId != 0)
        {
            placeable = Odyssey.ResolvePlaceable(requestedId);
            what = $"0x{requestedId:X}";

            if (placeable == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"{what} exists but is not a placeable");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            placeable = OdysseyPlaceables.FindPlaceableByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            placeable = OdysseyPlaceables.FindNearestOpenContainer(player, out seen);
            what = "the nearest open container";
        }

        if (placeable == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, seen == 0
                ? "the sweep found no placeables at all in this module"
                : $"the sweep found {seen} placeable(s) but none of them are open");
            return;
        }

        uint placeableId = OdysseyPlaceables.GetPlaceableObjectId(placeable);

        if (!Odyssey.IsPlaceableOpen(placeable))
        {
            Answer(SharedBlock.CheatStatusRefused, $"placeable 0x{placeableId:X} ({what}) is not open");
            return;
        }

        OdysseyPlaceables.CloseInventoryReal(placeable, player);
        PluginHost.Log($"placeable: close-inventory called for 0x{placeableId:X}");

        Answer(
            SharedBlock.CheatStatusApplied,
            $"placeable 0x{placeableId:X}: closed (close animation played, +0x338 cleared)");
    }

    /// <summary>How close the player has to be for <c>useplaceable</c> to send the use — the
    /// engine resolves range/reach itself once the input reaches it, but a request aimed at a
    /// container across the map is a mistake worth catching here rather than silently sent.</summary>
    private const float PlaceableUseRange = 15f;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/disconnect-reconnect.md §9): quits THIS instance
    /// straight to the main menu through <see cref="Odyssey.ShutDownToMainMenu"/> — the same
    /// engine call a real "Quit Game -&gt; Yes" click reaches. Exists to exercise the Away
    /// flag's third shape (agent alive, no player object) without navigating the vanilla pause
    /// menu by mouse, which a prior round found does not land clicks on a demo.ps1-repositioned
    /// window (docs/research/session-save.md).
    /// </summary>
    private void QuitToMenu()
    {
        PluginHost.Log("quittomenu: tearing down to the main menu (CClientExoAppInternal::ShutDownToMainMenu)");

        if (!Odyssey.ShutDownToMainMenu())
        {
            Answer(SharedBlock.CheatStatusNoWorld, "client internal not resolvable - nothing to tear down");
            return;
        }

        Answer(SharedBlock.CheatStatusApplied, "quit to main menu requested");
    }
}
