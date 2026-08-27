using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The script-agnostic SECOND trigger for solo-segment detection (docs/research/star-forge-
/// finale.md §11, assessed but not built in §9.5): a detour on
/// <c>CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember 0x00541C00</c> (VM routine 575
/// — VERIFIED, see the class remarks below), the single dispatch point EVERY
/// <c>RemovePartyMember(int nNPC)</c> call in the whole game funnels through, from any script,
/// in any module. <see cref="SoloSegmentController"/> already detects a story-forced solo
/// segment by POLLING the roster count for a drop edge (<c>SoloSegmentMachine.Step</c>); this
/// hook instead observes the ACTUAL engine call directly, immune to the polling-gap false
/// negative §9.5's own "Pros" list names and independent of the roster-count read ever catching
/// up (see <c>SoloSegmentMachine.NoteRemovePartyMemberCall</c>'s own remarks on the race that
/// creates).
///
/// <b>WHY THIS ADDRESS, NOT THE FUNCTION'S OWN ENTRY 0x00541C00.</b> The function's entry point
/// is <c>this</c>-only (thiscall, <c>ecx</c> = the VM command implementer) — the argument
/// (<c>nNPC</c>) has not been read yet; it lives on the VM's OWN parameter stack, not the x86
/// call stack, and is only popped a few instructions in
/// (<c>CVirtualMachine::StackPopInteger 0x005D1000</c>, called at 0x00541C0C). So the splice
/// sits at the first instruction PAST that pop's own success branch — the earliest point where
/// <c>nNPC</c> is actually known. This is this plugin's SECOND mid-function splice (after
/// <see cref="ItemTemplateSpawnHook"/>, whose own remarks explain why that shape needs no SEH
/// prologue care: this site is well past the function's SEH setup for the same reason).
///
/// <b>THE SPLICE POINT, VERIFIED live against the disassembled GOG v1.0.3 binary</b>
/// (<c>tools/disasm.py 0x541C00 60</c>):
/// <code>
/// 0x00541C00  51                   push ecx
/// 0x00541C01  8b0d003a7a00         mov ecx, dword ptr [0x7a3a00]      ; ecx = VM
/// 0x00541C07  8d442400             lea eax, [esp]                     ; eax = &amp;local (the
///                                                                      ;  slot push ecx just made)
/// 0x00541C0B  50                   push eax
/// 0x00541C0C  e8eff30800           call 0x5d1000                      ; StackPopInteger(vm, &amp;local)
/// 0x00541C11  85c0                 test eax, eax
/// 0x00541C13  7509                 jne 0x541c1e                       ; success -&gt; HookSite
/// 0x00541C15  b82ff8ffff           mov eax, 0xfffff82f                ; (failure path, untouched)
/// 0x00541C1A  59                   pop ecx
/// 0x00541C1B  c20800               ret 8
/// 0x00541C1E  8b0dfc397a00         mov ecx, dword ptr [0x7a39fc]      ; &lt;- OVERWRITTEN (HookSite)
/// 0x00541C24  53                   push ebx                           ; &lt;- ResumeAt
/// </code>
/// <c>StackPopInteger</c> is <c>__thiscall(CVirtualMachine* vm, int* out)</c> — one stack
/// argument, cleaned by the callee (<c>ret 4</c>), so by the time it returns <c>esp</c> is back
/// to exactly where it was right after <c>push ecx</c> at entry: the local slot
/// <c>lea eax,[esp]</c> pointed at, which now holds the popped <c>nNPC</c>. Nothing between
/// there and <see cref="HookSite"/> (<c>test</c>/<c>jne</c>) touches that memory or <c>esp</c>,
/// so at <see cref="HookSite"/> itself, <c>[esp] == nNPC</c> — read directly by the stub below,
/// before anything (including <c>pushad</c>) can disturb it.
///
/// <b>TWO ANCHORS</b>, the same double-check <see cref="ItemTemplateSpawnHook"/> uses for its
/// own first mid-function splice: <see cref="Anchor"/> (4 bytes immediately BEFORE the splice,
/// the failure path's own <c>pop ecx; ret 8</c> tail — never touched) and <see cref="Prologue"/>
/// (the 6 bytes actually overwritten). A coincidental match on six bytes alone would be far less
/// reassuring than also matching the four leading bytes that place them at exactly this
/// function's own success-branch target.
///
/// <b>ROUTINE 575, CROSS-CHECKED TWICE.</b> docs/research/star-forge-finale.md §1's own
/// <c>InitializeCommands</c> table scan already named
/// <c>575 = RemovePartyMember = 0x00541C00</c> (cross-checked against an independent claim in
/// minigames.md). Re-confirmed fresh this pass: <c>tools/disasm.py 0x54C960 60000</c> shows
/// <c>mov dword ptr [edx+0x8fc], 0x541c00</c> at 0x0054E3C6 (0x8FC / 4 = 575 — the routine
/// table's own store), and <c>tools/q.py func RemovePartyMember</c> names 0x00541C00 itself
/// <c>CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember [__thiscall]</c>. This hook
/// does not depend on the routine NUMBER at all (it patches the handler's own machine code
/// directly, the way every hook in this plugin does) — the cross-check is only evidence this is
/// truly the one function every <c>RemovePartyMember</c> call reaches, matching the class's own
/// "every call in the whole game funnels through this one function" claim.
///
/// <b>PLUGIN-ONLY RING, DELIBERATELY NOT A <see cref="SharedBlock"/> CHANNEL</b> — unlike
/// <see cref="ItemTemplateSpawnHook"/>, nothing on the agent side ever needs one of these
/// captures: <see cref="SoloSegmentController"/> (already plugin-side, already holds a
/// <c>SharedBlockView</c> for its own <c>LocalInSoloSegment</c> publish) is the only consumer,
/// draining this ring in-process every frame and feeding
/// <c>SoloSegmentMachine.NoteRemovePartyMemberCall</c> directly. No wire format, no version
/// bump, no agent-side field.
///
/// No engine calls, no allocation inside the hook itself — the same "never let a managed fault
/// unwind into the engine" discipline every hook in this plugin follows.
///
/// ⚠ NEEDS-LIVE (no rig this pass — see docs/research/star-forge-finale.md §11's own list): the
/// splice site, the <c>[esp] == nNPC</c> claim and the routine-table cross-check are all static
/// analysis against the verified GOG v1.0.3 binary, not yet observed running. The `removeparty`
/// dev verb (<c>CheatController.RemoveParty</c>) exists specifically so a live pass can exercise
/// this hook without needing to reach the Bastila dialogue's own entry 70 (§9.4's own recipe).
/// </summary>
internal static unsafe class RemovePartyMemberHook
{
    /// <summary>The first instruction past <c>StackPopInteger</c>'s own success branch — see the
    /// class remarks for the full disassembly this is read from.</summary>
    private const nint HookSite = 0x00541C1E;

    /// <summary>The 4 bytes immediately BEFORE the splice point — checked, never overwritten.
    /// The failure path's own <c>pop ecx</c> (1) then <c>ret 8</c> (3) tail, which happens to
    /// sit right before the success branch's own target in the function's linear layout.</summary>
    private static ReadOnlySpan<byte> Anchor => [0x59, 0xC2, 0x08, 0x00];

    /// <summary>The 6 bytes actually overwritten: <c>mov ecx, dword ptr [0x7a39fc]</c> (the
    /// <c>AppManagerPtr</c> global read — the SAME constant <c>Odyssey.Core.cs</c> already
    /// declares under that name) — VERIFIED, see the class remarks.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x0D, 0xFC, 0x39, 0x7A, 0x00];

    /// <summary>Where the overwritten instruction's own next instruction starts.</summary>
    private const nint ResumeAt = 0x00541C24;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    private static bool _installed;

    /// <summary>How many (slot, tick) captures are remembered between
    /// <see cref="SoloSegmentController"/> drains. A real wipe fires exactly nine calls back to
    /// back (docs/research/star-forge-finale.md §9.2 step 4); doubled for headroom the same way
    /// <see cref="ItemTemplateSpawnHook.Capacity"/> doubles <c>BarkHook.Capacity</c>'s own
    /// reasoning, so one undrained frame never loses a call from a real wipe.</summary>
    public const int Capacity = 18;

    private static readonly int[] Slot = new int[Capacity];
    private static readonly int[] TickMs = new int[Capacity];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    /// <summary>Reads one captured entry by slot index (wrapped). A zero <paramref
    /// name="serial"/> means this slot has never been written.</summary>
    public static void Read(int index, out int npcSlot, out int tickMs, out uint serial)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        npcSlot = Slot[i];
        tickMs = TickMs[i];
        serial = Serials[i];
    }

    /// <summary>Module change or session teardown: a capture from the module just left is not
    /// evidence toward a burst in the new one.</summary>
    public static void Invalidate()
    {
        Array.Clear(Serials);
        _write = 0;
        _sequence = 0;
    }

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* anchorSite = (byte*)(HookSite - Anchor.Length);
        for (int i = 0; i < Anchor.Length; i++)
        {
            if (anchorSite[i] != Anchor[i])
            {
                diagnostic = $"anchor mismatch at +{i} ({anchorSite[i]:X2}); not hooking RemovePartyMember";
                return false;
            }
        }

        byte* site = (byte*)HookSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking RemovePartyMember";
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

        // Entered by JMP at the exact point the original `mov ecx,[...]` would have run, so esp
        // still holds nNPC at [esp] (see the class remarks). Read it FIRST, before pushad.
        s[n++] = 0x8B; s[n++] = 0x04; s[n++] = 0x24; // mov eax, [esp]

        s[n++] = 0x60; // pushad — pushad SAVES every register (including the eax we just
                        //  loaded) without changing any of their values, so eax still holds
                        //  nNPC after popad below, exactly as it did before pushad ran.

        s[n++] = 0x50; // push eax — cdecl arg: nNPC

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, void>)&OnRemovePartyMember;
        s[n++] = 0xE8; // call OnRemovePartyMember
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04; // add esp, 4 (cdecl cleanup)
        s[n++] = 0x61;                               // popad — restores every register to
                                                       //  exactly what it held before pushad

        // The relocated `mov ecx, [0x7a39fc]` (Prologue, verbatim — reads a fixed global, does
        // not depend on anything pushad/popad could have disturbed).
        for (int i = 0; i < Prologue.Length; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt (near, the stub is far from the original site)
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (HookSite + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90; // pad the one leftover byte (Prologue is 6, the jmp only 5) so a
        }                    //  mid-prologue resume or disassembler sees no garbage — the same
                             //  idiom DialogueGateHook already uses.

        NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"RemovePartyMember calls at 0x{HookSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs at the exact point the engine has just popped a real <c>RemovePartyMember(nNPC)</c>
    /// call's own argument off the VM stack (see the class remarks). Records and returns — no
    /// engine calls, no allocation: this is the game's thread mid-dispatch of a VM command.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRemovePartyMember(int nNpc)
    {
        try
        {
            int slot = _write;
            Slot[slot] = nNpc;
            TickMs[slot] = Environment.TickCount;

            // Serial last: the reader trusts a slot only once this says it is complete.
            Serials[slot] = ++_sequence;

            _write = (slot + 1) % Capacity;
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}
