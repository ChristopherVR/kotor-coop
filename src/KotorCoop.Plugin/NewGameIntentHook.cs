using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>Observes accepted stock New Game without treating an absent PC as game over.</summary>
internal static unsafe class NewGameIntentHook
{
    private const nint Site = 0x0067B135;
    private static SharedBlockView? _view;
    private static bool _installed;
    public static bool Install(SharedBlockView view, out string diagnostic)
    {
        _view=view;
        if (_installed) { diagnostic="already installed"; return true; }
        if (!NativeMethods.IsReadable(Site,12)
            || !new ReadOnlySpan<byte>((void*)Site,6).SequenceEqual(NewGameIntentStub.Original)
            || !new ReadOnlySpan<byte>((void*)(Site+6),6).SequenceEqual(new byte[]{0x8B,0x0D,0xEC,0x39,0x7A,0x00}))
        { diagnostic="accepted OnNewGamePicked bytes differ"; return false; }
        nint stub=NativeMethods.VirtualAlloc(0,NewGameIntentStub.Length,0x3000,0x40);
        if(stub==0) { diagnostic="stub allocation failed"; return false; }
        NewGameIntentStub.Write(new Span<byte>((void*)stub,NewGameIntentStub.Length),(uint)stub,
            (uint)(nint)(delegate* unmanaged[Cdecl]<void>)&Observed,(uint)(Site+6));
        FlushInstructionCache((nint)(-1),stub,NewGameIntentStub.Length);
        if(!NativeMethods.VirtualProtect(Site,6,0x40,out uint old))
        { diagnostic="site protection failed"; return false; }
        *(byte*)Site=0xE9; *(int*)(Site+1)=(int)(stub-Site-5); *(byte*)(Site+5)=0x90;
        NativeMethods.VirtualProtect(Site,6,old,out _); FlushInstructionCache((nint)(-1),Site,6);
        _installed=true; diagnostic="accepted stock New Game at 0x67B135"; return true;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Observed() { try { if(_installed) PublishAccepted(); } catch { } }
    // Console drive executes the same body without entering OnNewGamePicked.
    internal static void PublishAccepted()
    {
        if(_view is not { } view)return;
        view.PublishNewGameIntent(Environment.TickCount64);
    }
    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process,nint address,nuint size);
}
