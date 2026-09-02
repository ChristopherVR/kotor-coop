// World state: the current module name, global variable (boolean/number) replication, save
// identity/digests, and world-load/session-load requests.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>Live engine read, never retained — see the interface remarks. The "areaXX"
    /// fallback is deliberately NOT applied here: a caller labelling a census wants "the
    /// name is unreadable" reported as failure, not disguised as a placeholder name.</summary>
    public bool TryReadCurrentModule(out string module) =>
        EngineGlobals.TryReadModuleName(_memory, out module) && module.Length > 0;
    // ---- world state -------------------------------------------------------------

    private byte[]? _shadowBooleans;
    private sbyte[]? _shadowNumbers;

    /// <summary>
    /// Variables that are per-player rather than world truth, and would do damage if
    /// replicated. `K_LAST_MODULE` / `K_LAST_LOCATION` / `K_STUNT_MODULE` are literally
    /// "where this player last was"; `K_CURRENT_PLANET` is the same idea. Matching is on
    /// the name, so this survives a catalogue that renumbers.
    /// </summary>
    private static readonly string[] NotWorldTruth =
        ["K_CURRENT_PLANET", "K_LAST_MODULE", "K_LAST_LOCATION", "K_STUNT_MODULE"];

    private readonly HashSet<uint> _excludedFlags = [];

    public bool SupportsWorldState => _globals.IsResolved;

    public IReadOnlyList<GlobalVariable> GlobalCatalogue => _globals.Catalogue;

    public uint GlobalCatalogueDigest => _globals.CatalogueDigest;

    /// <summary>The catalogue digest for the compatibility handshake; 0 until resolved.</summary>
    public uint GlobalsCatalogueDigest => _globals.IsResolved ? _globals.CatalogueDigest : 0;

    private SaveDigestCache? _saveDigests;
    private bool _saveDigestsResolved;

    /// <summary>
    /// The file-side digest source for the world handshake, resolved once from the game
    /// process's own executable path (correct whichever install this instance runs from —
    /// GOG, Steam, or a second co-op copy). Null when the path cannot be read, in which
    /// case both digests stay 0 = unknown, which only ever warns.
    /// </summary>
    private SaveDigestCache? SaveDigests
    {
        get
        {
            if (!_saveDigestsResolved)
            {
                _saveDigestsResolved = true;

                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
                    string? gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (gameDirectory is not null)
                    {
                        _saveDigests = new SaveDigestCache(gameDirectory);
                    }
                }
                catch (Exception)
                {
                    // No access to the process path: the save digests stay unknown.
                }
            }

            return _saveDigests;
        }
    }

    /// <summary>The staged/current save's identity digest for the world handshake; 0 until
    /// (unless) resolved — see <see cref="SaveDigest"/>.</summary>
    public uint SaveIdentityDigest => SaveDigests?.SaveIdentity ?? 0;

    /// <summary>The named module's GIT-id digest for the world handshake; 0 when unknown.</summary>
    public uint ModuleGitDigest(string module) => SaveDigests?.ModuleGit(module) ?? 0;

    /// <summary>
    /// Roadmap item 14 phase 2's load-detection edge: the plugin's own count of world
    /// teardowns it judged UNANNOUNCED (see <c>WorldLoadRule</c>/<c>SharedBlock.OffsetWorldLoadSerial</c>).
    /// 0 while no plugin block is attached, exactly like every other block-backed reading here.
    /// </summary>
    public uint WorldLoadSerial =>
        _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockWorldLoadSerial, out uint serial)
            ? serial
            : 0;

    /// <summary>See <see cref="IWorldStateBridge.TryReadWorldLoadSerial"/>: false while no
    /// block is attached (<c>TryFindPlugin</c> zeroes <c>_pluginBlock</c> for the length of
    /// its scan) or the read itself fails mid-teardown, so the caller skips the tick instead
    /// of baselining or edging on a 0 that is not a counter value.</summary>
    public bool TryReadWorldLoadSerial(out uint serial)
    {
        serial = 0;
        nint block = _pluginBlock;
        return block != 0 && _memory.TryReadUInt32(block + BlockWorldLoadSerial, out serial);
    }

    /// <summary>
    /// See <see cref="IWorldStateBridge.IsWorldBetweenLives"/>. Three block-backed readings,
    /// each covering a stretch of the load the others cannot (shared-inventory-v2.md §17):
    /// the serial moves at teardown and stays ahead of the caches until the new world's player
    /// resolves (<c>TryReadLocalState</c>'s reset); the engine-player pointer is 0 while no
    /// world is up at all; and a load THIS agent asked for is in flight from the moment of
    /// asking until the plugin acks the new world on screen — the first second, when the old
    /// world still looks perfectly loaded and neither of the other two has anything to say.
    /// 0/0/idle without a plugin: never true.
    /// </summary>
    public bool IsWorldBetweenLives =>
        WorldLoadSerial != _cachedForWorldSerial
        || (_pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockEnginePlayerPointer, out uint enginePlayer)
            && enginePlayer == 0)
        || PluginMenuLoader.TryReadLoadInFlight(_memory, _pluginBlock);

    /// <summary>The block address <see cref="TryRequestAdoptedWorldLoad"/> last requested a
    /// load against, for <see cref="TryGetAdoptedWorldLoadStatus"/> to poll. 0 until a request
    /// has been made this process.</summary>
    private nint _menuLoadBlock;

    /// <summary>
    /// Roadmap item 14 phase 2's rejoin action: fires the SAME agent-side request
    /// `agent adopt` (Program.cs's <c>AdoptAsync</c>) already uses for the first join —
    /// <see cref="PluginMenuLoader.TryRequestLoad"/> with <c>adoptedWorld: true</c>, so the
    /// login reads the carried character off <c>Temp\pifo.ifo</c> rather than the staged
    /// world's own (the host's). No new engine RE: this is the exact call the first-join path
    /// already makes, reused for a mid-session re-adopt.
    ///
    /// ⚠ NEEDS-LIVE, and this is the honest gap: this call always passes the default
    /// <c>inSession: false</c>, so <c>MenuLoadController</c> still acks-and-no-ops the instant
    /// <c>Odyssey.HasServer</c> is true (see its own header) — unchanged from before
    /// <see cref="TryRequestMidSessionLoad"/> existed. Whether the game has returned all the
    /// way to "no server" by the time this fires — after the PLAYER's own in-game Load Game
    /// action has already finished standing up a fresh one — has never been measured. If it
    /// has not, this call degrades to a harmless no-op ack rather than a crash; see
    /// docs/research/session-save.md's Phase 2 NEEDS-LIVE list. <see cref="TryRequestMidSessionLoad"/>
    /// is the new, separate entry point built to close a related but distinct gap (a genuine
    /// mid-session Load Game action, for testing <c>WorldLoadRule.IsUnannouncedTeardown</c>'s
    /// positive case) — once THAT is live-verified, passing <c>inSession: true</c> here too is
    /// the natural next step, not attempted by this pass.
    /// </summary>
    public bool TryRequestAdoptedWorldLoad(out string diagnostic) =>
        TryRequestAdoptedWorldLoad(inSession: false, out diagnostic);

    /// <summary>
    /// The same adopted-world load, able to ask for the MID-SESSION replay.
    ///
    /// ⚠ WHY THE NEW-CHARACTER JOIN NEEDS IT. A joiner who creates a character through retail
    /// chargen is, by the time that character exists, already standing in the Endar Spire — the
    /// engine's own New Game path loads a module as part of finishing chargen, and there is no
    /// safe place to interrupt it (the module name is built and consumed inside
    /// <c>CSWGuiClassSelection::BeginPlayGame 0x6DBDF0</c>, after the pifo is written). So the
    /// adoption that follows is not a main-menu load at all, and passing
    /// <c>inSession: false</c> would make <c>MenuLoadController</c> ack-and-no-op the instant
    /// it saw a server (its own <c>OnFrame</c>'s "server already exists" branch) — the player
    /// would sit in the Endar Spire believing they had joined.
    /// </summary>
    public bool TryRequestAdoptedWorldLoad(bool inSession, out string diagnostic)
    {
        bool ok = PluginMenuLoader.TryRequestLoad(
            _memory, out nint block, out diagnostic, adoptedWorld: true, inSession: inSession);
        if (ok)
        {
            _menuLoadBlock = block;
        }

        return ok;
    }

    /// <summary>The block the new-character capture was armed against, for
    /// <see cref="TryGetNewCharacterCapture"/> to poll. 0 until it has been armed.</summary>
    private nint _joinChargenBlock;

    /// <summary>
    /// Arms the plugin's new-character capture — coop.ini <c>join_mode = newcharacter</c>. See
    /// <see cref="PluginMenuLoader.TryArmNewCharacterCapture"/> and
    /// <c>KotorCoop.Plugin.NewCharacterCaptureController</c>.
    /// </summary>
    public bool TryArmNewCharacterCapture(out string diagnostic)
    {
        bool ok = PluginMenuLoader.TryArmNewCharacterCapture(_memory, out nint block, out diagnostic);
        if (ok)
        {
            _joinChargenBlock = block;
        }

        return ok;
    }

    /// <summary>
    /// Runs a character-creation drive verb through the plugin — <c>newgame</c>,
    /// <c>chargen quick</c>, <c>chargen accept</c>. See
    /// <see cref="PluginMenuLoader.TryDriveChargen"/> and
    /// <c>KotorCoop.Plugin.CharGenDriveController</c>.
    /// </summary>
    public bool TryDriveChargen(uint kind, int classIndex, out string diagnostic) =>
        PluginMenuLoader.TryDriveChargen(_memory, kind, classIndex, out diagnostic);

    /// <summary>Polls the capture armed by <see cref="TryArmNewCharacterCapture"/>. False
    /// while nothing has been armed yet or the block cannot be read.</summary>
    public bool TryGetNewCharacterCapture(out uint state, out uint bytes, out string saveDirectory)
    {
        state = 0;
        bytes = 0;
        saveDirectory = string.Empty;
        return _joinChargenBlock != 0
            && PluginMenuLoader.TryReadNewCharacterCapture(
                _memory, _joinChargenBlock, out state, out bytes, out saveDirectory);
    }

    /// <summary>Polls the outcome of the last <see cref="TryRequestAdoptedWorldLoad"/> (or
    /// <see cref="TryRequestMidSessionLoad"/> — both write the SAME status fields through
    /// <see cref="PluginMenuLoader"/>, so one poll serves either). False while nothing has
    /// been requested yet or the block cannot be read.</summary>
    public bool TryGetAdoptedWorldLoadStatus(out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        return _menuLoadBlock != 0
            && PluginMenuLoader.TryReadStatus(_memory, _menuLoadBlock, out applied, out status);
    }

    /// <summary>
    /// STATIC RE (2026-08-23, docs/research/session-save.md's dated entry): fires a GENUINE
    /// mid-session (pause-menu-equivalent) Load Game action — the `loadgame &lt;slot&gt;`
    /// console verb's own engine call. Stages <paramref name="slotIndex"/> over the autosave
    /// slot first (the SAME agent-side staging <c>SaveLoader</c>/<c>agent -Save N</c> already
    /// does), then requests the SAME <see cref="PluginMenuLoader.TryRequestLoad"/> channel
    /// <see cref="TryRequestAdoptedWorldLoad"/> uses, but with <c>inSession: true</c> so
    /// <c>MenuLoadController</c> runs retail's own extra
    /// <c>CAppManager::DestroyServer</c> + <c>CClientExoApp::UnloadModule</c> pre-step
    /// (<c>CSWGuiSaveLoad::LoadGame</c>'s <c>bFromMainMenu==0</c> branch) instead of
    /// acking-and-mooting the instant a server already exists.
    ///
    /// Built to let a live round exercise <c>WorldLoadRule.IsUnannouncedTeardown</c>'s
    /// positive case (a genuine in-game Load, as opposed to an ordinary save or a peer-follow
    /// warp) deterministically, without the mouse-click/screenshot rig-tooling gap
    /// docs/research/session-save.md's most recent round hit. Deliberately NOT wired into
    /// <see cref="TryRequestAdoptedWorldLoad"/>/the <c>client_load_rejoin</c>/
    /// <c>host_load_readopt</c> rejoin path — this is a separate, additive, test-only entry
    /// point; folding <c>inSession: true</c> into the rejoin path is a natural follow-up once
    /// THIS path is itself live-verified, not something this pass claims to have done.
    /// </summary>
    public bool TryRequestMidSessionLoad(int slotIndex, out string diagnostic)
    {
        string? gameDirectory;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
            gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);
        }
        catch (Exception ex)
        {
            diagnostic = $"cannot resolve the game directory: {ex.Message}";
            return false;
        }

        if (gameDirectory is null)
        {
            diagnostic = "cannot resolve the game directory";
            return false;
        }

        if (!PluginMenuLoader.TryStageSaveAsAutosave(gameDirectory, slotIndex, out string staging))
        {
            diagnostic = staging;
            return false;
        }

        bool ok = PluginMenuLoader.TryRequestLoad(
            _memory, out nint block, out string request, adoptedWorld: false, inSession: true);
        if (ok)
        {
            _menuLoadBlock = block;
        }

        diagnostic = ok ? $"{staging}; {request}" : request;
        return ok;
    }

    /// <summary>
    /// Locates the global variable table and takes the baseline snapshot. Called from
    /// TryResolve, since the catalogue is fixed for as long as a save is loaded.
    /// </summary>
    private void TryResolveGlobals()
    {
        if (!_globals.TryResolve(out _))
        {
            return;
        }

        _shadowBooleans = new byte[GlobalVariableTable.BooleanByteCount];
        _shadowNumbers = new sbyte[GlobalVariableTable.NumberCount];
        _globals.TryReadValues(_shadowBooleans, _shadowNumbers);

        _excludedFlags.Clear();
        foreach (GlobalVariable variable in _globals.Catalogue)
        {
            if (NotWorldTruth.Contains(variable.Name, StringComparer.OrdinalIgnoreCase)
                || variable.Name.StartsWith("k_vis_", StringComparison.OrdinalIgnoreCase))
            {
                _excludedFlags.Add(variable.FlagId);
            }
        }
    }

    /// <summary>
    /// Diffs the game's world state against the shadow copy and reports what moved. The
    /// whole comparison is 613 bytes, so this is cheap enough to run every tick and the
    /// common case exits after two memory reads that match.
    /// </summary>
    public List<KeyValuePair<uint, sbyte>> CollectChangedWorldState()
    {
        List<KeyValuePair<uint, sbyte>> changed = [];

        if (!_globals.IsResolved)
        {
            return changed;
        }

        // A null shadow with a resolved table means ForgetWorldShadows dropped it because the
        // world underneath was REPLACED (an adopted load). Re-take the baseline here and report
        // nothing this pass, rather than waiting for the next TryResolve: the whole point of
        // dropping it was to stop the OLD world's values being announced as changes, and this
        // is the one place that can see the new ones. Self-healing on purpose — a channel that
        // silently stops reporting for the rest of the session would be a worse bug than the
        // one this fixes.
        if (_shadowBooleans is null || _shadowNumbers is null)
        {
            _shadowBooleans = new byte[GlobalVariableTable.BooleanByteCount];
            _shadowNumbers = new sbyte[GlobalVariableTable.NumberCount];
            _globals.TryReadValues(_shadowBooleans, _shadowNumbers);
            return changed;
        }

        byte[] booleans = new byte[GlobalVariableTable.BooleanByteCount];
        sbyte[] numbers = new sbyte[GlobalVariableTable.NumberCount];

        if (!_globals.TryReadValues(booleans, numbers))
        {
            return changed;
        }

        for (int byteIndex = 0; byteIndex < booleans.Length; byteIndex++)
        {
            if (booleans[byteIndex] == _shadowBooleans[byteIndex])
            {
                continue;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                int index = (byteIndex * 8) + bit;
                bool now = GlobalVariableTable.GetBit(booleans, index);

                if (now == GlobalVariableTable.GetBit(_shadowBooleans, index))
                {
                    continue;
                }

                uint flagId = GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, index);
                if (!_excludedFlags.Contains(flagId))
                {
                    changed.Add(new KeyValuePair<uint, sbyte>(flagId, now ? (sbyte)1 : (sbyte)0));
                }
            }

            _shadowBooleans[byteIndex] = booleans[byteIndex];
        }

        for (int index = 0; index < numbers.Length; index++)
        {
            if (numbers[index] == _shadowNumbers[index])
            {
                continue;
            }

            uint flagId = GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, index);
            if (!_excludedFlags.Contains(flagId))
            {
                changed.Add(new KeyValuePair<uint, sbyte>(flagId, numbers[index]));
            }

            _shadowNumbers[index] = numbers[index];
        }

        return changed;
    }

    /// <summary>
    /// Writes a peer's world-state value into the game, and updates the shadow in the same
    /// breath. Skipping the shadow update is the classic failure here: the next scan would
    /// see the value as a fresh local change and echo it back, and the two instances would
    /// broadcast at each other forever.
    /// </summary>
    public bool TryApplyWorldState(uint flagId, sbyte value)
    {
        if (!_globals.IsResolved || _shadowBooleans is null || _shadowNumbers is null)
        {
            return false;
        }

        if (_excludedFlags.Contains(flagId))
        {
            return false;
        }

        var type = (GlobalVariableType)(flagId >> 16);
        int index = (int)(flagId & 0xFFFF);

        switch (type)
        {
            case GlobalVariableType.Boolean:
                if (index >= GlobalVariableTable.BooleanByteCount * 8
                    || !_globals.TryWriteBoolean(index, value != 0))
                {
                    return false;
                }

                int mask = 1 << (7 - (index & 7));
                _shadowBooleans[index >> 3] = (byte)(value != 0
                    ? _shadowBooleans[index >> 3] | mask
                    : _shadowBooleans[index >> 3] & ~mask);
                return true;

            case GlobalVariableType.Number:
                if (index >= GlobalVariableTable.NumberCount || !_globals.TryWriteNumber(index, value))
                {
                    return false;
                }

                _shadowNumbers[index] = value;
                return true;

            default:
                // Locations and strings are deliberately not replicated: every one of them
                // in a real save is a per-player scratchpad or cutscene staging value.
                return false;
        }
    }

}
