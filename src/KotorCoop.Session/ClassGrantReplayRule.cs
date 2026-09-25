namespace KotorCoop.Session;

/// <summary>What the receiver does with a peer's class grant this tick.</summary>
public enum ClassGrantReplayPath
{
    /// <summary>Ask the plugin to check whether this player needs the class-choice popup, now.</summary>
    Apply,

    /// <summary>Keep the grant and look again next tick — this instance is mid its own load
    /// (a cross-world engine write is the transition-window poison
    /// docs/research/transition-crashes.md exists to keep out), mid-conversation (the class list
    /// is read by dialogue gates; changing it under a running conversation is exactly the kind
    /// of mid-line write nothing here needs to risk, and the popup itself IS a conversation, so
    /// this also keeps two prompts from ever racing), or mid-combat (2026-09-25: a popup during
    /// a fight is exactly the kind of screen the task's own gating list rules out).</summary>
    Wait,
}

/// <summary>What the receiver does with the plugin's answer to an apply request.</summary>
public enum ClassGrantReplayOutcome
{
    /// <summary>The local character has the class (applied now, or already had it).</summary>
    Done,

    /// <summary>The engine refused (or the plugin never answered): try again after
    /// <see cref="ClassGrantReplayRule.RetryMs"/>.</summary>
    Retry,

    /// <summary>Nothing a retry changes (not a class, or both slots taken) or the attempts
    /// are spent: forget the grant and tell the player.</summary>
    GiveUp,
}

/// <summary>
/// The receiver's decisions for the class-grant relay (docs/research/class-and-power-grants.md),
/// pure so they are testable without an engine. Unlike the galaxy-map departure's follow rule
/// there is no same-module or expiry test: a class is durable world truth, not a moment — a
/// grant that arrives while the receiver is on another planet, or ten minutes late after a
/// reconnect, is exactly as valid, so the only question is whether THIS tick is a safe one to
/// write it in.
/// </summary>
public static class ClassGrantReplayRule
{
    /// <summary>Spacing between apply attempts after the plugin reported the engine refused
    /// (the VM stack was busy with a running script, the player was not resolvable yet).</summary>
    public const int RetryMs = 2_000;

    /// <summary>Attempts before giving up on one grant — 10 × <see cref="RetryMs"/> = 20 s of
    /// refusals is not a transient.</summary>
    public const int MaxAttempts = 10;

    /// <summary>How long to wait for the plugin to ack a request before treating silence as a
    /// refusal (the plugin's controller drops a request across its own module change without
    /// acking — <c>ClassGrantController.Invalidate</c>).</summary>
    public const int AckTimeoutMs = 10_000;

    // The plugin's ClassGrantStatusCode, by value (KotorCoop.Plugin cannot be referenced here).
    public const uint StatusApplied = 1;
    public const uint StatusEngineRefused = 2;
    public const uint StatusBadClass = 3;
    public const uint StatusAlreadyHas = 4;
    public const uint StatusSlotFull = 5;

    /// <summary>The class-choice popup is now showing on the receiver's own screen; the eventual
    /// pick (whichever class the player chooses) arrives later through the ordinary
    /// class-announce channel, not through this request/ack pair. Added 2026-09-25 — see
    /// <c>KotorCoop.Plugin.JediChoicePromptController</c>, the only producer of it.</summary>
    public const uint StatusPrompted = 6;

    /// <summary><paramref name="inCombat"/> added 2026-09-25: the task's own gating list for the
    /// class-choice popup is "not in a conversation, not loading, not in combat" — the first two
    /// were already this rule's job; combat is new.</summary>
    public static ClassGrantReplayPath Decide(string localModule, bool inDialogue, bool localMidTransition, bool inCombat = false)
    {
        if (localModule.Length == 0 || Pumps.ModuleNameGuard.IsFallbackModuleName(localModule) || localMidTransition)
        {
            return ClassGrantReplayPath.Wait;
        }

        return inDialogue || inCombat ? ClassGrantReplayPath.Wait : ClassGrantReplayPath.Apply;
    }

    /// <summary><paramref name="status"/> is the plugin's answer (0 when it never came);
    /// <paramref name="attempts"/> counts the request just answered.</summary>
    public static ClassGrantReplayOutcome Settle(uint status, int attempts)
    {
        switch (status)
        {
            case StatusApplied:
            case StatusAlreadyHas:
            case StatusPrompted:
                return ClassGrantReplayOutcome.Done;

            case StatusBadClass:
            case StatusSlotFull:
                return ClassGrantReplayOutcome.GiveUp;

            default:
                return attempts >= MaxAttempts ? ClassGrantReplayOutcome.GiveUp : ClassGrantReplayOutcome.Retry;
        }
    }
}
