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
        if (manager.Current is not { } session
            || PvpPumpRule.SaveAllowed(session.PvpState, session.LocalPeerId))
        {
            return false;
        }

        Reply(PvpText.SavingBlockedLine);
        return true;
    }
}
