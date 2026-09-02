using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>Session-info and misc verbs: say, status, who, where, goto, netsim, lagstats,
/// and the chat drain.</summary>
public sealed partial class CoopCommandDispatcher
{
    private void SayCommand(CoopCommand command)
    {
        if (manager.Current is { } session && command.Address is { Length: > 0 } message)
        {
            session.SendChat(message);
            Chat($"you: {message}"); // echo locally; peers get it over the wire
        }
        else
        {
            Reply("not connected - nothing to say to");
        }
    }

    private void StatusCommand(CoopCommand command)
    {
        string livenessSuffix = manager.Current is { } statusSession
            ? $" - liveness anomalies: {statusSession.GetStats().LivenessAnomalies}"
            : string.Empty;

        // Roadmap item 24: a hosting player's own status line names how reachable they are
        // from outside the LAN, not just whether the local socket bound.
        string publicSuffix = manager.Current is CoopHost host
            ? $" - {(host.PublicEndPoint is { } ep ? $"public {ep}" : "public address unknown")}"
              + $" ({UpnpStatusLine(host.UpnpStatus, host.ListenEndPoint.Port)})"
            : string.Empty;

        Reply($"{manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}{livenessSuffix}{publicSuffix}");
    }

    /// <summary>
    /// `public_address` (roadmap item 24): prints the host's own internet-reachable address
    /// for sharing with a joiner, and copies it to the clipboard when one is known (Windows
    /// only, best-effort). A no-op verdict on a client, or before STUN has answered — never a
    /// session action, so it is always safe to run.
    /// </summary>
    private void PublicAddressCommand()
    {
        if (manager.Current is not CoopHost host)
        {
            Reply("public_address: not hosting - only a host has a public address to share");
            return;
        }

        string upnp = UpnpStatusLine(host.UpnpStatus, host.ListenEndPoint.Port);

        if (host.PublicEndPoint is not { } endpoint)
        {
            Reply($"public address: unknown yet (STUN still discovering, or stun_servers is empty) - {upnp}");
            return;
        }

        Reply($"public address: {endpoint} - {upnp}");
        TryCopyToClipboard(endpoint.ToString());
    }

    private static string UpnpStatusLine(UpnpMappingStatus status, int port) => status switch
    {
        UpnpMappingStatus.Mapped => "UPnP mapped",
        UpnpMappingStatus.Unavailable => $"UPnP unavailable - forward UDP+TCP {port} manually",
        UpnpMappingStatus.Pending => "UPnP still checking",
        _ => $"UPnP disabled - forward UDP+TCP {port} manually",
    };

    /// <summary>
    /// Best-effort clipboard copy via the `clip` console tool that ships with Windows.
    /// Deliberately silent on any failure (no `clip.exe` on PATH, no console session, a
    /// headless/CI run): this is a convenience on top of the printed line above, never the
    /// only way to learn the address.
    /// </summary>
    private static void TryCopyToClipboard(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "clip",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

            if (process is null)
            {
                return;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(2000);
        }
        catch
        {
            // Best-effort only - see this method's own doc comment.
        }
    }

    private void WhoCommand(CoopCommand command)
    {
        if (manager.Current is not { } who)
        {
            Reply("not connected - no players to list");
            return;
        }

        ListPlayers(who);
    }

    private void WhereCommand(CoopCommand command)
    {
        if (bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0)
        {
            Reply($"module {local.Module}  at {local.Position.X:F1} {local.Position.Y:F1} {local.Position.Z:F1}"
                + $"  hp {local.Health}/{local.MaxHealth}");
        }
        else
        {
            Reply("no player loaded (not in a module yet)");
        }
    }

    /// <summary>
    /// Moves the local player to a peer's last known position.
    ///
    /// The module check is the whole point: peer positions are area coordinates, so the same
    /// numbers name a completely different place in a different module, and a cross-module
    /// <c>goto</c> would drop the player through the floor of the world they are in. Use
    /// <c>warp</c> to follow somebody to another module and <c>goto</c> once you are there.
    /// </summary>
    private void GoTo(string name)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to go to");
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        RemotePeer? found = null;

        // "#n" picks the n-th of the `who` listing, for a name nobody wants to type.
        if (name.StartsWith('#') && int.TryParse(name[1..], out int index)
            && index >= 1 && index <= peers.Count)
        {
            found = peers[index - 1];
        }
        else
        {
            foreach (RemotePeer peer in peers)
            {
                if (peer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = peer;
                    break;
                }
            }
        }

        if (found is not { } target)
        {
            Reply($"no player called '{name}' - run `who` to see who is here");
            return;
        }

        string localModule = bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0
            ? local.Module
            : string.Empty;

        if (localModule.Length > 0 && target.State.Module.Length > 0
            && !localModule.Equals(target.State.Module, StringComparison.OrdinalIgnoreCase))
        {
            Reply($"{target.Name} is in {target.State.Module} and you are in {localModule} - warp there first");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.Teleport,
            X: target.State.Position.X,
            Y: target.State.Position.Y,
            Z: target.State.Position.Z));
    }

    /// <summary>Posts the session roster: the local player first, then each remote peer with
    /// the module they were last seen in. Everything read here already exists per tick —
    /// <see cref="ICoopSession.GetRemotePeers"/> and the session stats.</summary>
    private void ListPlayers(ICoopSession session)
    {
        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        SessionStats stats = session.GetStats();

        string rtt = stats.RoundTripMs > 0 ? $" - rtt {stats.RoundTripMs:F0}ms" : string.Empty;
        Reply($"players ({peers.Count + 1}){rtt}:");

        string localModule = bridge.TryReadLocalState(out PlayerState state) && state.Module.Length > 0
            ? state.Module
            : "?";
        Reply($"  {session.LocalName} (you)  {localModule}");

        foreach (RemotePeer peer in peers)
        {
            string module = peer.State.Module.Length > 0 ? peer.State.Module : "?";
            string flags = peer.State.IsPartyWiped ? "  [party down]"
                : peer.State.IsDowned ? "  [down]"
                : peer.State.IsInDialogue ? "  [talking]"
                : peer.State.IsInCombat ? "  [fighting]"
                : string.Empty;
            Reply($"  {peer.Name}  {module}{flags}");
        }
    }

    /// <summary>
    /// `lagstats` (docs/research/lag-audit.md): what the next live round needs to MEASURE
    /// rather than guess about lag. Three lines, deliberately none of them a single combined
    /// verdict — the whole point is telling "the deliberate interpolation delay" apart from
    /// "the real send/recv cadence this round is actually achieving" apart from "how much of
    /// the agent's own budget the pump is spending", because those three can diverge
    /// independently and conflating them is how a live round ends up guessing.
    /// </summary>
    private void LagStats(SessionManager manager)
    {
        if (manager.Current is not { } session)
        {
            Reply("lagstats: not connected - nothing to measure yet");
            return;
        }

        SessionStats stats = session.GetStats();

        Reply($"lagstats: interpolation delay {stats.PhantomInterpolationDelayMs} ms "
            + "(deliberate - trades a fixed buffer for smoothness, not a stall)");

        Reply($"  send intervals (ms, oldest first): {FormatIntervals(stats.RecentSendIntervalsMs)}");
        Reply($"  recv intervals (ms, oldest first): {FormatIntervals(stats.RecentRecvIntervalsMs)}");

        // Host only (see SessionStats.WorldSnapshotPagesSent) - a client never sends this
        // packet, so 0 there just means "not the host", not "nothing is happening".
        if (stats.WorldSnapshotPagesSent > 0)
        {
            Reply($"  world snapshot pages sent: {stats.WorldSnapshotPagesSent} "
                + "(more than one per tick means the lobby is paging, not dropping)");
        }

        if (manager.Agent is { } agent)
        {
            Reply($"  agent pump: {agent.PumpAverageMs:F1} ms average, {agent.PumpPeakMs:F1} ms peak "
                + $"(budget {agent.PumpBudgetMs:F0} ms)");
        }
        else
        {
            Reply("  agent pump: no reading yet");
        }
    }

    private static string FormatIntervals(IReadOnlyList<long> intervals) =>
        intervals.Count == 0 ? "none yet" : string.Join(", ", intervals);

    /// <summary>Surfaces chat received from peers on the in-game console and stdout.</summary>
    private void DrainChat()
    {
        if (manager.Current is not { } session)
        {
            return;
        }

        foreach (ChatMessage message in session.DrainChat())
        {
            Chat($"{message.Name}: {message.Text}");
        }
    }

    /// <summary>
    /// Arms (or clears) the transport fault injector on the live session, so a disconnect can
    /// be produced on the rig without unplugging anything and without killing a process — a
    /// killed process is a clean socket close, which is the one failure shape that was already
    /// known to recover. See <see cref="NetworkSim"/>.
    ///
    /// Deliberately applied to the LIVE session only: a fault outlives no session, so
    /// disconnecting is always a way out of one you set by mistake.
    /// </summary>
    private void ApplyNetSim(string sub, string args)
    {
        if (manager.Current?.Network is not { } sim)
        {
            Reply("netsim: not connected - nothing to break");
            return;
        }

        string[] parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        switch (sub)
        {
            case "off":
                sim.Clear();
                break;

            case "loss":
                sim.SetLoss(double.Parse(parts[0]));
                break;

            case "latency":
                sim.SetLatency(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0);
                break;

            case "blackout":
                NetSimDirection direction = parts.Length > 1
                    ? parts[1].ToLowerInvariant() switch
                    {
                        "in" => NetSimDirection.Inbound,
                        "out" => NetSimDirection.Outbound,
                        _ => NetSimDirection.Both,
                    }
                    : NetSimDirection.Both;

                sim.StartBlackout((int)(double.Parse(parts[0]) * 1000), direction);
                break;
        }

        Reply(sim.Describe());
    }
}
