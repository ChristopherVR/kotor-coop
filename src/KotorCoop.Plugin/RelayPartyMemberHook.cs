namespace KotorCoop.Plugin;

/// <summary>
/// Lets <c>IsObjectPartyMember</c> answer TRUE for a peer's body while the host runs a trigger
/// that peer walked into (<see cref="OdysseyTriggers.FireTrigger"/>), and only then.
///
/// A relayed OnEnter runs on the host with the peer's phantom as the entering object. Story
/// triggers commonly gate on <c>IsObjectPartyMember(GetEnteringObject())</c> — the Endar Spire's
/// <c>k_pend_reinfrc06</c> spawns its Sith reinforcements behind exactly that check — and the
/// phantom is not in the host's party, so the host spawned nothing while the joiner's own engine,
/// running the same trigger for its real character, spawned them in its world alone (live
/// 2026-10-08). In co-op the other player IS a party member; this makes the script agree, for
/// the length of the one call the relay makes.
///
/// <code>
///   ExecuteCommandIsObjectPartyMember 0x0053F160
///   0x0053F18F  call GetCreatureByGameObjectID     ; eax = creature or 0
///   0x0053F194  test eax, eax
///   0x0053F196  je   0x0053F19E                    ; no creature: answer 0
///   0x0053F198  mov  esi, [eax + 0xA88]            ; &lt;- patched: the creature's party flag
///   0x0053F19E  mov  ecx, [0x7A3A00]               ; ecx reloaded here, so the stub may use it
/// </code>
///
/// The stub keeps the original read, and only when it reads 0 and the creature's id equals the
/// override cell does it answer 1. No managed code runs inside the engine call.
/// </summary>
internal static unsafe class RelayPartyMemberHook
{
    private const nint Site = 0x0053F198;
    private const nint Resume = 0x0053F19E;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov esi, [eax + 0xA88]</c>: one whole instruction, six bytes.</summary>
    private static ReadOnlySpan<byte> Original => [0x8B, 0xB0, 0x88, 0x0A, 0x00, 0x00];

    private static uint* _overrideId;
    private static bool _installed;

    public static bool Installed => _installed;

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)Site;
        for (int i = 0; i < Original.Length; i++)
        {
            if (site[i] != Original[i])
            {
                diagnostic = $"bytes mismatch at +{i} ({site[i]:X2}); relayed triggers keep the phantom out of the party";
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
        uint* cell = (uint*)(stub + 56);
        *cell = 0;
        int n = 0;

        for (int i = 0; i < Original.Length; i++)            // mov esi, [eax + 0xA88]
        {
            s[n++] = Original[i];
        }

        s[n++] = 0x85; s[n++] = 0xF6;                        // test esi, esi
        s[n++] = 0x75; int toBack1 = n++;                    // jnz back
        s[n++] = 0x8B; s[n++] = 0x0D;                        // mov ecx, [cell]
        *(uint*)(s + n) = (uint)(nint)cell; n += 4;
        s[n++] = 0x85; s[n++] = 0xC9;                        // test ecx, ecx
        s[n++] = 0x74; int toBack2 = n++;                    // jz back
        s[n++] = 0x3B; s[n++] = 0x48; s[n++] = 0x04;         // cmp ecx, [eax + 4]  (object id)
        s[n++] = 0x75; int toBack3 = n++;                    // jne back
        s[n++] = 0xBE; *(int*)(s + n) = 1; n += 4;           // mov esi, 1
        int back = n;
        s[n++] = 0xE9;                                       // back: jmp 0x0053F19E
        *(int*)(s + n) = (int)(Resume - (stub + n + 4));
        n += 4;

        s[toBack1] = (byte)(back - (toBack1 + 1));
        s[toBack2] = (byte)(back - (toBack2 + 1));
        s[toBack3] = (byte)(back - (toBack3 + 1));

        if (!NativeMethods.VirtualProtect(Site, (nuint)Original.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (Site + 5));
        site[5] = 0x90;
        NativeMethods.VirtualProtect(Site, (nuint)Original.Length, old, out _);

        _overrideId = cell;
        _installed = true;
        diagnostic = $"relayed-trigger party membership at 0x{Site:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>Counts <paramref name="objectId"/> as a party member until the returned scope is
    /// disposed. MAIN THREAD ONLY; scopes do not nest (the relay fires one trigger at a time).</summary>
    public static Scope CountAsPartyMember(uint objectId)
    {
        if (_overrideId != null)
        {
            *_overrideId = objectId;
        }

        return default;
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            if (_overrideId != null)
            {
                *_overrideId = 0;
            }
        }
    }
}
