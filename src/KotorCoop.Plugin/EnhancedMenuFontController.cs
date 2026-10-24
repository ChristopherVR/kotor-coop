using System.Text;
using Metrics = KotorCoop.Plugin.EnhancedMenuFontRule.Metrics;

namespace KotorCoop.Plugin;

/// <summary>
/// Main/render thread only. Reads bounded native font structures and scales from original
/// TXI metadata, never from an earlier scaled value. No textures or resources are rewritten.
/// </summary>
internal static unsafe class EnhancedMenuFontController
{
    private const nint TextureList = 0x007A4798;
    private const nint BasicTextureVtable = 0x0073F160;
    private static bool _ready;
    private static int _lastPercent;
    private static long _lastScan;
    private static readonly Dictionary<string, (int Characters, Metrics Metrics)?> Sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call each menu frame, including when percent is unchanged to discover new fonts.
    /// Returns true when existing text objects need wrapping again.</summary>
    public static bool UpdateScale(int targetPercent, bool restoreForWorld = false)
    {
        if (targetPercent is < 50 or > 600 || (Odyssey.HasServer && !restoreForWorld) || !EnsureRecognized()) return false;
        long now = Environment.TickCount64;
        if (!restoreForWorld && targetPercent == _lastPercent && now - _lastScan < 100) return false;
        _lastScan = now;
        if (!NativeMethods.IsReadable(TextureList, 12)) return false;
        nint array = *(nint*)TextureList;
        int count = *(int*)(TextureList + 4), capacity = *(int*)(TextureList + 8);
        if (count is < 0 or > 32768 || capacity < count || capacity > 65536
            || (count > 0 && !NativeMethods.IsReadable(array, (nuint)(count * 4)))) return false;
        int changed = 0, fonts = 0;
        for (int i = 0; i < count; i++)
        {
            nint texture = *(nint*)(array + i * 4);
            // 0xEC is the real allocation size. The vendor's 0x100-byte probe crosses
            // its end under page heap; its subsequent 0x80-byte font probe also crosses
            // the 0x30-byte CAurFontInfo allocation.
            if (!NativeMethods.IsReadable(texture, 0xEC) || *(nint*)texture != BasicTextureVtable) continue;
            ReadOnlySpan<byte> nameBytes = new((void*)(texture + 0x78), 32);
            int end = nameBytes.IndexOf((byte)0);
            if (end <= 0) continue;
            nameBytes = nameBytes[..end];
            bool safeName = true;
            foreach (byte c in nameBytes)
                if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'0' and <= (byte)'9' or (byte)'_')) safeName = false;
            if (!safeName) continue;
            bool fontName = false;
            for (int c = 0; c + 4 <= nameBytes.Length; c++)
            {
                int first = nameBytes[c] | 32, second = nameBytes[c + 1] | 32;
                int third = nameBytes[c + 2] | 32, fourth = nameBytes[c + 3] | 32;
                if ((first == 'f' && second == 'o' && third == 'n' && fourth == 't')
                    || (c == 0 && first == 'f' && second == 'n' && third == 't' && fourth == '_')) fontName = true;
            }
            if (!fontName) continue;
            string name = Encoding.ASCII.GetString(nameBytes);
            nint info = *(nint*)(texture + 0x38);
            if (!TryValidateInfo(info, out int characters) || !NativeMethods.IsWritable(info + 4, 20)) continue;
            if (!TrySource(texture, name, out var source) || characters != source.Characters) continue;
            Metrics current = Read(info);
            if (!EnhancedMenuFontRule.HasUniformScale(source.Metrics, current)) continue;
            fonts++;
            Metrics desired = source.Metrics.Scale(targetPercent / 100f);
            if (!desired.IsValid || desired == current) continue;
            float* fields = (float*)(info + 4);
            fields[0] = desired.Height;
            fields[1] = desired.Baseline;
            fields[2] = desired.Width;
            fields[3] = desired.Right;
            fields[4] = desired.Bottom;
            changed++;
        }
        if (_lastPercent != targetPercent)
        {
            _lastPercent = targetPercent;
            PluginHost.Log($"Enhanced: live fonts {targetPercent}% ({fonts} recognized, {changed} changed); original TXI metrics preserved");
        }
        return changed != 0;
    }

    /// <summary>Wrap an engine-owned CSWGuiText after all extents and metrics are updated.
    /// Restricts the font path to the native classes and a validated live font allocation.</summary>
    public static bool TryRewrap(nint guiText)
    {
        if (!_ready || !NativeMethods.IsReadable(guiText, 0x18)) return false;
        nint text = *(nint*)(guiText + 0x14);
        if (!NativeMethods.IsReadable(text, 0x48) || *(nint*)text != 0x00741878
            || *(int*)(text + 0x0C) <= 0 || *(int*)(text + 0x10) <= 0) return false;
        nint texture = *(nint*)(text + 0x18);
        if (!NativeMethods.IsReadable(texture, 0x18)) return false;
        nint vtable = *(nint*)texture;
        if (vtable != BasicTextureVtable && vtable != 0x0073F0A8) return false;
        nint referenced = *(nint*)(texture + 0x10);
        if (referenced != 0) texture = referenced;
        if (!NativeMethods.IsReadable(texture, 0xEC) || *(nint*)texture != BasicTextureVtable
            || !TryValidateInfo(*(nint*)(texture + 0x38), out _)) return false;
        ((delegate* unmanaged[Thiscall]<nint, int, void>)0x0045A2F0)(text, -1);
        return true;
    }

    private static Metrics Read(nint info)
    {
        float* p = (float*)(info + 4);
        return new(p[0], p[1], p[2], p[3], p[4]);
    }

    private static bool TryValidateInfo(nint info, out int characters)
    {
        characters = 0;
        if (!NativeMethods.IsReadable(info, 0x30)) return false;
        characters = *(int*)info;
        if (characters is <= 0 or > 512) return false;
        for (int offset = 0x18; offset <= 0x24; offset += 12)
        {
            nint coords = *(nint*)(info + offset);
            int size = *(int*)(info + offset + 4), capacity = *(int*)(info + offset + 8);
            if (size != characters || capacity < size || capacity > 4096
                || !NativeMethods.IsReadable(coords, (nuint)(size * 12))) return false;
        }
        return Read(info).IsValid;
    }

    private static bool TrySource(nint texture, string name, out (int Characters, Metrics Metrics) source)
    {
        if (Sources.TryGetValue(name, out var known) && known.HasValue)
        {
            source = known.Value;
            return true;
        }
        // GetTxiData/GetTxiSize expose the owned original text at +0x48/+0x64.
        nint txi = *(nint*)(texture + 0x48);
        int length = *(int*)(texture + 0x64);
        if (length is > 0 and <= 65536 && NativeMethods.IsReadable(txi, (nuint)length)
            && EnhancedMenuFontRule.TryParse(Encoding.ASCII.GetString(new ReadOnlySpan<byte>((void*)txi, length)),
                out int characters, out Metrics metrics))
        {
            source = (characters, metrics);
            Sources[name] = source;
            return true;
        }
        if (!Sources.TryGetValue(name, out var cached))
        {
            cached = ReadOverrideSource(name);
            Sources[name] = cached;
        }
        source = cached.GetValueOrDefault();
        return cached.HasValue;
    }

    private static (int Characters, Metrics Metrics)? ReadOverrideSource(string name)
    {
        try
        {
            string root = Path.Combine(NativeMethods.GetGameDirectory(), "Override", name);
            bool hasTxi = File.Exists(root + ".txi"), hasTpc = File.Exists(root + ".tpc");
            if (hasTxi && hasTpc) return null; // Ambiguous resource priority: do not guess.
            string path = root + (hasTxi ? ".txi" : ".tpc");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int length = (int)Math.Min(stream.Length, 65536);
            stream.Seek(-length, SeekOrigin.End);
            byte[] tail = new byte[length];
            stream.ReadExactly(tail);
            return EnhancedMenuFontRule.TryParse(Encoding.ASCII.GetString(tail), out int characters, out Metrics metrics)
                ? (characters, metrics) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool EnsureRecognized()
    {
        if (_ready) return true;
        nint renderer = NativeMethods.GetModuleHandle("opengl32.dll");
        if (renderer == 0 || NativeMethods.GetProcAddress(renderer, "KotorEnhancedMouseWheel") == 0
            || !NativeMethods.IsReadable(renderer, 0x1000)) return false;
        int pe = *(int*)(renderer + 0x3C);
        if (pe is < 0x40 or > 0xF00 || *(uint*)(renderer + pe) != 0x4550
            || *(ushort*)(renderer + pe + 4) != 0x14C
            || *(uint*)(renderer + pe + 8) != 0x6AD934F3
            || *(uint*)(renderer + pe + 24 + 56) != 0x1E3000) return false;
        // Preferred image base 0x10000000; only the RVAs are used at runtime.
        if (!Matches(renderer + 0x1500, [0x55,0x53,0x57,0x56,0x81,0xEC,0x04,0x02,0,0])
            || !Matches(renderer + 0x1BF0, [0x9C,0x60,0x89,0xE5,0x81,0xEC,0x20,0x02,0,0,0x83,0xE4,0xF0])
            || !Matches(0x00423510, [0x68,0xEC,0,0,0,0xE8,0xCC,0x72,0x2D,0])
            || !Matches(0x00459610, [0x8B,0x49,0x18,0x8B,0x01,0xFF,0x50,0x38,0xD9,0x40,0x04])
            || !Matches(0x00421560, [0x56,0x8B,0x35,0x9C,0x47,0x7A,0,0x33,0xC0])
            || !Matches(0x0045A2F0, [0x83,0xEC,0x10,0x53,0x56,0x8B,0xF1])) return false;
        nint executable = NativeMethods.GetModuleHandle(null);
        if (executable != 0x00400000 || !NativeMethods.IsReadable(executable, 0x1000)) return false;
        int executablePe = *(int*)(executable + 0x3C);
        if (executablePe is < 0x40 or > 0xF00 || *(uint*)(executable + executablePe) != 0x4550
            || *(uint*)(executable + executablePe + 8) != 0x402BC2D9) return false;
        _ready = true;
        return true;
    }

    private static bool Matches(nint address, ReadOnlySpan<byte> expected) =>
        NativeMethods.IsReadable(address, (nuint)expected.Length)
        && new ReadOnlySpan<byte>((void*)address, expected.Length).SequenceEqual(expected);
}
