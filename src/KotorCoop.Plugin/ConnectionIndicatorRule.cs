namespace KotorCoop.Plugin;

/// <summary>
/// The pure part of the persistent connection indicator (item 18, roadmap-seamless.md): what
/// <c>SharedBlock.OffsetConnectStatus</c>'s packed uint should show on screen, and whether a
/// freshly-decoded line is worth re-drawing. Separated from
/// <see cref="ConnectionIndicatorController"/> the same way <see cref="LivenessRule"/> is split
/// from its controller — no <c>SharedBlockView</c>, no engine pointers, so it is unit-testable
/// without a live block; see <c>ConnectionIndicatorRuleTests</c> and
/// <c>KotorCoop.Tests.csproj</c>'s plugin-file include list.
///
/// The plugin cannot reference <c>KotorCoop.Agent.ConnectionSnapshot</c>/<c>Pack</c> (a
/// NativeAOT win-x86 DLL with no dependencies on purpose — see
/// <c>KotorCoop.Plugin.csproj</c>'s own comment), so the bit layout — state in the low byte,
/// peer count in the next byte up — is duplicated here by VALUE, the same way
/// <see cref="LivenessHostState"/> duplicates <c>Protocol.CreatureLivenessState</c> and
/// <see cref="AgentLivenessState"/> stands alone from anything agent-side. Kept in exact sync
/// with <c>SharedBlock.ConnectStatusIdle/Connecting/Connected/Failed/Reconnecting</c> — the
/// same four-plus-one sentinels <c>ConnectionSnapshot.Pack</c> writes — by using those constants
/// directly rather than re-declaring them a third time.
/// </summary>
internal static class ConnectionIndicatorRule
{
    /// <summary>
    /// Decodes the packed <c>SharedBlockView.ConnectStatus</c> word into the line to show, or
    /// null to show nothing at all (the Idle state — no connect attempted yet, and the block's
    /// own zero-initialised default, so a fresh install with no agent shows no stray line).
    /// Wording matches <c>KotorCoop.Agent.ConnectionStatusAnnouncer.Line</c> exactly, so the
    /// persistent HUD line and the fading chat/console toast never say the connection is in two
    /// different states at once.
    /// </summary>
    public static string? Text(uint packedStatus)
    {
        uint state = packedStatus & 0xFF;
        int peerCount = (int)((packedStatus >> 8) & 0xFF);

        if (state == SharedBlock.ConnectStatusIdle)
        {
            return null;
        }

        if (state == SharedBlock.ConnectStatusConnecting)
        {
            return "co-op: connecting...";
        }

        if (state == SharedBlock.ConnectStatusConnected)
        {
            return peerCount switch
            {
                <= 0 => "co-op: connected, alone",
                1 => "co-op: connected, 1 player",
                _ => $"co-op: connected, {peerCount} players",
            };
        }

        if (state == SharedBlock.ConnectStatusFailed)
        {
            return "co-op: connection failed";
        }

        if (state == SharedBlock.ConnectStatusReconnecting)
        {
            return "co-op: reconnecting...";
        }

        // An undefined state byte (should never happen on a block only this codebase writes):
        // say nothing rather than show a stale or garbled line.
        return null;
    }

    /// <summary>
    /// Whether the on-screen line needs to change. Text-equality is the whole rule — the
    /// controller re-posts every frame regardless (the engine's queued-text node expires on
    /// its own <c>life</c> countdown; see <c>Odyssey.PostTextLine</c>), but only re-encodes the
    /// ASCII byte buffer when this says the string itself moved, the same cheap-path split
    /// <see cref="AgentLivenessController"/> already uses for its own warning line.
    /// </summary>
    public static bool ShouldRedraw(string? previousText, string? currentText) =>
        !string.Equals(previousText, currentText, StringComparison.Ordinal);
}
