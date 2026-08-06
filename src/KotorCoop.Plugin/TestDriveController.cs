namespace KotorCoop.Plugin;

/// <summary>
/// Drives the local player from the shared block — talk to an NPC, walk to a point — so
/// co-op behaviour that depends on a player DOING something can be exercised without
/// injected keyboard and mouse input.
///
/// That matters more here than it sounds: KOTOR tracks its own cursor from relative
/// DirectInput deltas rather than the OS pointer, and injected input is a focus lottery
/// (traps 22 and 25), so "click the NPC" is exactly the step automation keeps failing at.
/// These hooks run the engine's own calls instead — <c>StartDialogueWith</c> sends the
/// same player-to-server dialogue request a click produces, so the whole conversation
/// path, including the exclusivity gate, is exercised for real rather than simulated.
///
/// Test-only surface, like the pickup and phantom-hit hooks beside it: agent-written,
/// serial-edged, and inert until something writes a serial.
/// </summary>
internal sealed unsafe class TestDriveController(SharedBlockView block)
{
    private uint _talkSerial;
    private uint _walkSerial;

    public void Invalidate()
    {
        // Serials are NOT reset: a module change must not replay the last request.
        _talkSerial = block.TestTalkSerial;
        _walkSerial = block.TestWalkSerial;
    }

    public void OnFrame()
    {
        HandleTalk();
        HandleWalk();
    }

    private void HandleTalk()
    {
        uint serial = block.TestTalkSerial;
        if (serial == _talkSerial)
        {
            return;
        }

        _talkSerial = serial;

        byte* tag = block.TestTalkTag;
        int length = 0;
        while (length < SharedBlock.TestTalkTagLength && tag[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        // Exactly what a click does — so a refusal here is a real refusal, including the
        // one the dialogue gate issues while a peer holds the conversation token.
        bool started = Odyssey.StartDialogueWith(new ReadOnlySpan<byte>(tag, length));

        PluginHost.Log(started
            ? $"test: opened a conversation with '{System.Text.Encoding.ASCII.GetString(tag, length)}'"
            : $"test: could not open a conversation with '{System.Text.Encoding.ASCII.GetString(tag, length)}'"
                + " (not here, already talking, or refused)");
    }

    private void HandleWalk()
    {
        uint serial = block.TestWalkSerial;
        if (serial == _walkSerial)
        {
            return;
        }

        _walkSerial = serial;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            return;
        }

        nint player = Odyssey.ResolveServerObject(playerId);
        if (player == 0)
        {
            return;
        }

        Vec3 here = Odyssey.GetPosition(player);
        Vec3 destination = new() { X = block.TestWalkX, Y = block.TestWalkY, Z = here.Z };

        bool ok = Odyssey.MoveToLocation(playerId, destination, run: true, out int failure);

        PluginHost.Log(ok
            ? $"test: walking to ({destination.X:F2}, {destination.Y:F2})"
            : $"test: walk refused (failure {failure})");
    }
}
