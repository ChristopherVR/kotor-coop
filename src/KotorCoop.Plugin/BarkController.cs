namespace KotorCoop.Plugin;

/// <summary>
/// Moves NPC barks both ways: publishes what <see cref="BarkHook"/> caught here, and replays
/// what a peer's instance caught there.
///
/// A bark never enters the dialogue system, so none of the conversation machinery sees it —
/// one player clicked the utility droid and heard it beep while everyone else got silence.
/// This is the whole channel, and it is deliberately small: barks are occasional, unordered
/// and idempotent enough that a dropped one costs a line rather than desynchronising
/// anything.
///
/// Replay is keyed on the ENGINE OBJECT ID, which is the same number in both instances for
/// anything the module loaded (Handoff 4.5), and refuses when the speaker does not resolve
/// locally — a bark from a creature this instance does not have is dropped rather than
/// attached to whatever else holds that id.
/// </summary>
internal sealed unsafe class BarkController(SharedBlockView block)
{
    /// <summary>Frames between passes. A bark is a line of text; it does not need the frame
    /// rate, and the engine's own bubble stays up for seconds.</summary>
    private const int IntervalFrames = 10;

    private int _countdown;

    /// <summary>
    /// Barks already replayed, as (speaker, serial) pairs — NOT indexed by slot.
    ///
    /// ⚠ SLOT IS THE WRONG KEY, and keying on it made one bark play seven times. The agent
    /// writes each delivery into the NEXT slot of the remote ring
    /// (<c>KotorGameBridge.TryWriteBark</c> advances <c>_barkWriteSlot</c> on every call), so
    /// a bark delivered more than once occupies a different slot each time and every one of
    /// them looks new to that slot's own high-water mark. Measured: seven "replayed a peer's
    /// line from 0x37" for a single bark, in pairs about 160 ms apart.
    ///
    /// This was invisible for as long as the channel was broken — every replay failed on a
    /// speaker that did not resolve, so the duplicates were seven identical failures instead
    /// of seven beeps.
    ///
    /// Dedup belongs on the RECEIVER anyway: barks travel unreliably by design, and sending
    /// one more than once is a legitimate way to make a cheap, lossy channel likely to
    /// arrive. What must not happen twice is the NPC opening its mouth.
    /// </summary>
    private readonly uint[] _appliedSpeaker = new uint[AppliedMemory];
    private readonly uint[] _appliedSerial = new uint[AppliedMemory];
    private int _appliedWrite;

    /// <summary>How many recent barks are remembered for dedup. Comfortably more than the
    /// ring the agent writes into, so a bark cannot be forgotten while its slot still holds
    /// it and be replayed a second time.</summary>
    private const int AppliedMemory = 32;

    /// <summary>Whether this exact bark has already been spoken here. Linear over a short
    /// array, on a pass that runs a few times a second — the simple thing is the right one.</summary>
    private bool AlreadyPlayed(uint speaker, uint serial)
    {
        for (int i = 0; i < AppliedMemory; i++)
        {
            if (_appliedSerial[i] == serial && _appliedSpeaker[i] == speaker)
            {
                return true;
            }
        }

        return false;
    }

    private void Remember(uint speaker, uint serial)
    {
        _appliedSpeaker[_appliedWrite] = speaker;
        _appliedSerial[_appliedWrite] = serial;
        _appliedWrite = (_appliedWrite + 1) % AppliedMemory;
    }

    /// <summary>True once the slots have been primed, so barks published before this
    /// instance was listening are not all replayed at once on the first pass.</summary>
    private bool _primed;

    private int _logged;

    /// <summary>
    /// Whether a peer's bark is REPLAYED here. Capture and publication are always on.
    ///
    /// ⚠ THIS USED TO KILL THE INSTANCE, twice: the body vanished with no crash-probe line
    /// and no event-log entry, and the replay never reached the log statement after it. The
    /// cause is now known and fixed. <c>BarkString</c> takes a <c>CExoString*</c> and a
    /// <c>CResRef*</c>, and both were being forwarded across the wire as raw addresses in the
    /// PRODUCING process; the receiving engine dereferences both, on BOTH branches of the
    /// speaker lookup at 0x005F2CA8 — which is why no guard on the speaker ever helped, and
    /// why swapping `this` changed nothing.
    ///
    /// They are dereferenced at capture now, and the CONTENTS travel; the replay rebuilds a
    /// real CExoString with the engine's own constructor and frees it after the call. Left
    /// switchable because it is a call into the engine on the frame hook, and one live
    /// verification is not the same as a season of play. See docs/research/bark-replay.md.
    /// </summary>
    private readonly bool _replayEnabled = PluginConfig.ReadBool("bark_replay", true);

    /// <summary>A module change reassigns every object id, so a remembered bark would name a
    /// different creature. Drop both halves and re-prime.</summary>
    public void Invalidate()
    {
        BarkHook.Invalidate();

        for (int i = 0; i < SharedBlock.BarkCapacity; i++)
        {
            block.SetLocalBark(i, 0, 0, [], [], 0);
        }

        for (int i = 0; i < AppliedMemory; i++)
        {
            _appliedSpeaker[i] = 0;
            _appliedSerial[i] = 0;
        }

        _appliedWrite = 0;
        _primed = false;
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // Publish outward whatever the hook captured, every pass and unconditionally: the
        // agent decides what is new by serial, and a session that has not started yet simply
        // never reads it.
        Span<byte> text = stackalloc byte[SharedBlock.BarkTextMax];
        Span<byte> resRef = stackalloc byte[SharedBlock.BarkResRefBytes];

        for (int i = 0; i < SharedBlock.BarkCapacity; i++)
        {
            BarkHook.Read(i, out uint speaker, out uint flags, out uint serial, text, out int textLength, resRef);
            block.SetLocalBark(i, speaker, flags, text[..textLength], resRef, serial);
        }

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // A conversation owns the screen: a bark bubble thrown over a running dialogue is
        // noise, and the engine's own bark path competes with the dialogue camera.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        for (int i = 0; i < SharedBlock.BarkCapacity; i++)
        {
            BarkView bark = block.GetRemoteBark(i, text, out int lineLength, resRef);

            if (bark.Serial == 0 || AlreadyPlayed(bark.Speaker, bark.Serial))
            {
                continue;
            }

            Remember(bark.Speaker, bark.Serial);

            // First sight of a slot only records where it had got to. Without this, joining
            // a session mid-module would replay every bark the peer had ever thrown.
            if (!_primed || bark.Speaker == 0)
            {
                continue;
            }

            if (!_replayEnabled)
            {
                if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log(
                        $"bark: a peer's line from 0x{bark.Speaker:X} arrived but replay is off "
                        + "(coop.ini bark_replay=1)");
                }

                continue;
            }

            bool played = Odyssey.PlayBark(bark.Speaker, text[..lineLength], resRef, bark.Flags);

            if (_logged < 20)
            {
                _logged++;
                // The serial and slot are in the line because "how many times did this play"
                // is a question the log has already had to answer once, and could not.
                PluginHost.Log(played
                    ? $"bark: replayed a peer's line from 0x{bark.Speaker:X} (serial {bark.Serial}, slot {i})"
                    : $"bark: could not replay a peer's line from 0x{bark.Speaker:X} "
                        + $"(serial {bark.Serial}, slot {i}) — that creature does not exist here");
            }
        }

        _primed = true;
    }
}
