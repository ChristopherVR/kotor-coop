using KotorCoop.Protocol;

namespace KotorCoop.Session.Events;

// Typed event surface for CoopAgent. Every event is raised synchronously on the pump thread,
// in the same tick — and from the same bookkeeping — that already detects the underlying
// condition today (module-settle tracking, the local-state read, the peer-set diff in
// PumpOnce). Subscribing changes nothing about the existing wire/timing behaviour; these are
// additive notifications layered on top of state CoopAgent already computes.

/// <summary>The local player settled into a module it was already in a DIFFERENT module
/// before — a genuine transition. See <see cref="WorldAdoptedHandler"/> for the first-ever
/// module case, which this does not fire for.</summary>
public delegate void ModuleChangedHandler(string previousModule, string currentModule);

/// <summary>The local player's module became known for the very first time this session —
/// there was no earlier module to report as "previous".</summary>
public delegate void WorldAdoptedHandler(string module);

/// <summary>A peer id appeared in <see cref="CoopAgent.LastPeers"/> that was not there the
/// previous pump.</summary>
public delegate void PeerJoinedHandler(uint peerId);

/// <summary>A peer id that was in <see cref="CoopAgent.LastPeers"/> the previous pump is no
/// longer there.</summary>
public delegate void PeerLeftHandler(uint peerId);

/// <summary>The local player's state was successfully read off the game this pump (mirrors
/// <see cref="CoopAgent.LastReadSucceeded"/> being true this tick).</summary>
public delegate void LocalStateReadHandler(PlayerState state);
