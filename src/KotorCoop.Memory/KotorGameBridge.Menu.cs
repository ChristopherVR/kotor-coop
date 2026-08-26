// Shared pause/combat toggles, the in-game Connect menu, the console/chat overlay channels,
// and the server browser (publishing/reading session lists, connecting, hosting).
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- shared pause ----------------------------------------------------------

    /// <summary>Pause sync needs the plugin: only an in-process SetPauseState call
    /// actually stops the world timer.</summary>
    public bool SupportsPauseSync => _pluginBlock != 0;

    /// <summary>Tells the plugin whether any remote peer currently holds a pause.</summary>
    public bool TrySetRemotePause(bool desired) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockRemotePauseDesired, desired ? 1u : 0u);

    // ---- shared combat state ----------------------------------------------------

    /// <summary>Combat replication needs the plugin: only an in-process
    /// CSWSCreature::SetCombatState call moves the state the engine actually reads.</summary>
    public bool SupportsCombatSync => _pluginBlock != 0;

    /// <summary>
    /// Tells the plugin whether a REMOTE peer standing in this player's module is fighting.
    /// Never includes the local player — see <c>SharedBlock.OffsetSessionInCombat</c>.
    /// </summary>
    public bool TrySetSessionInCombat(bool fighting) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockSessionInCombat, fighting ? 1u : 0u);

    /// <summary>
    /// The plugin's count of times this player has released a pause mirrored from a peer.
    /// A CHANGE in this value is the release; the value itself means nothing.
    /// </summary>
    public uint LocalPauseReleaseSerial =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalPauseReleaseSerial, out uint serial)
            ? serial
            : 0;

    /// <summary>Tells the plugin a peer has ended the shared pause, so this instance drops
    /// the pause it started.</summary>
    public bool TrySignalRemotePauseRelease(uint serial) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockRemotePauseReleaseSerial, serial);

    // ---- in-game Connect flow --------------------------------------------------

    private uint _lastConnectSerial;

    /// <summary>Writes the main-menu Co-op button override (the plugin shows the button by
    /// itself now — a 1 here FORCES it on over a coop.ini opt-out, a 0 defers to coop.ini)
    /// and remembers the current request serial so the first poll does not fire spuriously.</summary>
    public bool EnableConnectMenu(bool enabled)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        _memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out _lastConnectSerial);
        return _memory.TryWrite(_pluginBlock + BlockConnectEnable, enabled ? 1u : 0u);
    }

    /// <summary>
    /// True (once) when the player has pressed the Co-op button since the last check.
    /// <paramref name="typedHost"/> receives any host IP entered in-game (empty when the
    /// button carries no address, i.e. coop.ini decides).
    /// </summary>
    public bool TryTakeConnectRequest(out string typedHost)
    {
        typedHost = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out uint serial)
            || serial == _lastConnectSerial)
        {
            return false;
        }

        _lastConnectSerial = serial;

        Span<byte> buffer = stackalloc byte[BlockConnectHostLength];
        if (_memory.TryRead(_pluginBlock + BlockConnectHost, buffer))
        {
            int end = buffer.IndexOf((byte)0);
            typedHost = System.Text.Encoding.ASCII.GetString(buffer[..(end < 0 ? buffer.Length : end)]);
        }

        return true;
    }

    /// <summary>Reports the connect status back to the plugin (for a menu status line).</summary>
    public bool SetConnectStatus(uint status) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockConnectStatus, status);

    private uint _lastConsoleSerial;
    private uint _consoleReplySerial;

    /// <summary>
    /// True (once) when the in-game console has entered a new command line since the last
    /// check; <paramref name="commandLine"/> receives it (e.g. "connect 1.2.3.4 secret").
    /// </summary>
    public bool TryTakeConsoleCommand(out string commandLine)
    {
        commandLine = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockConsoleSerial, out uint serial)
            || serial == _lastConsoleSerial)
        {
            return false;
        }

        _lastConsoleSerial = serial;

        if (_memory.TryReadAsciiString(_pluginBlock + BlockConsoleCommand, BlockConsoleCommandLength, out string raw))
        {
            commandLine = raw.Trim();
        }

        return commandLine.Length > 0;
    }

    // ---- server browser ---------------------------------------------------------

    private const int BlockBrowserOpen = 0xE00;
    private const int BlockBrowserSelection = 0xE04;
    private const int BlockBrowserCount = 0xE08;
    private const int BlockBrowserConnectSerial = 0xE0C;
    private const int BlockBrowserListSerial = 0xE10;
    private const int BlockBrowserEntries = 0xE20;
    private const int BlockBrowserEntryStride = 0x40;
    private const int BlockBrowserEntryName = 0x00;
    private const int BlockBrowserEntryNameLength = 32;
    private const int BlockBrowserEntryAddress = 0x20;
    private const int BlockBrowserEntryAddressLength = 24;
    private const int BlockBrowserEntryPlayers = 0x38;
    private const int BlockBrowserEntryMaxPlayers = 0x39;
    private const int BlockBrowserCapacity = 8;

    // ---- one-shot animations (block version 16) ---------------------------------

    private const int BlockLocalOneShotAnim = 0x1050;
    private const int BlockLocalOneShotSerial = 0x1054;
    private const int BlockRemoteOneShot = 0x1060;
    private const int BlockRemoteOneShotStride = 0x08;

    private uint _lastSeenOneShotSerial;
    private byte _oneShotSequence;
    private ushort _oneShotAnimation;

    /// <summary>
    /// The local player's most recent one-shot animation, as an EDGE. The plugin hooks the
    /// engine's fire-and-forget dispatch (these finish between polls, so they cannot be
    /// sampled) and bumps a serial; this turns that into a small sequence byte that rides the
    /// per-tick state, so a repeated identical flourish still replicates.
    /// </summary>
    private void RefreshOneShotAnimation()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalOneShotSerial, out uint serial)
            || serial == _lastSeenOneShotSerial)
        {
            return;
        }

        _lastSeenOneShotSerial = serial;

        if (_memory.TryReadUInt32(_pluginBlock + BlockLocalOneShotAnim, out uint animation))
        {
            _oneShotAnimation = (ushort)animation;
            _oneShotSequence++;
        }
    }

    /// <summary>Publishes a peer's one-shot into their slot; the plugin fires it once.</summary>
    private void WriteRemoteOneShot(int slot, in PlayerState state)
    {
        if (_pluginBlock == 0 || state.OneShotAnimation == 0)
        {
            return;
        }

        nint at = _pluginBlock + BlockRemoteOneShot + (slot * BlockRemoteOneShotStride);

        // Animation before serial: the plugin reads the serial last, so it can never see a
        // bumped serial paired with a stale id.
        _memory.TryWrite(at, (uint)state.OneShotAnimation);
        _memory.TryWrite(at + 4, (uint)state.OneShotSequence);
    }

    // ---- co-op death handling (block version 14) --------------------------------

    /// <summary>The plugin owns the suppressions and the revive; without it there is no
    /// death mediation and the game behaves exactly as single-player.</summary>
    public bool SupportsDeathHandling => _pluginBlock != 0;

    private const int BlockLocalDowned = 0x1020;
    private const int BlockLocalDownedSerial = 0x1024;
    private const int BlockGameOverSerial = 0x1028;
    private const int BlockSuppressGameOver = 0x102C;
    private const int BlockSuppressSwap = 0x1030;
    private const int BlockReviveSerial = 0x1034;
    private const int BlockReviveApplied = 0x1038;
    private const int BlockReviveStatus = 0x103C;
    private const int BlockReviveHitPoints = 0x1040;

    private uint _lastGameOverSerial;
    private uint _reviveSerial;
    private bool _deathBaselined;

    /// <summary>
    /// Turns the two death suppressions on (or off). While a session is live the game must
    /// not swap control away from the player's own PC, nor drop this instance to the main
    /// menu on a party wipe — the session decides that jointly. Clearing
    /// <paramref name="suppressGameOver"/> lets the next attempt through, which is how a
    /// genuine, synchronised wipe is delivered.
    /// </summary>
    public bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        if (!_deathBaselined)
        {
            _deathBaselined = true;
            _memory.TryReadUInt32(_pluginBlock + BlockGameOverSerial, out _lastGameOverSerial);
            _memory.TryReadUInt32(_pluginBlock + BlockReviveSerial, out _reviveSerial);
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockSuppressGameOver, suppressGameOver ? 1u : 0u);
        ok &= _memory.TryWrite(_pluginBlock + BlockSuppressSwap, suppressSwap ? 1u : 0u);
        return ok;
    }

    /// <summary>
    /// True (once) when the engine tried to show the game-over screen and the plugin
    /// swallowed it — i.e. this instance's whole party is down. The caller decides whether
    /// the session is genuinely wiped (every player down) or this player is waiting for a
    /// rescue.
    /// </summary>
    public bool TryTakeGameOverAttempt()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockGameOverSerial, out uint serial)
            || serial == _lastGameOverSerial)
        {
            return false;
        }

        _lastGameOverSerial = serial;
        return true;
    }

    /// <summary>
    /// Stands the local PC back up at <paramref name="hitPoints"/> (0 = the plugin's
    /// default). Used when a peer has reached a downed player.
    /// </summary>
    public bool RequestRevive(int hitPoints = 0)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        // Hit points before the serial: the plugin reads the serial last, so it can never
        // see a bumped serial paired with a stale amount.
        bool ok = _memory.TryWrite(_pluginBlock + BlockReviveHitPoints, (uint)Math.Max(hitPoints, 0));
        ok &= _memory.TryWrite(_pluginBlock + BlockReviveSerial, ++_reviveSerial);
        return ok;
    }

    /// <summary>The plugin's verdict on the last revive: 0 = applied, non-zero = refused.</summary>
    public bool TryReadReviveStatus(out uint status)
    {
        status = 0;
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockReviveApplied, out uint applied)
            && applied == _reviveSerial
            && _memory.TryReadUInt32(_pluginBlock + BlockReviveStatus, out status);
    }

    private const int BlockBrowserHostSerial = 0xE18;

    /// <summary>Agent -&gt; plugin: bumped to make the GAME ask the player for a password.</summary>
    private const int BlockBrowserPasswordSerial = 0xE14;

    /// <summary>Entry +0x3A: non-zero when the host requires a password (a lock marker in the row).</summary>
    private const int BlockBrowserEntryLocked = 0x3A;

    /// <summary>Non-zero when this agent already holds a password for the next join
    /// (coop.ini, --password, or an answered prompt): the plugin prompts on a locked row
    /// only when this is clear, so configured players are never asked. Same value on every
    /// row; previously unused padding, so no signature bump.</summary>
    private const int BlockBrowserEntryPasswordHeld = 0x3B;

    // SharedBlock's browser details array (block version 36).
    private const int BlockBrowserDetails = 0x3400;
    private const int BlockBrowserDetailStride = 0x70;
    private const int BlockBrowserDetailModule = 0x00;
    private const int BlockBrowserDetailModuleLength = 40;
    private const int BlockBrowserDetailPortraits = 0x28;
    private const int BlockBrowserDetailPortraitLength = 16;
    private const int BlockBrowserDetailPortraitCount = 3;
    private const int BlockBrowserDetailLoadScreen = 0x58;
    private const int BlockBrowserDetailLoadScreenLength = 16;

    /// <summary>Module resref -> what to show a browsing player, cached because the answer
    /// costs an archive open and never changes for a given resref.</summary>
    private readonly Dictionary<string, string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A readable name for a module resref, resolved from THIS install's own module files.
    ///
    /// Deliberately local rather than sent by the host: both installs have the base game, the
    /// answer is identical on both, and a resref is 16 bytes on the wire where a display name
    /// is not. Falls back to the resref itself, which is what the browser showed before and is
    /// never wrong — only terse.
    /// </summary>
    private string DescribeModule(string module)
    {
        if (module.Length == 0)
        {
            return string.Empty;
        }

        if (_moduleNames.TryGetValue(module, out string? cached))
        {
            return cached;
        }

        string name = module;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
            string? gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);

            if (gameDirectory is not null
                && GameFiles.ModuleNames.TryRead(gameDirectory, module, out string readable)
                && readable.Length > 0)
            {
                name = readable;
            }
        }
        catch (Exception)
        {
            // A browser label is not worth failing anything for; keep the resref.
        }

        _moduleNames[module] = name;
        return name;
    }

    private uint _browserListSerial;
    private uint _lastBrowserConnectSerial;
    private uint _lastBrowserHostSerial;
    private uint _passwordPromptSerial;
    private string _lastBrowserSignature = string.Empty;

    /// <summary>True while the in-game browser panel is on screen.</summary>
    public bool BrowserOpen =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockBrowserOpen, out uint open)
        && open != 0;

    /// <summary>
    /// Publishes the discovered servers for the in-game panel to draw. Writes the rows first and
    /// the count last, so the plugin can never read a count that outruns the rows. A cheap
    /// signature check keeps this from rewriting the same list every tick.
    /// <paramref name="passwordHeld"/> is this agent's "I already hold a password for the next
    /// join" flag (coop.ini, --password, or an answered prompt), stamped on every row: the
    /// plugin's in-browser prompt fires only on locked-and-not-held, so a player who
    /// configured a password is never asked for it.
    /// </summary>
    public bool PublishBrowserServers(
        IReadOnlyList<(string Name, string Address, byte Players, byte MaxPlayers, bool Locked, string Module)> servers,
        bool passwordHeld = false) =>
        PublishBrowserServers(
            [.. servers.Select(s => (s.Name, s.Address, s.Players, s.MaxPlayers, s.Locked, s.Module, (IReadOnlyList<string>)[]))],
            passwordHeld);

    public bool PublishBrowserServers(
        IReadOnlyList<(string Name, string Address, byte Players, byte MaxPlayers, bool Locked,
            string Module, IReadOnlyList<string> Portraits)> servers,
        bool passwordHeld = false)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(servers.Count, BlockBrowserCapacity);

        // The held flag folds into the signature so gaining or losing a password rewrites
        // the rows even when the server list itself has not changed.
        string signature = $"held={(passwordHeld ? 1 : 0)}|" + string.Join('|', servers.Take(count)
            .Select(s => $"{s.Name}@{s.Address}:{s.Players}/{s.MaxPlayers}:{(s.Locked ? 1 : 0)}:{s.Module}"
                + $":{string.Join(',', s.Portraits)}"));
        if (signature == _lastBrowserSignature)
        {
            return true;
        }

        _lastBrowserSignature = signature;

        for (int i = 0; i < count; i++)
        {
            nint entry = _pluginBlock + BlockBrowserEntries + (i * BlockBrowserEntryStride);
            nint detail = _pluginBlock + BlockBrowserDetails + (i * BlockBrowserDetailStride);
            (string name, string address, byte players, byte max, bool locked, string module,
                IReadOnlyList<string> portraits) = servers[i];

            // ⚠ The module no longer rides inside the name. It used to be folded in as
            // "Name [module]" because the row was the only place to put it; the row is now
            // the host's chosen name alone, and the world has its own field for the details
            // pane — which is what a player actually reads before joining.
            _memory.TryWriteAsciiString(entry + BlockBrowserEntryName, name, BlockBrowserEntryNameLength);
            _memory.TryWriteAsciiString(entry + BlockBrowserEntryAddress, address, BlockBrowserEntryAddressLength);
            _memory.TryWrite(entry + BlockBrowserEntryPlayers, players);
            _memory.TryWrite(entry + BlockBrowserEntryMaxPlayers, max);
            _memory.TryWrite(entry + BlockBrowserEntryLocked, locked ? (byte)1 : (byte)0);
            _memory.TryWrite(entry + BlockBrowserEntryPasswordHeld, passwordHeld ? (byte)1 : (byte)0);

            // Resolved HERE, not in the plugin: reading a module's display name means opening
            // module files, which is the agent's job — the plugin draws what it is handed.
            _memory.TryWriteAsciiString(
                detail + BlockBrowserDetailModule, DescribeModule(module), BlockBrowserDetailModuleLength);

            // "where the host is", as the game's own loading art. Composed rather than looked
            // up: the engine builds these names the same way ("load_%s"), and the area's own
            // LoadScreenID reads 0 on every base-game module.
            _memory.TryWriteAsciiString(
                detail + BlockBrowserDetailLoadScreen,
                module.Length > 0 ? "load_" + module.ToLowerInvariant() : string.Empty,
                BlockBrowserDetailLoadScreenLength);

            for (int p = 0; p < BlockBrowserDetailPortraitCount; p++)
            {
                _memory.TryWriteAsciiString(
                    detail + BlockBrowserDetailPortraits + (p * BlockBrowserDetailPortraitLength),
                    p < portraits.Count ? portraits[p] : string.Empty,
                    BlockBrowserDetailPortraitLength);
            }
        }

        _memory.TryWrite(_pluginBlock + BlockBrowserCount, (uint)count);
        return _memory.TryWrite(_pluginBlock + BlockBrowserListSerial, ++_browserListSerial);
    }

    /// <summary>
    /// True (once) when the player confirmed a row in the panel; <paramref name="index"/> is the
    /// chosen server. The plugin writes the selection before the serial, so a bumped serial
    /// always pairs with the right index.
    /// </summary>
    public bool TryTakeBrowserConnect(out int index)
    {
        index = -1;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockBrowserConnectSerial, out uint serial)
            || serial == _lastBrowserConnectSerial)
        {
            return false;
        }

        _lastBrowserConnectSerial = serial;

        if (!_memory.TryReadUInt32(_pluginBlock + BlockBrowserSelection, out uint selection))
        {
            return false;
        }

        index = (int)selection;
        return true;
    }

    /// <summary>
    /// True (once) when the player pressed HOST in the in-game browser panel rather than
    /// picking a listed server.
    /// </summary>
    public bool TryTakeBrowserHost()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockBrowserHostSerial, out uint serial)
            || serial == _lastBrowserHostSerial)
        {
            return false;
        }

        _lastBrowserHostSerial = serial;
        return true;
    }

    /// <summary>Agent -&gt; plugin: peers the session has admitted right now, written every
    /// dispatcher tick regardless of player resolve — the seamless-arrival gate's signal.</summary>
    private const int BlockSessionPeers = 0xE1C;

    /// <summary>Publishes the session's admitted peer count (0 = no session/nobody), so the
    /// plugin can hold the load screen for the first sync without waiting on the 1 Hz
    /// player-resolve that RemoteCount depends on.</summary>
    public bool PublishSessionPeerCount(int count) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockSessionPeers, (uint)Math.Max(0, count));

    /// <summary>Agent -&gt; plugin: this agent's own clock, stamped every dispatcher tick, so
    /// the plugin can tell "an agent is pumping" from "an agent stopped responding" — the
    /// plugin's own Heartbeat only proves the block itself is live. See
    /// SharedBlock.OffsetAgentHeartbeat and AgentLiveness.</summary>
    private const int BlockAgentHeartbeat = 0x3780;

    /// <summary>
    /// Stamps <see cref="BlockAgentHeartbeat"/> with this process's own clock
    /// (Environment.TickCount64 — the OS uptime counter both processes read independently,
    /// so the plugin needs no handshake to judge staleness). Call once per dispatcher tick;
    /// a no-op with no plugin attached.
    /// </summary>
    public bool PublishHeartbeat() =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockAgentHeartbeat, Environment.TickCount64);

    /// <summary>
    /// Asks the game to prompt the player for a password: the plugin force-opens the developer
    /// console with <c>password&#160;</c> pre-typed, and the answer comes back as an ordinary
    /// console command.
    ///
    /// The agent cannot take the keystroke itself — it is a console window behind a fullscreen
    /// game, and the player is looking at the main menu — so the ask has to be handed to the
    /// side that owns the screen. Seeds the serial from whatever is already in the block, so a
    /// reattached agent does not re-ask on its first request.
    /// </summary>
    public bool RequestPasswordPrompt()
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        if (_passwordPromptSerial == 0
            && _memory.TryReadUInt32(_pluginBlock + BlockBrowserPasswordSerial, out uint current))
        {
            _passwordPromptSerial = current;
        }

        return _memory.TryWrite(_pluginBlock + BlockBrowserPasswordSerial, ++_passwordPromptSerial);
    }

    /// <summary>Posts a status line for the in-game console to display.</summary>
    public bool PostConsoleReply(string line)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockConsoleReply, line, BlockConsoleReplyLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockConsoleReplySerial, ++_consoleReplySerial);
        return ok;
    }

    private uint _chatSerial;

    /// <summary>Pushes one line to the always-on chat overlay (serial bumped last).</summary>
    public bool PostChatLine(string line)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockChatLine, line, BlockChatLineLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockChatSerial, ++_chatSerial);
        return ok;
    }

    private uint _chatClearSerial;

    /// <summary>Wipes the in-game overlay log (the <c>clear</c> command).</summary>
    public bool PostChatClear() =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockChatClearSerial, ++_chatClearSerial);

    /// <summary>
    /// Writes the coop.ini-driven chat-overlay settings the plugin reads. <paramref
    /// name="opacity"/> is 0..100; <paramref name="anchor"/> is 0=top-left, 1=top-right,
    /// 2=bottom-left, 3=bottom-right.
    /// </summary>
    public bool SetChatOverlayConfig(bool enabled, uint opacity, uint anchor, uint maxLines, uint fadeMs)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockChatOverlayOpacity, Math.Clamp(opacity, 0u, 100u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayAnchor, Math.Min(anchor, 3u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayMaxLines, Math.Clamp(maxLines, 1u, 12u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayFadeMs, fadeMs);

        // Enable flag last, so the plugin never reads a half-written config.
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayEnabled, enabled ? 1u : 0u);
        return ok;
    }

}
