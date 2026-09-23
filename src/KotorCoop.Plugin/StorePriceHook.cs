using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Applies the agent's resolved price percent after KOTOR computes each store's retail buy or
/// sell value. The two getter entry points are common to row text, affordability/persuasion
/// checks, transaction handlers and this mod's own store verbs. The retail function is called
/// exactly once through a trampoline; its integer result is adjusted once and returned to the
/// original caller. No store fields or saved world state are changed.
/// </summary>
internal static unsafe class StorePriceHook
{
    private const nint BuyGetter = 0x006C0790;
    private const nint SellGetter = 0x006C07F0;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;

    private static ReadOnlySpan<byte> Prologue => StorePriceHookStubBuilder.GetterPrologue;

    private static bool _installed;
    private static nint _buyOriginal;
    private static nint _sellOriginal;
    private static SharedBlockView? _block;

    public static bool IsInstalled => _installed;

    /// <summary>Verify both full prologues before patching either getter. MAIN THREAD ONLY.</summary>
    public static bool Install(SharedBlockView block, out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        if (!new ReadOnlySpan<byte>((void*)BuyGetter, Prologue.Length).SequenceEqual(Prologue))
        {
            diagnostic = $"buy getter prologue mismatch; expected GOG getter entry at 0x{BuyGetter:X}, leaving retail prices";
            return false;
        }

        if (!new ReadOnlySpan<byte>((void*)SellGetter, Prologue.Length).SequenceEqual(Prologue))
        {
            diagnostic = $"sell getter prologue mismatch; expected GOG getter entry at 0x{SellGetter:X}, leaving retail prices";
            return false;
        }

        nint buyTrampoline = AllocateStub(StorePriceHookStubBuilder.TrampolineLength);
        nint sellTrampoline = AllocateStub(StorePriceHookStubBuilder.TrampolineLength);
        nint buyStub = AllocateStub(StorePriceHookStubBuilder.DetourLength);
        nint sellStub = AllocateStub(StorePriceHookStubBuilder.DetourLength);
        if (buyTrampoline == 0 || sellTrampoline == 0 || buyStub == 0 || sellStub == 0)
        {
            FreeIfAllocated(buyTrampoline);
            FreeIfAllocated(sellTrampoline);
            FreeIfAllocated(buyStub);
            FreeIfAllocated(sellStub);
            diagnostic = "VirtualAlloc failed; leaving retail prices";
            return false;
        }

        nint buyCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnBuyPrice;
        nint sellCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnSellPrice;
        if (StorePriceHookStubBuilder.WriteTrampoline(
                new Span<byte>((void*)buyTrampoline, StorePriceHookStubBuilder.TrampolineLength),
                new ReadOnlySpan<byte>((void*)BuyGetter, Prologue.Length), unchecked((uint)BuyGetter), unchecked((uint)buyTrampoline)) == 0
            || StorePriceHookStubBuilder.WriteTrampoline(
                new Span<byte>((void*)sellTrampoline, StorePriceHookStubBuilder.TrampolineLength),
                new ReadOnlySpan<byte>((void*)SellGetter, Prologue.Length), unchecked((uint)SellGetter), unchecked((uint)sellTrampoline)) == 0
            || StorePriceHookStubBuilder.WriteDetour(
                new Span<byte>((void*)buyStub, StorePriceHookStubBuilder.DetourLength), unchecked((uint)buyCallback), unchecked((uint)buyStub)) == 0
            || StorePriceHookStubBuilder.WriteDetour(
                new Span<byte>((void*)sellStub, StorePriceHookStubBuilder.DetourLength), unchecked((uint)sellCallback), unchecked((uint)sellStub)) == 0)
        {
            FreeIfAllocated(buyTrampoline);
            FreeIfAllocated(sellTrampoline);
            FreeIfAllocated(buyStub);
            FreeIfAllocated(sellStub);
            diagnostic = "could not build getter trampolines; leaving retail prices";
            return false;
        }

        // Prebuild both entries before changing executable protection; the writes after that
        // point are bounded copies and cannot leave only one getter installed.
        Span<byte> buyEntry = stackalloc byte[Prologue.Length];
        Span<byte> sellEntry = stackalloc byte[Prologue.Length];
        bool entriesReady = StorePriceHookStubBuilder.WriteEntryJump(
                buyEntry, unchecked((uint)buyStub), unchecked((uint)BuyGetter))
            && StorePriceHookStubBuilder.WriteEntryJump(
                sellEntry, unchecked((uint)sellStub), unchecked((uint)SellGetter));
        if (!entriesReady)
        {
            FreeIfAllocated(buyTrampoline);
            FreeIfAllocated(sellTrampoline);
            FreeIfAllocated(buyStub);
            FreeIfAllocated(sellStub);
            diagnostic = "could not build getter entry jumps; leaving retail prices";
            return false;
        }

        NativeMethods.FlushInstructionCache((nint)(-1), buyTrampoline, (nuint)StorePriceHookStubBuilder.TrampolineLength);
        NativeMethods.FlushInstructionCache((nint)(-1), sellTrampoline, (nuint)StorePriceHookStubBuilder.TrampolineLength);
        NativeMethods.FlushInstructionCache((nint)(-1), buyStub, (nuint)StorePriceHookStubBuilder.DetourLength);
        NativeMethods.FlushInstructionCache((nint)(-1), sellStub, (nuint)StorePriceHookStubBuilder.DetourLength);

        // The two verified sites sit on the same page. Change protection once, after all
        // allocations and code generation succeeded, then install both entry jumps together.
        nuint patchSpan = (nuint)(SellGetter + Prologue.Length - BuyGetter);
        if (!NativeMethods.VirtualProtect(BuyGetter, patchSpan, PageExecuteReadWrite, out uint oldProtect))
        {
            FreeIfAllocated(buyTrampoline);
            FreeIfAllocated(sellTrampoline);
            FreeIfAllocated(buyStub);
            FreeIfAllocated(sellStub);
            diagnostic = "VirtualProtect failed; leaving retail prices";
            return false;
        }

        _block = block;
        _buyOriginal = buyTrampoline;
        _sellOriginal = sellTrampoline;

        buyEntry.CopyTo(new Span<byte>((void*)BuyGetter, Prologue.Length));
        sellEntry.CopyTo(new Span<byte>((void*)SellGetter, Prologue.Length));
        NativeMethods.FlushInstructionCache((nint)(-1), BuyGetter, patchSpan);

        _ = NativeMethods.VirtualProtect(BuyGetter, patchSpan, oldProtect, out _);

        _installed = true;
        diagnostic = $"buy/sell price getters hooked at 0x{BuyGetter:X}/0x{SellGetter:X}";
        return true;
    }

    private static nint AllocateStub(int length) =>
        NativeMethods.VirtualAlloc(0, (nuint)length, MemCommitReserve, PageExecuteReadWrite);

    private static void FreeIfAllocated(nint address)
    {
        if (address != 0)
        {
            _ = NativeMethods.VirtualFree(address, 0, MemRelease);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBuyPrice(nint panel, nint item) => GetScaledPrice(_buyOriginal, panel, item);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSellPrice(nint panel, nint item) => GetScaledPrice(_sellOriginal, panel, item);

    private static int GetScaledPrice(nint original, nint panel, nint item)
    {
        int retailPrice = ((delegate* unmanaged[Thiscall]<nint, nint, int>)original)(panel, item);
        try
        {
            uint word = _block?.ScalingPricesWord ?? 0;
            int percent = (ushort)word;
            if (percent == 0)
            {
                percent = PlayerCountScalingRule.Vanilla;
            }

            return StorePriceRule.ScaleRetailPrice(retailPrice, percent);
        }
        catch
        {
            // A managed exception must never escape through this unmanaged getter boundary.
            return retailPrice;
        }
    }
}
