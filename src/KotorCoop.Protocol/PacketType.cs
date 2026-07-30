namespace KotorCoop.Protocol;

public enum PacketType : byte
{
    None = 0,

    /// <summary>client -> host: request to join.</summary>
    Hello = 1,

    /// <summary>host -> client: accepted, here is your peer id and session parameters.</summary>
    Welcome = 2,

    /// <summary>host -> client: rejected, with a reason.</summary>
    Reject = 3,

    /// <summary>client -> host: my current avatar state, sent every tick.</summary>
    StateUpdate = 4,

    /// <summary>host -> all: everyone's avatar state plus the current world flag version.</summary>
    WorldSnapshot = 5,

    /// <summary>peer -> host: set a world flag. Retransmitted until acked.</summary>
    FlagOp = 6,

    /// <summary>host -> peer: flag op received, stop retransmitting.</summary>
    FlagAck = 7,

    /// <summary>client -> host: my flag version is stale, send me the full set.</summary>
    FlagSyncRequest = 8,

    /// <summary>host -> client: the authoritative flag set.</summary>
    FlagSnapshot = 9,

    Ping = 10,
    Pong = 11,

    /// <summary>peer -> host: leaving cleanly.</summary>
    Bye = 12,

    /// <summary>peer -> host: drop or remove a ground item. Retransmitted until acked.</summary>
    ItemOp = 13,

    /// <summary>host -> peer: item op received, stop retransmitting.</summary>
    ItemAck = 14,

    /// <summary>client -> host: my ground-item version is stale, send me the full set.</summary>
    ItemSyncRequest = 15,

    /// <summary>host -> client: the authoritative set of items currently on the ground.</summary>
    ItemSnapshot = 16,

    /// <summary>
    /// host -> clients: authoritative health of every enemy in the module, broadcast each
    /// tick. The host owns enemy state; clients mirror it and cannot kill anything locally.
    /// </summary>
    EnemySnapshot = 17,
}
