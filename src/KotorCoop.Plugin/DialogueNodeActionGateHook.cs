using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// On a client, suppresses the two exact Leviathan transform node action scripts in
/// <c>lev_m40aa</c>. The host remains the sole executor of the actual rescue: on the follower,
/// running either script would first move its own PC to the rescuer's waypoint and then switch
/// the PC into a companion. The whole VM script is skipped so its following native side effects
/// cannot be redirected to the wrong local character.
///
/// The one splice is shared with <see cref="DelayEventProbeHook"/>'s existing observer at
/// <c>CSWSDialog::RunScript 0x0059ED70</c>; no second hook is installed at that address. The
/// verified GOG 1.0.3 site is 0x0059EDC1, after OBJECT_SELF and the local CExoString are ready,
/// immediately before VM-call setup. The skip path resumes at 0x0059EDD4; it bypasses both the
/// call and its three preceding argument pushes, preserving the caller's stack before cleanup.
/// It does not write the party-table controlled-NPC field or call the engine's switch routine.
/// </summary>
internal static unsafe class DialogueNodeActionGateHook
{
    internal delegate void DialogueScriptObserver(
        ReadOnlySpan<byte> module,
        ReadOnlySpan<byte> scriptResref,
        uint objectSelf,
        bool isClient,
        uint sequence);

    private const nint HookSite = 0x0059EDC1;
    private const nint ResumeRunScript = 0x0059EDC7;
    private const nint ResumeAfterSkippedScript = 0x0059EDD4;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    private static ReadOnlySpan<byte> Anchor => [0x1C, 0x8B, 0x41, 0x04];
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x0D, 0x00, 0x3A, 0x7A, 0x00];
    private static ReadOnlySpan<byte> SkippedVmCall =>
        [0x6A, 0x01, 0x50, 0x8D, 0x54, 0x24, 0x0C, 0x52, 0xE8, 0xEC, 0x21, 0x03, 0x00];

    private static SharedBlockView? _view;
    private static bool _enabled;
    private static bool _installed;
    private static uint _dispatchSequence;

    internal static DialogueScriptObserver? Observer { get; set; }

    public static bool Install(SharedBlockView view, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _enabled = PluginConfig.ReadBool("leviathan_action_script_gate", true);

        if (_installed)
        {
            diagnostic = _enabled ? "already installed; client Leviathan action gate enabled" : "already installed; client Leviathan action gate disabled by coop.ini";
            return true;
        }

        byte* anchorSite = (byte*)(HookSite - Anchor.Length);
        for (int i = 0; i < Anchor.Length; i++)
        {
            if (anchorSite[i] != Anchor[i])
            {
                diagnostic = $"anchor mismatch at +{i} ({anchorSite[i]:X2}); not gating Leviathan action scripts";
                return false;
            }
        }

        byte* site = (byte*)HookSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not gating Leviathan action scripts";
                return false;
            }
        }

        // The skip path jumps over the call setup as well as the call itself. Verify that
        // exact continuation block, including the relative call target, before relying on its
        // stack/return contract or patching the earlier instruction.
        byte* skipped = site + Prologue.Length;
        for (int i = 0; i < SkippedVmCall.Length; i++)
        {
            if (skipped[i] != SkippedVmCall[i])
            {
                diagnostic = $"VM-call continuation mismatch at +{i} ({skipped[i]:X2}); not gating Leviathan action scripts";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 96, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed; Leviathan action scripts remain ungated";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        s[n++] = 0x60; // pushad
        // Before pushad, eax is OBJECT_SELF and [esp+4] points at the caller's CExoString.
        // After pushad, the id is [esp+0x1C], and the CExoString address is esp+0x24.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x1C; // push [esp+0x1C]
        s[n++] = 0x8D; s[n++] = 0x44; s[n++] = 0x24; s[n++] = 0x28; // lea eax,[esp+0x28]
        s[n++] = 0x50; // push eax: CExoString* first cdecl argument

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, int>)&ShouldSkipScript;
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;
        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // add esp,8
        s[n++] = 0x85; s[n++] = 0xC0; // test eax,eax; preserve flags through popad
        s[n++] = 0x61; // popad
        s[n++] = 0x0F; s[n++] = 0x85; // jne skip VM call
        int skipDisplacementAt = n;
        n += 4;

        for (int i = 0; i < Prologue.Length; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeRunScript
        *(int*)(s + n) = (int)(ResumeRunScript - (stub + n + 4));
        n += 4;

        int skipPath = n;
        // The splice is before the three original push instructions. Skipping to 0x59EDD4
        // therefore also skips those pushes, while an executed RunScript would have had its
        // callee clean them. Net ESP is unchanged in either case. The caller does not consume
        // EAX from the void RunScript call.
        s[n++] = 0xE9; // jmp caller cleanup
        *(int*)(s + n) = (int)(ResumeAfterSkippedScript - (stub + n + 4));
        n += 4;
        *(int*)(s + skipDisplacementAt) = (int)(stub + skipPath - (stub + skipDisplacementAt + 4));

        if (!NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed; Leviathan action scripts remain ungated";
            return false;
        }

        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (HookSite + 5));
        site[5] = 0x90;
        if (old != PageExecuteReadWrite)
        {
            NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, old, out _);
        }

        FlushInstructionCache((nint)(-1), HookSite, (nuint)Prologue.Length);
        _installed = true;
        diagnostic = _enabled
            ? "client-only gate installed on k_plev_transform/k_plev_transfrm1 in lev_m40aa"
            : "shared dialogue-script hook installed; Leviathan gate disabled by coop.ini";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShouldSkipScript(uint exoString, uint objectId)
    {
        try
        {
            if (exoString is <= 0x10000 or >= 0x7FFF0000
                || !NativeMethods.IsReadable((nint)exoString, sizeof(uint)))
            {
                return 0;
            }

            byte* module = stackalloc byte[SharedBlock.ModuleNameLength];
            int moduleLength = Odyssey.ReadCurrentModuleName(module, SharedBlock.ModuleNameLength);
            if (moduleLength <= 0 || moduleLength >= SharedBlock.ModuleNameLength)
            {
                return 0;
            }

            uint chars = *(uint*)exoString;
            if (chars is <= 0x10000 or >= 0x7FFF0000)
            {
                return 0;
            }

            byte* name = stackalloc byte[32];
            int length = 0;
            for (; length < 31; length++)
            {
                if (!NativeMethods.IsReadable((nint)(chars + (uint)length), 1))
                {
                    return 0;
                }

                byte value = *((byte*)chars + length);
                if (value == 0)
                {
                    break;
                }

                name[length] = value;
            }

            if (length == 31)
            {
                return 0; // resref wasn't terminated inside the fixed buffer; fail open
            }

            DelayEventProbeHook.ObserveDialogueScript(exoString, objectId);
            SharedBlockView? view = _view;
            bool isClient = view is not null && view.TryGetHostPartyRoster(out _, out _);
            uint sequence = unchecked(++_dispatchSequence);
            Observer?.Invoke(
                new ReadOnlySpan<byte>(module, moduleLength),
                new ReadOnlySpan<byte>(name, length),
                objectId,
                isClient,
                sequence);

            if (!_enabled)
            {
                return 0;
            }

            if (!isClient)
            {
                return 0; // no positive client-role proof; fail open
            }

            return DialogueNodeActionGateRule.ShouldSuppress(
                isClient: true,
                new ReadOnlySpan<byte>(module, moduleLength),
                new ReadOnlySpan<byte>(name, length)) ? 1 : 0;
        }
        catch
        {
            return 0; // never let a managed error change the engine's own dialogue behavior
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
