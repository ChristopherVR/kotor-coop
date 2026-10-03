using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Enforces the one-looter-at-a-time rule at the engine's own front door — the DialogueGateHook
/// pattern applied to a container instead of a conversation. While another player holds the
/// session's Container-kind interaction lock on a placeable, this instance's own attempt to
/// open it is refused INSIDE the engine, so the player never sees the loot panel at all, exactly
/// like a blocked conversation.
///
/// The detour sits on <c>CSWSPlaceable::OpenInventory 0x00587420</c>, VERIFIED disassembled this
/// pass (<c>tools/disasm.py 0x587420 350</c> against the sha256-matched <c>tools/kotor1_0_3.db</c>
/// build). <c>tools/xrefs.py 0x00587420</c> shows exactly three callers in the whole binary, all
/// inside <c>CSWSObject::AIActionUseObject 0x0057E8C0</c> (offsets +0x5e3, +0x8cc) and
/// <c>CSWSPlaceable::EventHandler 0x00587BA0</c> (offset +0xa6e, the OnUsed arm the doors'
/// equivalent event handler mirrors) — i.e. every path a click or a queued "use" action takes to
/// open a container's loot panel funnels through this one function, on whichever instance's own
/// local player is doing the using (see docs/research/container-sync.md).
///
/// <b>THE REFUSAL IS THE ENGINE'S OWN NO-OP, NOT A SYNTHESISED ONE.</b> Unlike
/// <see cref="DialogueGateHook"/> (which invents a `xor eax,eax; ret 0xC` because its callers
/// specifically branch on the return value), refusing here needs nothing invented at all: this
/// hook's stub runs at the function's TRUE entry — reached by a `jmp` splice, so the stack is
/// exactly as the caller left it (`[esp+0]` return address, `[esp+4]`/`[esp+8]` the two
/// stack args `ret 8` pops) — and a `ret 8` at that exact point, after the stub's own
/// `pushad`/`popad` have balanced back out, is bit-for-bit what the real function's own
/// "nothing to do" early exits already do to the stack. Nothing is pushed, no SEH frame is
/// touched, no engine call is skipped that mattered: this is equivalent to the caller having
/// invoked a version of <c>OpenInventory</c> that does nothing, which is exactly what "someone
/// else is already looting this" should mean. <c>OpenInventory</c>'s own return value is never
/// read by any of its three callers (none of the three call sites this pass inspected branch on
/// <c>eax</c> afterward, and none of the function's own exits set it deliberately), so leaving
/// <c>eax</c> unspecified on the refusal path is exactly as safe as the engine's own indifference
/// to it already implies.
///
/// A claim with no holder (0) is never refused — the engine's own scripted opens (a script's
/// <c>OpenInventory</c> or an NPC's own inventory access) are never blocked, the same rule
/// <c>DialogueGateHook</c> already applies via <c>PeerHoldsConversation</c>.
/// </summary>
internal static unsafe class ContainerUseHook
{
    private const nint OpenInventory = 0x00587420;

    /// <summary>Where the relocated prologue resumes: right after <c>mov esi,ecx</c>, at the
    /// original <c>mov eax,[esi+0x338]</c>.</summary>
    private const nint ResumeAt = 0x00587439;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue this stub relocates, VERIFIED byte-for-byte against
    /// <c>tools/disasm.py 0x587420 40</c>:
    /// <code>
    /// mov eax, fs:[0]           ; 64 A1 00 00 00 00
    /// push -1                   ; 6A FF
    /// push 0x7204bb             ; 68 BB 04 72 00   (the SEH handler, an absolute constant)
    /// push eax                  ; 50
    /// mov fs:[0], esp           ; 64 89 25 00 00 00 00
    /// push ebx                  ; 53
    /// push esi                  ; 56
    /// mov esi, ecx              ; 8B F1             (esi = the placeable, this call's ecx)
    /// </code>
    /// 25 bytes, none position-dependent (the handler push is an absolute constant, everything
    /// else is register-to-register or fs-relative), so they move verbatim — the identical
    /// technique <see cref="DialogueGateHook"/> and <see cref="MoviePlayHook"/> already use.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
    [
        0x64, 0xA1, 0x00, 0x00, 0x00, 0x00,
        0x6A, 0xFF,
        0x68, 0xBB, 0x04, 0x72, 0x00,
        0x50,
        0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00,
        0x53,
        0x56,
        0x8B, 0xF1,
    ];

    /// <summary>Master switch, from coop.ini <c>container_locks</c> (default on) — mirrors
    /// <c>dialogue_exclusive</c>'s own naming.</summary>
    public static bool Enabled;

    /// <summary>
    /// From coop.ini <c>container_lock_arbitration</c> (default ON — docs/research/container-sync.md),
    /// meaningless while <see cref="Enabled"/> is false. Closes the container-loot race
    /// (docs/roadmap-seamless.md Tier 1 item 4): a fresh click on an object nobody is CURRENTLY
    /// known to hold is DEFERRED — the engine's own open is swallowed exactly like a
    /// peer-held refusal — instead of allowed outright, so <see cref="InteractionLockController"/>
    /// gets a chance to ask the host to arbitrate before either player's panel opens. See
    /// <see cref="AllowNextOpen"/> and <see cref="LatestDeferred"/>.
    /// </summary>
    public static bool ArbitrationEnabled;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin: an unrecognised
    /// prologue means we do nothing rather than corrupt the engine. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)OpenInventory;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not gating container use";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 96, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x60; // pushad
        // Original stack arguments are at +36 and +40 after pushad. Capture the
        // second first, then the first (now +40 after that push), without changing ecx.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28;
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28;
        s[n++] = 0x51; // push ecx — the CSWSPlaceable this call is opening, so the gate can
                        //  tell WHICH container was clicked. Still valid post-pushad: pushad
                        //  only saves copies, it does not clobber the live register.

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, uint, int, int>)&OnOpenInventory;
        s[n++] = 0xE8; // call OnOpenInventory
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x0C; // add esp, 12 — three cdecl args
                                                      //  (before the test: it writes flags)

        s[n++] = 0x85; s[n++] = 0xC0; // test eax, eax  (before popad — popad overwrites eax)
        s[n++] = 0x61;                // popad (flags survive)
        s[n++] = 0x75;                // jnz refuse
        s[n++] = (byte)(Prologue.Length + 5); //   skip the prologue + jmp

        for (int i = 0; i < Prologue.Length; i++) // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp OpenInventory+25
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        // refuse: ret 8 — the caller's own two stack args are popped with the return address,
        // exactly what the real function's "nothing to do" exits already do to the stack (see
        // the class remarks for why this needs no synthesised failure value).
        s[n++] = 0xC2; s[n++] = 0x08; s[n++] = 0x00;

        if (!NativeMethods.VirtualProtect(OpenInventory, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (OpenInventory + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90; // pad so a mid-prologue resume or disassembler sees no garbage
        }

        NativeMethods.VirtualProtect(OpenInventory, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"container-use gate at 0x{OpenInventory:X} (stub 0x{stub:X})";
        return true;
    }

    // ---- the refresh half: which containers a PEER holds right now ----------------------
    //
    // Refreshed once a frame by InteractionLockController from the block's interaction-lock
    // list, exactly like DialogueGateHook.PeerPartnerTag. No cross-thread race to guard against:
    // OnOpenInventory only ever runs on the game's main thread (OpenInventory is itself a
    // MAIN-THREAD-ONLY engine call, same discipline as every other hook in this plugin), and so
    // does the controller's own OnFrame — both are the SAME thread, so a plain array update is
    // enough.

    private const int Capacity = SharedBlock.InteractionLockCapacity;

    private static readonly uint[] _heldIds = new uint[Capacity];
    private static readonly uint[] _heldPeerIds = new uint[Capacity];
    private static int _heldCount;

    /// <summary>Replaces the held-container set for this frame. <paramref name="ids"/> and
    /// <paramref name="peerIds"/> must be the same length; only the first
    /// <see cref="Capacity"/> entries are kept (the wire already caps at that many locks).</summary>
    public static void SetHeldContainers(ReadOnlySpan<uint> ids, ReadOnlySpan<uint> peerIds)
    {
        int n = Math.Min(ids.Length, Capacity);
        for (int i = 0; i < n; i++)
        {
            _heldIds[i] = ids[i];
            _heldPeerIds[i] = peerIds[i];
        }

        _heldCount = n;
    }

    /// <summary>A module change means every object id names a different world; a peer's hold on
    /// an id from the old one must never carry over and refuse something unrelated.</summary>
    public static void Invalidate()
    {
        _heldCount = 0;
        _blockedCount = 0;
        _lastBlockedId = 0;
        _lastBlockedPeerId = 0;
        _lastAttemptId = 0;
        _lastAttemptSerial = 0;
        _sequence = 0;
        _grantedId = 0;
        _lastDeferredId = 0;
        _lastDeferredSerial = 0;
        _deferSequence = 0;
        _deferredOpen.Clear();
    }

    // ---- arbitration deferral (container_lock_arbitration) -------------------------------
    //
    // A fresh click on an object nobody is KNOWN to hold (the loop above found no peer-held
    // entry) is not allowed outright while ArbitrationEnabled: it is captured here instead, so
    // InteractionLockController can ask the host to arbitrate before letting the real open run.
    // Same "OnOpenInventory only ever runs on the main thread" reasoning as the refresh half
    // above — no lock needed, a plain field is enough.

    /// <summary>The single container id allowed to open on its NEXT attempt, regardless of
    /// <see cref="ArbitrationEnabled"/> — set by <see cref="InteractionLockController"/> the
    /// instant a request is granted, and consumed (cleared back to 0) the moment that attempt
    /// is seen, so a grant is good for exactly one re-issued click.</summary>
    private static uint _grantedId;

    /// <summary>Marks <paramref name="id"/> as pre-approved for its next <c>OpenInventory</c>
    /// attempt. Idempotent to call more than once before the click actually lands (a re-issue
    /// that is slow to arrive simply keeps the same id armed).</summary>
    public static void AllowNextOpen(uint id) => _grantedId = id;

    /// <summary>Withdraws a grant that was never consumed — a module change, or the deferral
    /// state machine giving up on a request it does not want honoured after all (there is
    /// currently no such path, but withdrawing is one call and a stale grant silently letting a
    /// LATER, unrelated click on the same numeric id through would be a real, if rare, bug).</summary>
    public static void WithdrawGrant() => _grantedId = 0;

    private static uint _lastDeferredId;
    private static uint _lastDeferredSerial;
    private static uint _deferSequence;
    private static readonly DeferredContainerOpen _deferredOpen = new();

    /// <summary>Resume the exact final open, rather than replaying walking, animations
    /// and OnUsed scripts through UseObject. Resolve by id anew; never retain a native pointer.
    /// The patched entry still checks peer holds and consumes the one-shot grant.</summary>
    public static bool ResumeDeferredOpen(uint id, uint serial)
    {
        if (!_installed || !_deferredOpen.TryTake(id, serial, out uint actor, out int feedback))
            return false;

        nint placeable = Odyssey.ResolvePlaceable(id);
        if (placeable == 0) return false;
        AllowNextOpen(id);
        ((delegate* unmanaged[Thiscall]<nint, uint, int, void>)OpenInventory)(placeable, actor, feedback);
        WithdrawGrant(); // also clear if a newer peer hold refused the call
        return true;
    }

    /// <summary>The most recently DEFERRED open attempt's placeable id, and the serial it was
    /// captured at (0 = nothing captured yet). Same shape as <see cref="LatestAttempt"/> — a
    /// poller compares the serial against its own last seen value.</summary>
    public static uint LatestDeferred(out uint serial)
    {
        serial = _lastDeferredSerial;
        return _lastDeferredId;
    }

    // ---- the capture half: what THIS instance's own attempts looked like ----------------
    //
    // Every ALLOWED entry (never a refused one — nothing opened, so there is nothing to
    // confirm) is recorded here for InteractionLockController to correlate against
    // Odyssey.IsPlaceableOpen once the real function has actually run: an id landing here does
    // not by itself mean the container opened (OpenInventory has its own internal "already
    // open" / "+0x324 not set" no-op branches this hook does not re-implement), only that this
    // instance's own engine reached the point of trying.

    private static uint _lastAttemptId;
    private static uint _lastAttemptSerial;
    private static uint _sequence;

    /// <summary>The most recently ALLOWED open attempt's placeable id, and the serial it was
    /// captured at (0 = nothing captured yet). A poller compares the serial against its own last
    /// seen value to tell "there is something new" without the id alone (id 0 is a valid,
    /// if unusual, object id concern only in theory — the serial is the real edge).</summary>
    public static uint LatestAttempt(out uint serial)
    {
        serial = _lastAttemptSerial;
        return _lastAttemptId;
    }

    // ---- refusal diagnostics, for the on-screen notice -----------------------------------

    private static uint _blockedCount;
    private static uint _lastBlockedId;
    private static uint _lastBlockedPeerId;

    /// <summary>Bumped on every refused open.</summary>
    public static uint BlockedCount => _blockedCount;

    /// <summary>The container id and holder peer id of the most recent refusal, for the
    /// "&lt;name&gt; is looting that" notice.</summary>
    public static (uint ContainerId, uint HolderPeerId) LastBlocked => (_lastBlockedId, _lastBlockedPeerId);

    /// <summary>CSWSPlaceable + this = the object's own id.</summary>
    private const int ObjectIdOffset = 0x04;

    /// <summary>
    /// Runs at every <c>OpenInventory</c> call, refused or not. No engine calls, no allocation
    /// — a click on a container, or a queued use action reaching its target, lands here.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnOpenInventory(nint placeable, uint actor, int feedback)
    {
        try
        {
            if (!Enabled || placeable is <= 0x10000 or >= 0x7FFF_0000)
            {
                return 0;
            }

            uint id = *(uint*)(placeable + ObjectIdOffset);
            if (id == 0)
            {
                return 0;
            }

            for (int i = 0; i < _heldCount; i++)
            {
                if (_heldIds[i] != id)
                {
                    continue;
                }

                // A peer's own KNOWN hold always wins, even over a re-issued click we just
                // granted ourselves — the peer's claim is by definition newer information than
                // whatever this instance decided a few frames ago.
                _blockedCount++;
                _lastBlockedId = id;
                _lastBlockedPeerId = _heldPeerIds[i];
                return 1;
            }

            if (id == _grantedId)
            {
                // The re-issued click after a grant (or arbitration disabled and this id
                // happened to match a stale grant — impossible in practice since Invalidate
                // clears it, but the equality check is what makes this one-shot regardless).
                _grantedId = 0;
                _lastAttemptId = id;
                _lastAttemptSerial = ++_sequence;
                return 0;
            }

            if (ArbitrationEnabled)
            {
                // Neither a known peer hold nor a grant we already issued: swallow the click
                // exactly like a refusal (identical ret-8 stack effect, see the class remarks)
                // and let InteractionLockController ask the host before this goes any further.
                _lastDeferredId = id;
                _lastDeferredSerial = ++_deferSequence; // written last
                _deferredOpen.Capture(id, _lastDeferredSerial, actor, feedback);
                return 1;
            }

            _lastAttemptId = id;
            _lastAttemptSerial = ++_sequence; // written last
            return 0;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's OpenInventory call.
            return 0;
        }
    }
}
