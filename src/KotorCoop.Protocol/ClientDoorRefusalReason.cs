namespace KotorCoop.Protocol;

/// <summary>
/// Why the plugin's client-door-refusal watch declined a crossing — read by
/// <c>KotorCoop.Plugin.TransitionAnnounceWatch</c> to choose the on-screen line, decided by
/// <c>KotorCoop.Session.ClientDoorRefusalRule</c>. Lives in <c>KotorCoop.Protocol</c> rather
/// than <c>KotorCoop.Session</c> because <c>IGameBridge</c> (<c>KotorCoop.Memory</c>) needs the
/// type too, and Memory does not (and must not) reference Session — this is the shared layer
/// both already depend on. Written into the shared-block slot the old boolean "armed" flag used
/// (nonzero still means armed), so widening it needed no new offset and no block version bump.
/// </summary>
public enum ClientDoorRefusalReason
{
    /// <summary>Not armed — the crossing is not refused.</summary>
    None = 0,

    /// <summary><c>follow_lead=host</c>: today's exact behaviour. The client never leads, so
    /// every premature crossing is refused regardless of what the host is doing.</summary>
    HostMustLead = 1,

    /// <summary><c>follow_lead=any</c>, but the host cannot safely follow right now (mid its
    /// own transition, in dialogue, or in real combat). The client COULD lead in general; it
    /// just cannot right now.</summary>
    HostBusy = 2,
}
