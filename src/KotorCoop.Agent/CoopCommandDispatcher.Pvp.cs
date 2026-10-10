using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>Duels (docs/research/pvp-plan.md): <c>duel &lt;name&gt;</c>, <c>duel off|on</c>,
/// <c>accept</c>, <c>decline</c>, <c>yield</c>, and the plain-language lines the host's round
/// sends back. The round itself lives on the host (<c>PvpDuelRound</c>); this only asks and
/// reports.</summary>
public sealed partial class CoopCommandDispatcher
{
    private void DuelCommand(CoopCommand command)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to duel");
            return;
        }

        string argument = command.Address?.Trim() ?? string.Empty;

        // The opt-out works whether or not the host allows duels: it is a statement about this
        // player, and costs nothing to keep for when they do.
        if (argument.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            session.RequestPvp(PvpOp.OptOut);
            Reply(PvpText.OptedOutLine);
            return;
        }

        if (argument.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            session.RequestPvp(PvpOp.OptIn);
            Reply(PvpText.OptedInLine);
            return;
        }

        if (!session.PvpEnabled)
        {
            Reply(PvpText.HostOffLine);
            return;
        }

        if (FindPeer(session.GetRemotePeers(), argument) is not { } target)
        {
            Reply(PvpText.Format(PvpNoticeCode.NoSuchPlayer, argument));
            return;
        }

        session.RequestPvp(PvpOp.Challenge, target.PeerId);
    }

    /// <summary><c>arena &lt;name&gt;</c>: the same challenge as a duel, fought in the arena module.
    /// The host does the moving; this only asks.</summary>
    private void ArenaCommand(CoopCommand command)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to duel");
            return;
        }

        if (!session.PvpEnabled)
        {
            Reply(PvpText.HostOffLine);
            return;
        }

        if (!session.PvpArenaEnabled)
        {
            Reply(PvpText.Format(PvpNoticeCode.ArenaNotEnabled, string.Empty));
            return;
        }

        string argument = command.Address?.Trim() ?? string.Empty;
        if (FindPeer(session.GetRemotePeers(), argument) is not { } target)
        {
            Reply(PvpText.Format(PvpNoticeCode.NoSuchPlayer, argument));
            return;
        }

        session.RequestPvp(PvpOp.ChallengeArena, target.PeerId);
    }

    /// <summary><c>chungus</c>: asks the host to take everyone to the arena for the boss fight.
    /// The host's answer (who is not ready, or "moving everyone now") comes back as a line.</summary>
    private void ChungusCommand(CoopCommand command)
    {
        if (command.Address is { Length: > 0 } piece)
        {
            ChungusStaging(piece, command);
            return;
        }

        if (manager.Current is not { } session)
        {
            Reply("not connected - start or join a game first, then call for Chungus");
            return;
        }

        if (!session.BossReady)
        {
            Reply(PvpText.BossNotInstalledLine);
            return;
        }

        session.RequestPvp(PvpOp.Chungus);
    }

    /// <summary>The staging forms: one piece of the boss, on this game only, no session needed.</summary>
    private void ChungusStaging(string piece, CoopCommand command)
    {
        switch (piece)
        {
            case "spawn":
                if (!bridge.TryReadLocalState(out PlayerState local))
                {
                    Reply("no player loaded - load a save first");
                    return;
                }

                SendCheat(new CheatRequest(
                    CheatKinds.Chungus, Amount: ChungusCheat.OpSpawn,
                    X: local.Position.X + 4f, Y: local.Position.Y, Z: local.Position.Z));
                break;

            case "remove":
                SendCheat(new CheatRequest(CheatKinds.Chungus, Amount: ChungusCheat.OpDespawn));
                break;

            case "anim":
                SendCheat(new CheatRequest(CheatKinds.Chungus, Target: (uint)command.Amount, Amount: ChungusCheat.OpAnimation));
                break;

            case "sound":
                SendCheat(new CheatRequest(CheatKinds.Chungus, Amount: ChungusCheat.OpSound, Text: command.Password ?? string.Empty));
                break;

            case "power":
            {
                BossCue cue = new((BossPower)command.Amount, BossPowers.Strength((BossPower)command.Amount), 0);
                if (!cue.IsKnown)
                {
                    Reply("chungus power takes a number from 1 to 10");
                    return;
                }

                uint word = ChungusCheat.Word(cue, hitsLocalPlayer: true, isHost: true);
                SendCheat(new CheatRequest(CheatKinds.Chungus, Target: word, Amount: ChungusCheat.OpWindUp));
                SendCheat(new CheatRequest(CheatKinds.Chungus, Target: word, Amount: ChungusCheat.OpLand));
                break;
            }
        }
    }

    private void DuelAnswerCommand(PvpOp op)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to duel");
            return;
        }

        session.RequestPvp(op);
    }

    /// <summary>Puts the round's messages for this player on screen, through the same trio every
    /// reply uses. Not gated on <c>status_toasts</c>: a challenge that cannot be seen cannot be
    /// answered.</summary>
    private void AnnouncePvp()
    {
        if (manager.Current is not { } session)
        {
            return;
        }

        IReadOnlyList<PvpNotice> notices = session.DrainPvpNotices();
        if (notices.Count == 0)
        {
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        foreach (PvpNotice notice in notices)
        {
            if (notice.Code == PvpNoticeCode.BossPower)
            {
                BossCue cue = BossCue.Unpack(notice.Other);
                string target = peers.FirstOrDefault(p => p.PeerId == cue.TargetPeer)?.Name ?? "someone";
                string said = PvpText.FormatBossPower(cue, target, aimedAtReader: cue.TargetPeer == session.LocalPeerId);
                if (said.Length > 0)
                {
                    Reply(said);
                }

                continue;
            }

            string name = notice.Other == session.LocalPeerId
                ? session.LocalName
                : peers.FirstOrDefault(p => p.PeerId == notice.Other)?.Name ?? "Someone";
            string line = PvpText.Format(notice.Code, name, aboutSelf: notice.Other == session.LocalPeerId);
            if (line.Length > 0)
            {
                Reply(line);
            }
        }
    }

    /// <summary>True (after saying why) when a save must not happen: the 1 hp pin of a duel
    /// rides a save as a persistent creature field.</summary>
    private bool RefuseSaveDuringDuel()
    {
        if (manager.Current is not { } session)
        {
            return false;
        }

        // Standing in the arena is enough, whatever the round says: the arena module must never
        // be baked into a campaign save (a lost host packet could leave the round idle early).
        bool inArena = bridge.TryReadLocalState(out PlayerState local)
            && string.Equals(local.Module, PvpDuelRound.ArenaModule, StringComparison.OrdinalIgnoreCase);
        if (!inArena && PvpPumpRule.SaveAllowed(session.PvpState, session.LocalPeerId))
        {
            return false;
        }

        Reply(inArena || session.PvpState.ArenaActive ? PvpText.SavingBlockedInArenaLine : PvpText.SavingBlockedLine);
        return true;
    }
}
