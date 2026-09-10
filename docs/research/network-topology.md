# Network topology — peer-to-peer, and a dedicated host (2026-08-14)

Two questions asked together, because they turn out to have the same answer underneath:
*how far is this from real peer-to-peer over the internet*, and *what would it take for a
host to run a session with nobody at the controls*. Both are really questions about one
thing — how much of this mod assumes that the machine which decides what is true is also a
machine with a person playing on it.

Tag key: **VERIFIED** = read from the binary here, from the source in this worktree, or
covered by a passing test · **INFERRED** = deduced, not yet run · **NEEDS-LIVE** = must be
confirmed on the running game.

## Headline

**Peer-to-peer: keep the star relay.** Not because P2P is hard here — the transport is
already destination-per-send, the host is already a multi-endpoint router that survives a
client rebinding its address, and the join handshake already carries both halves of the
identity material a hole punch would need. Keep it because the star is not a networking
choice at all: every channel in this protocol is *shaped* around having exactly one writer
(monotonic merge-upward stores, level-broadcasts, cumulative ledgers), and the measurable
prize for going direct is one hop of latency on the avatar stream, and only at three or
more players. What is genuinely missing for internet play is not peer-to-peer — it is
discovery past a LAN broadcast and NAT traversal for the one connection that already
exists. That work is orthogonal to topology, and §7 says what it costs.

**Dedicated host: option (b), a full game instance whose player character is parked.**
Headless is not available (§8): the engine's own login chain always manufactures a PC, and
the plugin's load handshake will not declare a world present until both a server-side and a
client-side player creature exist. Option (b) is also the *cheap* one, and for a reason
worth stating plainly: almost every authority channel in the agent is gated on
`_player is not null`, so a host with no PC at all does not fail loudly — it quietly stops
publishing enemies, party, roster and progression, and the session looks healthy while
nothing replicates. Parking the PC keeps those gates true and reduces the work to closing
the dozen places where the parked body leaks into the session *as a player*. §9 enumerates
all of them.

---

# PART 1 — how far is this from peer-to-peer?

## 1. The transport is already peer-addressable. One end is not.

`UdpChannel` has no notion of an upstream at all (VERIFIED):

* `src/KotorCoop.Session/UdpChannel.cs:48` — binds `IPAddress.Any`, so it accepts from
  anywhere.
* `src/KotorCoop.Session/UdpChannel.cs:66` — `Send(IPEndPoint destination, ...)`; the
  destination is an argument, per call. There is no connected socket and no default peer.

The **host** is already a router over that channel (VERIFIED):

* `src/KotorCoop.Session/CoopHost.cs:20` — `PeerConnection.EndPoint`, one per peer,
  mutable.
* `src/KotorCoop.Session/CoopHost.cs:1487` — every `StateUpdate` refreshes it from the
  datagram's source, with the comment already in place: *"Endpoints can change (NAT
  rebinding); trust the latest source address."*
* `src/KotorCoop.Session/CoopHost.cs:1423-1425` — a returning `Hello` is matched on the
  client's nonce **first**, endpoint second. The slot is keyed on identity, not address.
* Every broadcast is a loop over `peer.EndPoint` (CoopHost.cs:1000, :1038, :701, :785,
  :819, :1072, :1102, :1144, :1181, :1211, :1240). Adding a peer that is reached some other
  way changes nothing above the `Send`.

The **client** is the single-upstream half, and it is three lines (VERIFIED):

* `src/KotorCoop.Session/CoopClient.cs:20` — `private readonly IPEndPoint _hostEndPoint`,
  assigned once at `:102`, never reassigned.
* Every send in the file targets it (≈30 call sites).
* `src/KotorCoop.Session/CoopClient.cs:1052` — **the line that forecloses direct traffic**:

  ```csharp
  if (!datagram.From.Address.Equals(_hostEndPoint.Address) || datagram.From.Port != _hostEndPoint.Port)
  {
      return;
  }
  ```

  A datagram from any other source is dropped before it is even typed. This is correct
  today (it is the only anti-spoof the client has) and it is exactly the thing a direct
  peer channel would have to become a *set* membership test instead of an equality test.

So the honest answer to "is the transport peer-addressable": **yes, on three sides out of
four**. Nothing is hardcoded in the socket, the packet layer, or the host. The client's
notion of "the one machine I talk to" is a field and a filter.

## 2. Peer identity — what existed, what was missing, what this change adds

There *is* a stable peer identity separate from the endpoint, and it is the reason
reconnection works at all:

* `HelloPacket.ClientNonce` (`src/KotorCoop.Protocol/Packets.cs:4`) — 32 bits, sent on
  every join attempt.
* `CoopHost.PeerConnection.Nonce` (CoopHost.cs:25) and the match at CoopHost.cs:1423 — a
  client that comes back from a *new ephemeral port* reclaims its slot: same peer id, same
  name, its phantom never doubled. Covered by
  `tests/KotorCoop.Tests/PasswordReconnectTests.cs:86`.

What was missing is that the identity did not outlive the object holding it. Before this
change `CoopClient` minted it per instance, so a relaunched agent — or the in-game console's
own `reconnect`, which builds a fresh client through `SessionManager.Connect`
(SessionManager.cs:112, :222) — arrived as a stranger and got a new slot, with the old one
holding that player's phantom, gear and damage totals until it aged out
(`SessionSettings.PeerTimeoutMs`, default 5 s).

**Added here** (see §14 for the exact diff): `src/KotorCoop.Session/PeerIdentity.cs`, a
32-bit value minted once and kept in `coop.id` beside the game, wired through
`SessionSettings.PeerIdentity` and used as the Hello nonce when set. Two tests pin the two
halves of the rule (`tests/KotorCoop.Tests/PeerIdentityTests.cs`):

* a relaunched client with the same identity **reclaims** its slot rather than becoming a
  second player, and the host's peer count stays at one;
* a client that said `Bye` and comes back is a **new** join — an identity must not
  resurrect a player who left.

⚠ It is an identity, not a credential. Anyone can send any nonce; the password remains the
stranger-gate. And the reclaim window is the peer slot's lifetime — the host matches
against its *live* peers only, so an identity cannot reclaim a slot that has already been
retired. Making it durable past that would mean the host remembering departed peers, which
is a `CoopHost` change and deliberately out of scope here.

The **host** has no identity at all beyond its address. Nothing in the handshake names it;
a client knows only where it dialled. Which brings us to:

## 3. The rendezvous material is already on the wire, and already discarded

`WelcomePacket` carries a `SessionId` (`src/KotorCoop.Protocol/Packets.cs:7`), minted at
`CoopHost.cs:61` and sent on every accept (`CoopHost.cs:1460`). `CoopClient.HandleWelcome`
(CoopClient.cs:1273-1295) reads the packet, takes `AssignedPeerId` and `Digest`, and
**never stores the session id**. It is a field that is allocated, minted, transmitted, and
thrown away.

That is the exact value a rendezvous keys on, and it is also the value that would tell a
client "the host I am talking to is not the host I joined" after a restart. Wiring it up is
additive and tiny; it is left for the agent currently working in `CoopClient` rather than
raced here (§14).

## 4. What a UDP hole punch would actually require

Plausible? Yes — the packet layer imposes nothing. The list is short and none of it is
exotic:

| Ingredient | Status here |
| --- | --- |
| A rendezvous both peers can reach | **Absent.** Discovery is a LAN broadcast to `255.255.255.255:27016` (`ServerDiscovery.cs:267`, `:316`), which does not cross a router. There is no internet discovery of any kind. New service, new work. |
| Each peer's *observed* public endpoint | **Absent, but one field away.** The host already sees a client's source address on every datagram; echoing it back (on `Welcome`, or on `Pong`) is the reflexive address a punch needs. |
| Simultaneous open | **Free.** `UdpChannel.Send` takes an arbitrary destination; the socket accepts from anywhere. Only `CoopClient.cs:1052` narrows it, and only in software. |
| A session id both ends agree on | **Present and unused** — §3. |
| An identity that outlives an address | **Present** — §2, now durable. |
| Relay fallback for symmetric NAT | **Absent** — and see below. |

The relay fallback is the interesting one. Roughly a fifth to a third of NAT pairs will not
punch, and the standard answer is a hosted relay: a machine both peers can reach that
forwards their datagrams. For a session of two to eight players where one machine is
already the authority, that machine *is* the natural relay — which is to say the fallback
for peer-to-peer, in this architecture, is the star relay we already have. Building P2P
would mean building a system that degrades into the current one.

Wire format is no obstacle either: `ServerBeacon` (`ServerDiscovery.cs:39`) is already
version-tagged with an optional, append-only tail (v1 → v2 locked/module → v3 portraits,
every reader parsing every version), so it is a serviceable payload for a rendezvous
registration without a new format.

## 5. Which channels could go direct — and which never can

The dividing line is not "reliable vs unreliable". It is **does the host decide, or does
the host merely forward**.

**Could go direct** (the host adds nothing but a hop):

| Channel | Why |
| --- | --- |
| `StateUpdate` (4) client→host, and the per-peer entries of `WorldSnapshot` (5) | Unreliable, superseded every tick, rendered through an interpolation buffer 80 ms in the past (`ProtocolConstants.cs:220`). A lost one costs nothing; a stale one is discarded by the buffer. |
| `Chat` (27) | Pure relay: `CoopHost.HandleChat` (CoopHost.cs:320-337) checks only `_peers.ContainsKey(sender)` and re-enqueues. |
| `Bark` (34) | Same shape: `CoopHost.HandleBark` (CoopHost.cs:739-757). |
| `PhantomDamage` (31), peer→peer half | The host applies it when it is the addressee and otherwise relays verbatim (CoopHost.cs:791-823). It is a cumulative total, so it is already order- and loss-tolerant. |

**Must stay host-authoritative** — every one of these is a decision, not a forward:

| Channel | The decision |
| --- | --- |
| `EnemySnapshot` (17) | Only the host's engine kills anything; clients' copies are pinned at 1 hp. |
| `EnemyDamage` (33) | The host spends each increase as *real engine damage* on its own creature (`PeerDamageLedger`, `CoopAgent.cs:1540`). Two spenders is double damage. |
| `FlagOp/Ack/Snapshot` (6-9), `JournalOp/...` (18-21), `ItemOp/...` (13-16), `ContainerLoot` (29-30) | Version-tripwire stores that merge upward. One writer needs no consensus; two do. |
| `DialogueSnapshot`/`DialogueRequest` (25-26) | The arbiter is a single-token allocator. Two allocators is two conversations, which is the one thing the design refuses. |
| `InteractionClaim`/`InteractionLock` (35-36) | Same, and `shared-state-audit.md` §4-answered records that *latency asymmetry alone* broke fairness here — the host's in-process claim beat every client's networked one until the lock was added. A mesh reintroduces that asymmetry per pair. |
| `PartySnapshot` (24), `PartyRoster` (37) | Levels broadcast by the one instance that owns the companions. |
| `ProgressionSnapshot` (28) / `ProgressionReport` (38) | The host's party table *is* the purse (`CoopAgent.cs:588`). |
| `EncounterTrigger` (32) | The host runs the engine's eligibility test and its engine does the spawn. |

**The size of the prize.** Going direct saves exactly one hop on the avatar stream between
two *clients*. With two players there are no two clients, so the saving at the only
configuration ever tested live is **zero**. At three players it is one hop on one of three
pairs. Against that: a rendezvous service, NAT traversal, a relay fallback, a per-pair
source filter, and a second delivery path for data that already has one.

## 6. Surviving an address change

| Direction | Survives? | Evidence |
| --- | --- | --- |
| Client rebinds (CGNAT, mobile, NAT timeout) | **Yes** | `CoopHost.cs:1487` trusts the latest source; `CoopHost.cs:1423` re-matches Hello on the nonce. The client never notices. |
| **Host** rebinds mid-session | **No** | `CoopClient._hostEndPoint` is readonly (CoopClient.cs:20) and `CoopClient.cs:1052` discards everything from any other source. The client goes silent, drops to `_reconnecting`, and retries the *old* address forever. |

The asymmetry is defensible in practice — the host is the end with a forwarded port and the
more stable address — but it is unguarded, and it is the same fix as the P2P one: turn the
equality test into a membership test over known peers, keyed on the session id (§3) so a
stranger cannot claim to be the host.

## 7. What the ENGINE still contains — and why it does not change the answer

Worth recording because it looks like it should matter. KOTOR is built on NWN's Aurora
engine and the **client/server messaging layer survived into the retail single-player
build** (VERIFIED, from `tools/kotor1_0_3.db` and disassembly of the GOG binary):

| Address | Symbol | Note |
| --- | --- | --- |
| `0x005D4FB0` | `CNetLayer::StartServerMode` | Real function, not a stub: sets `netlayer+0x2003C = 1`, initialises three `CExoString`s, returns 1. Idempotent (early-out on `+0x2003C == 1`). |
| `0x004B1B00` | `CServerExoAppInternal::GetServerInfoFromIniFile` | **The single caller** (`xrefs.py 0x5D4FB0` → one direct call, at `0x004B1B7A`), passing the literal string `"Server"` (`0x745110`) and **`0x10` = 16 max players**. |
| `0x005D5040` | `CNetLayer::StartConnectToSession` | Four callers: `CSWGuiMainMenu::OnModulePicked` (`0x0067BA91`), `CSWGuiSaveLoad::LoadGame` (`0x006CB3A1`, `0x006CB5BC`), `CSWGuiClassSelection::BeginPlayGame` (`0x006DBEDE`). **Starting a game in retail KOTOR is a connect-to-session.** |
| — | `CSWCMessage` / `CSWSMessage` | 146 and 210 catalogued member functions respectively — a full `HandleServerToPlayer*` / `HandlePlayerToServer*` message set, including `HandlePlayerToServerShutDownServer` (`0x00654970`). |

So single-player KOTOR is a client and a server in one process, talking over a net layer
that boots in server mode with a sixteen-player cap. That is the same fact
`MEMORY.md`'s "client and server halves" note records from the other side.

**It does not change the recommendation.** Nothing here says the transport is reachable
from another machine, that the message set is complete for a foreign client, or that the
16-player cap is anything but a leftover constant; and finding out would be an
open-ended reverse-engineering project with a plausible outcome of "BioWare stripped the
socket path and left the dispatch". The mod's whole method — external agent, in-process
plugin, no game file modified — exists because reusing engine surfaces is cheap *when the
surface is proven*. This one is not, and betting the topology on it would be exactly the
kind of speculation `docs/research/` exists to prevent. Recorded as a lead, marked
**INFERRED / unexplored**, not as a plan.

## 8. Verdict, stated plainly

**Keep the star relay. It is the right call**, and the README's existing argument is
correct.

1. The topology is downstream of the authority model, and the authority model is why this
   works. One engine decides; every channel is built for one writer.
2. The prize is one hop, at three or more players, on the one channel that is already
   loss-tolerant.
3. **Nothing forecloses it.** Sends are destination-per-call; the host is already a
   multi-endpoint router that survives rebinding; the peer identity is now durable across a
   process; the session id is already minted and transmitted. The remaining P2P delta is
   *relax one source filter, echo one reflexive address, stand up a rendezvous* — all
   additive.
4. The work that would actually make internet play good is not P2P at all: internet
   discovery (the LAN broadcast is the only discovery there is) and NAT traversal for the
   single host connection. Both are worth doing on their own terms, and both leave the star
   exactly where it is.

---

# PART 2 — a dedicated host

The requirement: *a host can start a session as **dedicated**, but someone is still the
"primary" player — whoever connects first — and controls the other characters.*

## 9. What a dedicated host can actually BE

Three candidate shapes. The evidence picks one.

### (c) Headless — NOT ACHIEVABLE

The engine will not stand a world up without a player character, and the mod's own load
path will not declare one present without two of them:

* `docs/research/world-adoption.md` §1 (VERIFIED): `RunModule` (`0x4B6270`) forks the
  character login on `DoingStartNewModule`. Cleared → `LoadPrimaryPlayer` (`0x4B5F50`)
  reads the PC from `module.ifo`. Set → `LoadCharacterFromIFO(-1)` (`0x561E30`) reads
  `TEMP:pifo`. **Both branches produce a PC**, and when the second finds no file,
  `InitiateModuleForPlayer` (`0x4B3A90`) *manufactures a blank 1/1-hit-point creature at
  the module start point* — measured live on 2026-08-13.
* `src/KotorCoop.Plugin/MenuLoadController.cs:365` — the load is not acknowledged until
  `Odyssey.HasPlayer` (a server-side player creature).
* `src/KotorCoop.Plugin/MenuLoadController.cs:614-627` — presentation additionally waits on
  a **client-side** player creature; `clientCreature == 0` returns false forever.
* There is no headless renderer. The render window has to be activated at all
  (`shared-state-audit.md` §5: `Global::ActivateRenderWindow 0x00401E00`, latch at
  `0x7A3A38`), which is a thing the mod already does deliberately.

### (a) A full instance with nobody at the controls, otherwise untouched — WORKS, BADLY

This is what you get today by launching, loading, hosting and walking away. It does not
crash. It quietly degrades, and §10 is the list. The three that matter most: the host is
published to everyone as a real player standing at (0,0,0); the companions follow a body
nobody is moving; and if the host is left at the **menu** instead of in a world, roughly
every authority channel disables itself silently.

### (b) A full instance whose player character is parked — **RECOMMENDED**

Load the save, keep the PC alive and in the world, and take it *out of the session* rather
than out of the game. The argument is not aesthetic, it is arithmetic: almost every
authority pump in the agent is gated on `_player is not null`, so shape (b) keeps those
gates true and the work reduces to suppressing the parked body's *participation*.

The gates, all VERIFIED in `src/KotorCoop.Memory/KotorGameBridge.cs`:

| Gate | Line | What dies without a PC |
| --- | --- | --- |
| `SupportsEnemySync` = `_pluginBlock != 0 && _player is not null` | `:3145` | The **entire** enemy authority — census, damage-owed, encounter requests, encounter watch (`CoopAgent.cs:1474-1477`). |
| `SupportsPartySync` = `_player is not null` | `:3788` | Party snapshots and the roster level (`CoopAgent.cs:498`, `:536`). |
| `SupportsProgressionSync` = `_player is not null` | `:3821` | Gold and XP, both directions (`CoopAgent.cs:609`). |
| `WriteToPlugin` requires `_player` | `:1211-1220` | **Phantoms.** `block.PlayerCreature` is the host PC's address, and the plugin's comment says why: *"The plugin needs a valid creature to spawn near."* |

That last one is the load-bearing dependency and the reason (b) beats (a)-with-the-PC-deleted:
`src/KotorCoop.Plugin/PhantomController.cs:304-313` hard-returns when `block.PlayerCreature == 0`,
so no PC means **no client bodies exist in the authority's world at all** — and the
encounter-firing path (`EncounterController.cs:254-284`) fires encounters *through the
peer's phantom*. Delete the host's player and clients stop being able to trigger anything.

**NEEDS-LIVE (dedicated-1):** park a loaded PC — `SetMinOneHP`, hold still, hostile factions
neutral — and confirm over ten minutes that (i) nothing kills it, (ii) area scripts that
address `GetFirstPC()` do not wedge, and (iii) module transitions still work when the
parked PC is warped by the travel channel rather than walked. Judge on the PC's hit points
and the module name, never on position (`world-adoption.md` §7).

## 10. The enumeration — every place that assumes the host has a human

Read this as the actual specification for the feature. Grouped by subsystem; every entry is
`file:line` from this worktree, VERIFIED by reading the source.

### 10.1 The role test itself

There is exactly one, and it conflates two different things:

* `src/KotorCoop.Session/ICoopSession.cs:47` — `IsEnemyAuthority`, *"True when this peer
  owns enemy state. Exactly one peer does — the host"*. It is used everywhere as a proxy
  for "I am the host" and therefore, transitively, for "I am the host's player".
* Inside the plugin there is **no** host flag at all. Role is inferred from four
  agent-written block fields, and every one of them means "I am a **client**":
  `block.DamageDealtEnabled` (`SharedBlock.cs`, offset `0x2118`),
  `block.EncounterWatchEnabled` (`0x1DE0`), the `TryGetHostPartyRoster` valid bit
  (`0x3304`), and `block.AdoptWorldLogin` (`0x3320`). On a dedicated host all four are off,
  so **every controller runs in "host" mode, which today means "I am the human player"**.

The design conclusion falls straight out: the codebase needs a *second* boolean —
"does this instance have a human player" — that is not the same as "does this instance own
the world".

### 10.2 The ghost player — a bug that already bites today

Not a dedicated-mode problem; a present-day one, whenever a host is at the menu or between
worlds while clients are connected.

* `src/KotorCoop.Session/CoopHost.cs:962-965` — the host's own entry is added to every
  snapshot **unconditionally**: `new(HostPeerId, _settings.PlayerName, _localState)`.
* `src/KotorCoop.Session/CoopAgent.cs:240` — with no readable PC the pump keeps the last
  state, or `PlayerState.Empty`.
* `src/KotorCoop.Protocol/PlayerState.cs:70,73` — `Empty` is `(0,0,0)`, module `""`, 0 hp —
  and `IsPlausible` is **true** for it (it only checks the floats are finite).
* `src/KotorCoop.Session/CoopAgent.cs:281-282` — the module filter passes anything whose
  module is `""`, so the ghost is written into every client's game: **a motionless copy of
  the host at the origin with zero hit points**.
* `src/KotorCoop.Session/CoopAgent.cs:1120-1129` — `PumpDeath` iterates that ghost, whose
  `IsDowned` is false, so **`allDown` can never become true**: a genuine party wipe is
  suppressed forever and the players lie on the floor with no game over. The host's own
  vote never fires either, for the same reason.
* `src/KotorCoop.Agent/PeerAnnouncer.cs:27-30` — clients announce the ghost as
  *"&lt;name&gt; joined the game"*.

### 10.3 The pump, with no readable local player

* `CoopAgent.cs:240` — read failure is recorded, not fatal; `ReadFailureCount` grows
  unbounded and the dashboard shows **FAILING** forever (`AgentDashboard.cs:357-360`).
* `CoopAgent.cs:180-204` — the ten-second shutdown is driven by *exceptions*. A PC-less
  host never throws, so the agent stays up. (Useful for dedicated mode; note it deliberately.)
* `CoopAgent.cs:264` — `CanWriteRemotes` is true whenever the plugin is loaded
  (`KotorGameBridge.cs:541`), so the heavy pump runs against a worldless game instead of
  short-circuiting.
* `CoopAgent.cs:281` and `:1664` — both module filters are `... && local.Module.Length > 0`,
  so an empty host module **disables filtering entirely**: every peer and every ground item
  from every module is written into the host's game.
* `CoopAgent.cs:389` — the world digest publishes an empty module, and `WorldDigest.Compare`
  treats unknown-vs-known as *not* a mismatch, so the per-module GIT capability check
  silently stops discriminating.
* `CoopAgent.cs:997` (`PumpPause`) — proximity is measured against the host's own position,
  so a parked PC at spawn means the host never mirrors a client's pause.
* `CoopAgent.cs:313` (`PumpSharedCombat`) — fed the unfiltered peer list, so any peer
  fighting anywhere puts the host's engine into combat.

### 10.4 Party and companions — the README limitation, at its root

* `src/KotorCoop.Session/CoopAgent.cs:328` — `LockControlToPlayer()` is **client-only**.
  This is the "Only the host can switch party character" limitation, in one line.
* `CoopAgent.cs:1142` — `SetDeathSuppression(..., suppressSwap: !session.IsEnemyAuthority)`
  — the host stays free to swap *"because the host owns the companions and must stay free
  to control them"*. A dedicated host has nobody to do the controlling.
* `CoopAgent.cs:498-517` (`PumpParty`) → `KotorGameBridge.cs:4011-4020` (`ReadPartyMembers`)
  — the published party is read from the **host's own party table**.
* `CoopAgent.cs:536-567` (`PumpPartyRoster`) — with `SupportsPartySync` false it takes the
  *clearing* branch and never publishes, so `CoopHost._hasPartyRoster` stays false and
  `BroadcastPartyRoster` returns at `CoopHost.cs:1123`: **no client ever receives a roster
  level, permanently.**
* `src/KotorCoop.Plugin/PartyPresenceController.cs:76` — the mirror half only runs on a
  client. On a dedicated host, companions stay engine-AI-driven and **follow the parked
  host PC**.
* `src/KotorCoop.Plugin/PartyRosterController.cs:126-137` — the host branch reconciles
  against nothing, so a primary client's roster changes never reach the authority.
* `src/KotorCoop.Plugin/PartyRosterController.cs:232-238` — companions are spawned at the
  local PC's feet, i.e. at the parked body, not where the players are.
* `src/KotorCoop.Plugin/PortraitController.cs:30,41,92` — the portrait/Tab lock is
  `block.DamageDealtEnabled`-gated, i.e. client-only; *"never on the host."*
* `src/KotorCoop.Session/CoopHost.cs:164,183` — `GetPartyMembers() => []` and
  `TryGetPartyRoster => false`: *"The host IS the roster; it never mirrors anyone's."* Both
  must invert if the primary becomes the companion source.
* Engine levers, already reverse-engineered and available:
  `docs/research/client-control-lock.md` (the control gate is
  `[[[0x7A39FC]+4]+0x270]+0x24`, an **object id**, so writes are crash-proof; funnels at
  `0x005F6B60` and `0x005F7960`) and `docs/research/party-reconciliation.md` (solo mode at
  `partyTable+0x190`; slot↔object-id map at `+0x0C`; `AddMember` refusals at `0x00565620`).

### 10.5 Dialogue

* `src/KotorCoop.Plugin/DialogueGateHook.cs:244` — the gate refuses **only** when a *peer*
  holds the token. A dedicated host is never told a peer holds it (the agent filters the
  local instance out), so on the host `StartDialog` is wide open: any script, encounter or
  `force_dialogue_party` push starts a conversation on the authority.
* `src/KotorCoop.Plugin/DialogueReplyHook.cs:7-8` — replies are recorded from *the local
  player*, and a follower's replies are suppressed. So **a conversation the dedicated
  host's engine opens hangs the whole session**: the host has nobody to pick a reply and
  every client is gagged.
* `src/KotorCoop.Plugin/DialogueSpectateController.cs:237` → `Odyssey.cs:3495` — the
  conversation partner is identified by *skipping the local PC* among the GUI participants.
  With a parked PC this can pick the host's own body as "the NPC".
* `src/KotorCoop.Session/DialogueArbiter.cs` — **already peer-id agnostic and pure**; no
  change needed. Its `lockHolder` input is what is broken:
* `src/KotorCoop.Session/CoopAgent.cs:778` — the host publishes an interaction claim every
  tick with `local.Module == ""`, which sets `CoopHost._interactionModule = ""`
  (CoopHost.cs:624). Then `HolderOf` (CoopHost.cs:226, :1683) can never match a client's
  real-module claim and `Collect` (CoopHost.cs:678) always returns empty — so **the
  client-versus-client interaction lock silently stops working**, and with it the fairness
  fix that `shared-state-audit.md` §4 was written for.
* `src/KotorCoop.Plugin/PartyPresenceController.cs:83`,
  `src/KotorCoop.Plugin/PeerDamageController.cs:213`,
  `src/KotorCoop.Plugin/BarkController.cs:149` — all hold on
  `Odyssey.IsConversationActive`, meaning a conversation the host wandered into stalls
  companion driving, peer-damage application and bark replay **on the authority**.

### 10.6 Camera, spectate, death

* `src/KotorCoop.Plugin/SpectateController.cs:12-14,58-60` — the prose is explicit: on the
  host, KOTOR's own control-swap to a living companion is *"the right experience"*.
* `SpectateController.cs:110-118` — gated on the host's own body being down; with no
  controlled creature it no-ops, so a dedicated host can never spectate anything (correct,
  and the operator surface should be the dashboard instead).
* `SpectateController.cs:149` — `RestoreCameraToControlledCreature` hands the camera back to
  the host-local controlled creature.
* `src/KotorCoop.Plugin/DeathController.cs:111-119` — publishes the *host's own* downed
  state as the session's; `:181-207` — a peer's revive request lands on the parked body.
* `src/KotorCoop.Plugin/SharedBlock.cs:1216-1219` — `OffsetLocalDowned`: the wire has
  exactly **one** "local PC" per instance.

### 10.7 Encounters

* `src/KotorCoop.Session/CoopAgent.cs:1508-1512` — the host sets `SetEncounterWatch(false)`
  on the premise *"the owner's own engine fires its own encounters"*. Nobody walks around
  on a dedicated host, so **100% of encounters must come through the peer-request path**.
* `CoopAgent.cs:1631-1633` — requests are collected by comparing a client's reported module
  against `local.Module`; an empty host module collects **zero** requests.
* `src/KotorCoop.Plugin/EncounterController.cs:254-284` — ✅ **already dedicated-ready**:
  `Fire(encounterId, peerId)` runs entirely off `FindPhantom(peerId)`. The owner-side firing
  path does not need the host's PC at all — only the phantom, which needs
  `block.PlayerCreature` (§9).

### 10.8 Damage

* `src/KotorCoop.Session/PeerDamageLedger.cs` (whole file) and
  `src/KotorCoop.Session/EncounterTriggerStore.cs` (whole file) — ✅ fully peer-driven, no
  PC dependency. Only their call sites are gated.
* `CoopAgent.cs:448-489` (`PumpPhantomDamage`) — the outbound loop walks
  `bridge.PeerInSlot(slot)`, and slots only populate through `WriteToPlugin` — so no host
  PC means no phantoms means enemies never attack any client's avatar and no phantom damage
  is ever billed back.
* `src/KotorCoop.Session/CoopHost.cs:473,497-509` — damage aimed at, and owed by, the host's
  own phantom: dead weight in dedicated mode.
* `src/KotorCoop.Plugin/IncomingDamageController.cs:272-276` — a downed local body silently
  **swallows the whole forwarded debt**. A parked PC that goes down absorbs damage owed to
  nobody.

### 10.9 Progression

* `CoopAgent.cs:609-614` — gated on `SupportsProgressionSync`; a PC-less host returns
  immediately, `_hasProgression` stays false, `BroadcastProgression` returns at
  `CoopHost.cs:1160`: **gold and XP sharing is completely dead, silently, with no log line.**
* `CoopAgent.cs:588-590` — *"The host's party table IS the purse."* Under a parked-PC host
  this stays true and correct: the shared party lives in the host's table either way. Shape
  (b) needs no change here at all.
* `CoopAgent.cs:709-731` (`ObserveLocalGold`) — infers earn/spend from the host's own gold
  level; harmless with a parked PC that never shops, but it should be told to stop.

### 10.10 The beacon and the lobby

* `src/KotorCoop.Session/SessionManager.cs:136` — `(PeerCount ?? 0) + 1`: **the host is
  counted as a player.** A dedicated host over-reports by one in every browser row.
* `SessionManager.cs:138` — the advertised module is read from the host's own PC.
* `SessionManager.cs:141` → `KotorGameBridge.cs:3904-3941` — the advertised portraits start
  with the host's own PC's face.
* `src/KotorCoop.Session/CoopHost.cs:1436` — `if (_peers.Count + 1 >= MaxPeers)`: the lobby
  reserves a slot for the host-as-player, so a dedicated host burns a seat for nobody.
* `src/KotorCoop.Session/CoopHost.cs:905-907` — *"hosting on ... (waiting for players)"*.

### 10.11 Starting a session at all

The second structural blocker: **every path that stands a host session up is a human input
event.**

* `src/KotorCoop.Plugin/ConnectMenuController.cs:59,100-103,112-133` — the Co-op button
  exists only at the main menu and only responds to a mouse click.
* `src/KotorCoop.Plugin/NativeBrowserController.cs:777-794` and `:252-255` — `OnHostClicked`
  → `RequestBrowserHost()` is the **only** writer of `OffsetBrowserHostSerial`
  (`SharedBlock.cs:1112-1114`). There is no agent-writable "host now" field — unlike
  `MenuLoadSerial`, which the agent *does* drive.
* `src/KotorCoop.Plugin/AgentLauncher.cs:140-142` — the plugin always launches the agent
  with `--menu`, i.e. *wait for the player to choose*.
* `src/KotorCoop.Agent/AgentOptions.cs:26-31` — `enum CoopMode { None, Host, Join }`; the
  full option switch at `:213-331` has **no** `--dedicated`, `--headless` or `--server-only`.
* `src/KotorCoop.Agent/Program.cs:665-678` — **`run --host` against a game with no PC exits
  with code 2 today.** `--menu` is the only survivable path, and its own comment is the
  closest thing to a declared headless mode in the codebase.

Two pieces already exist and should be reused rather than rebuilt:

* `src/KotorCoop.Agent/CoopCommandDispatcher.cs:228-241` (`FireMenuAutoLoadIfArmed`) and
  `src/KotorCoop.Agent/MenuAutoLoad.cs:22-27` — *"A HOST loads immediately: there is nobody
  to be admitted by, and it has to be in the world for a client to have anything to join."*
  That is the dedicated-host requirement, already written and already working; it just
  hard-codes save slot 0 (`Program.cs:582-601`).
* `src/KotorCoop.Plugin/MenuLoadController.cs:286-300` — the whole load is driven by
  `block.MenuLoadSerial`, an agent write. ✅ This half is already headless-capable.
* `src/KotorCoop.Agent/CoopCommandDispatcher.cs:237` — `!bridge.TryReadLocalState(out _)`
  is already the codebase's "am I at the menu" probe.

### 10.12 Travel — where the authority has to stand

* `CoopAgent.cs:1218-1222` — `PumpTravel` returns immediately when `IsEnemyAuthority`:
  *"The host never follows anyone."*
* `CoopAgent.cs:1272-1284` — every client follows the peer whose `PeerId == HostPeerId`, and
  bails when that peer's module is empty. **With a PC-less host no client ever follows
  anyone**, and the party scatters across modules with no error.

This is the single most important dedicated-host mechanic. Every id-keyed channel — the
census, encounters, doors, items, loot — is keyed on the host's module, so the authority
*must* be standing where the players are. Mechanism already exists (`TryRequestTravel`, the
console `warp` at `CoopCommandDispatcher.cs:317-335`); only the direction is wrong.

### 10.13 Already graceful — the cheap wins

Worth listing, because they mean the work is smaller than §10 makes it look. These already
no-op cleanly with no local PC and need nothing:

`PluginHost.cs:500` · `DeathController.cs:111,:181` · `EncounterController.cs:158,:163` ·
`IncomingDamageController.cs:174,:263` · `PartyRosterController.cs:233` ·
`PeerDamageController.cs:109` · `PhantomHitHook.cs:355` · `EnemyPresenceController.cs:440` ·
`CombatStateController.cs:86` · `PauseKeyController.cs:48` · `ItemController.cs:135` ·
`TestDriveController.cs:205` · `Odyssey.cs:2111` (minimap) · `Odyssey.cs:1597` (camera
restore) · `MapMarkerController.cs` (whole file — markers come purely from
`block.GetRemote(i).Position`) · `EncounterController.Fire` (`:254-284`) ·
`PeerDamageLedger.cs` and `EncounterTriggerStore.cs` (whole files) · `DialogueArbiter.cs`
(whole file) · `KotorGameBridge.cs:1211`, `:3385`, `:3830`, `:568` ·
`ServerDiscovery.cs:326-343` · `CoopHost.cs:1059,:1089,:1123,:1168,:1226` (every broadcast
skips an empty payload — which is also *why* the failures above are silent).

And the best existing precedent for the whole design, `KotorGameBridge.cs:3300-3309`: the
party roster's **valid bit**, written precisely because *"a game sitting at the main menu
has no party table at all ... an unset bit 31 is reported as 'no reading', never as an
empty roster."* That is the exact distinction — *absent* is not *empty* — that a dedicated
host needs applied to the local player.

**Count: 68 distinct sites**, across 12 subsystems, of which 24 already degrade gracefully.

## 11. The "primary player" abstraction

### What it is

A **session-level role**, held by at most one peer, that owns everything the host's human
owns today. It is deliberately *not* the same thing as the authority:

| Concern | Owner |
| --- | --- |
| What is true (enemies, doors, flags, journal, items, encounters, the purse, the object-id space) | **The authority** — the host's engine. Unchanged, always. |
| The relay (who forwards chat, barks, phantom damage) | **The host.** `CoopClient.LootRecipients = [CoopHost.HostPeerId]` (CoopClient.cs:35) is a *relay* identity and must stay one — keeping it distinct from the primary is the point. |
| Who drives the companions | **The primary player.** |
| Which module the world follows | **The primary player.** |
| Who may drive a conversation the session then spectates | The token owner, as now — but the *host* stops being an eligible claimant. |
| Whose Tab and portraits are unlocked | **The primary player** (everyone else keeps the control lock). |

### Assignment

**Whoever connects first**, exactly as asked, and it needs no new bookkeeping:
`CoopHost._nextPeerId` is monotonic and never recycled (`CoopHost.cs:53`, `:1447`), so
**the live peer with the lowest id is the earliest joiner**, by construction. The rule is
therefore:

```
primary = dedicated ? min(id of live peers)   // 0 when there are none
                    : HostPeerId              // unchanged, today's behaviour exactly
```

The durable identity added in §2 strengthens this for free: a primary that crashes and
relaunches inside the peer-timeout window reclaims its id and therefore its role, instead of
handing it to whoever was second.

### Transfer, and the empty session

* **The primary leaves** (a `Bye`, or the slot times out at `CoopHost.cs:941-960`): the role
  passes to the next-lowest live peer id on the very next tick. No handshake, no vote —
  the same discipline as the interaction lock, which is a *lease* precisely so no message
  loss can strand it.
* **Zero players connected**: the role is **vacant** (`PrimaryPeerId == 0`), and the world
  keeps running. The authority must NOT pause — a paused authority is a stopped authority,
  and `shared-state-audit.md` §6 records how badly the pause reconciler behaves when it is
  asked to hold state nobody set. Instead, on vacancy: hold solo mode
  (`partyTable+0x190 = 1`) so companions stand still, stop publishing a party leader,
  suppress the wipe vote, and freeze the shared purse's spend loop. The first peer to
  arrive becomes primary and everything resumes.
* **Non-dedicated host**: `primary == HostPeerId` always, so every rule above collapses to
  today's behaviour and the change is a no-op for existing sessions. This is the property
  that makes the whole thing shippable incrementally.

### On the wire

A new packet `PrimaryPlayer = 39`, protocol version 24, broadcast by the host every tick as
a **LEVEL** — timestamp, primary peer id, a flags byte carrying "this host is dedicated".
Level, not edge, for the reason every channel that matters here is: a dropped edge strands
the divergence forever, and a peer joining mid-session learns who the primary is without
anyone re-announcing. Sixteen bytes; re-sending the whole truth every tick costs nothing.

⚠ Deliberately **not** a field on `WorldSnapshot`. That packet already overflows at a full
lobby (`CoopHost.cs:978-993`, and `ProtocolConstants.cs:163-173` records the 1200→1400 raise
that a full lobby still did not fit), and adding to it would put the role at the mercy of
the one packet that is already known to be dropped whole.

## 12. Phased plan

Ordered by value over risk. "Cheap" means a day or less and no protocol change; "large"
means it changes who owns a class of objects.

### Phase 0 — the ghost player (CHEAP, and worth doing on its own)

Fixes a bug that bites **today**, with no dedicated mode anywhere in sight: a host at the
menu, or between worlds, publishes a zero-hp player at the origin and thereby makes a party
wipe impossible for everyone (§10.2).

* `CoopHost.cs:962-965` — do not add the host's own snapshot entry when its state is
  unpublished. Needs a "have I ever published" bool beside `_localState`, exactly the shape
  `_hasPartyRoster` (CoopHost.cs:171) and `_hasProgression` (`:191`) already use.
* `CoopAgent.cs:1120-1129` — exclude a peer with an empty module from the wipe vote.
* `CoopAgent.cs:281,1664` — an empty local module should mean *filter everything out*, not
  *filter nothing*.

Tests: extend `DeathPumpTests` and the snapshot tests; no live pass required.
⚠ Touches `CoopHost.cs` — coordinate with the reconnect work.

### Phase 1 — `--dedicated`: stand a host up with nobody there (CHEAP)

* `AgentOptions.cs` — `--dedicated` (and `coop.ini dedicated = 1`), plus a `--load-save`
  that `MenuAutoLoad` honours instead of hard-coding slot 0 (`Program.cs:582-601`).
* `Program.cs:665-678` — a dedicated host must not exit 2 while it is still at the menu;
  take the `--menu` tolerance path, then auto-load, then dispatch `Host`. All three pieces
  exist (`MenuAutoLoad.cs:22-27`, `CoopCommandDispatcher.cs:228-241`,
  `MenuLoadController.cs:286-300`).
* Park the PC: min-one-hp, hold still, and keep it out of the session — suppress its
  snapshot entry, the beacon `+1` (`SessionManager.cs:136`), its portrait
  (`KotorGameBridge.cs:3908`), and the lobby seat reservation (`CoopHost.cs:1436`).
* Log it once, loudly. The failure mode of every gate in §9 is *silence*.

**NEEDS-LIVE (dedicated-1)**, as specified in §9.

### Phase 2 — the primary-player role (CHEAP-MEDIUM)

* `PacketType.PrimaryPlayer = 39`, protocol 24, level-broadcast (§11).
* `CoopHost` — compute and broadcast it; `ICoopSession.PrimaryPeerId` as a default-0
  interface member so nothing existing has to change.
* Re-point travel: `CoopAgent.cs:1272-1284` follows the primary rather than
  `HostPeerId`, and `CoopAgent.cs:1218-1222` stops exempting the authority — **a dedicated
  host follows the primary's module**, through the travel channel that already exists. This
  is the phase that makes a dedicated session actually playable, because everything
  id-keyed is keyed on the host's module (§10.12).
* Fix `CoopAgent.cs:778` so a host with no module does not blank `_interactionModule` and
  silently kill the client-versus-client interaction lock (§10.5).

**NEEDS-LIVE (dedicated-2):** with the host parked in module A and the primary walking into
module B, confirm the host transitions, the census re-establishes in B, and a client's
encounter request in B fires. Judge on the census count and the agent's `*` event lines,
never on the host's on-screen position (`docs/research/transition-window-races.md`).

### Phase 3 — dialogue (CHEAP)

* `DialogueGateHook.cs:244` — on a dedicated host, refuse **every** local conversation
  start, not just the ones a peer holds. The host has no human to pick a reply, and
  `DialogueReplyHook` gags every follower, so a host-side conversation hangs the session
  (§10.5).
* Make `PartyPresenceController.cs:83`, `PeerDamageController.cs:213` and
  `BarkController.cs:149` untrippable by a host-side conversation that can now never start.
* `DialogueArbiter` itself needs **no change** — it is already peer-id agnostic.

### Phase 4 — hand the primary its controls (MEDIUM)

Move the control lock's gate from "am I the authority" to "am I the primary":
`CoopAgent.cs:328` (`LockControlToPlayer`), `CoopAgent.cs:1142` (`suppressSwap`),
`PortraitController.cs:92`. The engine levers are already reverse-engineered and proven
safe — the control gate is an **object id**, and `GetCreatureByGameObjectID` is null-safe
with every caller null-checking, so a wrong write is a no-op frame rather than a crash
(`client-control-lock.md`, VERIFIED).

⚠ This phase is only half a feature on its own: the primary can now select a companion, but
that companion's body on the primary's machine is a **mirror** the host overwrites every
pass. Which is Phase 5.

### Phase 5 — companion authority follows the primary (LARGE)

The `party-reconciliation.md` design, run in the other direction: the primary owns the
companions' bodies and the host mirrors them, instead of the reverse. It is the phase that
actually delivers *"the primary player controls the other characters"*, and it is the
expensive one — it inverts `CoopHost.cs:164,183`, `PumpParty`, `PumpPartyRoster`,
`PartyPresenceController` and `PartyRosterController` all together, and it interacts with
the roster channel that keys on exactly this.

**NEEDS-RE**, before anything is written: the host's engine must be made to run companions
that follow a *phantom* rather than its own PC. `ActionFollowLeader` (action id 61, sole
enqueue site `ExecuteCommandActionFollowLeader 0x0052CBE0`) follows the **party leader**,
not an arbitrary object, and the leader is a slot index in the host's own table
(`partyTable+0xEC`). Establish whether the leader slot can name a body the host does not
control, or whether companions must instead be driven at the primary's phantom the way
`PartyPresenceController` already drives mirrored bodies. Do not begin Phase 5 before this
is answered from the binary.

### Phase 6 — operator surface (CHEAP, do last)

`AgentDashboard.cs:342-371` reads "FAILING / (unknown) / 0 tracked" for a healthy dedicated
host. Give it a dedicated-mode readout: who is primary, which module the authority is
standing in, peer count without the phantom `+1`. And `CoopCommandDispatcher.cs:348-397`
(`where`, `who`) should report the primary rather than a player who is not there.

## 13. NEEDS-LIVE register

| Id | Check | Blocks |
| --- | --- | --- |
| dedicated-1 | A parked PC survives ten minutes in a loaded module: hit points unchanged, no area script wedged, transitions still work when warped rather than walked. Judge on hp + module, never position. | Phase 1 |
| dedicated-2 | A dedicated host follows the primary into another module; the census re-establishes there and a client's encounter request fires. Judge on census count and agent `*` event lines. | Phase 2 |
| dedicated-3 | With zero players connected, the authority keeps simulating and does not pause itself; the first peer to arrive becomes primary and the party resumes. | Phase 2 |
| dedicated-4 | The primary's Tab/portrait swap does not fight the host's mirror — i.e. Phase 4 is genuinely inert until Phase 5. Watch for the ≤1-frame portrait flicker `client-control-lock.md` predicts. | Phase 4 |

## 14. What this document changed in the code

Small, additive, and deliberately separable — another session is reworking reconnect and
fault injection in `UdpChannel.cs` / `CoopHost.cs` / `CoopClient.cs`.

| File | Change |
| --- | --- |
| `src/KotorCoop.Session/PeerIdentity.cs` | **New.** Mint-once-and-persist 32-bit identity in `coop.id`; never returns 0; never throws. |
| `src/KotorCoop.Session/ICoopSession.cs` | **+1 property.** `SessionSettings.PeerIdentity`, default 0 = "mint per session" = today's behaviour. |
| `src/KotorCoop.Session/CoopClient.cs` | **1 line, in the constructor.** `_nonce` comes from the setting when it is non-zero. |
| `src/KotorCoop.Agent/Program.cs` | **+1 line in `SettingsFor`, +1 small helper.** Resolves the install directory and asks `PeerIdentity` for its value. |
| `tests/KotorCoop.Tests/PeerIdentityTests.cs` | **New**, 7 tests: mint/read-back, two installs stay distinct, hand-edited and unusable files, slot reclaim after a relaunch, and a `Bye` still being a farewell. |

Not done here, deliberately, because they are edits inside the file another session owns:

* storing `WelcomePacket.SessionId` on the client (§3);
* relaxing `CoopClient.cs:1052` from an equality test to a known-peer membership test (§6);
* the Phase 0 host-snapshot fix (§12), which is in `CoopHost.Tick`.

## Key addresses and code sites

```
0x005D4FB0  CNetLayer::StartServerMode          one caller, 0x004B1B7A, name "Server", 16 players
0x004B1B00  CServerExoAppInternal::GetServerInfoFromIniFile
0x005D5040  CNetLayer::StartConnectToSession    4 callers: main menu, load game, class selection
0x004B6270  RunModule                           forks the character login (world-adoption.md §1)
0x004B5F50  LoadPrimaryPlayer                   the saved-character branch
0x00561E30  LoadCharacterFromIFO(-1)            the TEMP:pifo branch
0x004B3A90  InitiateModuleForPlayer             manufactures a blank PC when neither supplies one
0x007A39FC  APP_MANAGER_PTR                     [[+4]+0x270]+0x24 = the control gate (object id)
0x005F6B60  CClientExoAppInternal::ChangeCharacter          Tab / portrait funnel
0x00565620  CSWPartyTable::AddMember                        9 slots, 2 companions, the refusals
0x0052CBE0  ExecuteCommandActionFollowLeader                the ONLY enqueue of party follow
```

```
UdpChannel.cs:48,66            bind Any; destination per send
CoopHost.cs:20,1423,1487       per-peer endpoint, nonce match, rebinding tolerance
CoopHost.cs:962                the unconditional host snapshot entry (Phase 0)
CoopClient.cs:20,102,1052      the single upstream, and the filter that forecloses direct peers
Packets.cs:4,7                 HelloPacket.ClientNonce; WelcomePacket.SessionId (unused)
ServerDiscovery.cs:267,316     LAN broadcast — the only discovery there is
CoopAgent.cs:1272-1284         clients follow HostPeerId (Phase 2 re-points this)
CoopAgent.cs:328,1142          the control lock and the swap suppression (Phase 4)
KotorGameBridge.cs:3145,3788,3821,1211   the four `_player is not null` authority gates
KotorGameBridge.cs:3300-3309   the valid-bit precedent: absent is not empty
```

## 15. Internet play, 2026-08-27

Roadmap item 24, the achievable half: this document's own §8 verdict ("keep the star relay")
already established that peer-to-peer is not the missing piece — discovery past the LAN
broadcast and NAT traversal for the one host connection are. This round builds both, without a
relay or hole-punching, and states exactly where that stops.

**VERIFIED** = read from the source in this worktree or covered by a passing test ·
**NEEDS-LIVE** = reasoned and unit-tested against a fake responder, but not yet watched work
against a real consumer router · **INFERRED** = a reasonable deduction, not directly proven.

### What was built

1. **Public address discovery — a minimal STUN client (VERIFIED, unit-tested).**
   `src/KotorCoop.Session/StunClient.cs` is a ~180-line RFC 5389 codec (Binding Request build,
   Binding Success parse, XOR-MAPPED-ADDRESS with a plain MAPPED-ADDRESS fallback for an older
   server) — no authentication, none needed for a bare Binding Request. It is pure and
   socket-free: every test in `StunClientTests.cs` exercises it against hand-built or captured
   bytes with no network at all.

   The wiring that sends a probe and waits for the answer lives in
   `UdpChannel.SendStunRequestAsync` (`src/KotorCoop.Session/UdpChannel.cs`) — and it sends from
   **the same socket the session already listens on**, which matters: a NAT's mapping is keyed
   per local-port, so a STUN query from a *different* socket would report a mapping nobody's
   game traffic actually uses. Because the host's receive loop (`ReceiveAsync`) is the only
   reader of that socket, a STUN reply is intercepted inside that same loop (matched by
   transaction id against a single pending waiter) rather than requiring a second socket — and
   is never handed to the session as a "malformed" game packet.
   `PublicAddressDiscovery.cs` wraps this into a background loop: try each configured server in
   order, keep the first answer, refresh every 5 minutes (`RefreshIntervalMs`). Wired into
   `CoopHost`'s constructor, gated on `SessionSettings.StunServers` being non-empty.

   Config: coop.ini `stun_servers` (default `stun.l.google.com:19302,stun1.l.google.com:19302`
   when the key is absent; **an explicit empty value disables discovery entirely**).

   ⚠ **Privacy.** Every STUN query is a UDP datagram to a third party — by default Google's
   public STUN infrastructure — that gets to see this host's socket talk to it. Nothing else is
   sent (no player name, no session content), but it is real third-party contact a fully
   offline/LAN-only host would not otherwise make. The empty-list opt-out exists specifically so
   a player who cares can turn this off without losing anything else co-op does — hosting still
   works on a LAN with `stun_servers=`.

2. **UPnP IGD port mapping (NEEDS-LIVE — unit-tested against a fake router, never against a
   real one).** `src/KotorCoop.Session/UpnpClient.cs`: SSDP M-SEARCH for
   `WANIPConnection:1`/`WANPPPConnection:1` (target address is a parameter, so a test can point
   it at a loopback fake router instead of joining the real multicast group — multicast in a
   sandboxed CI network is unreliable, and this sidesteps it entirely), a device-description
   fetch (`System.Xml.Linq`, already in the BCL — no new dependency), and hand-rolled SOAP
   `AddPortMapping`/`DeletePortMapping` calls (`System.Net.Http.HttpClient`, also BCL). A SOAP
   *fault* still carries HTTP 200 or 500 depending on the router, so success is judged by the
   response body (absence of a `Fault` element), not the status code alone.

   `UpnpMappingService.cs` owns one host session's mapping lifecycle: discover once, map the
   game's UDP port **and** the world-transfer TCP listener sharing that same port number (see
   `WorldTransfer.cs`'s own doc comment on why they share one — a player who has forwarded a
   port for co-op has already forwarded the save-transfer channel too), renew the lease every 55
   minutes, delete both mappings on host stop/disconnect. Fails soft at every step — a router
   with UPnP off (common; frequently disabled deliberately) or simply absent leaves
   `UpnpMappingStatus.Unavailable`, not an exception.

   Config: coop.ini `upnp` (default on; `upnp = 0` disables it and a player forwards by hand).

   **NAT-PMP (RFC 6886) was evaluated as the fallback and not built.** UPnP IGD covers the large
   majority of consumer routers still running any auto-mapping protocol at all; NAT-PMP is
   principally an Apple/older-router protocol that has been mostly superseded by its own
   successor (PCP) where it still exists, and building a second parallel port-mapping client for
   a shrinking minority of routers was judged not worth the added surface for this round — a
   router with neither is exactly the "forward the port yourself" case the status line already
   names. Left here as a documented gap, not a plan.

3. **Surfaced in-game.** `CoopCommandDispatcher`'s `status` verb now appends the host's public
   address and UPnP verdict; a new `public_address` console verb (aliases `publicaddress`,
   `pubaddr`) prints it on demand and best-effort-copies it to the clipboard on Windows (via the
   `clip` tool that ships with the OS — silently skipped if unavailable). A
   `PublicAddressAnnouncer` (the same diff-and-repost shape `HostSaveAnnouncer`/
   `ConnectionStatusAnnouncer` already use) posts the line once, unprompted, the moment STUN and
   UPnP have an answer — matching the task's example wording: *"hosting on port N - public
   address A.B.C.D:N (UPnP mapped)"* or *"... (UPnP unavailable - forward UDP+TCP N manually)"*.
   The verb is registered in the plugin's console catalogs too (`FuncInterpHook.Verbs`,
   `ConsoleVerbCatalog.Entries`) so the in-game `~` console recognises it instead of handing it
   to the engine's own "function not found". The packed `ConnectionSnapshot` (the on-screen
   indicator's shared-block word) is unchanged — this rides the existing reply/chat-overlay
   path, not a new field.

4. **Join-by-address robustness.** `CoopCommand.TryResolveEndpoint` (`ConsoleCommand.cs`)
   already handled a plain hostname (DNS resolution, `ResolvesAPlainHostname` test) and a
   bracketed IPv6 literal with a port (`[::1]:9100`, via `IPEndPoint.TryParse`'s native support
   for that syntax — `ResolvesABracketedIPv6LiteralWithAPort` test) before this round; both are
   now pinned by tests rather than merely assumed. **IPv6 is untested beyond that parse-level
   check** — nothing downstream (the UDP socket, `IsLikelyLan`'s NAT-forwarding heuristic, UPnP,
   STUN's IPv4-only XOR-MAPPED-ADDRESS decode) has been exercised over IPv6, so treat it as
   INFERRED, not verified.

   `SessionManager.TimeoutConnect` now appends a hint — *"is UDP port N forwarded on the
   host?"* — when a silent connect attempt's endpoint is not loopback/RFC 1918/link-local
   (`IsLikelyLan`), since a genuinely unreachable LAN address is far more likely a typo or a
   host that isn't up, while a silent *internet* address is disproportionately likely to mean
   the port was never forwarded.

5. **NAT keepalive — already sufficient, confirmed by reading rather than by a new write.**
   `CoopClient.cs`'s `PingIntervalMs = 1000` sends a `Ping` to the host at least once a second
   whenever no `StateUpdate` has gone out more recently (and a connected client's `StateUpdate`
   itself goes out at the session tick rate, 30 Hz default) — client→host traffic is therefore
   at least 1 Hz, comfortably under the ~30 s idle timeout most consumer NATs use before
   dropping a UDP mapping. `CoopHost.Tick()` broadcasts a `WorldSnapshotPacket` to every
   connected peer every tick (30 Hz default) unconditionally, so the host→client direction is
   equally covered — no new packet was needed either way. This is a reading, not a build: no
   code changed for this item.

### What a player actually has to do

- **Hosting**: nothing extra, by default. `stun_servers` and `upnp` are both on out of the box;
  `status` or `public_address` shows the address to share once discovery lands (seconds, in the
  ordinary case). If UPnP mapping fails (router has it off, or is absent), the same line says
  "forward UDP+TCP N manually" — port-forward the game port for **both** protocols in the
  router's admin page and share the address STUN found (or a static/DDNS hostname, since
  `connect` accepts one).
- **Joining**: `connect <host[:port]>` exactly as before; a hostname or a bracketed IPv6
  literal with a port both parse. A join that goes silent for the full timeout against a
  non-LAN address now says so ("is UDP port N forwarded on the host?") instead of leaving the
  player to guess.
- **Privacy-conscious hosts**: `stun_servers=` (blank) disables the one piece of this that talks
  to a third party; UPnP never leaves the LAN (it talks only to the router) and needs no
  separate opt-out for that reason, though `upnp = 0` is available for a host who does not want
  their router touched automatically at all.

### NEEDS-LIVE — what only a real router can confirm

Everything above is unit-tested against fake STUN/SSDP/HTTP responders, which proves the wire
format and control flow but not real-router behaviour. Confirmed still open:

| Id | Check |
| --- | --- |
| internet-1 | A STUN query against a real public server (the shipped default, `stun.l.google.com:19302`) returns this host's actual public IP:port, observed correct from outside the host's own network. |
| internet-2 | `AddPortMapping` against a real consumer router (a common brand — not just a compliant test rig) succeeds, and a second machine on the real internet can then reach the mapped port. |
| internet-3 | `DeletePortMapping` on host stop actually clears the entry from the router's own mapping table (checked in the router's admin UI), not just reported success. |
| internet-4 | A join across a real NAT (not loopback) exercises the whole path end to end: STUN-discovered address shared, UPnP-forwarded port, a second player's `connect` reaching it. |
| internet-5 | The 55-minute UPnP lease-renewal loop actually re-asserts the mapping before a router's own lease timer would drop it, over a session long enough to cross that boundary. |
| internet-6 | IPv6 end to end — join, NAT/forwarding heuristics, UPnP (most IGD implementations are IPv4-only in practice) — genuinely untested, not merely unverified. |

### What remains out, by design

No relay and no UDP hole-punching. §4/§5 above already inventoried what a hole punch would
need and found most of the ingredients already present in the protocol (a rendezvous, a
reflexive address, an identity, simultaneous open); what is missing is the rendezvous SERVICE
itself and a relay fallback for the NAT pairs (symmetric NAT paired with a router that also has
UPnP off) that neither STUN nor UPnP can reach on their own. That is a materially bigger
project — a hosted or peer-elected rendezvous point, plus the fallback relay for when punching
fails — and is left for a future round, not attempted here. Roadmap item 24 is updated to
reflect exactly this split.

## 16. First real internet test, and a UDP world-transfer fallback (2026-09-10)

Two independent physical machines, genuinely on the public internet (not loopback, not the same
LAN address — one host on a home connection, one joiner reaching it from outside), for the
first time since §15 shipped.

### internet-2/internet-3/internet-4, partially answered — and the answer is a real gap

The direct STUN+UPnP path FAILED against a real second machine. The host's own status line
reported success throughout — `public address 103.95.113.80:51429 (UPnP mapped)` — but a
joiner's real `connect 103.95.113.80:51429` timed out completely: `no reply from
103.95.113.80:51429 - is UDP port 51429 forwarded on the host?`. This is the SESSION's own UDP
channel failing to arrive at all, not merely the world-transfer TCP channel — a more
fundamental result than §15 anticipated. `AddPortMapping` reporting success is evidently not
sufficient evidence that the mapped address is actually reachable from the wider internet: the
likeliest explanations are CGNAT (this ISP connection's "public" address is itself behind a
carrier-level NAT the mapped router can never see through) or a double-NAT (an ISP modem in
front of the mapped router, itself unmapped). Neither was isolated further this round — doing
so needs access to the ISP-side equipment, out of scope for a two-machine test. Filed as a real,
reproducible negative result rather than left as merely "untested": a host behind either
topology cannot rely on UPnP+STUN alone, whatever the status line says, and needs a genuine
external relay instead (exactly what item 15 above already named as future work).

### The relay path worked — and found a narrower, more specific gap

A playit.gg tunnel (a third-party TCP/UDP relay, unrelated to this project — used here only as
a stand-in for "some relay exists between the two machines") carried the co-op session's own UDP
traffic cleanly: a real handshake, ~36-41ms RTT, party-pool sync observed correctly crossing in
both directions. The base protocol works over a genuine internet path when SOMETHING relays it.

What broke: the joiner's world did not match the host's, so `auto_adopt_world` tried to fetch
the host's world — over `WorldTransfer.cs`'s separate TCP listener, sharing the session's port
number but a distinct protocol. The playit tunnel used for this test carried UDP only, so that
TCP connection attempt got no reply at all (confirmed from the host side: zero trace of an
incoming TCP attempt in the agent log at the moment the fetch failed) and the joiner's session
cycled connect → mismatch detected → fetch times out → disconnect → reconnect, repeatedly.
Switching the client to `auto_adopt_world = 0` (the pre-existing degraded-session fallback)
confirmed the diagnosis directly: with no world-transfer attempted at all, the SAME tunnel held
a stable connection indefinitely.

The practical lesson for a player relaying through a third-party UDP+TCP tunnel service: check
that BOTH protocols are actually enabled on the SAME mapped port, the same requirement §15's own
UPnP mapping already names for a router forwarded by hand — a UDP-only tunnel silently drops
world adoption the same way a UDP-only manual port-forward would.

### `world_transfer_udp_fallback` — a UDP path for world-transfer itself (NEEDS-LIVE)

Rather than only document the gap, a genuine UDP-only alternative now exists for the cases
where nothing will ever carry TCP to the host on that port (a UDP-only tunnel/relay, or a NAT
pairing that happens to punch UDP but not TCP) — coop.ini `world_transfer_udp_fallback`, OFF by
default.

**Why not just reuse the session's own reliable channels.** `WorldTransfer.cs`'s own doc
comment already explains why the original design chose TCP: a save is ~654 KB, three orders of
magnitude past a datagram, and the one payload in this protocol that must arrive exactly once,
complete, before anything else can happen — "reusing the retransmit-until-acked machinery would
mean reinventing ordering, windowing and flow control on top of it for a transfer that happens
once per session." That tradeoff is unchanged and still correct for the common case (this
fallback is OFF by default for exactly that reason); today's finding is that a real, not
hypothetical, network shape now exists where TCP genuinely cannot be used at all, and playing a
permanently degraded session is a worse trade than the added complexity below.

**Shape.** Five new packet types (`PacketType.WorldTransferRequest` through
`WorldTransferComplete`, protocol version 52), multiplexed onto the ordinary session UDP socket
— a second UDP socket cannot share `WorldTransfer.cs`'s TCP listener's port the way TCP and UDP
namespaces coexist for free, so this rides `CoopHost`'s existing receive loop
(`CoopHost.WorldTransferUdp.cs`) instead of opening a second listener. The client half
(`WorldTransferUdp.cs`) is fully standalone, like `WorldTransfer.FetchAsync` already is — by the
time `WorldAdoption` fetches, the joiner's own co-op socket has already been closed
(`SessionManager.RejoinAfterLoadAsync`'s own remarks), so there is no live session channel to
ride on the client side even if one were still open.

The transfer itself: the SAME flat byte layout `WorldTransfer.cs`'s TCP stream already sends
(`WorldTransfer.SerializePayload`/`ParsePayload`, shared by both paths so the file-manifest
parsing and its bounds checks are not duplicated), cut into
`ProtocolConstants.WorldTransferChunkPayloadSize`-byte (1 KB) chunks, capped at
`ProtocolConstants.MaxWorldTransferBytes` (8 MiB — over 12x the ~654 KB a real save measures).
The host blasts every chunk once on the first pass without waiting for feedback, then the
client's periodic `WorldTransferChunkStatus` (a missing-chunk bitmap, small enough at this cap
to always fit one datagram) drives retransmission of whatever is still missing — the same
"republish the level every cycle" idiom this protocol already uses elsewhere (party-pool/store
V2 snapshots), applied to a one-shot bulk transfer instead of continuously-changing state.

**Wiring.** `WorldAdoption.FetchStageAndVerifyAsync` tries a bare TCP connect probe first
(`ProbeTcpReachableAsync`, a 5s timeout — short enough not to eat into a real transfer's own 60s
budget, long enough that a real host answers within one RTT even on a slow link) only when the
fallback is enabled; a probe failure switches to `WorldTransferUdp.FetchAsync` for the whole
fetch, a probe success (or the fallback being off) behaves exactly as before. Threaded through
`SessionManager.RejoinAfterLoadAsync`/`AdoptWithNewCharacterAsync` as an opt-in parameter (default
off, so every existing caller is unaffected) and wired live in `CoopCommandDispatcher` for the
mid-session rejoin path (`RejoinTrigger.SaveMismatch`/`TriggerRejoin` — the exact path today's
live failure went through). The first-join `join_mode=newcharacter` path (`JoinFlow.cs` →
`SessionManager.AdoptWithNewCharacterAsync`) is now threaded through too, added once today's live
retest needed it: the joining machine was mid-chargen on the same UDP-only tunnel, which goes
through this exact path, not the rejoin one.

### NEEDS-LIVE — added by this section

| Id | Check |
| --- | --- |
| internet-7 | ~~`world_transfer_udp_fallback` end to end against a real UDP-only tunnel/NAT path~~ **LIVE-VERIFIED same day**: probe correctly detected TCP unreachable (`the world-transfer port did not answer TCP - falling back to the UDP path`), the full 654,006-byte transfer completed, and the staged world's identity matched the host's (`0x155107BD`) — confirmed independently from both machines' logs, followed by a stable reconnect (peers 1, 36-37ms RTT, 90+ seconds with no drops). |
| internet-8 | The probe's 5s timeout does not misfire on a genuinely slow-but-working TCP path (a real host that is merely slow to answer, rather than unreachable), which would wrongly prefer the UDP path over a TCP one that would have succeeded. |
| internet-9 | A transfer at or near `ProtocolConstants.MaxWorldTransferBytes` (8 MiB) — this round only ever measured a ~654 KB save, two orders of magnitude under the cap. |
| internet-10 | Whether this host's own CGNAT/double-NAT (found this round, internet-2/3/4) is confirmed as one or the other, and whether a manual port-forward on the ISP-side equipment (where reachable at all) resolves it — out of scope for this round's two-machine test. |

### New findings from the same session, unrelated to the UDP fallback (NOT investigated further this round)

Observed after the UDP-fallback join completed and the session was stable: the joiner's own reported appearance showed no clothing (a bare/default body model rather than the equipped starting gear a fresh `join_mode=newcharacter` chargen should have), and the host's screen briefly showed Trask's portrait rather than the joining player's before it corrected itself a short time later. The Endar Spire intro cinematic also played once at the start before the two installs' state converged. The joiner also could not click Trask in-world to start a conversation with him. None of these touch the world-transfer channel this section is about — appearance/equipment sync, party-roster portraits, conversation targeting and movie relay are separate existing subsystems (see their own docs: companion-state-sync.md, party-reconciliation.md, dialogue-sync.md, cinematics.md) — and the phantom-spawn log line's own `portrait 0` field has read `0` at every spawn across this whole project's history, not just this session, so it may be an unrelated pre-existing quirk rather than something this round's changes caused. Deliberately NOT investigated this round (a genuinely new fresh-character join over a real, higher-latency internet path may simply be exercising a timing corner case no earlier LAN-only test hit) — filed here as a reproduction lead for whoever picks this up next, not a diagnosis.

**UPDATE (2026-09-10, same day, later): the "could not click Trask" symptom is root-caused** —
see `docs/research/dialogue-sync.md`'s "Continued and root-caused" entry. It is
`docs/research/control-return.md`'s already-known connect-time control-return race (Round 70,
2026-08-22), reproducing through a trigger this project had not previously tied to it —
an ordinary `join_mode=newcharacter` adoption, reproduced live on a plain LOCAL LAN session with
no internet/UDP fallback involved at all. `GetControlledCreaturePointer()` briefly resolves to
Trask's own object id instead of the joiner's PC; a real click's `ActionInitiateDialog` wrapper
dispatches with that same (wrong) creature as the acting party, so the engine is asked to make
Trask converse with himself and silently does nothing. Self-heals within
`ControlReturnController`'s ~5s budget by default — no fix shipped, root cause and live evidence
only. The wrong-portrait symptom above was separately traced to the same general control-return
family of races by a sibling investigation (`docs/research/party-reconciliation.md`, commit
`2707b62`) — plausibly the same underlying connect-time window producing two visible symptoms,
though this was not independently confirmed this round.

**Click-to-talk specifically was picked up and audited (2026-09-10) — see dialogue-sync.md's
own dated section for the full write-up.** Several plausible causes (a stuck `DialogueGateHook`
lease across the adoption's disconnect/reconnect, the historic click-to-join-annulled-a-frame-
later defect, a Trask object-id mismatch from the GIT-body reuse path) were ruled out by reading
the code; a live pass rebuilt and reinstalled both sides, drove an unattended
`join_mode=newcharacter` join end to end through chargen and adoption (which completed
successfully — Trask resolved to the same id, `0x127`, on both installs, matching earlier
verified passes) but was cut short before the actual click test by both `swkotor.exe` processes
being killed externally (not crashed — no crash-probe record, no Application-Error event) at the
exact moment a concurrent agent (`trask-click-investigation`) overwrote the shared rig lock,
suggesting a duplicate/parallel investigation of the same report. Root cause is still open; the
next diagnostic step (watch `DialogueGateHook.StartAttempts`/`BlockedCount` right after a
post-adoption click) is written up in full in dialogue-sync.md.
