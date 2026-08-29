namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- minigame peer lock (region 0x4704..0x4720, no TotalSize growth) ----------------
    //
    // docs/research/minigames.md §24. One peer is racing a swoop (or flying a turret); every
    // OTHER peer is control-locked with a notice naming them, because the racer's engine has
    // swapped the whole world for a track and nothing either player does in the meantime can
    // reach the other.
    //
    // Direction is AGENT -> PLUGIN, the opposite of OffsetLocalInSoloSegment: the decision
    // needs the peer roster, which only the agent has — and specifically it needs the roster
    // BEFORE CoopAgent's own peer filter, since minigame_hide_peer (default ON) removes a
    // racing peer from the shared block's remote slots in the very tick their flag turns true,
    // so a plugin-side reader of those slots could never see the owner at all
    // (MinigameAnnouncer's own doc comment documents that same trap). A plain LEVEL,
    // republished every tick, matching every other cross-process field here.
    //
    // Rides in the free tail after the item-template-spawn ring's cursor (0x4700..0x4704),
    // inside the 0xFC bytes TotalSize 0x4800 leaves after it; this region uses 0x1C of them.
    // No TotalSize growth, no relayout of anything else.

    /// <summary>Agent -&gt; plugin. Nonzero while THIS instance's player must be held still
    /// because a PEER is inside a minigame. Zero at every other moment, including for the
    /// racer themselves — the owner is never locked by their own race.</summary>
    public const int OffsetMinigameLockActive = 0x4704;

    /// <summary>Agent -&gt; plugin. The racing peer's own published name, null-padded ASCII,
    /// <see cref="NameLength"/> bytes — the "X" in the on-screen "X is racing - waiting".
    /// Written in the same tick as <see cref="OffsetMinigameLockActive"/> and left alone when
    /// the lock is clear (the plugin only reads it while the flag is set).</summary>
    public const int OffsetMinigameLockName = 0x4708;
}
