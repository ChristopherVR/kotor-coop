namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>The game's one <c>CExoSound</c> (the pointer every engine caller of
    /// <see cref="PlayOneShotSoundFn"/> loads into ecx: <c>mov ecx,[0x7A39EC]</c> at 0x006467A8,
    /// 0x006559A0 and 0x006800A8).</summary>
    private const nint ExoSoundPtr = 0x007A39EC;

    /// <summary>The priority group the engine's own interface and item sounds pass
    /// (<c>push 0xB</c> at 0x006467B6 and 0x006800B5).</summary>
    private const int SoundPriorityInterface = 0xB;

    /// <summary>
    /// <c>CExoSound::PlayOneShotSound(CResRef* name, int priorityGroup, int, int, float, float)</c>
    /// at 0x005D5E00: a thunk that loads the internal object and jumps to 0x005D7550, which ends
    /// in <c>ret 0x18</c> (six stack arguments). Every engine caller read (16 call sites) passes
    /// the name, a priority group and four zeros. It plays a sound file by name once, not placed
    /// in the world, and returns at once; a name with no file behind it plays nothing.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int, int, int, float, float, int> PlayOneShotSoundFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int, int, int, float, float, int>)0x005D5E00;

    /// <summary>
    /// Plays a sound file (a .wav by its name without the extension, 16 characters at most)
    /// once, at full volume wherever the camera is. Returns false when the sound system is not
    /// up or the name does not fit.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PlaySound(string name)
    {
        nint sound = *(nint*)ExoSoundPtr;
        if (sound == 0 || name.Length is 0 or > 16)
        {
            return false;
        }

        // A CResRef is 16 bytes, lower case, zero padded (no terminator at full length).
        byte* resref = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            char c = i < name.Length ? char.ToLowerInvariant(name[i]) : '\0';
            if (c > 0x7E)
            {
                return false;
            }

            resref[i] = (byte)c;
        }

        PlayOneShotSoundFn(sound, resref, SoundPriorityInterface, 0, 0, 0f, 0f);
        return true;
    }
}
