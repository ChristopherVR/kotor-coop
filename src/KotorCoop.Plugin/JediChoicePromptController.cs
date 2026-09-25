namespace KotorCoop.Plugin;

/// <summary>Where <see cref="JediChoicePromptController"/> is in showing the popup.</summary>
internal enum JediChoicePromptState
{
    /// <summary>No popup showing; ready to act on a new request.</summary>
    Idle,

    /// <summary><c>k_coop_jediclass</c> is (or is about to be) on screen; watching for the
    /// player's pick and for the conversation to end.</summary>
    Prompting,
}

/// <summary>
/// The RECEIVING half of the Jedi-choice relay (docs/research/class-and-power-grants.md's
/// 2026-09-25 section): a peer's Dorak-style choice has happened, so this player — if their OWN
/// character has no Jedi class yet — is shown a private popup (<c>k_coop_jediclass.dlg</c>) and
/// picks their OWN class, applied through the SAME <c>Odyssey.AddMultiClass</c> VM-handler call
/// the announcing side uses. This REPLACES the old behaviour of forcing the peer's exact class
/// onto this player (<c>ClassGrantApplyRule</c>/<c>ClassGrantApplyVerdict.Apply</c>, still kept
/// for the local <c>addclass</c> dev verb but no longer reachable from the network relay).
///
/// Reuses <see cref="ClassGrantController"/>'s wire fields (<c>ClassGrantRequestSerial</c> /
/// <c>ClassGrantRequestClass</c> / <c>ClassGrantApplied</c> / <c>ClassGrantStatus</c>) verbatim
/// — no shared-block layout change was needed, only what each side of the request means. The
/// SESSION layer (<c>ClassGrantPump</c>) still gates WHEN it is safe to even ask (module
/// settled, not mid-transition, not in dialogue, not in combat —
/// <c>ClassGrantReplayRule.Decide</c>) before writing a request; this controller only decides
/// what happens once one arrives.
///
/// Three things happen here, in order:
/// <list type="number">
/// <item>A NEW request serial arrives. Read the local class list. Already a Jedi -&gt; ack
/// <see cref="ClassGrantStatusCode.AlreadyHas"/>, nothing shown (never re-prompt).</item>
/// <item>Otherwise call <see cref="Odyssey.StartConversation"/> with
/// <see cref="JediChoiceRule.DialogResRef"/>, targeting the LOCAL PLAYER at itself (a private,
/// self-only conversation — NEEDS-LIVE whether the engine accepts speaker==listener; see the
/// research doc). One-shot bypass of <see cref="DialogueGateHook"/>'s peer-token gate, since a
/// peer finishing their OWN conversation must not block this player's private popup. Ack
/// <see cref="ClassGrantStatusCode.Prompted"/> once the popup is up — this does NOT mean a
/// class was applied yet, only that the player is now being asked.</item>
/// <item>While <see cref="JediChoicePromptState.Prompting"/>, watch
/// <c>Odyssey.GetDialogueNodeIndex()</c> every frame. Landing on one of the dlg's three
/// terminal entries (<see cref="JediChoiceRule.ClassForTerminalEntry"/>) calls
/// <c>Odyssey.AddMultiClass</c> on the LOCAL player with THEIR pick — never a replay, so
/// <see cref="AddMultiClassHook"/> captures it as a genuine local grant and
/// <see cref="ClassGrantController"/> announces it onward exactly like Dorak's own script
/// would, extending the chain to any THIRD player who still lacks a class. The conversation
/// ending (node index back to -1, whether by reaching a dead end with no replies or the player
/// escaping) returns to <see cref="JediChoicePromptState.Idle"/>.</item>
/// </list>
///
/// A request that arrives while already <see cref="JediChoicePromptState.Prompting"/> is
/// coalesced into the popup already showing (acked immediately, not queued) rather than made to
/// wait — see <see cref="OnFrame"/>'s own remarks.
/// </summary>
internal sealed unsafe class JediChoicePromptController(SharedBlockView block)
{
    /// <summary>coop.ini <c>class_grant_sync</c> — same master switch as
    /// <see cref="ClassGrantController"/>; this is the other half of the same feature.</summary>
    private static readonly bool _enabled = PluginConfig.ReadBool("class_grant_sync", true);

    private uint _handledRequestSerial;

    private JediChoicePromptState _state = JediChoicePromptState.Idle;

    private bool _appliedThisPrompt;

    /// <summary>Whether the popup has been OBSERVED active at least once this prompt.
    /// <c>Odyssey.StartConversation</c> only QUEUES the action (the same "a successful call is
    /// not an arrival" lesson docs/research/dialogue-sync.md's node-sync section records live),
    /// so <c>GetDialogueNodeIndex() &lt; 0</c> on the very first frame after a successful queue
    /// means "not open YET", not "already closed" — without this the controller would flip
    /// straight back to <see cref="JediChoicePromptState.Idle"/> before the conversation ever
    /// had a chance to appear.</summary>
    private bool _sawOpen;

    private long _promptStartedAtMs;

    /// <summary>Generous on purpose: this is a self-target conversation (no travel, no range),
    /// so a real open is expected within one or two frames; this only bounds the wait if
    /// something refuses it silently (an unexpected engine-side gate).</summary>
    private const long OpenTimeoutMs = 5_000;

    /// <summary>True while <c>k_coop_jediclass.dlg</c> is (or is about to be) on screen for the
    /// LOCAL player. Read by <see cref="DialogueSpectateController.PublishLocal"/> to exempt
    /// this private, per-player popup from the peer-mirroring machinery — see that call site's
    /// own remarks. Static because the spectate controller has no reference to this one (and
    /// should not need one just to read a single flag); there is exactly one of each controller
    /// per process, like every other <c>PluginHost</c> singleton controller.</summary>
    public static bool IsPromptActive { get; private set; }

    /// <summary>A module change invalidates every request filed for the OLD world (a rejoin
    /// gets a fresh one via <c>CoopHost</c>'s late-join resync, so nothing is lost) and any
    /// popup on screen is gone with the world it was shown in.</summary>
    public void Invalidate()
    {
        _handledRequestSerial = block.ClassGrantRequestSerial;
        SetState(JediChoicePromptState.Idle);
        _appliedThisPrompt = false;
    }

    private void SetState(JediChoicePromptState state)
    {
        _state = state;
        IsPromptActive = state == JediChoicePromptState.Prompting;
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        if (_state == JediChoicePromptState.Prompting)
        {
            WatchPrompt();
            CoalesceNewRequest();
            return;
        }

        uint serial = block.ClassGrantRequestSerial;
        if (serial == 0 || serial == _handledRequestSerial)
        {
            return;
        }

        bool readable = Odyssey.TryReadLocalClassList(out int count, out int class0, out _, out int class1, out _);
        JediChoicePromptVerdict verdict = JediChoiceRule.Decide(readable, count, class0, class1);

        if (verdict == JediChoicePromptVerdict.NotReady)
        {
            return; // no world yet: ask again next frame
        }

        _handledRequestSerial = serial;

        if (verdict == JediChoicePromptVerdict.AlreadyHasClass)
        {
            PluginHost.Log($"jedi-choice: request #{serial} - this character already has a Jedi class ({count}:[{class0},{class1}]); nothing to prompt");
            Ack(serial, ClassGrantStatusCode.AlreadyHas);
            return;
        }

        StartPrompt(serial);
    }

    /// <summary>A request arriving while a popup is already up cannot start a second one (the
    /// engine will not run two conversations) and does not need to: whatever the player picks
    /// resolves THIS player's "needs a class" state regardless of which peer's choice triggered
    /// it. Acking it now, rather than queuing it behind the current prompt, keeps the SESSION
    /// pump from spinning through <see cref="ClassGrantReplayRule.AckTimeoutMs"/> retries for a
    /// request this instance has already, functionally, answered.</summary>
    private void CoalesceNewRequest()
    {
        uint serial = block.ClassGrantRequestSerial;
        if (serial == 0 || serial == _handledRequestSerial)
        {
            return;
        }

        _handledRequestSerial = serial;
        PluginHost.Log($"jedi-choice: request #{serial} coalesced into the prompt already on screen");
        Ack(serial, ClassGrantStatusCode.Prompted);
    }

    private void StartPrompt(uint serial)
    {
        // The engine's own one-conversation-at-a-time gate must not refuse a private popup just
        // because a peer happens to still be mid their OWN conversation — see DialogueGateHook's
        // own remarks on BypassGrants being exactly the extension point for "the next thing that
        // needs one". Checked, not applied unconditionally: an unconsumed grant would let one
        // unrelated later conversation through the gate for free.
        if (DialogueGateHook.PeerHoldsConversation)
        {
            DialogueGateHook.BypassGrants++;
        }

        uint player = Odyssey.GetPlayerObjectId();
        bool started = Odyssey.StartConversation(player, player, JediChoiceRule.DialogResRef, out int failure);
        if (!started)
        {
            PluginHost.Log($"jedi-choice: request #{serial} - could not start the class-choice prompt (failure {failure}); retrying");
            _handledRequestSerial = 0; // not actually handled yet — try again next frame
            return;
        }

        SetState(JediChoicePromptState.Prompting);
        _appliedThisPrompt = false;
        _sawOpen = false;
        _promptStartedAtMs = Environment.TickCount64;
        PluginHost.Log($"jedi-choice: request #{serial} - showing the class-choice prompt ({JediChoiceRule.DialogResRef})");
        Ack(serial, ClassGrantStatusCode.Prompted);
    }

    private void WatchPrompt()
    {
        int node = Odyssey.GetDialogueNodeIndex();

        if (node < 0)
        {
            if (!_sawOpen)
            {
                // Queued, not open yet (StartConversation only queues the action) — wait, don't
                // conclude "closed" from a conversation that never started.
                if (Environment.TickCount64 - _promptStartedAtMs < OpenTimeoutMs)
                {
                    return;
                }

                PluginHost.Log($"jedi-choice: the prompt never opened locally within {OpenTimeoutMs} ms - will retry on the next trigger");
                SetState(JediChoicePromptState.Idle);
                _handledRequestSerial = 0; // the whole request is still owed an answer
                return;
            }

            // Ended — either a terminal entry's empty RepliesList closed it naturally, or the
            // player escaped without picking (they will be asked again the next time a request
            // arrives, exactly like never having answered).
            if (!_appliedThisPrompt)
            {
                PluginHost.Log("jedi-choice: the prompt closed without a pick (escaped?) - will ask again on the next trigger");
            }

            SetState(JediChoicePromptState.Idle);
            return;
        }

        _sawOpen = true;

        if (_appliedThisPrompt)
        {
            return;
        }

        if (JediChoiceRule.ClassForTerminalEntry(node) is not { } picked)
        {
            return; // still on the prompt line itself
        }

        _appliedThisPrompt = true; // one attempt per prompt, whatever the outcome

        uint player = Odyssey.GetPlayerObjectId();
        bool applied = Odyssey.AddMultiClass(player, picked, isReplay: false, out int failure);
        PluginHost.Log(applied
            ? $"jedi-choice: the local player chose class {picked} at the prompt"
            : $"jedi-choice: could not apply the chosen class {picked} - engine refused, failure {failure}");
    }

    private void Ack(uint serial, ClassGrantStatusCode status)
    {
        // Status first, applied last: the agent reads applied, then status, so a torn read can
        // only ever show "not handled yet" — same discipline ClassGrantController's old
        // ApplyRequest used for the same two fields.
        block.ClassGrantStatus = (uint)status;
        block.ClassGrantApplied = serial;
    }
}
