namespace KotorCoop.Plugin;

// Dialogue spectate/follow/node-sync and per-object interaction locks. See
// SharedBlockView.cs for the class declaration and shared helpers.
internal sealed unsafe partial class SharedBlockView
{
    public byte* LocalDialogueResref => baseAddress + SharedBlock.OffsetLocalDialogueResref;
    public byte* RemoteDialogueResref => baseAddress + SharedBlock.OffsetRemoteDialogueResref;
    // ---- dialogue spectate ------------------------------------------------------

    /// <summary>Pointer to the current dialogue speaker (40 bytes), plugin-written.</summary>
    public byte* LocalDialogueSpeaker => baseAddress + SharedBlock.OffsetLocalDialogueSpeaker;

    /// <summary>Pointer to the current dialogue line (64 bytes), plugin-written.</summary>
    public byte* LocalDialogueLine => baseAddress + SharedBlock.OffsetLocalDialogueLine;

    /// <summary>Plugin-written; bumped when the speaker/line pair changes.</summary>
    public uint LocalDialogueSerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueSerial);
        set => *U32(SharedBlock.OffsetLocalDialogueSerial) = value;
    }


    // ---- dialogue follow --------------------------------------------------------

    /// <summary>Tag (32 bytes) of the NPC the local player is talking to, plugin-written.</summary>
    public byte* LocalDialoguePartnerTag => baseAddress + SharedBlock.OffsetLocalDialoguePartnerTag;

    /// <summary>Plugin-written; bumped on every reply the local player picks.</summary>
    public uint LocalDialogueReplySerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueReplySerial);
        set => *U32(SharedBlock.OffsetLocalDialogueReplySerial) = value;
    }

    /// <summary>Plugin-written; the reply slot the local player clicked.</summary>
    public int LocalDialogueReplyIndex
    {
        get => *(int*)U32(SharedBlock.OffsetLocalDialogueReplyIndex);
        set => *(int*)U32(SharedBlock.OffsetLocalDialogueReplyIndex) = value;
    }

    /// <summary>Agent-written; nonzero while a peer owns a conversation to follow.</summary>
    public bool RemoteDialogueActive => *U32(SharedBlock.OffsetRemoteDialogueActive) != 0;

    /// <summary>Agent-written; a change means "the owner picked a reply, replay it".</summary>
    public uint RemoteDialogueReplySerial => *U32(SharedBlock.OffsetRemoteDialogueReplySerial);

    /// <summary>Agent-written; the reply slot to replay.</summary>
    public int RemoteDialogueReplyIndex => *(int*)U32(SharedBlock.OffsetRemoteDialogueReplyIndex);

    /// <summary>Agent-written; tag of the NPC to open the conversation against.</summary>
    public byte* RemoteDialoguePartnerTag => baseAddress + SharedBlock.OffsetRemoteDialoguePartnerTag;

    /// <summary>Plugin-written pick-log entry base (serial u32, slot u32, lineHash u32).</summary>
    public byte* LocalDialoguePick(int index) =>
        baseAddress + SharedBlock.OffsetLocalDialoguePicks
        + (Clamp(index, SharedBlock.DialoguePickCapacity) * SharedBlock.DialoguePickStride);

    /// <summary>Agent-written pick-log entry base for the conversation owner's picks.</summary>
    public byte* RemoteDialoguePick(int index) =>
        baseAddress + SharedBlock.OffsetRemoteDialoguePicks
        + (Clamp(index, SharedBlock.DialoguePickCapacity) * SharedBlock.DialoguePickStride);


    // ---- dialogue node sync -----------------------------------------------------

    /// <summary>Plugin-written; the entry index the local conversation shows, or -1.</summary>
    public int LocalDialogueNode
    {
        get => *(int*)U32(SharedBlock.OffsetLocalDialogueNode);
        set => *(int*)U32(SharedBlock.OffsetLocalDialogueNode) = value;
    }

    /// <summary>Plugin-written; bumped after the node, so the pair is never read torn.</summary>
    public uint LocalDialogueNodeSerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueNodeSerial);
        set => *U32(SharedBlock.OffsetLocalDialogueNodeSerial) = value;
    }

    /// <summary>Agent-written; the entry index the conversation's owner shows, or -1.</summary>
    public int RemoteDialogueNode => *(int*)U32(SharedBlock.OffsetRemoteDialogueNode);

    /// <summary>Agent-written; bumped when the owner moves to a different node.</summary>
    public uint RemoteDialogueNodeSerial => *U32(SharedBlock.OffsetRemoteDialogueNodeSerial);

    /// <summary>Plugin-written; nonzero while the local conversation is a forced one (the
    /// vanilla engine itself would run it regardless of distance) — see
    /// <see cref="KotorCoop.Plugin.DialogueForceFlagHook"/>.</summary>
    public bool LocalDialogueForced
    {
        get => *U32(SharedBlock.OffsetLocalDialogueForced) != 0;
        set => *U32(SharedBlock.OffsetLocalDialogueForced) = value ? 1u : 0u;
    }

    /// <summary>Agent-written; nonzero while the conversation named by
    /// <see cref="RemoteDialoguePartnerTag"/> is a forced one.</summary>
    public bool RemoteDialogueForced => *U32(SharedBlock.OffsetRemoteDialogueForced) != 0;


    // ---- per-object interaction locks -------------------------------------------

    /// <summary>Plugin-written; the tag of the object the local player is interacting with.</summary>
    public byte* LocalInteractionTag => baseAddress + SharedBlock.OffsetLocalInteractionTag;

    public uint LocalInteractionKind
    {
        get => *U32(SharedBlock.OffsetLocalInteractionKind);
        set => *U32(SharedBlock.OffsetLocalInteractionKind) = value;
    }

    public bool LocalInteractionActive
    {
        get => *U32(SharedBlock.OffsetLocalInteractionActive) != 0;
        set => *U32(SharedBlock.OffsetLocalInteractionActive) = value ? 1u : 0u;
    }

    /// <summary>Agent-written; how many lock slots are valid, clamped to the capacity.</summary>
    public uint InteractionLockCount =>
        Math.Min(*U32(SharedBlock.OffsetInteractionLockCount), (uint)SharedBlock.InteractionLockCapacity);

    /// <summary>
    /// Agent-written, but lowered by the PLUGIN when a session is released: the count is a
    /// level, so an agent that goes away without tearing the block down would otherwise leave
    /// its peers' claims standing for the life of the process. The agent overwrites it again
    /// on its next publish, so clearing it costs a live session nothing.
    /// </summary>
    public void ClearInteractionLocks() => *U32(SharedBlock.OffsetInteractionLockCount) = 0;

    /// <summary>Same reasoning as <see cref="ClearInteractionLocks"/>: without the valid bit
    /// the roster reconciler has nothing to reconcile against, which is the correct state for
    /// an instance with no peers.</summary>
    public void ClearHostPartyRoster() => *U32(SharedBlock.OffsetHostPartyRoster) = 0;

    /// <summary>Agent-written lock entry base: peerId u32 @0, kind u32 @4, tag[32] @8.</summary>
    public byte* InteractionLock(int index) =>
        baseAddress + SharedBlock.OffsetInteractionLocks
        + (Clamp(index, SharedBlock.InteractionLockCapacity) * SharedBlock.InteractionLockStride);

    /// <summary>Plugin-written; the placeable object id claimed by a Container-kind local
    /// interaction claim, or 0 for any other kind — see <see cref="LocalInteractionKind"/>.</summary>
    public uint LocalInteractionTargetId
    {
        get => *U32(SharedBlock.OffsetLocalInteractionTargetId);
        set => *U32(SharedBlock.OffsetLocalInteractionTargetId) = value;
    }

    /// <summary>Agent-written; the object id a peer's Container-kind lock at the SAME index
    /// names (0 otherwise). Parallel array to <see cref="InteractionLock"/> rather than folded
    /// into its stride — see SharedBlock's own region comment.</summary>
    public uint InteractionLockTargetId(int index) =>
        *(uint*)(baseAddress + SharedBlock.OffsetInteractionLockTargetIds
            + (Clamp(index, SharedBlock.InteractionLockCapacity) * SharedBlock.InteractionLockTargetIdStride));


    // ---- container-lock explicit arbitration (docs/roadmap-seamless.md Tier 1 item 4) -----

    /// <summary>Plugin-written; the placeable id a deferred click wants arbitrated, or 0.
    /// Set alongside <see cref="LocalContainerLockRequestSerial"/> — write the target id
    /// FIRST, the serial LAST, so the agent never sees a fresh serial pointing at an id that
    /// has not landed yet.</summary>
    public uint LocalContainerLockRequestTargetId
    {
        get => *U32(SharedBlock.OffsetLocalContainerLockRequestTargetId);
        set => *U32(SharedBlock.OffsetLocalContainerLockRequestTargetId) = value;
    }

    /// <summary>Plugin-written; the serial naming the request above. 0 = nothing pending.</summary>
    public uint LocalContainerLockRequestSerial
    {
        get => *U32(SharedBlock.OffsetLocalContainerLockRequestSerial);
        set => *U32(SharedBlock.OffsetLocalContainerLockRequestSerial) = value;
    }

    /// <summary>Agent-written; the serial <see cref="ContainerLockResponseGranted"/> answers.</summary>
    public uint ContainerLockResponseSerial => *U32(SharedBlock.OffsetContainerLockResponseSerial);

    /// <summary>Agent-written; whether the request named by
    /// <see cref="ContainerLockResponseSerial"/> was granted.</summary>
    public bool ContainerLockResponseGranted => *U32(SharedBlock.OffsetContainerLockResponseGranted) != 0;
}
