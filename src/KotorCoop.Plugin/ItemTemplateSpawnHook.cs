using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Captures (object id, resref) for every item
/// <c>CSWVirtualMachineCommands::ExecuteCommandCreateItemOnObject</c> (0x0052F570) actually
/// creates — the VM command a KOTOR script's <c>CreateItemOnObject(resref, target)</c> reaches,
/// and the SAME one <c>giveloot</c>/<c>scriptgive</c>/<c>partypooladd</c>/<c>storeinsert</c>
/// already use (<c>Odyssey.GiveItemToInventory</c>). See docs/research/item-identity.md §13 for
/// the full write-up this class's remarks summarise.
///
/// <b>WHY THIS FUNCTION, NOT <c>CSWSItem::LoadFromTemplate</c> ITSELF.</b>
/// <c>LoadFromTemplate</c> (0x005608B0) is the true choke point for every templated item body
/// in the game (24 call sites) and is where docs/research/item-identity.md §12 already proved
/// the resref is read but never written back into the object — so it looked like the obvious
/// hook point. It is not, and re-checking why is the point of this pass:
///
/// <list type="bullet">
/// <item>Fresh disassembly of the CALLER (<c>tools/disasm.py 0x52F570 700</c>) shows the item's
/// OWN <c>CGameObject::Id</c> (item+0x14) is initialised by
/// <c>CSWSItem::Constructor 0x005530A0</c>, whose ONE argument is forwarded, unchanged, all the
/// way down through <c>CSWSObject::Constructor 0x004CFCB0</c> to
/// <c>CGameObject::Constructor 0x004D7D60</c>, which writes it straight into <c>+0x04</c> (id)
/// — confirmed against <c>tools/q.py offsets CGameObject</c>, which lists <c>Id</c> at exactly
/// that offset.</item>
/// <item>At the ONE call site this pass traced in full (0x0052F669: <c>push 0x7f000000</c>;
/// 0x0052F670: <c>call 0x5530a0</c>), that argument is the literal constant
/// <c>OBJECT_INVALID</c> (0x7F000000) — so the item's id is STILL <c>OBJECT_INVALID</c> at
/// 0x0052F689, the very next instruction, which calls <c>LoadFromTemplate</c>. <b>VERIFIED:
/// the object id is not assigned yet inside <c>LoadFromTemplate</c></b> — confirming exactly
/// the risk this round's task named up front.</item>
/// <item>The item is given a REAL id somewhere inside the inventory-placement sequence that
/// runs AFTER <c>LoadFromTemplate</c> returns (the vtable calls at 0x0052F6EC/0x0052F70A/
/// 0x0052F71A on the target creature, matching whichever of
/// merge-into-an-existing-stack/AddItem/AcquireItem the stack logic picks) — this pass did not
/// trace which SPECIFIC virtual call performs the write, because it did not need to: what
/// matters is only that by 0x0052F748 (<c>mov esi, dword ptr [eax + 0x14]</c>, where
/// <c>eax</c> is the very item <c>LoadFromTemplate</c> built) the id is final on every path
/// that reaches it. This is, in fact, the SAME read <c>ExecuteCommandCreateItemOnObject</c>
/// itself uses to hand the created item's id back to the calling script (<c>esi</c> feeds
/// 0x0052F7CA's push into the VM's own result-push call, 0x5d10d0) — so whatever this hook
/// captures is, by construction, exactly what the SCRIPT ITSELF believes it just created,
/// including in the "the new item merged into an existing stack" case, since <c>eax</c> here
/// is unconditionally the item this function's OWN <c>LoadFromTemplate</c> call produced,
/// never whatever it may have merged into.</item>
/// </list>
///
/// So <b>this function is the hook</b>, at the one point inside it where both facts — the
/// resolved resref AND the final object id — are simultaneously available, exactly the "hook
/// the caller that has both" fallback the task named.
///
/// <b>THE SPLICE POINT, VERIFIED.</b> <c>tools/disasm.py 0x52F570 700</c> shows, immediately
/// before the read this hooks:
/// <code>
/// 0x0052F740  8b442440             mov eax, dword ptr [esp + 0x40]   ; the item LoadFromTemplate built
/// 0x0052F744  85c0                 test eax, eax
/// 0x0052F746  747c                 je 0x52f7c4                       ; (never null on this path; kept as an anchor, not overwritten)
/// 0x0052F748  8b7014               mov esi, dword ptr [eax + 0x14]   ; <- OVERWRITTEN: the id, final
/// 0x0052F74B  eb77                 jmp 0x52f7c4                      ; <- OVERWRITTEN: unconditional exit
/// </code>
/// The two overwritten instructions are exactly 5 bytes (3 + 2) — precisely enough for a
/// 5-byte <c>jmp rel32</c>, so no instruction is left half-overwritten and nothing needs
/// padding. The resref this item was built from is STILL on the stack at this point too: the
/// CResRef local <c>LoadFromTemplate</c> was called with (<c>lea ecx, [esp+0x1c]; push ecx</c>
/// at 0x0052F679, then consumed by the callee's own <c>ret 4</c>) lives at the SAME
/// <c>[esp+0x1C]</c> the whole way from there to 0x0052F748 — verified by walking every
/// intervening instruction in the disassembly and confirming none of them writes to
/// <c>[esp+0x1C..0x2B]</c> (the only writes nearby touch <c>[esp+4]</c>, <c>[esp+8]</c>,
/// <c>[esp+0x40]</c>, <c>[esp+0x48]</c> and <c>[esp+0x4C]</c> — all outside that 16-byte span)
/// and that <c>esp</c> itself is provably back at its <c>LoadFromTemplate</c>-call-time value
/// by 0x0052F740 (the SAME literal <c>[esp+0x40]</c> expression is used both there and at
/// 0x0052F696, immediately after <c>LoadFromTemplate</c> returns — the compiler would not
/// reuse that literal offset for the same local across two different net stack depths).
///
/// <b>THIS IS THE FIRST MID-FUNCTION SPLICE IN THIS PLUGIN</b> — every other hook here patches
/// a function's TRUE ENTRY (see <see cref="ContainerUseHook"/>'s remarks on why that matters
/// for the SEH prologue). This site needs none of that care: it sits well past the function's
/// own SEH setup, so the splice only has to relocate the two instructions it replaces, not an
/// SEH-frame-establishing prologue. To compensate for being novel, TWO anchors are verified
/// before patching, not one: <see cref="Anchor"/> (the 8 bytes immediately BEFORE the splice,
/// never touched) AND <see cref="Prologue"/> (the 5 bytes actually overwritten) — a coincidental
/// match on the 5 target bytes alone would be far less reassuring than also matching the
/// leading context that puts them at exactly the point this class's remarks describe.
///
/// No engine calls, no allocation inside the hook itself: it copies at most 20 bytes into a
/// small fixed ring and returns, the same "never let a managed fault unwind into the engine"
/// discipline every hook in this plugin follows.
///
/// ⚠ NEEDS-LIVE (no rig this round — see docs/research/item-identity.md §13's own list): the
/// splice site itself, the id-is-final claim, and the stack-slot-survives claim are all static
/// analysis against the verified GOG v1.0.3 binary, not yet observed running.
/// </summary>
internal static unsafe class ItemTemplateSpawnHook
{
    private const nint HookSite = 0x0052F748;

    /// <summary>The 8 bytes immediately BEFORE the splice point — checked, never overwritten.
    /// <c>mov eax, [esp+0x40]</c> (the item this call's own <c>LoadFromTemplate</c> built);
    /// <c>test eax, eax</c>; <c>je 0x52f7c4</c> (never taken on the path this hook cares about,
    /// since a null item here means <c>LoadFromTemplate</c> already failed and control never
    /// reached this far — see the earlier <c>je 0x52f7b6</c> at 0x0052F690). Verifying this
    /// too, and not just <see cref="Prologue"/>, is the extra insurance the class remarks
    /// describe for this plugin's first mid-function splice.</summary>
    private static ReadOnlySpan<byte> Anchor =>
        [0x8B, 0x44, 0x24, 0x40, 0x85, 0xC0, 0x74, 0x7C];

    /// <summary>The 5 bytes actually overwritten: <c>mov esi, [eax+0x14]</c> (3) then
    /// <c>jmp short 0x52f7c4</c> (2) — VERIFIED, see the class remarks.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x70, 0x14, 0xEB, 0x77];

    /// <summary>Where both overwritten instructions unconditionally sent control — the
    /// function's own cleanup/return tail. Reconstructed in the stub as a near jump, since the
    /// stub itself lives far from the original short jump's reach.</summary>
    private const nint ResumeAt = 0x0052F7C4;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    private const uint ObjectInvalid = 0x7F000000;

    private static bool _installed;

    /// <summary>How many (id, resref) pairs are remembered between agent drains. A script
    /// creating an item directly in inventory is rare compared to a bark or a frame of combat,
    /// so this stays small — matches <see cref="BarkHook.Capacity"/>'s own reasoning, doubled
    /// for headroom since nothing here throttles a burst of several `giveloot`/`storeinsert`
    /// cheats landing inside one frame.</summary>
    public const int Capacity = 16;

    /// <summary>A CResRef is a fixed char[16] — NOT a C string, and not NUL-terminated.</summary>
    public const int ResRefBytes = 16;

    private static readonly uint[] ObjectId = new uint[Capacity];
    private static readonly byte[] ResRef = new byte[Capacity * ResRefBytes];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    /// <summary>Reads one captured entry by slot index (wrapped). A zero <paramref
    /// name="serial"/> means this slot has never been written.</summary>
    public static void Read(int index, out uint objectId, out uint serial, Span<byte> resRef)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        objectId = ObjectId[i];
        serial = Serials[i];

        for (int b = 0; b < ResRefBytes && b < resRef.Length; b++)
        {
            resRef[b] = ResRef[(i * ResRefBytes) + b];
        }
    }

    /// <summary>
    /// Finds the most-recently captured resref for <paramref name="objectId"/> still resident in
    /// this ring, trimmed at the first NUL (a CResRef is a fixed <c>char[16]</c>, not a C string,
    /// but NUL-padded in practice — the same cut-at-first-null discipline this codebase already
    /// uses reading a live Tag). Searched newest-first from the write cursor so a (should-not-
    /// happen, but not impossible after enough churn) id reuse resolves to the most recent spawn.
    ///
    /// Used by <c>Odyssey.PartyPool.cs</c>'s <see cref="PartyPoolStackMatcher"/> call as the
    /// PRIMARY match signal for <c>RemovePartyPoolStack</c> — see that class's own remarks and
    /// docs/research/party-pool-sync.md §13/§14 for why a fresh spawn capture is trusted over the
    /// item's own (occasionally still-settling) live Tag field.
    ///
    /// Null when this ring never captured a spawn for this id: evicted by <see cref="Capacity"/>
    /// more recent spawns, a module change (<see cref="Invalidate"/>) cleared it, or the item
    /// predates this plugin session / was never created through <c>CreateItemOnObject</c> at all
    /// — the caller's own Tag-based fallback is the only signal left in that case.
    /// </summary>
    public static string? TryGetResRef(uint objectId)
    {
        for (int step = 0; step < Capacity; step++)
        {
            int i = (((_write - 1 - step) % Capacity) + Capacity) % Capacity;
            if (Serials[i] == 0 || ObjectId[i] != objectId)
            {
                continue;
            }

            int baseIndex = i * ResRefBytes;
            int length = 0;
            while (length < ResRefBytes && ResRef[baseIndex + length] != 0)
            {
                length++;
            }

            return length == 0 ? null : Encoding.ASCII.GetString(ResRef, baseIndex, length);
        }

        return null;
    }

    /// <summary>A module change reassigns every object id, so a not-yet-drained entry would
    /// name a stranger. Forgets every capture.</summary>
    public static void Invalidate()
    {
        for (int i = 0; i < Capacity; i++)
        {
            Serials[i] = 0;
            ObjectId[i] = 0;
        }

        _write = 0;
        _sequence = 0;
    }

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin: an unrecognised
    /// context means we do nothing rather than corrupt the engine. MAIN THREAD ONLY, once.</summary>
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
                diagnostic = $"anchor mismatch at +{i} ({anchorSite[i]:X2}); not hooking item-template spawns";
                return false;
            }
        }

        byte* site = (byte*)HookSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking item-template spawns";
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

        // Entered by JMP at the exact point the original mov/jmp pair would have run, so esp
        // is still the caller function's own esp and eax is still the item pointer the
        // preceding (untouched) `mov eax,[esp+0x40]` loaded. Compute the resref address FIRST,
        // from THIS esp, before pushad shifts it.
        s[n++] = 0x8D; s[n++] = 0x54; s[n++] = 0x24; s[n++] = 0x1C; // lea edx, [esp+0x1C]

        s[n++] = 0x60; // pushad — eax (item ptr) and edx (resref ptr) survive unchanged

        // cdecl args, right to left: push edx (resref ptr) then eax (item ptr), so the
        // callback's first parameter is the item pointer.
        s[n++] = 0x52; // push edx
        s[n++] = 0x50; // push eax

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, void>)&OnItemTemplateSpawn;
        s[n++] = 0xE8; // call OnItemTemplateSpawn
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // add esp, 8 (cdecl cleanup)
        s[n++] = 0x61;                               // popad — restores eax for the relocated read below

        // The relocated `mov esi, [eax+0x14]` (Prologue's first 3 bytes) — position-independent,
        // moves verbatim. Prologue's own trailing `jmp short` (its last 2 bytes) is NOT copied:
        // its 1-byte displacement is meaningless from the stub's address, so a proper near jump
        // to the same destination is emitted instead, below.
        for (int i = 0; i < Prologue.Length - 2; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt (near, since the stub is far from the original site)
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (HookSite + 5));
        NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"item-template spawns at 0x{HookSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>A pointer plausible enough to read: user-mode, and not a small integer that
    /// happens to be sitting in a register. Cheap, no engine call — runs on the game's thread
    /// inside the dispatch.</summary>
    private static bool Readable(uint p) => p >= 0x00010000 && p < 0x7FFF0000;

    /// <summary>
    /// Runs at the exact point the engine has just settled the created item's final object id
    /// (see the class remarks). Records and returns — no engine calls, no allocation, nothing
    /// that can throw: this is the game's thread mid-dispatch of a VM command.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnItemTemplateSpawn(uint itemPtr, uint resrefPtr)
    {
        try
        {
            if (!Readable(itemPtr) || !Readable(resrefPtr))
            {
                return;
            }

            uint id = *(uint*)(itemPtr + 0x14);

            // OBJECT_INVALID here means the id was never resolved past the constructor's own
            // placeholder (0x7F000000) — the create ultimately failed further down this same
            // function, on a path this hook's own anchor (`je 0x52f7c4`) does not exclude on
            // its own. Nothing to record.
            if (id == 0 || id == ObjectInvalid)
            {
                return;
            }

            int slot = _write;
            byte* r = (byte*)resrefPtr;
            for (int b = 0; b < ResRefBytes; b++)
            {
                ResRef[(slot * ResRefBytes) + b] = r[b];
            }

            ObjectId[slot] = id;

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
