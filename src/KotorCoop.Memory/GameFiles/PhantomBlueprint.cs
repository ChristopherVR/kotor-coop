namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Generates <c>k_coop_phantom.utc</c>, the creature blueprint shipped in the game's
/// Override folder for remote-player phantoms (see docs/research/override-surfaces.md).
/// The spawner prefers this resref over the stock <c>n_commm</c> fallback; the field set
/// mirrors n_commm's (so the loader finds every field it expects) with the co-op deltas:
/// all fourteen Script* resrefs blanked (no stock commoner AI, no .ncs authoring),
/// SoundSetFile 65535 (no barks), Min1HP 1, explicit neutral faction, and a FeatList
/// carrying the armour and weapon proficiencies n_commm lacks — so equipping the
/// phantom never bounces off a proficiency check. Appearance, name and faction are
/// still patched per peer by the plugin at runtime; this file just gives it a safe,
/// silent body to patch.
/// </summary>
public static class PhantomBlueprint
{
    /// <summary>The blueprint's resref — what the phantom spawner asks ResMan for.</summary>
    public const string ResRef = "k_coop_phantom";

    /// <summary>The instantiated creature's script/engine tag.</summary>
    public const string Tag = "coop_phantom";

    /// <summary>feat.2da rows granted so equip never fails a proficiency check:
    /// armour light/medium/heavy (4, 5, 6) and weapon blaster pistol/blaster rifle/
    /// heavy weapons/melee/lightsaber (39, 40, 42, 43, 44).</summary>
    public static readonly IReadOnlyList<ushort> ProficiencyFeats = [4, 5, 6, 39, 40, 42, 43, 44];

    /// <summary>The fourteen creature event-script fields, all shipped empty.</summary>
    public static readonly IReadOnlyList<string> ScriptFields =
    [
        "ScriptHeartbeat", "ScriptOnNotice", "ScriptSpellAt", "ScriptAttacked",
        "ScriptDamaged", "ScriptDisturbed", "ScriptEndRound", "ScriptEndDialogu",
        "ScriptDialogue", "ScriptSpawn", "ScriptRested", "ScriptDeath",
        "ScriptUserDefine", "ScriptOnBlocked",
    ];

    /// <summary>Builds the .utc bytes. Deterministic — same output every call.</summary>
    public static byte[] Build()
    {
        GffWriter writer = new("UTC ");
        GffWriterStruct root = writer.Root;

        // Field order and struct ids follow the decoded n_commm.utc; values are either
        // n_commm's own (sane loader defaults) or the co-op overrides called out inline.
        root.SetResRef("TemplateResRef", ResRef);
        root.SetByte("Race", 6);
        root.SetByte("SubraceIndex", 0);
        root.SetLocString("FirstName", -1);   // blank; the plugin sets the peer's name
        root.SetLocString("LastName", -1);
        root.SetWord("Appearance_Type", 114); // plugin repaints per peer regardless
        root.SetByte("Gender", 0);
        root.SetInt("Phenotype", 0);
        root.SetWord("PortraitId", 0);
        root.SetLocString("Description", -1);
        root.SetString("Tag", Tag);
        root.SetResRef("Conversation", "");
        root.SetByte("IsPC", 0);
        root.SetWord("FactionID", 5);         // Neutral, explicit rather than inherited
        root.SetByte("Disarmable", 0);
        root.SetString("Subrace", "");
        root.SetString("Deity", "");
        root.SetWord("SoundSetFile", 65535);  // no soundset: silences barks
        root.SetByte("Plot", 0);
        root.SetByte("Interruptable", 1);
        root.SetByte("NoPermDeath", 0);
        root.SetByte("BodyBag", 0);
        root.SetByte("BodyVariation", 1);
        root.SetByte("TextureVar", 1);
        root.SetByte("Min1HP", 1);            // phantoms never die locally
        root.SetByte("PartyInteract", 0);
        root.SetByte("Str", 10);
        root.SetByte("Dex", 10);
        root.SetByte("Con", 10);
        root.SetByte("Int", 10);
        root.SetByte("Wis", 10);
        root.SetByte("Cha", 10);
        root.SetInt("WalkRate", 7);
        root.SetByte("NaturalAC", 0);
        root.SetShort("HitPoints", 30);       // raised from n_commm's 8: more Min1HP slack
        root.SetShort("CurrentHitPoints", 30);
        root.SetShort("MaxHitPoints", 30);
        root.SetShort("ForcePoints", 0);
        root.SetShort("CurrentForce", 0);
        root.SetShort("refbonus", 0);
        root.SetShort("willbonus", 0);
        root.SetShort("fortbonus", 0);
        root.SetByte("GoodEvil", 50);
        root.SetByte("LawfulChaotic", 0);
        root.SetFloat("ChallengeRating", 1f);
        root.SetByte("PerceptionRange", 1);   // minimal ranges.2da row; phantom needs none

        foreach (string script in ScriptFields)
        {
            root.SetResRef(script, "");       // no stock commoner AI
        }

        // Eight zero-rank skill entries, exactly the table shape the loader expects.
        GffWriterList skills = root.AddList("SkillList");
        for (int i = 0; i < 8; i++)
        {
            skills.Add(0).SetByte("Rank", 0);
        }

        GffWriterList feats = root.AddList("FeatList");
        foreach (ushort feat in ProficiencyFeats)
        {
            feats.Add(1).SetWord("Feat", feat);
        }

        root.AddList("TemplateList");
        root.AddList("SpecAbilityList");

        // One level of class 0 (Soldier), like n_commm.
        GffWriterStruct soldier = root.AddList("ClassList").Add(2);
        soldier.SetInt("Class", 0);
        soldier.SetShort("ClassLevel", 1);
        soldier.AddList("KnownList0");

        root.AddList("Equip_ItemList");
        root.SetByte("PaletteID", 3);
        root.SetString("Comment", "KOTOR co-op remote-player phantom (generated by PhantomBlueprint)");

        return writer.ToBytes();
    }
}
