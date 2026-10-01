using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

// Opt-in observation only. In native callbacks: bounded preallocated records,
// guarded reads, no allocations, file I/O, engine calls or native writes.
internal static unsafe class ReviveEffectLifetimeProbe
{
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static uint _thread;
    public static bool Enabled { get; set; }
    private const int Capacity = 1024;
    private readonly record struct Entry(uint Kind, uint Target, uint Source, uint Payload,
        ulong Id, uint EffectType, uint Caller, uint Body, bool Readable,
        uint Subtype, uint Creator, uint Child0, uint Child1, uint BodyId, uint BodyVtable,
        uint EnqueueSerial, bool Captured, bool FreedSeen, bool PageReadable);
    private static readonly Entry[] Ring = new Entry[Capacity];
    private static readonly Entry[] Pending = new Entry[Capacity];
    private static readonly bool[] Freed = new bool[Capacity];
    private static uint _enqueueSerial;
    private static int _write, _drained, _pending;
    private static uint _count;
    private static void Record(Entry value)
    {
        if (!OnObservedThread || _count++ >= 20000) return;
        Ring[_write] = value; _write = (_write + 1) % Capacity;
        if (_write == _drained) _drained = (_drained + 1) % Capacity;
    }
    private static Entry Snapshot(uint kind, uint target, uint source, uint payload, uint caller, uint body)
    {
        bool readable = NativeMethods.IsReadable((nint)payload, 8);
        bool header = NativeMethods.IsReadable((nint)payload, 12);
        bool creator = NativeMethods.IsReadable((nint)payload, 28);
        bool links = NativeMethods.IsReadable((nint)payload, 48);
        bool bodyReadable = NativeMethods.IsReadable((nint)body, 8);
        return new(kind, target, source, payload, readable ? *(ulong*)payload : 0,
            header ? *(ushort*)(payload + 8) : uint.MaxValue, caller, body, readable,
            header ? *(byte*)(payload + 10) & 7u : uint.MaxValue,
            creator ? *(uint*)(payload + 24) : 0,
            links ? *(uint*)(payload + 40) : 0, links ? *(uint*)(payload + 44) : 0,
            bodyReadable ? *(uint*)(body + 4) : 0, bodyReadable ? *(uint*)body : 0, 0, false, false, readable);
    }
    private static bool OnObservedThread => Enabled && _thread != 0 && GetCurrentThreadId() == _thread;
    private static int FindPending(uint payload)
    {
        int found = -1; uint sequence = 0;
        for (int i = 0; i < Pending.Length; i++)
        {
            if (payload != 0 && Pending[i].Payload == payload && Pending[i].EnqueueSerial > sequence)
            { found = i; sequence = Pending[i].EnqueueSerial; }
        }
        return found;
    }
    public static uint OriginalStack(uint context) =>
        NativeMethods.IsReadable((nint)context, 36)
        && NativeProbeContextRule.TryReadOriginalStack(new ReadOnlySpan<byte>((void*)context, 36), out uint original)
            ? original : 0;

    public static void Enqueue(uint context)
    {
        if (!OnObservedThread) return;
        uint sp = OriginalStack(context);
        if (!NativeMethods.IsReadable((nint)sp, 28) || *(uint*)(sp + 20) != 14) return;
        Entry value = Snapshot(1, *(uint*)(sp + 16), *(uint*)(sp + 12),
            *(uint*)(sp + 24), *(uint*)sp, 0) with { EnqueueSerial = ++_enqueueSerial, Captured = true };
        Pending[_pending] = value; _pending = (_pending + 1) % Capacity;
        Freed[(_pending + Capacity - 1) % Capacity] = false;
        Record(value);
    }
    public static void Dispatch(uint context, bool discarded)
    {
        if (!OnObservedThread || !NativeMethods.IsReadable((nint)context, 36)) return;
        uint sp = OriginalStack(context);
        if (!NativeMethods.IsReadable((nint)sp, 0x38) || *(uint*)(context + 4) != 14) return;
        uint payload = *(uint*)context;
        uint body = *(uint*)(sp + 0x34);
        bool bodyReadable = NativeMethods.IsReadable((nint)body, 8);
        int pending = FindPending(payload);
        // A captured fingerprint is enough: never read payload data again here,
        // especially after the matching destructor was seen. Unknown payloads
        // have no free witness and their guarded snapshot is labelled uncaptured.
        Entry entry = pending < 0
            ? Snapshot(discarded ? 4u : 3u, *(uint*)(sp + 0x30), *(uint*)(sp + 0x18), payload, 0, body)
            : Pending[pending] with { Kind = discarded ? 4u : 3u,
                Target = *(uint*)(sp + 0x30), Source = *(uint*)(sp + 0x18), Caller = 0,
                Body = body, BodyId = bodyReadable ? *(uint*)(body + 4) : 0,
                BodyVtable = bodyReadable ? *(uint*)body : 0, FreedSeen = Freed[pending],
                PageReadable = NativeMethods.IsReadable((nint)payload, 8) };
        Record(entry);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destructor(uint context)
    {
        try
        {
            uint sp = OriginalStack(context);
            if (!OnObservedThread || !NativeMethods.IsReadable((nint)sp, 4)) return;
            uint effect = *(uint*)(context + NativeProbeContextRule.SavedEcx);
            int index = FindPending(effect);
            if (index < 0 || Freed[index]) return;
            if (!NativeMethods.IsReadable((nint)effect, 12)) return;
            ulong id = *(ulong*)effect;
            if (index < 0 || !Pending[index].Readable || Pending[index].Id != id) return;
            Entry pending = Pending[index];
            Freed[index] = true;
            Record(Snapshot(2, pending.Target, pending.Source, effect, *(uint*)sp, 0)
                with { EnqueueSerial = pending.EnqueueSerial, Captured = true, FreedSeen = true });
        }
        catch { }
    }
    public static bool InstallDestructor(out string diagnostic)
    {
        _thread = GetCurrentThreadId();
        return DelayEventProbeHook.SpliceContext(0x503590, [0x90, 0x90, 0x90, 0x90],
            [0x6A, 0xFF, 0x68, 0xE6, 0xB7, 0x71, 0x00], 0x503597,
            (nint)(delegate* unmanaged[Cdecl]<uint, void>)&Destructor, out diagnostic);
    }

    public static void ObserveCurrentEffect(uint body, uint target, uint effect, bool after)
    {
        if (!OnObservedThread) return;
        int index = FindPending(effect);
        if (index >= 0 && Freed[index])
        {
            Record(Pending[index] with { Kind = after ? 6u : 5u, Target = target,
                Source = 0, Body = body, Caller = 0, FreedSeen = true,
                PageReadable = NativeMethods.IsReadable((nint)effect, 8) });
            return;
        }
        Entry current = Snapshot(after ? 6u : 5u, target, 0, effect, 0, body);
        if (current.EffectType is 35 or 57) Record(current);
    }
    public static void ObserveCurrentQueued(uint target, uint source, uint payload, uint body, bool after)
    {
        if (!OnObservedThread) return;
        int pending = FindPending(payload);
        if (pending >= 0 && Freed[pending])
        {
            Record(Pending[pending] with { Kind = after ? 8u : 7u, Target = target,
                Source = source, Body = body, Caller = 0, FreedSeen = true,
                PageReadable = NativeMethods.IsReadable((nint)payload, 8) });
            return;
        }
        Record(Snapshot(after ? 8u : 7u, target, source, payload, 0, body));
    }
    public static void Drain()
    {
        if (!OnObservedThread) return;
        while (_drained != _write)
        {
            Entry entry = Ring[_drained]; _drained = (_drained + 1) % Capacity;
            PluginHost.Log($"reviveprobe: kind={entry.Kind} target=0x{entry.Target:X} source=0x{entry.Source:X} payload=0x{entry.Payload:X} uid={entry.Id:X16} type={entry.EffectType} sub={entry.Subtype} creator=0x{entry.Creator:X} child0=0x{entry.Child0:X} child1=0x{entry.Child1:X} fingerprintreadable={entry.Readable} pagereadable={entry.PageReadable} caller=0x{entry.Caller:X} body=0x{entry.Body:X} bodyid=0x{entry.BodyId:X} bodyvt=0x{entry.BodyVtable:X} enqueue={entry.EnqueueSerial} captured={entry.Captured} freedseen={entry.FreedSeen}");
        }
    }
}
