using System.Buffers.Binary;

namespace KotorCoop.Plugin;

/// <summary>One bounded relative motion per device poll. Delayed input expires rather
/// than turning the camera after a movie, freeze, or menu has ended.</summary>
internal sealed class SyntheticMouseDeltaQueue
{
    public const int Capacity = 64;
    public const int MaximumDelta = 2048;
    public const int LifetimeMs = 1000;
    private readonly (int X, int Y, long Time)[] _queue = new (int, int, long)[Capacity];
    private readonly object _sync = new();
    private int _head;
    private int _count;

    public int Pending { get { lock (_sync) return _count; } }

    public void Clear() { lock (_sync) { _head = 0; _count = 0; } }

    public bool Enqueue(int x, int y, long nowMs)
    {
        if (x is < -MaximumDelta or > MaximumDelta || y is < -MaximumDelta or > MaximumDelta) return false;
        lock (_sync)
        {
            DiscardExpired(nowMs);
            if (_count == Capacity) return false;
            _queue[(_head + _count) % Capacity] = (x, y, nowMs);
            _count++;
            return true;
        }
    }

    private void DiscardExpired(long nowMs)
    {
        while (_count != 0 && (nowMs < _queue[_head].Time || nowMs - _queue[_head].Time >= LifetimeMs))
        {
            _head = (_head + 1) % Capacity;
            _count--;
        }
    }

    /// <returns>Whether synthetic input was delivered, including when the real device
    /// is unacquired. Unsupported state formats never consume pending motion.</returns>
    public bool Apply(Span<byte> state, bool realSucceeded, bool isolate, long nowMs)
    {
        if (state.Length is not (16 or 20)) return false;
        (int X, int Y, long Time) delta = default;
        bool delivered;
        lock (_sync)
        {
            DiscardExpired(nowMs);
            delivered = _count != 0;
            if (delivered)
            {
                delta = _queue[_head];
                _head = (_head + 1) % Capacity;
                _count--;
            }
        }
        if (!realSucceeded && !delivered) return false;
        if (InputIsolationRule.ShouldNeutralizeMouseState(isolate, (uint)state.Length) || !realSucceeded) state.Clear();
        if (delivered)
        {
            BinaryPrimitives.WriteInt32LittleEndian(state,
                AddClamped(BinaryPrimitives.ReadInt32LittleEndian(state), delta.X));
            BinaryPrimitives.WriteInt32LittleEndian(state[4..],
                AddClamped(BinaryPrimitives.ReadInt32LittleEndian(state[4..]), delta.Y));
        }
        return delivered;
    }

    private static int AddClamped(int physical, int synthetic) =>
        (int)Math.Clamp((long)physical + synthetic, int.MinValue, int.MaxValue);
}
