namespace KotorCoop.Plugin;

/// <summary>
/// Catches the character the player just made in the game's OWN character-creation screens, so
/// the agent can carry it into the host's world instead of into the Endar Spire.
///
/// WHY THIS EXISTS. World adoption (docs/research/world-adoption.md) lets a joiner play the
/// host's world with their own character — but it lifts that character out of the joiner's own
/// SAVE, and a first-time joiner has no save. Before this, that player had nothing to join
/// with: <c>WorldAdoption.TryAdoptAsync</c> fails at its very first step ("the save's
/// Mod_PlayerList is empty") and, correctly, refuses to touch anything.
///
/// THE OBSERVATION THAT MAKES IT CHEAP. Retail chargen already writes exactly the file adoption
/// carries. <c>CSWCCreature::SaveCharGenCreature 0x6123E0</c> — one of the three
/// <c>TEMP:pifo</c> writers docs/research/character-login-chain.md §2 enumerates exhaustively,
/// and the only one on the New Game path — emits <c>Temp\pifo.ifo</c> when the player accepts
/// their character: a standalone one-entry <c>Mod_PlayerList</c> IFO, the same shape
/// <c>PlayerIfo.BuildPifo</c> hand-builds for an adopted join. So "let a first-time joiner make
/// a character" needs no character-creation code of our own at all. It needs somebody to
/// notice that file and put a copy somewhere safe.
///
/// SOMEWHERE SAFE IS THE POINT. The adoption load that follows calls <c>LoadPCAutoSave</c>,
/// which copies <c>&lt;export&gt;\pifo</c> over <c>TEMP:pifo</c> by name and TRUNCATES IT TO
/// ZERO when the staged save has no pifo to copy — which every adopted world is
/// (world-adoption.md §7, measured: a blank 1-hit-point stranger at the module start point).
/// The stash that survives it is <c>Temp\kcoop_carried.ifo</c>, and
/// <see cref="MenuLoadController.RestoreCarriedCharacter"/> already copies it back on the
/// game's own thread in the one gap where nothing can race it. This controller writes THAT
/// file, so the character a player creates and the character an adoption carries are the same
/// bytes travelling the same road.
///
/// ⚠ NO ENGINE CALLS, DELIBERATELY. This could have been a detour on <c>0x6123E0</c> — one call
/// site (0x006DBE9B), a clean capture point. It is a file poll instead, because a poll cannot
/// crash the game and the only thing a hook would buy is a quarter of a second. The engine
/// writes the file; we read it. The one thing the plugin does that the agent could not is
/// choose the DESTINATION — the stash name the loader's own FileCopy does not target — and
/// that is a <c>File.Copy</c>, not an engine call.
///
/// Runs from <c>PluginHost.MenuFrame</c>, not <c>OnFrame</c>: the whole point is the main menu
/// and the chargen screens, where the SwapBuffers hook is dormant (the same reason
/// <see cref="MenuLoadController"/> lives there).
/// </summary>
internal sealed class NewCharacterCaptureController(SharedBlockView block)
{
    /// <summary>The engine's own chargen output — <c>TEMP:pifo</c>, restype 0x7DE ("ifo").
    /// </summary>
    private const string PifoFile = "pifo.ifo";

    /// <summary>Request serial already acted on, so one arm arms exactly once.</summary>
    private uint _armedSerial;

    /// <summary>True between arming and either a capture or a failure.</summary>
    private bool _armed;

    /// <summary>UTC ticks at the moment of arming. Anything on disk older than this is a
    /// LEFTOVER pifo from a previous session — see <see cref="NewCharacterCaptureRule"/>'s
    /// first condition.</summary>
    private long _armedAtTicksUtc;

    private long _nextPollMs;

    /// <summary>The length seen on the previous poll, or -1 before the first one. The
    /// stability half of <see cref="NewCharacterCaptureRule.ShouldCapture"/>.</summary>
    private long _previousBytes = -1;

    /// <summary>Throttles the "still waiting" line so a player who takes ten minutes over their
    /// appearance does not produce ten minutes of log.</summary>
    private long _nextWaitingLogMs;

    public void OnFrame()
    {
        uint serial = block.JoinChargenRequest;

        if (serial != block.JoinChargenApplied)
        {
            Arm(serial);
            return;
        }

        if (!_armed)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs < _nextPollMs)
        {
            return;
        }

        _nextPollMs = nowMs + NewCharacterCaptureRule.PollIntervalMs;
        Poll(nowMs);
    }

    private void Arm(uint serial)
    {
        _armedSerial = serial;
        _armed = true;
        _armedAtTicksUtc = DateTime.UtcNow.Ticks;
        _previousBytes = -1;
        _nextPollMs = 0;
        _nextWaitingLogMs = Environment.TickCount64 + 15000;

        block.JoinChargenBytes = 0;
        block.JoinChargenState = SharedBlock.JoinChargenStateArmed;
        block.JoinChargenApplied = serial;

        // ASCII only: the plugin log is written as bytes, so an em-dash arrives as '?'.
        PluginHost.Log(
            $"newchar: armed (request {serial}) - watching Temp\\{PifoFile} for the character "
            + "the game's own chargen writes");
    }

    private void Poll(long nowMs)
    {
        try
        {
            string directory = NativeMethods.GetGameDirectory();
            string temp = Path.Combine(directory, "Temp");
            string pifo = Path.Combine(temp, PifoFile);

            FileInfo info = new(pifo);
            bool exists = info.Exists;
            long bytes = exists ? info.Length : 0;
            long writtenTicks = exists ? info.LastWriteTimeUtc.Ticks : 0;

            if (!NewCharacterCaptureRule.ShouldCapture(
                exists, bytes, _previousBytes, writtenTicks, _armedAtTicksUtc))
            {
                _previousBytes = exists ? bytes : -1;

                if (nowMs >= _nextWaitingLogMs)
                {
                    _nextWaitingLogMs = nowMs + 15000;
                    PluginHost.Log(
                        $"newchar: still waiting for a character (pifo exists={exists} bytes={bytes})");
                }

                return;
            }

            string carried = Path.Combine(temp, MenuLoadController.CarriedCharacterFile);
            File.Copy(pifo, carried, overwrite: true);

            long copied = new FileInfo(carried).Length;
            if (copied < NewCharacterCaptureRule.MinimumBytes)
            {
                // The copy itself came out short. Do NOT report a capture the agent would then
                // adopt with: a short pifo produces a blank level-1 stranger rather than an
                // error, which is the single hardest failure in this whole area to diagnose.
                Fail($"the copy came out {copied} bytes");
                return;
            }

            _armed = false;
            block.JoinChargenBytes = (uint)copied;
            block.JoinChargenState = SharedBlock.JoinChargenStateCaptured;
            PluginHost.Log(
                $"newchar: captured the created character into Temp\\{MenuLoadController.CarriedCharacterFile} "
                + $"({copied} bytes) - the agent can now adopt the host's world");
        }
        catch (IOException ex)
        {
            // The engine still has the file open; try again next poll rather than failing the
            // whole join on one unlucky quarter-second.
            _previousBytes = -1;
            PluginHost.Log($"newchar: poll deferred ({ex.Message})");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Fail(string why)
    {
        _armed = false;
        block.JoinChargenState = SharedBlock.JoinChargenStateFailed;
        PluginHost.Log($"newchar: capture failed for request {_armedSerial} - {why}");
    }
}
