using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Enforces the one-conversation rule at the engine's own front door: while another player
/// holds the session's conversation token, this instance cannot START a dialogue at all —
/// clicking an NPC does nothing, exactly like Baldur's Gate 3's party rule. The token
/// arbiter alone could only ignore the loser's CLAIM; the loser's conversation still ran
/// locally and raced the plot flags. This closes that hole at the single choke point.
///
/// The detour sits on <c>CSWSObject::StartDialog 0x004CF490</c>, the one constructor of a
/// live conversation, reached only from <c>ExecuteCommandBeginConversation</c>
/// (docs/research/dialogue-sync.md — both call sites verified). Refusing is safe by the
/// engine's own contract, settled by disassembly of both callers: they do
/// <c>test eax,eax; je &lt;cleanup&gt;</c>, so returning 0 takes the failure path retail
/// already ships (which clears the pending conversation action via 0x004F34A0). The guard
/// runs BEFORE the function's SEH prologue executes, so the refusal is a plain
/// <c>ret 0xC</c> (three stack args, verified from the epilogue at 0x004CF8DF).
///
/// The spectate controller's own mirrored-conversation open must pass, or a spectator
/// could never follow: <see cref="BypassGrants"/> covers that one open, and only it.
/// </summary>
internal static unsafe class DialogueGateHook
{
    private const nint StartDialog = 0x004CF490;

    /// <summary>Where the relocated prologue resumes.</summary>
    private const nint ResumeAt = 0x004CF49D;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c>, <c>push 0x719D9E</c> (the handler),
    /// <c>mov eax, fs:[0]</c>. Thirteen bytes, none position-dependent (the handler push is
    /// an absolute constant), so they move verbatim.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x68, 0x9E, 0x9D, 0x71, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Master switch, from coop.ini <c>dialogue_exclusive</c> (default on).</summary>
    public static bool Enabled;

    /// <summary>
    /// Renewed per frame from the block: a REMOTE peer currently owns the conversation token
    /// (the agent writes the flag only for peers other than this instance, so an owner is
    /// never gated on its own conversation).
    ///
    /// ⚠ A LEASE, NOT A FLAG — the same argument as
    /// <see cref="DialogueReplyHook.SuppressReplies"/>, and for the same reason. The spectate
    /// controller does not run on every frame: <c>PluginHost</c> returns early above it during
    /// an area-transition flush, during a travel warp, and for the whole of a loading screen.
    /// A plain boolean held its last value across all of those, and a stale <c>true</c> here
    /// refuses every conversation the player tries to start, silently, with the refusal path
    /// looking exactly like an NPC who has nothing to say.
    ///
    /// The gate fails OPEN when the lease runs out, which is the same direction the rest of
    /// this hook already fails in (an unrecognised prologue means no gate; a managed fault
    /// means the start proceeds). A competing conversation that slips through in that window
    /// is bounded and already handled — the spectate controller's yield path closes it the
    /// moment the tags resolve and disagree.
    /// </summary>
    private static volatile int _peerHoldsUntilTick = Environment.TickCount;

    /// <summary>How long one renewal buys. Matches the reply hook's lease so the two levels
    /// this instance asserts on the engine expire together.</summary>
    public const int PeerHoldLeaseMs = DialogueReplyHook.SuppressLeaseMs;

    /// <summary>Renew the gate. Called every frame a peer's conversation is known.</summary>
    public static void HoldForPeer() => _peerHoldsUntilTick = Environment.TickCount + PeerHoldLeaseMs;

    /// <summary>Open the gate now, so the level is live in both directions.</summary>
    public static void ReleaseForPeer() => _peerHoldsUntilTick = Environment.TickCount;

    public static bool PeerHoldsConversation =>
        unchecked(_peerHoldsUntilTick - Environment.TickCount) > 0;

    /// <summary>
    /// Permission for exactly ONE conversation to open while a peer holds the token: the
    /// spectate controller's own follow-open, which must reach the engine precisely because
    /// the peer's token is active. Consumed by the first StartDialog that arrives.
    ///
    /// ⚠ COUNTED, not a time window. This was a boolean held up from the follow request
    /// until the conversation appeared — a second or more, because the server walks the
    /// player into range first — and for that whole window the gate was open to ANY
    /// conversation start. A click on another NPC during setup therefore sailed through and
    /// the engine, which will not run two conversations, answered by dropping the one being
    /// followed: the spectator ended up in no conversation at all. Measured live.
    ///
    /// A grant is one request's worth. If a stray click consumes it first, our follow-open is
    /// refused and retried — which is a far better failure than losing the conversation.
    ///
    /// ⚠ NOTHING GRANTS ONE TODAY, and that is not an oversight — it is the measure of how
    /// completely the self-open was removed. The spectate controller opens no conversation of
    /// its own any more (joining is the player's own click, which the tag test below lets
    /// through on its own merits), so the counter sits at zero for the whole session. The
    /// mechanism is kept rather than deleted because it is the only correct way to let a
    /// specific, self-initiated open past the gate, and the next thing that needs one — a
    /// scripted cutscene join, say — should use it instead of inventing a second door.
    /// It was previously cleared from three places in the controller, all of which were
    /// clearing a counter no code path could raise; those are gone.
    /// </summary>
    public static volatile int BypassGrants;

    /// <summary>Bumped on every refused open, for the controller's on-screen feedback.</summary>
    private static uint _blockedCount;

    public static uint BlockedCount => _blockedCount;

    /// <summary>
    /// Every time the engine reaches <c>StartDialog</c> at all, refused or not.
    ///
    /// ⚠ Diagnostic, and it exists to answer one question that inspection could not: a
    /// JOINER never opens a conversation — not a followed one, not one asked for directly —
    /// while the host does the identical thing to the same NPC and succeeds. Measurement has
    /// already ruled out this gate (it never logged a refusal), the co-op token, party
    /// membership, <c>CanSendDialog</c>, range and a stale <c>gui+0xB4</c>.
    ///
    /// This splits the remaining search in half. StartDialog is reached ONLY from
    /// <c>ExecuteCommandBeginConversation</c>, which is the far end of
    /// message → action queue → <c>RunActions</c> → <c>AIActionDialogObject</c> → the VM. So
    /// if this counter moves on the joiner, the request survives the whole chain and dies in
    /// StartDialog's own gates; if it never moves, the chain is broken upstream and the next
    /// place to look is the dialog branch of <c>HandlePlayerToServerInputMessage</c> at
    /// 0x00525F2E, whose first instruction discards the message when the server could not
    /// resolve the sending player to a creature.
    /// </summary>
    private static uint _startAttempts;

    public static uint StartAttempts => _startAttempts;

    /// <summary>
    /// Whether the MOST RECENT <c>StartDialog</c> call — refused or not, any object — was
    /// forced (docs/research/dialogue-sync.md's 2026-08-23 Item 1). Sourced from
    /// <see cref="DialogueForceFlagHook.WasForced"/> keyed on <c>target</c>'s own object id
    /// (<c>CSWSObject+4</c>), so a conversation this instance's own engine would have run
    /// regardless of distance is distinguishable from an ordinary click. Latched rather than
    /// consumed: <c>DialogueSpectateController</c> reads it once, at the moment it notices a
    /// NEW local conversation, and that read happens within a frame or two of the StartDialog
    /// call that produced it — no other consumer exists.
    /// </summary>
    public static volatile bool LastStartForced;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every patch in the plugin: an unrecognised
    /// prologue means we do nothing rather than corrupt the engine. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)StartDialog;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not gating dialogue";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x60;                                       // pushad
        s[n++] = 0x51;                                       // push ecx — the CSWSObject the
                                                             //  conversation is starting on,
                                                             //  so the gate can tell WHICH
                                                             //  NPC was clicked

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&OnStartDialog;
        s[n++] = 0xE8;                                       // call OnStartDialog
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;         // add esp, 4 — cdecl, we clean up
                                                             //  (before the test: it writes flags)

        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax  (before popad —
                                                             //  popad overwrites eax)
        s[n++] = 0x61;                                       // popad (flags survive)
        s[n++] = 0x75;                                       // jnz blocked
        s[n++] = (byte)(Prologue.Length + 5);                //   skip the prologue + jmp

        for (int i = 0; i < Prologue.Length; i++)            // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp StartDialog+13
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x33; s[n++] = 0xC0;                        // blocked: xor eax, eax
        s[n++] = 0xC2; s[n++] = 0x0C; s[n++] = 0x00;         // ret 0xC — the caller's own
                                                             //  test/je failure path runs

        if (!NativeMethods.VirtualProtect(StartDialog, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (StartDialog + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90;                                  // pad so a mid-prologue resume
        }                                                    //  or disassembler sees no garbage

        NativeMethods.VirtualProtect(StartDialog, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"dialogue gate at 0x{StartDialog:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// The tag of the NPC the peer is talking to, as a pointer into the shared block, or
    /// null. Set by the spectate controller every frame.
    ///
    /// This is what makes JOINING possible: a conversation the peer owns is not refused when
    /// the player deliberately clicks THAT NPC — it is exactly the conversation they are
    /// asking to see — while a click on anyone else is still refused, so the session keeps
    /// one conversation at a time.
    /// </summary>
    public static volatile nint PeerPartnerTag;

    /// <summary>CSWSObject + this = the object's tag (CExoString: char* @0, length @4).</summary>
    private const int ObjTag = 0x18;

    private const int MaxTagBytes = 64;

    /// <summary>Runs at every conversation start, before the engine's own body. Returns
    /// non-zero to refuse. No engine calls, no allocation — a click on an NPC lands here.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStartDialog(nint target)
    {
        try
        {
            _startAttempts++;

            // Recorded regardless of what happens below — a refused start is still worth
            // knowing was forced (diagnostically), and the spectate controller only ever reads
            // this for a start that actually proceeds.
            if (target is > 0x10000 and < 0x7FFF_0000)
            {
                LastStartForced = DialogueForceFlagHook.WasForced(*(int*)(target + 4));
            }

            if (!Enabled || !PeerHoldsConversation)
            {
                return 0;
            }

            if (BypassGrants > 0)
            {
                BypassGrants--; // an open this instance asked for itself
                return 0;
            }

            // The player clicked the very NPC the peer is talking to: that is a request to
            // JOIN, not a competing conversation. Let it through — the spectate controller
            // sees the tags match and adopts it as a follow.
            if (TargetIsPeersPartner(target))
            {
                _joinsAllowed++;
                return 0;
            }

            _blockedCount++;
            return 1;
        }
        catch
        {
            // Never let a managed fault unwind into the engine; fail open.
            return 0;
        }
    }

    private static int _joinsAllowed;

    public static int JoinsAllowed => _joinsAllowed;

    /// <summary>
    /// Whether the object a conversation is starting on carries the same tag as the peer's
    /// partner. Raw reads with hard bounds — this runs on the engine's thread from a click,
    /// where a fault would be the game closing rather than an exception.
    /// </summary>
    private static bool TargetIsPeersPartner(nint target)
    {
        nint expected = PeerPartnerTag;
        if (target is <= 0x10000 or >= 0x7FFF_0000 || expected == 0)
        {
            return false;
        }

        nint chars = *(nint*)(target + ObjTag);
        int length = *(int*)(target + ObjTag + 4);

        if (chars is <= 0x10000 or >= 0x7FFF_0000 || length is <= 0 or > MaxTagBytes)
        {
            return false;
        }

        // The stored length can include the terminating null (the CExoString trap).
        byte* actual = (byte*)chars;
        if (actual[length - 1] == 0)
        {
            length--;
        }

        byte* want = (byte*)expected;
        for (int i = 0; i < length; i++)
        {
            byte w = want[i];
            if (w == 0 || Lower(actual[i]) != Lower(w))
            {
                return false;
            }
        }

        // Both must end together: "carth" must not match "carthx".
        return want[length] == 0;
    }

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
}
