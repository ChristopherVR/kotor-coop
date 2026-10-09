namespace KotorCoop.Plugin;

// The co-op cheat-channel region: request/response fields for every CheatController verb.
// See SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // ---- co-op cheats (version 41, region 0x3328..0x33C0) ------------------------
    //
    // Version 40, not 39: this landed in the same integration as item identity, which took
    // 39 first. Two builds that disagree about whether this region exists must fail to find
    // each other rather than read each other's fields as noise, so the number has to move.
    //
    // Version 41 grew the region's tail by four dwords (0x33B0..0x33C0) for the `setitem`
    // staging cheat — the four identity fields that do not fit the generic target/amount
    // pair. Nothing already in the region moved, and a mixed pair would technically survive
    // (an old plugin answers "unknown cheat kind 7" and an old agent never sends it), but
    // the rule here is that the layout number moves whenever the layout does: a channel whose
    // meaning is decided by which build you are running is not a channel anyone can debug.
    //
    // The staging surface: one serial-edged request the agent writes and the plugin runs on
    // the game thread, because every one of these ends in an engine call and engine calls
    // only ever happen there.
    //
    // It carries a DETAIL string back, which is not decoration. A cheat that cannot be
    // applied has to say WHAT IT SAW — "that id is a phantom", "no creature 0x2A here",
    // "the module pins you at one hit point" — or a guard that fails safe is
    // indistinguishable from a guard that silently does nothing, and the whole point of
    // staging commands is that a live session can trust the answer without a human watching
    // the screen. The plugin logs the same sentence.
    //
    // Deliberately ONE request in flight. These are typed by a human a few times a minute;
    // a ring would add ordering questions to a channel whose whole value is that its answer
    // arrives before the next line is typed.

    /// <summary>Agent -&gt; plugin. Bumped LAST, after every other field of the request, so
    /// the plugin can never observe a half-written cheat.</summary>
    public const int OffsetCheatSerial = 0x3328;

    /// <summary>Agent -&gt; plugin. One of the <c>Cheat*</c> kind constants below.</summary>
    public const int OffsetCheatKind = 0x332C;

    /// <summary>Agent -&gt; plugin. Engine object id the cheat aims at; 0 = "the local player"
    /// for the self cheats, or "resolve <see cref="OffsetCheatText"/> as a tag" for slay.</summary>
    public const int OffsetCheatTarget = 0x3330;

    /// <summary>Agent -&gt; plugin. The numeric argument: hit points for a top-up, stack size
    /// for an item, 1/0 for a toggle.</summary>
    public const int OffsetCheatAmount = 0x3334;

    /// <summary>Agent -&gt; plugin. Destination for a teleport (world coordinates).</summary>
    public const int OffsetCheatX = 0x3338;
    public const int OffsetCheatY = 0x333C;
    public const int OffsetCheatZ = 0x3340;

    /// <summary>Agent -&gt; plugin. A tag or a blueprint resref, null-terminated ASCII.</summary>
    public const int OffsetCheatText = 0x3344;

    public const int CheatTextLength = 32;

    /// <summary>Plugin -&gt; agent. The serial the plugin has finished; equal to
    /// <see cref="OffsetCheatSerial"/> means the answer below is for the current request.</summary>
    public const int OffsetCheatApplied = 0x3364;

    /// <summary>Plugin -&gt; agent. One of the <c>CheatStatus*</c> constants.</summary>
    public const int OffsetCheatStatus = 0x3368;

    /// <summary>Plugin -&gt; agent. What actually happened, in words, null-terminated ASCII.
    /// Occupies 0x336C..0x33AC, inside the region.</summary>
    public const int OffsetCheatDetail = 0x336C;

    public const int CheatDetailLength = 64;

    // The four identity fields `setitem` can poke that have nowhere else to ride. Stack size
    // reuses OffsetCheatAmount (0 = leave it alone), because a stack of zero is not a thing
    // the engine produces; these four have no such spare value, so each carries its own
    // "leave alone" sentinel instead of overloading a legal one.

    /// <summary>Agent -&gt; plugin. New charges for <see cref="CheatKindSetItem"/>, or
    /// <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatCharges = 0x33B0;

    /// <summary>Agent -&gt; plugin. New max charges, or <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatMaxCharges = 0x33B4;

    /// <summary>Agent -&gt; plugin. New installed-upgrade MASK, or
    /// <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatUpgrades = 0x33B8;

    /// <summary>Agent -&gt; plugin. New identity flag bits (the <c>ItemIdentity*</c> wire
    /// flags, not the engine's own bit numbers), or <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatItemFlags = 0x33BC;

    /// <summary>"Do not touch this field." Chosen as all-ones because every one of the four
    /// is a value the engine keeps far below it — charges and max charges are persisted as
    /// GFF bytes, and the upgrade mask indexes upgrade SLOTS, of which there are single
    /// digits. A caller that really did mean 0xFFFFFFFF upgrades is asking for every one of
    /// 32 slots at once, which no item has.</summary>
    public const uint CheatFieldUnchanged = 0xFFFF_FFFF;

    /// <summary>
    /// <see cref="OffsetCheatTarget"/> sentinel meaning "the LOCAL player, not a materialised
    /// stand-in" for <see cref="CheatKindGiveLoot"/> — the dialogue item-grant channel's give
    /// (docs/research/dialogue-consequences.md §3.4) targets the speaker directly rather than
    /// a corpse's stand-in, so it must never be resolved through
    /// <c>MaterialisedEnemies.ToLocal</c>. Distinct from <see cref="CheatFieldUnchanged"/>
    /// (0xFFFF_FFFF) so the two "not a real value" sentinels can never be confused with each
    /// other on a field that could plausibly carry either. Equal by VALUE (not by reference —
    /// <c>KotorCoop.Session</c> cannot reference this NativeAOT-only project) to
    /// <c>CoopAgent.LocalPlayerObjectId</c>, which is itself the engine's own verified player
    /// id (docs/re/object-model.md: "Player object id is exactly 0x7FFFFFFF") — chosen
    /// deliberately rather than an arbitrary sentinel, so the "local player" target never needs
    /// translation at all: it already names the same creature on every instance.
    /// </summary>
    public const uint CheatTargetLocalPlayer = 0x7FFFFFFF;

    /// <summary>GiveLoot: a target with this bit set names a LOCAL object id directly (the low
    /// bits), no owner-id translation: a body bag that shared corpse loot fills to match the
    /// host's (CorpseLootLedger). Server object ids never reach this bit.</summary>
    public const uint CheatTargetLocalObjectFlag = 0x40000000;























    /// <summary>The request was carried out here.</summary>
    public const int CheatStatusApplied = 1;

    /// <summary>The request was handed to the owning peer rather than applied locally —
    /// the honest answer for a client slaying an enemy it does not own.</summary>
    public const int CheatStatusForwarded = 2;

    /// <summary>No world (main menu), or no local player creature yet.</summary>
    public const int CheatStatusNoWorld = -1;

    /// <summary>The target named by the request does not exist in this instance.</summary>
    public const int CheatStatusNoTarget = -2;

    /// <summary>The co-op authority rules refuse it; the detail says which rule.</summary>
    public const int CheatStatusRefused = -3;

    /// <summary>The engine refused the call; the detail carries its failure code.</summary>
    public const int CheatStatusEngineRefused = -4;

}
