namespace KotorCoop.Plugin;

// Host spawns only: a joiner builds the host's script-spawned creature from its real blueprint,
// as a puppet. docs/re/spawn.md §8's direct construction, so the blueprint's scripts can be
// blanked between LoadFromTemplate and AddToArea, whichever of the two raises OnSpawn.
internal static unsafe partial class Odyssey
{
    private static readonly delegate* unmanaged[Cdecl]<nuint, nint> PuppetOperatorNew =
        (delegate* unmanaged[Cdecl]<nuint, nint>)0x006FA7E6;

    // CSWSCreature::Constructor(uint id, int) — 0x7F000000 allocates a fresh id. ret 8.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, nint> CreatureConstructor =
        (delegate* unmanaged[Thiscall]<nint, uint, int, nint>)0x004F7A10;

    // CResRef::CResRef(const char*). ret 4.
    private static readonly delegate* unmanaged[Thiscall]<byte*, byte*, void> ResRefFromString =
        (delegate* unmanaged[Thiscall]<byte*, byte*, void>)0x00406D80;

    // CSWSCreature::LoadFromTemplate(CResRef*, int) -> int, zero on failure. ret 8.
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int, int> CreatureLoadFromTemplate =
        (delegate* unmanaged[Thiscall]<nint, byte*, int, int>)0x005026D0;

    // CSWSObject::SetOrientation(Vector*). ret 4.
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, void> ObjectSetOrientation =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, void>)0x004CD170;

    // CSWSCreature::AddToArea(CSWSArea*, float x, float y, float z, int, int). ret 0x18.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int, int> CreatureAddToArea =
        (delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int, int>)0x004FA100;

    /// <summary>The 14 creature script CExoStrings, +0x230..+0x298 (Heartbeat, Notice, SpellAt,
    /// Attacked, Damaged, Disturbed, EndRound, Dialogue, Spawn, Rested, Death, UserDefined,
    /// Blocked, EndConversation). VERIFIED live 2026-10-09 on end_sith3 and n_commm; +0x260
    /// (EndRound, k_def_combend01) matches the slot Odyssey.Combat.Attack.cs already proved.</summary>
    private const int CreatureScriptsOffset = 0x230;
    private const int CreatureScriptCount = 14;

    /// <summary>
    /// Builds <paramref name="template"/> as a driven puppet: every script blanked before it
    /// joins the area (a scene's OnSpawn or OnDeath must run on the host only). It keeps the
    /// blueprint's own tag: a joiner's conversation with a scripted story NPC names its partner
    /// by tag. The agent recognises the mirror's bodies by id instead
    /// (SharedBlock.OffsetMaterialisedLocalIds). Returns the new local id or
    /// <see cref="ObjectInvalid"/>. MAIN THREAD ONLY.
    /// </summary>
    public static uint SpawnPuppet(string template, Vec3 position, Vec3 facingUnitVector, out int failure)
    {
        failure = 0;
        nint area = GetAreaPointer();
        if (area == 0)
        {
            failure = 1;
            return ObjectInvalid;
        }

        if (template.Length is 0 or > 16)
        {
            failure = 2;
            return ObjectInvalid;
        }

        nint creature = PuppetOperatorNew(0xAC8);
        if (creature == 0)
        {
            failure = 3;
            return ObjectInvalid;
        }

        CreatureConstructor(creature, ObjectInvalid, 0);

        byte* name = stackalloc byte[17];
        for (int i = 0; i < template.Length; i++)
        {
            name[i] = (byte)template[i];
        }

        name[template.Length] = 0;
        byte* resref = stackalloc byte[32];
        ResRefFromString(resref, name);
        if (CreatureLoadFromTemplate(creature, resref, 0) == 0)
        {
            // The engine's own failure path: the scalar deleting destructor, vtable slot 0.
            ((delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint**)creature)[0])(creature, 1);
            failure = 4;
            return ObjectInvalid;
        }

        for (int i = 0; i < CreatureScriptCount; i++)
        {
            nint script = creature + CreatureScriptsOffset + (i * 8);
            nint text = *(nint*)script;
            if (text != 0 && *(uint*)(script + 4) != 0 && NativeMethods.IsReadable(text, 1))
            {
                *(byte*)text = 0;
            }

            *(uint*)(script + 4) = 0;
        }

        EmptyPuppetBackpack(creature);

        Vec3 facing = facingUnitVector;
        ObjectSetOrientation(creature, &facing);
        CreatureAddToArea(creature, area, position.X, position.Y, position.Z, 1, 1);
        return *(uint*)(creature + 4);
    }

    /// <summary>
    /// A blueprint's backpack is that creature's loot; the host's real corpse already sends its
    /// own (MaterialisedLoot), so a puppet carrying the blueprint's too would double it. Each
    /// item is unlinked from the creature's repository (+0xA30) and destroyed. Equipped items
    /// are not in the backpack and stay, so the puppet looks right.
    /// </summary>
    private static void EmptyPuppetBackpack(nint creature)
    {
        nint repo = *(nint*)(creature + CreatureItemRepo);
        if (repo == 0 || !NativeMethods.IsReadable(repo, RepositoryItemCount + 4))
        {
            return;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 256)
        {
            return;
        }

        Span<uint> items = stackalloc uint[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = *(uint*)(ids + (i * 4));
        }

        foreach (uint itemId in items)
        {
            nint item = ResolveServerObject(itemId);
            if (item != 0)
            {
                RepositoryRemoveItemFn(repo, item);
                DestroyObjectById(itemId, out _);
            }
        }
    }
}
