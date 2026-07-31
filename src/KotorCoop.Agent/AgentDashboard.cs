using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Console readout for the run command, plus the demo drop key. Reads the live agent from
/// the <see cref="SessionManager"/> each tick rather than holding a fixed one, so the view
/// follows connect/disconnect/reconnect: between sessions there is no agent and the readout
/// says so instead of rendering stale peers.
/// </summary>
public sealed class AgentDashboard(SessionManager manager, IGameBridge bridge, string? dropFile = null)
{
    /// <summary>
    /// What the D key drops, cycling. KOTOR 1 has no native drop-item UI, so the demo
    /// conjures well-known blueprints at the player's feet instead.
    /// </summary>
    private static readonly string[] DropCycle =
    [
        "g_w_lghtsbr01",   // blue lightsaber
        "g_w_blstrpstl001", // blaster pistol
        "g_i_medeqpmnt01", // medpac
    ];

    private int _dropIndex;
    private string _lastDropNote = string.Empty;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool canPosition = !Console.IsOutputRedirected;
        bool canReadKeys = !Console.IsInputRedirected;

        if (canPosition)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(200));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (canReadKeys)
                {
                    while (Console.KeyAvailable)
                    {
                        HandleKey(Console.ReadKey(intercept: true).Key);
                    }
                }

                CheckDropFile();

                if (canPosition)
                {
                    Console.SetCursorPosition(0, 0);
                }

                Render();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (canPosition)
            {
                Console.CursorVisible = true;
            }
        }
    }

    /// <summary>
    /// Honours a drop request left in <see cref="dropFile"/>: its contents name the
    /// blueprint, or an empty file takes the next entry from the cycle. The file is
    /// deleted first, so a failed drop cannot retrigger on every subsequent tick.
    /// </summary>
    private void CheckDropFile()
    {
        if (dropFile is null || !File.Exists(dropFile))
        {
            return;
        }

        string requested;

        try
        {
            requested = File.ReadAllText(dropFile).Trim();
            File.Delete(dropFile);
        }
        catch (IOException)
        {
            // Half-written by the requester; try again on the next tick.
            return;
        }

        if (requested.Equals("pickup", StringComparison.OrdinalIgnoreCase))
        {
            PickUpNearest();
            return;
        }

        Drop(requested.Length > 0 ? requested : null);
    }

    private void HandleKey(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.D:
                Drop(null);
                break;

            case ConsoleKey.P:
                PickUpNearest();
                break;
        }
    }

    /// <summary>
    /// Has the local player pick up the first shared ground item, through the engine's own
    /// pick-up action. The point is to exercise the real path: the item leaves the floor
    /// here, this instance notices, and every other instance should destroy its copy.
    /// </summary>
    private void PickUpNearest()
    {
        CoopAgent? agent = manager.Agent;
        if (agent is null)
        {
            _lastDropNote = "pickup: not connected";
            return;
        }

        if (agent.Bridge is not KotorGameBridge kotor)
        {
            _lastDropNote = "pickup needs --kotor";
            return;
        }

        List<GroundItem> items = agent.Session.Items.Snapshot();
        if (items.Count == 0)
        {
            _lastDropNote = "pickup: nothing on the ground";
            return;
        }

        GroundItem item = items[0];
        _lastDropNote = kotor.TryRequestPickUp(item.ItemId)
            ? $"picking up {item.Resref} (0x{item.ItemId:X})"
            : $"pickup refused: 0x{item.ItemId:X} is not on this instance's floor";
    }

    private void Drop(string? requestedResref)
    {
        CoopAgent? agent = manager.Agent;
        if (agent is null)
        {
            _lastDropNote = "drop: not connected";
            return;
        }

        PlayerState local = agent.LastLocalState;
        if (!local.IsPlausible || !agent.LastReadSucceeded)
        {
            _lastDropNote = "drop refused: local player not readable";
            return;
        }

        string resref = requestedResref ?? DropCycle[_dropIndex % DropCycle.Length];

        // In front of the player, along their facing, rather than at a fixed world offset:
        // the camera sits behind the character, so a world-axis offset drops the item
        // off-screen as often as not. Facing is the angle of the orientation vector, so
        // forward is (cos, sin) of it.
        const float DropDistance = 1.6f;
        Vector3f at = new(
            local.Position.X + (MathF.Cos(local.Facing) * DropDistance),
            local.Position.Y + (MathF.Sin(local.Facing) * DropDistance),
            local.Position.Z);

        uint itemId = agent.Session.DropItem(resref, local.Module, at);
        if (itemId == 0)
        {
            _lastDropNote = $"drop refused: {resref} (not connected, or ground full)";
            return;
        }

        _dropIndex++;
        _lastDropNote = $"dropped {resref} as item 0x{itemId:X}";
    }

    private void Render()
    {
        CoopAgent? agent = manager.Agent;

        WriteLine($"KotorCoop agent  {bridge.Description}");
        WriteLine(string.Empty);

        if (agent is null)
        {
            RenderDisconnected();
            return;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        WriteLine($"session  {stats.Status}");
        WriteLine($"         peers {stats.PeerCount}  rtt {stats.RoundTripMs:F1}ms  tx {stats.PacketsSent}  rx {stats.PacketsReceived}  malformed {stats.PacketsDropped}");
        WriteLine($"         flags v{stats.FlagVersion} ({agent.Session.Flags.Count} known)  pending ops {stats.PendingFlagOps}");
        WriteLine(string.Empty);


        WriteContentSection(agent);

        string readStatus = agent.LastReadSucceeded ? "ok" : $"FAILING ({agent.ReadFailureCount} failures)";
        string writeStatus = agent.Bridge.CanWriteRemotes
            ? agent.LastWriteSucceeded ? "ok" : "FAILING"
            : "not supported by this profile (read-only)";

        WriteLine($"memory   read {readStatus}");
        WriteLine($"         write {writeStatus}");
        WriteLine(string.Empty);
        WriteLine($"local    module {(local.Module.Length > 0 ? local.Module : "(unknown)"),-16} pos {local.Position}  facing {local.Facing:F2}  hp {local.Health}/{local.MaxHealth}");
        WriteLine(string.Empty);
        WriteLine($"remote peers ({peers.Count})");

        for (int i = 0; i < 6; i++)
        {
            if (i < peers.Count)
            {
                RemotePeer peer = peers[i];
                string sameModule = peer.IsInModule(local.Module) ? " " : "*";
                WriteLine($"  {sameModule}{peer.PeerId,3} {peer.Name,-14} {peer.State.Module,-16} {peer.State.Position}  hp {peer.State.Health}/{peer.State.MaxHealth}");
            }
            else
            {
                WriteLine(string.Empty);
            }
        }

        IReadOnlyList<EnemyState> enemies = agent.Session.IsEnemyAuthority
            ? agent.Bridge.ReadEnemies()
            : agent.Session.GetEnemies();

        int alive = 0;
        int dead = 0;
        foreach (EnemyState enemy in enemies)
        {
            if (enemy.Dead)
            {
                dead++;
            }
            else
            {
                alive++;
            }
        }

        WriteLine(string.Empty);
        WriteLine(
            $"enemies  {enemies.Count} known ({alive} alive, {dead} dead)   "
            + (agent.Session.IsEnemyAuthority ? "THIS instance is authoritative" : "mirroring the host"));

        DialogueState convo = agent.Session.GetDialogue();
        string convoWho = convo.OwnerPeerId == agent.Session.LocalPeerId
            ? "you are"
            : $"peer {convo.OwnerPeerId} is";
        WriteLine($"dialogue {(convo.Active ? $"{convoWho} in a conversation" + (convo.SpeakerName.Length > 0 ? $" ({convo.SpeakerName})" : string.Empty) : "none")}");

        List<GroundItem> items = agent.Session.Items.Snapshot();
        WriteLine(string.Empty);
        WriteLine($"ground items ({items.Count})  {_lastDropNote}");

        for (int i = 0; i < 3; i++)
        {
            if (i < items.Count)
            {
                GroundItem item = items[i];
                WriteLine($"   0x{item.ItemId:X8} {item.Resref,-16} {item.Module,-16} {item.Position}");
            }
            else
            {
                WriteLine(string.Empty);
            }
        }

        WriteLine(string.Empty);
        WriteLine("* = in a different module, so not written into the game.");
        WriteLine("[D] drop an item at your feet   [P] pick the first one up.   Ctrl+C to stop.");
    }

    /// <summary>Steady-state readout while no session is live (between connects).</summary>
    private void RenderDisconnected()
    {
        WriteLine($"session  {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
        WriteLine(string.Empty);
        WriteLine("not connected.");
        WriteLine(string.Empty);
        WriteLine("In-game: press ~ and type  connect <ip> [password] | reconnect | help");

        // Keep the frame height stable so the previous, taller frame is fully overwritten.
        for (int i = 0; i < 18; i++)
        {
            WriteLine(string.Empty);
        }

        WriteLine("Ctrl+C to stop.");
    }

    /// <summary>
    /// What the two installs agreed on. Without this the headline mod-compatibility feature
    /// is invisible: a channel silently switched off (the peer's appearance, say, because
    /// their appearance.2da differs) looks exactly like a bug, and the player has no way to
    /// tell the difference. Says nothing while everything is on and nothing was warned
    /// about, so the normal case costs no space.
    /// </summary>
    private static void WriteContentSection(CoopAgent agent)
    {
        CompatibilityVerdict? verdict = agent.Session.Compatibility;
        if (verdict is null)
        {
            return;
        }

        SessionCapabilities off = SessionCapabilities.All & ~verdict.Allowed;
        if (off == SessionCapabilities.None && verdict.Warnings.Count == 0)
        {
            return;
        }

        WriteLine($"content  {verdict.Summary}");

        if (off != SessionCapabilities.None)
        {
            WriteLine($"         OFF: {off.ToString().Replace(", ", " ")}  (a mod/save difference makes these unsafe)");
        }

        for (int i = 0; i < verdict.Warnings.Count && i < 3; i++)
        {
            WriteLine($"         {verdict.Warnings[i]}");
        }

        WriteLine(string.Empty);
    }

    private static void WriteLine(string text)
    {
        // Pad so shorter frames do not leave fragments of longer ones behind.
        int width = Console.IsOutputRedirected ? text.Length : Math.Max(Console.WindowWidth - 1, text.Length);
        Console.WriteLine(text.PadRight(width));
    }
}
