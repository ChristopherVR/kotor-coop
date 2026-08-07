namespace KotorCoop.Protocol;

/// <summary>What a spectator should do with a conversation it asked to follow.</summary>
public enum DialogueFollowVerdict
{
    /// <summary>Still arriving or running normally — leave it alone.</summary>
    Wait,

    /// <summary>It is open and carrying content; drive it.</summary>
    Drive,

    /// <summary>The request never became a conversation. Back out.</summary>
    GiveUpNeverOpened,

    /// <summary>It opened but stayed an empty shell. Back out.</summary>
    GiveUpNoContent,
}

/// <summary>
/// The two deadlines a followed conversation lives under, kept pure so they can be tested
/// without an engine.
///
/// "Requested" and "on screen" are different states with different patience, and they run on
/// DIFFERENT CLOCKS. The request goes to the server, which resolves the participants, walks
/// the player into conversation range and only then opens the conversation — seconds, the
/// same as a click. Once it IS open, an empty shell means something went wrong, and that is
/// the state worth escaping quickly.
///
/// ⚠ The bug this type exists to prevent: the content deadline was measured from the REQUEST,
/// so the open consumed the content budget. A conversation that opened 2.8 s after being
/// asked for got 200 ms to deliver a line before its own watchdog tore it down — an empty
/// dialogue box with no subtitles and no replies, on a conversation that was working. The
/// content clock starts at <c>openedAtMs</c>, never at <c>requestedAtMs</c>.
/// </summary>
public static class DialogueFollowClock
{
    public static DialogueFollowVerdict Evaluate(
        long nowMs,
        long requestedAtMs,
        long? openedAtMs,
        bool isOpen,
        bool hasContent,
        long openTimeoutMs,
        long contentTimeoutMs)
    {
        // Never seen open: the only deadline that applies is the long one, from the request.
        if (openedAtMs is not { } openedAt)
        {
            return nowMs - requestedAtMs > openTimeoutMs
                ? DialogueFollowVerdict.GiveUpNeverOpened
                : DialogueFollowVerdict.Wait;
        }

        if (hasContent)
        {
            return DialogueFollowVerdict.Drive;
        }

        // It opened and has since gone quiet (the engine's active flag drops between
        // entries). That is not an empty shell — there is nothing on screen to escape from,
        // and the follow ends when the OWNER releases the token, not on a guess here.
        if (!isOpen)
        {
            return DialogueFollowVerdict.Wait;
        }

        return nowMs - openedAt > contentTimeoutMs
            ? DialogueFollowVerdict.GiveUpNoContent
            : DialogueFollowVerdict.Wait;
    }
}
