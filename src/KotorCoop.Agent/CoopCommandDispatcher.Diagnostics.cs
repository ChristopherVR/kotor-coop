using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The enemy-census diagnostics: resolve/movenear/firescript (straight to the plugin, answered
/// on the game thread in one frame), slay (which needs the census to pick "the nearest
/// hostile"), and census/DescribeId itself — the id-by-id cross-check between the engine's
/// object array, a raw memory scan, and the census a peer was sent.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    private void ResolveObjectCommand(CoopCommand command)
    {
        // Straight to the plugin: this question can only be answered on the game thread, and
        // the value of the answer is that it is taken in ONE frame.
        if (string.Equals(command.Address, "party", StringComparison.OrdinalIgnoreCase))
        {
            // The plugin's CheatTargetParty: the whole CSWParty member array in one frame.
            SendCheat(new CheatRequest(CheatKinds.Resolve, Target: uint.MaxValue - 1));
        }
        else if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint resolveId))
        {
            SendCheat(new CheatRequest(CheatKinds.Resolve, Target: resolveId));
        }
        else
        {
            Reply($"resolve: '{command.Address}' is not an object id (try 0x35)");
        }
    }

    private void MoveNearCommand(CoopCommand command)
    {
        if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint moveId))
        {
            // X/Y/Z cross as-is (0,0,0 when the console line named no explicit destination) -
            // CheatController.MoveNear tells "explicit" from "default near the player" apart by
            // the same origin sentinel Teleport/WalkTo already use.
            SendCheat(new CheatRequest(CheatKinds.MoveNear, Target: moveId, X: command.X, Y: command.Y, Z: command.Z));
        }
        else
        {
            Reply($"movenear: '{command.Address}' is not an object id (try 0x127)");
        }
    }

    private void FireScriptCommand(CoopCommand command)
    {
        if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint scriptId))
        {
            SendCheat(new CheatRequest(CheatKinds.FireScript, Target: scriptId, Amount: command.Amount));
        }
        else
        {
            Reply($"firescript: '{command.Address}' is not an object id (try 0x35)");
        }
    }

    /// <summary>
    /// <c>faction &lt;id|me&gt; [vsId|me]</c> — read-only, no engine write. See
    /// <c>CoopCommandKind.Faction</c> and docs/research/combat-participation.md §10.
    /// <c>command.Address</c> is "0" for the local player (the parser's own convention, matching
    /// <c>grantxp</c>); <c>command.Amount</c> carries the vsId (0 = local player too).
    /// </summary>
    private void FactionCommand(CoopCommand command)
    {
        if (!ConsoleCommand.TryParseObjectId(command.Address ?? "0", out uint targetId))
        {
            Reply($"faction: '{command.Address}' is not an object id (try 0x14, or 'me')");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.Faction, Target: targetId, Amount: command.Amount));
    }

    /// <summary>
    /// Resolves what <c>slay</c> should aim at and hands it over. A bare <c>slay</c> means
    /// "the nearest hostile", which only the agent can answer — it is the side that runs the
    /// enemy census — so the nearest id is picked here and the plugin is given a number
    /// rather than a policy.
    ///
    /// The authority decision itself is deliberately NOT made here: whether this instance
    /// kills the creature or forwards the kill to its owner depends on state only the plugin
    /// can see (the phantom table, the party roster, the mirror's own pin), and splitting
    /// that rule across two processes is how it would drift.
    /// </summary>
    private void Slay(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            if (!TryFindNearestEnemy(out uint nearest, out float distance))
            {
                // Names the census, because "none in range" and "the owner has not published
                // any here" look identical from the console and mean different things.
                Reply(
                    manager.Current is { IsEnemyAuthority: false }
                        ? "no living enemy in the owner's census here - name one: slay <tag|id>"
                        : "no living enemy in range - name one: slay <tag|id>");
                return;
            }

            // Names the census it came from: an id is only meaningful in one id space, and
            // when a forwarded kill goes missing the FIRST question is which list chose it.
            Reply(
                $"slaying the nearest enemy 0x{nearest:X} ({distance:F1}m away) "
                + $"[from {_lastCensusSource}]");
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: nearest));
            return;
        }

        if (ConsoleCommand.TryParseObjectId(token, out uint objectId))
        {
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: objectId));
            return;
        }

        // A tag. Refuse one that names a PLAYER before it reaches the engine: the plugin
        // rejects a phantom by id anyway, but a peer's name is what a person actually types
        // and the reason is clearer said here.
        if (manager.Current is { } session)
        {
            foreach (RemotePeer peer in session.GetRemotePeers())
            {
                if (peer.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    Reply($"{peer.Name} is a player - their character is theirs, not yours to kill");
                    return;
                }
            }
        }

        SendCheat(new CheatRequest(CheatKinds.Slay, Text: token));
    }

    /// <summary>Which list the last nearest-enemy sweep read, for the reply line. An id
    /// is only meaningful in one id space, so the source belongs next to the id.</summary>
    private string _lastCensusSource = "none";

    /// <summary>
    /// The nearest living enemy to the local player.
    ///
    /// ⚠ Which census answers this depends on who owns the fight, and getting it wrong is
    /// silent. On a MIRRORING instance the local engine's creature list is in OUR id space,
    /// and those ids mean nothing to the owner — the kill is forwarded as a debt keyed by
    /// object id, so an id the owner cannot resolve is a debt it holds for ever (measured
    /// live: a client's list held creatures the host had never heard of at all).
    ///
    /// So a mirroring instance sweeps the OWNER's published set instead: those ids are the
    /// only ones a forwarded kill can be keyed by, and the positions travel with them.
    /// </summary>
    private bool TryFindNearestEnemy(out uint objectId, out float distance)
    {
        objectId = 0;
        distance = 0f;

        if (!bridge.TryReadLocalState(out PlayerState local))
        {
            return false;
        }

        IReadOnlyList<EnemyState> census;

        if (manager.Current is { IsEnemyAuthority: false } mirror)
        {
            census = mirror.GetEnemies(local.Module);
            _lastCensusSource = $"owner census, {census.Count} in '{local.Module}'";
        }
        else
        {
            census = bridge.ReadEnemies([]);
            _lastCensusSource = $"local engine, {census.Count}";
        }

        float best = float.MaxValue;
        foreach (EnemyState enemy in census)
        {
            if (enemy.Dead || enemy.Plot || enemy.CurrentHealth == 0)
            {
                continue;
            }

            float dx = enemy.Position.X - local.Position.X;
            float dy = enemy.Position.Y - local.Position.Y;
            float dz = enemy.Position.Z - local.Position.Z;
            float d = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            if (d < best)
            {
                best = d;
                objectId = enemy.ObjectId;
            }
        }

        distance = best;
        return objectId != 0;
    }

    /// <summary>
    /// Dumps the enemy census beside the engine's own verdict on every id in it.
    ///
    /// ⚠ THIS EXISTS BECAUSE THE ARGUMENT COULD NOT BE SETTLED FROM OUTSIDE. The failure it
    /// serves is "an id in the census names nothing its own publisher can resolve", and the
    /// evidence for it came from three places that disagreed — a peer's console reply, the
    /// other instance's plugin log, and a `creatures.ps1` scan — with no way to ask either
    /// side what it thought it was publishing. Every branch of that question is answered here,
    /// on demand, from the side that knows:
    ///
    /// <list type="bullet">
    /// <item>the liveness gate's state, including the two cases in which it checks NOTHING;</item>
    /// <item>how many published ids this engine's own object array holds, and which do not;</item>
    /// <item>for a disputed id, whether it is in the raw memory scan, in the object array, and
    /// in the census — "in the scan but not the array" is a ghost the gate should have caught,
    /// while "in neither" means the id never came from this instance at all.</item>
    /// </list>
    /// </summary>
    private void Census(string? token)
    {
        if (_kotor is null)
        {
            Reply("census: only the KOTOR bridge can answer that");
            return;
        }

        bool mirror = manager.Current is { IsEnemyAuthority: false };
        string module = bridge.TryReadLocalState(out PlayerState here) ? here.Module : string.Empty;

        // Always first, and always printed: this is the line whose ABSENCE from a log was
        // ambiguous between "no ghosts", "never ran" and "never wired".
        Reply($"census: {_kotor.LastCensusGate.Describe()}");

        // The owner is asked what it WOULD publish right now (the same call the pump makes,
        // gate and all); a mirror is asked what it was sent.
        IReadOnlyList<EnemyState> census = mirror
            ? manager.Current?.GetEnemies(module) ?? []
            : bridge.ReadEnemies([]);

        string source = mirror ? "the owner's census" : "my own census";

        List<uint> unresolved = [];
        foreach (EnemyState enemy in census)
        {
            if (!_kotor.TryResolveObjectHere(enemy.ObjectId, out _))
            {
                unresolved.Add(enemy.ObjectId);
            }
        }

        // ⚠ Short lines, deliberately. The console answer channel is 96 bytes and truncates
        // without a mark; the previous round's answers were all cut off mid-sentence.
        Reply(
            $"census: {(mirror ? "MIRROR" : "OWNER")} {census.Count} id(s) '{module}': "
            + $"{census.Count - unresolved.Count} resolve here, {unresolved.Count} do not");

        if (unresolved.Count > 0)
        {
            Reply(mirror
                ? "census: a mirror need not resolve them all (stand-ins carry local ids)"
                : "census: as the OWNER, every id that does not resolve here is the defect");
        }

        if (!mirror && _kotor.CensusPublishRejected.Count > 0)
        {
            Reply(
                "census: publish refused "
                + string.Join(" ", _kotor.CensusPublishRejected.Take(4).Select(id => $"0x{id:X}")));
        }

        foreach (uint id in unresolved.Take(CensusDetailLines))
        {
            DescribeId(id, census);
        }

        if (unresolved.Count > CensusDetailLines)
        {
            Reply($"census: ...and {unresolved.Count - CensusDetailLines} more");
        }

        if (!string.IsNullOrWhiteSpace(token) && ConsoleCommand.TryParseObjectId(token, out uint asked))
        {
            DescribeId(asked, census);
        }
    }

    /// <summary>How many per-id lines a dump prints before summarising. A console overlay is
    /// a few lines tall; a log can take more, but an unbounded dump helps nobody.</summary>
    private const int CensusDetailLines = 8;

    /// <summary>
    /// One id, checked against all three lists at once: the engine's object array (the
    /// authority), the raw memory scan (what a vtable sweep sees, ghosts included), and the
    /// census itself. Their DISAGREEMENT is the finding, so they are reported together — but
    /// across TWO short lines, because one line of all three does not survive the 96-byte
    /// console answer channel.
    /// </summary>
    private void DescribeId(uint objectId, IReadOnlyList<EnemyState> census)
    {
        string array = _kotor!.TryResolveObjectHere(objectId, out nint address)
            ? $"array 0x{address:X}"
            : "array NOT HELD";

        CreatureRef? scanned = null;
        foreach (CreatureRef creature in _kotor.UnconfirmedCreatureScan())
        {
            if (creature.ObjectId == objectId)
            {
                scanned = creature;
                break;
            }
        }

        string scan = scanned is null
            ? "scan absent"
            : $"scan 0x{scanned.Address:X} '{Shorten(scanned.Tag)}'";

        Reply($"census 0x{objectId:X}: {array} | {scan}");

        foreach (EnemyState enemy in census)
        {
            if (enemy.ObjectId == objectId)
            {
                Reply(
                    $"census 0x{objectId:X}: listed hp {enemy.CurrentHealth}/{enemy.MaxHealth}"
                    + $" at {enemy.Position.X:F1},{enemy.Position.Y:F1}"
                    + (enemy.Dead ? " dead" : string.Empty));
                return;
            }
        }

        Reply($"census 0x{objectId:X}: not listed in this census");
    }

    /// <summary>Keeps a tag from pushing a diagnostic line past the answer channel's 96
    /// bytes. Tags are resref-sized; a long one is still recognisable from its head.</summary>
    private static string Shorten(string tag) =>
        tag.Length == 0 ? "untagged" : tag.Length <= 24 ? tag : tag[..24];
}
