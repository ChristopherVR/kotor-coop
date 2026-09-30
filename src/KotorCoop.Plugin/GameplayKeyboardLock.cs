namespace KotorCoop.Plugin;

/// <summary>Release keys already delivered when a solo-section lock begins, then suppress
/// gameplay presses from either physical or synthetic input. Never defer a press to unlock.</summary>
internal sealed class GameplayKeyboardLock
{
    private readonly bool[] _deliveredDown = new bool[256];
    internal static bool Allows(byte key) => key is 0x01 or 0x29 or 0x1C; // Escape, console, chat

    internal static int PurgeQueued(Span<(byte Key, bool Down)> pending, int head, int count)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = pending[(head + i) % pending.Length];
            if (entry.Down && !Allows(entry.Key)) continue;
            pending[(head + kept++) % pending.Length] = entry;
        }
        return kept;
    }

    internal int Apply(Span<uint> events, int count, bool locked, uint timestamp)
    {
        int capacity = events.Length / 5;
        int visible = 0;
        for (int i = 0; i < Math.Min(count, capacity); i++)
        {
            Span<uint> entry = events.Slice(i * 5, 5);
            if (entry[0] >= 256) continue;
            byte key = (byte)entry[0];
            bool down = (entry[1] & 0x80) != 0;
            if (locked && down && !Allows(key)) continue;
            entry.CopyTo(events.Slice(visible++ * 5, 5));
            _deliveredDown[key] = down;
        }
        if (locked)
        {
            for (int key = 0; key < 256 && visible < capacity; key++)
            {
                if (!_deliveredDown[key] || Allows((byte)key)) continue;
                Span<uint> release = events.Slice(visible++ * 5, 5);
                release.Clear();
                release[0] = (uint)key;
                release[2] = timestamp;
                _deliveredDown[key] = false;
            }
        }
        return visible;
    }
}
