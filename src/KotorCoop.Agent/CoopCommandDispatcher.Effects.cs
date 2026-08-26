using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The peer-effect staging verbs: applyeffect, effects, and the per-tick pump that
/// keeps <see cref="_effectLog"/> current.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Backs the `effects` console verb — see <see cref="PeerEffectLog"/> for why the
    /// timestamps it reports are observation times rather than capture times.</summary>
    private readonly PeerEffectLog _effectLog = new();

    /// <summary>
    /// Runs one of the peer-effect channel's non-damage constructors directly, through the SAME
    /// <c>OdysseyEffects.ApplyEffect</c> a peer's forwarded effect calls on arrival — the staging
    /// instrument for docs/research/effect-sync.md's live list, which had never been run because
    /// no reachable test module has a Force-using hostile and there was previously no way to
    /// apply a non-damage effect on demand at all.
    ///
    /// Staging only, local apply: this never crosses the network by itself. Aimed at a peer's
    /// PHANTOM (the default — a blank <see cref="CoopCommand.Address"/>) it IS live verification,
    /// because <c>PhantomEffectHook</c> captures it landing there exactly as it would a real
    /// enemy attack and the ordinary channel forwards it for real — what that channel does with
    /// it is the actual test. Aimed at an explicit id (including your own player, via `me`) it
    /// is a constructor smoke test only.
    /// </summary>
    private void ApplyEffect(CoopCommand command)
    {
        uint target = 0;
        if (!string.IsNullOrEmpty(command.Address) && !ConsoleCommand.TryParseObjectId(command.Address, out target))
        {
            Reply($"applyeffect: '{command.Address}' is not an object id (try 0x35, or 'me')");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.ApplyEffect,
            Target: target,
            Amount: command.Amount,
            Charges: command.EffectDurationMs,
            MaxCharges: command.EffectMagnitude));
    }

    /// <summary>Folds the shared block's peer-effect rings into <see cref="_effectLog"/> every
    /// tick, so an entry's "seen Ns ago" reflects roughly when it actually happened rather than
    /// only when a player last typed `effects`.</summary>
    private void PumpPeerEffectLog()
    {
        if (_kotor is null)
        {
            return;
        }

        _effectLog.Observe(_kotor.ReadPeerEffectsOut(), _kotor.ReadPeerEffectsIn(), DateTime.UtcNow);
    }

    /// <summary>
    /// Lists the last few peer-effect records this agent has captured, forwarded or applied —
    /// the read-only companion to <see cref="ApplyEffect"/>, so a live pass can read the whole
    /// non-damage effect pipeline from one console line instead of grepping the plugin log for
    /// `effect:` and the agent log for `PublishPeerEffect`/`DrainPeerEffects` separately.
    /// </summary>
    private void ReportEffects()
    {
        if (_kotor is null)
        {
            Reply("effects needs --kotor (there is no game to read)");
            return;
        }

        // One extra pump so a check run immediately after triggering something sees it right
        // away, rather than waiting for the next 150ms tick.
        PumpPeerEffectLog();

        IReadOnlyList<PeerEffectLog.Entry> rows = _effectLog.Recent;
        if (rows.Count == 0)
        {
            Reply("effects: nothing captured or applied yet (is effect_sync=1 set in coop.ini?)");
            return;
        }

        foreach (PeerEffectLog.Entry row in rows)
        {
            Reply(PeerEffectLog.Format(row, DateTime.UtcNow));
        }
    }
}
