namespace KotorCoop.Session;

/// <summary>
/// A CLIENT's process-lifetime checkpoint: the last point this process's own
/// <see cref="Protocol.SaveLineage"/> recorded agreement with a host, kept by
/// <see cref="SessionManager"/> past any one <see cref="CoopClient"/>'s lifetime so a bare
/// <c>disconnect</c>/<c>reconnect</c> — no load edge on either side in between — does not
/// start the next connection's lineage "never agreed" again (docs/research/session-save.md).
/// </summary>
/// <param name="HostSessionId">The host's <c>WelcomePacket.SessionId</c> at the checkpoint.
/// Never 0 for a real host (<c>CoopHost</c> mints it from <c>Random.Shared.Next(1,
/// int.MaxValue)</c>), which is what lets 0 mean "no seed" on the wire and in this record.</param>
/// <param name="HostLoadAtAgreement">The host's own <c>WorldDigest.LoadSerial</c> at the
/// checkpoint.</param>
/// <param name="LocalLoadAtAgreement">This client's own <c>WorldDigest.LoadSerial</c> at the
/// checkpoint.</param>
public readonly record struct LineageSeed(uint HostSessionId, uint HostLoadAtAgreement, uint LocalLoadAtAgreement);
