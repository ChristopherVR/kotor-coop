using System;

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private const int ReviveRemovalCapacity = 64;
    private const int ReviveQueueLimit = 4096;
    private static int _reviveExpiryWitnesses;
    private readonly struct ReviveQueueNativeReader : IReviveQueueNodeReader
    {
        public bool TryReadNode(nint node, out nint next, out nint record)
        {
            next = record = 0;
            if (!NativeMethods.IsReadable(node, 12)) return false;
            next = *(nint*)(node + ReviveEffectEventRule.NextNodeOffset);
            record = *(nint*)(node + 8);
            return true;
        }
    }

    private static bool ReviveRemovalContractMatches()
    {
        if (!NativeMethods.IsReadable(0x4FED68, 12) || !NativeMethods.IsReadable(0x7441EC, 4)
            || !NativeMethods.IsReadable(0x7473C0, 2) || !NativeMethods.IsReadable(0x4B0B59, 1)
            || !NativeMethods.IsReadable(0x4E1480, 48) || !NativeMethods.IsReadable(0x4AFDF8, 21)
            || !NativeMethods.IsReadable(0x5E9CA9, 16)
            || !NativeMethods.IsReadable(0x4E1472, 5) || *(byte*)0x4E1472 != 0xBB) return false;
        int branch = *(byte*)0x4B0B59;
        nint branchAddress = 0x4B0B34 + branch * 4;
        return branch < 6 && NativeMethods.IsReadable(branchAddress, 4)
            && new ReadOnlySpan<byte>((void*)0x5E9CA9, 16).SequenceEqual(ReviveEffectEventRule.QueueHeadRemoval)
            && ReviveEffectEventRule.LayoutMatches(new ReadOnlySpan<byte>((void*)0x4E1480, 48),
                new ReadOnlySpan<byte>((void*)0x4AFDF8, 21), *(uint*)0x4E1473)
            && ReviveEffectEventRule.NativeContractMatches(new ReadOnlySpan<byte>((void*)0x4FED68, 12),
                *(uint*)0x7441EC, *(ushort*)0x7473C0, *(uint*)branchAddress);
    }

    // Verified GOG layout: CServerAIMaster+54 -> CExoLinkedList; node+0 is
    // PREVIOUS, node+4 NEXT (GetHead 5E9C80), node+8 ->
    // 24-byte event (day,time,sender,target,type,borrowed payload). No ownership
    // transfer: DeleteEventData(14,...) is a no-op. EventHandler(14,null) is also
    // a no-op. See the disassembly preserved in the Manaan crash report.
    private static nint ReviveEventQueue()
    {
        nint server = GetServer();
        if (server == 0) return 0;
        nint master = GetServerAIMasterFn(server);
        return NativeMethods.IsReadable(master + 0x54, 4) ? *(nint*)(master + 0x54) : 0;
    }

    private static bool TryReadReviveEffects(nint body, out nint array, out int count)
    {
        array = 0; count = 0;
        if (!NativeMethods.IsReadable(body + 0x124, 8)) return false;
        array = *(nint*)(body + 0x124); count = *(int*)(body + 0x128);
        if (count is < 0 or > 1024) return false;
        return count == 0 || NativeMethods.IsReadable(array, (nuint)(count * 4));
    }

    private static bool TryReadReviveEvent(nint record, out ReviveRemovalEvent value)
    {
        value = default;
        if (!NativeMethods.IsReadable(record, 24)) return false;
        value = new(record, *(uint*)record, *(uint*)(record + 4),
            *(uint*)(record + 8), *(uint*)(record + 12), *(uint*)(record + 16),
            *(nint*)(record + 20), 0);
        return true;
    }

    private static bool SnapshotReviveRemovalEvents(uint ownId, nint body,
        Span<ReviveRemovalEvent> events, out int captured, out nint queue)
    {
        captured = 0; queue = ReviveEventQueue();
        if (!ReviveRemovalContractMatches()
            || !TryReadReviveEffects(body, out nint effects, out int effectCount)
            || !NativeMethods.IsReadable(queue, 12)) return false;
        var walk = new ReviveQueueWalker<ReviveQueueNativeReader>(*(nint*)queue,
            ReviveQueueLimit, default);
        while (walk.TryMoveNext(out nint record))
        {
            if (!TryReadReviveEvent(record, out var pending)) return false;
            if (pending.Type == 14 && pending.Target == ownId && pending.Payload != 0)
            {
                for (int i = 0; i < effectCount; i++)
                {
                    nint effect = *(nint*)(effects + i * 4);
                    if (effect != pending.Payload) continue;
                    if (!NativeMethods.IsReadable(effect, 12)) return false;
                    if (!ReviveEffectEventRule.RemovedByResurrection(*(ushort*)(effect + 8), *(byte*)(effect + 10))) break;
                    if (captured == events.Length) return false;
                    events[captured++] = pending with { EffectId = *(ulong*)effect };
                    if (_reviveExpiryWitnesses++ < 40)
                        PluginHost.Log($"revive: captured dying effect expiry target 0x{ownId:X} event 0x{pending.Record:X} effect 0x{effect:X} uid {*(ulong*)effect:X16} type 57 subtype {*(byte*)(effect + 10) & 7}");
                    break;
                }
            }
        }
        return !walk.Failed;
    }

    private static bool ReviveEffectIsAbsent(nint body, ReviveRemovalEvent before)
    {
        if (!TryReadReviveEffects(body, out nint effects, out int count)) return false;
        for (int i = 0; i < count; i++)
        {
            nint effect = *(nint*)(effects + i * 4);
            if (!NativeMethods.IsReadable(effect, 8)) return false;
            // Do not touch freed payload memory. A reused pointer or surviving UID
            // cannot authorize retirement of the captured event.
            if (effect == before.Payload || *(ulong*)effect == before.EffectId) return false;
        }
        return true;
    }

    private static void RetireReviveRemovalEvents(uint ownId, nint body, ulong world,
        nint queue, ReadOnlySpan<ReviveRemovalEvent> events)
    {
        if (events.IsEmpty || !TryGetWorldEpoch(out ulong currentWorld) || currentWorld != world
            || ResolveServerObject(ownId) != body || !NativeMethods.IsReadable(body, 8)
            || *(uint*)body != 0x7470C8 || *(uint*)(body + 4) != ownId
            || ReviveEventQueue() != queue
            || !NativeMethods.IsReadable(queue, 12)) return;
        // Walk the CURRENT queue after the VM call. Never dereference cached nodes
        // or records: Resurrection callbacks may have drained/reordered the queue.
        var walk = new ReviveQueueWalker<ReviveQueueNativeReader>(*(nint*)queue,
            ReviveQueueLimit, default);
        while (walk.TryMoveNext(out nint record))
        {
            if (!TryReadReviveEvent(record, out var current)) return;
            for (int i = 0; i < events.Length; i++)
            {
                var before = events[i];
                var identified = current with { EffectId = before.EffectId };
                if (!ReviveEffectEventRule.MayRetire(before, identified, ownId, true, true,
                    ReviveEffectIsAbsent(body, before))
                    || !NativeMethods.IsWritable(current.Record + 20, 4)) continue;
                *(nint*)(current.Record + 20) = 0;
                PluginHost.Log($"revive: retired redundant effect expiry target 0x{ownId:X} event 0x{current.Record:X} effect 0x{before.Payload:X} uid {before.EffectId:X16}");
            }
        }
    }

    private static void ObserveReviveEffectLifetime(nint body, uint target, bool after)
    {
        if (!ReviveEffectLifetimeProbe.Enabled) return;
        if (TryReadReviveEffects(body, out nint effects, out int count))
        {
            for (int i = 0; i < count; i++)
            {
                nint effect = *(nint*)(effects + i * 4);
                ReviveEffectLifetimeProbe.ObserveCurrentEffect((uint)body, target, (uint)effect, after);
            }
        }
        nint queue = ReviveEventQueue();
        if (!NativeMethods.IsReadable(queue, 12)) return;
        var walk = new ReviveQueueWalker<ReviveQueueNativeReader>(*(nint*)queue, ReviveQueueLimit, default);
        while (walk.TryMoveNext(out nint record))
        {
            if (!TryReadReviveEvent(record, out var entry)) return;
            if (entry.Type != 14) continue;
            nint currentTarget = ResolveServerObject(entry.Target);
            ReviveEffectLifetimeProbe.ObserveCurrentQueued(entry.Target, entry.Sender,
                (uint)entry.Payload, (uint)currentTarget, after);
        }
    }
}
