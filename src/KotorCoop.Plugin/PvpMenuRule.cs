namespace KotorCoop.Plugin;

/// <summary>What the duel panel is showing (docs/research/pvp-plan.md, "In-game surface").</summary>
internal enum PvpMenuMode
{
    /// <summary>Nothing: duels are off on this host.</summary>
    Hidden,

    /// <summary>Duels are on and nothing is under way. The panel is closed (a one-line hint) or
    /// open (the list of players here).</summary>
    Idle,

    /// <summary>Someone challenged this player. The prompt is up until it is answered or times out.</summary>
    Incoming,

    /// <summary>This player challenged someone and is waiting for the answer.</summary>
    Outgoing,

    /// <summary>Countdown or fight.</summary>
    Fighting,
}

/// <summary>What a key press asks for. Each one becomes at most one console line.</summary>
internal enum PvpMenuAction
{
    None,
    Challenge,
    Accept,
    Decline,
    Yield,
}

/// <summary>One player the panel can challenge.</summary>
internal readonly record struct PvpMenuEntry(uint PeerId, string Name, bool Busy);

/// <summary>Rising edges of the keys the panel reads, sampled once per frame. <see cref="Digit"/> is
/// 1..9 for a number key pressed this frame, 0 for none.</summary>
internal readonly record struct PvpMenuKeys(
    bool Open, bool Up, bool Down, bool Enter, bool Escape, bool Accept, bool Decline, int Digit)
{
    public bool Any => Open || Up || Down || Enter || Escape || Accept || Decline || Digit != 0;
}

internal readonly record struct PvpMenuDecision(PvpMenuAction Action, int Selection, bool ListOpen, string? Argument);

/// <summary>
/// The decisions behind the in-game duel panel, kept pure so every one of them is tested. The panel
/// is a keyboard surface drawn with the queued-text overlay (the same one the server browser uses),
/// not a clickable GUI button: see docs/research/pvp-plan.md for why. Nothing here ever fires an
/// action without a key edge in <see cref="PvpMenuKeys"/>.
/// </summary>
internal static class PvpMenuRule
{
    /// <summary>The agent's flag word (<c>SharedBlock.OffsetPvpUi</c>): duels are allowed here.</summary>
    public const uint UiAllowed = 1;

    /// <summary>Someone challenged this player and is waiting for an answer.</summary>
    public const uint UiIncoming = 2;

    /// <summary>This player challenged someone and is waiting.</summary>
    public const uint UiOutgoing = 4;

    public const int MaxEntries = 9;

    public static PvpMenuMode Mode(uint uiFlags, uint duelPhase)
    {
        if ((uiFlags & UiAllowed) == 0)
        {
            return PvpMenuMode.Hidden;
        }

        if (duelPhase != 0)
        {
            return PvpMenuMode.Fighting;
        }

        if ((uiFlags & UiIncoming) != 0)
        {
            return PvpMenuMode.Incoming;
        }

        return (uiFlags & UiOutgoing) != 0 ? PvpMenuMode.Outgoing : PvpMenuMode.Idle;
    }

    /// <summary>Whether the one-line "F10" reminder is worth drawing: duels are on, nothing is
    /// going on, the list is closed and there is somebody to challenge.</summary>
    public static bool ShowHint(PvpMenuMode mode, bool listOpen, int entries) =>
        mode == PvpMenuMode.Idle && !listOpen && entries > 0;

    /// <summary>Clamps a highlight into the list; 0 for an empty one.</summary>
    public static int ClampSelection(int selection, int count) =>
        count <= 0 ? 0 : Math.Clamp(selection, 0, count - 1);

    /// <summary>The key press to what happens. <paramref name="entries"/> is the live list
    /// (players in this area), so a name is always read at the moment of the press.</summary>
    public static PvpMenuDecision Decide(
        PvpMenuMode mode, bool listOpen, int selection, IReadOnlyList<PvpMenuEntry> entries, in PvpMenuKeys keys)
    {
        int count = Math.Min(entries.Count, MaxEntries);
        selection = ClampSelection(selection, count);

        switch (mode)
        {
            case PvpMenuMode.Incoming:
                // The prompt takes over from the list; a half-made pick does not survive it.
                if (keys.Accept)
                {
                    return new(PvpMenuAction.Accept, 0, false, null);
                }

                return keys.Decline
                    ? new(PvpMenuAction.Decline, 0, false, null)
                    : new(PvpMenuAction.None, 0, false, null);

            case PvpMenuMode.Fighting:
                // The decline key is the yield key during a fight (the on-screen line says so).
                return keys.Decline
                    ? new(PvpMenuAction.Yield, 0, false, null)
                    : new(PvpMenuAction.None, 0, false, null);

            case PvpMenuMode.Idle:
                break;

            default:
                return new(PvpMenuAction.None, 0, false, null);
        }

        if (!listOpen)
        {
            return keys.Open
                ? new(PvpMenuAction.None, 0, true, null)
                : new(PvpMenuAction.None, selection, false, null);
        }

        if (keys.Open || keys.Escape)
        {
            return new(PvpMenuAction.None, selection, false, null);
        }

        if (keys.Digit is >= 1 and <= MaxEntries)
        {
            // A number key picks that row (it does not challenge: Enter does), so a stray digit
            // can never start anything.
            return keys.Digit <= count
                ? new(PvpMenuAction.None, keys.Digit - 1, true, null)
                : new(PvpMenuAction.None, selection, true, null);
        }

        if (keys.Up)
        {
            return new(PvpMenuAction.None, ClampSelection(selection - 1, count), true, null);
        }

        if (keys.Down)
        {
            return new(PvpMenuAction.None, ClampSelection(selection + 1, count), true, null);
        }

        if (keys.Enter && count > 0)
        {
            return new(PvpMenuAction.Challenge, 0, false, entries[selection].Name);
        }

        return new(PvpMenuAction.None, selection, true, null);
    }

    /// <summary>The console line a decision becomes, or null for none. These are the lines the
    /// console verbs already take.</summary>
    public static string? CommandFor(in PvpMenuDecision decision) => decision.Action switch
    {
        PvpMenuAction.Challenge when !string.IsNullOrWhiteSpace(decision.Argument) => $"duel {decision.Argument}",
        PvpMenuAction.Accept => "accept",
        PvpMenuAction.Decline => "decline",
        PvpMenuAction.Yield => "yield",
        _ => null,
    };

    // ---- text (plain language, pvp-product.md section 2) -----------------------------------------

    public const string HintLine = "F10: challenge a player to a duel";

    public const string ListTitle = "=== DUEL ===";

    public const string ListEmpty = "  Nobody else is in this area. Go to them first.  (Esc closes)";

    public const string ListFooter = "  Up/Down or 1-9 pick   Enter challenge   Esc closes";

    public static string ListRow(int index, bool selected, in PvpMenuEntry entry) =>
        $"{(selected ? "> " : "  ")}{index + 1}. {entry.Name}{(entry.Busy ? "  (busy right now)" : string.Empty)}";

    public static string IncomingLine1(string name) => $"{name} challenges you to a duel.";

    public const string IncomingLine2 = "F11 accept   F12 decline   No answer in 30 seconds counts as no.";

    public static string OutgoingLine(string name) => $"You challenged {name} to a duel. Waiting for an answer.";

    public static string FightingLine(string name) => $"Duel with {name}.   F12 yield (stops it, nothing is lost)";

    /// <summary>The same line for the boss fight, where the player is pinned but has no
    /// opponent among the players (the opponent peer is 0).</summary>
    public const string BossFightLine = "Fight with Cairn Sentinel.   F12 yield (calls it off for everyone, nothing is lost)";

    /// <summary>The name a peer id stands for, "Someone" when they are not in this area.</summary>
    public static string NameOf(uint peerId, IReadOnlyList<PvpMenuEntry> entries)
    {
        foreach (PvpMenuEntry entry in entries)
        {
            if (entry.PeerId == peerId)
            {
                return entry.Name;
            }
        }

        return "Someone";
    }
}
