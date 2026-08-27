using System.Text;

using static KotorCoop.Plugin.Vec3;

namespace KotorCoop.Plugin;

/// <summary>
/// The host side of "announced transitions" (docs/research/transition-smoothness.md): the
/// instant THIS instance's own engine commits to a real, engine-native door transition — the
/// rising edge of <c>Odyssey.IsAreaTransitionPending()</c>, the same signal
/// <c>PluginHost.OnFrame</c>'s pre-store flush already keys on — resolve where it is headed
/// and publish that to the shared block for the agent to relay, so a follower can start its
/// own warp immediately instead of waiting for this instance's published module to catch up.
///
/// Also carries the client-door-refusal watch (task 3): when armed, the SAME rising edge is
/// the earliest point a client can refuse its OWN premature crossing, before the load starts.
///
/// WHY THE DOOR SWEEP, AND WHY IT IS SAFE HERE. <c>Odyssey.CollectDoors</c> is documented "a
/// debug verb only, never per frame" — true, and irrelevant to this caller: this runs exactly
/// once per transition, on the rising edge, the identical cost class the <c>usedoor</c> cheat
/// already pays every time a human runs it. <c>CSWSArea::GetNextTransitionPendingID</c> was
/// considered and rejected as the alternative: docs/research/cinematics.md §8.2 shows it
/// returns an opaque, single-byte REQUEST id fed into <c>SetTransitionPending(1, id)</c>, not
/// the door's own object id — there is no cheaper way to name which door just fired.
/// </summary>
internal static unsafe class TransitionAnnounceWatch
{
    /// <summary>Same capacity <c>CheatController</c>'s own door sweep uses.</summary>
    private const int DoorSweepCapacity = 128;

    /// <summary>
    /// Runs on the rising edge of <c>Odyssey.IsAreaTransitionPending()</c> — call once, not
    /// every frame while the flag stays true. Resolves the nearest LINKED door to the local
    /// player (the one that just fired; there is no cheaper way to name it, see the type
    /// doc), publishes its destination for the agent to relay, and — if
    /// <paramref name="view"/>'s <see cref="SharedBlockView.ClientDoorRefusalArmed"/> is set —
    /// attempts the best-effort refusal.
    /// </summary>
    public static void OnTransitionBegan(SharedBlockView view)
    {
        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            PluginHost.Log("announce: transition pending but no player resolved; cannot name the destination");
            return;
        }

        nint* doors = stackalloc nint[DoorSweepCapacity];
        int doorsSeen = Odyssey.CollectDoors(doors, DoorSweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistance = float.MaxValue;
        int linkedSeen = 0;

        for (int i = 0; i < doorsSeen; i++)
        {
            if (!Odyssey.DoorIsLinked(doors[i]))
            {
                continue;
            }

            linkedSeen++;

            float distance = Distance(here, Odyssey.GetPosition(doors[i]));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = doors[i];
            bestDistance = distance;
        }

        if (best == 0)
        {
            PluginHost.Log(
                $"announce: transition pending but no linked door found nearby (swept "
                + $"{doorsSeen} door(s), {linkedSeen} linked) - the fallback module-change "
                + "detection will still carry the follow");
            return;
        }

        byte* moduleBuf = stackalloc byte[SharedBlock.TravelNameLength];
        int moduleLength = Odyssey.ReadDoorLinkedModule(best, moduleBuf, SharedBlock.TravelNameLength);
        if (moduleLength <= 0)
        {
            PluginHost.Log("announce: nearest linked door would not read its own LinkedToModule");
            return;
        }

        byte* waypointBuf = stackalloc byte[SharedBlock.TravelNameLength];
        int waypointLength = Odyssey.ReadDoorLinkedWaypoint(best, waypointBuf, SharedBlock.TravelNameLength);

        string module = Encoding.ASCII.GetString(moduleBuf, moduleLength);
        string waypoint = waypointLength > 0 ? Encoding.ASCII.GetString(waypointBuf, waypointLength) : string.Empty;
        uint doorId = *(uint*)(best + 0x04);

        if (view.ClientDoorRefusalArmed)
        {
            Refuse(view, doorId, module, view.ClientDoorRefusalReason);
            return;
        }

        view.PublishTransitionAnnounce(module, waypoint);
        PluginHost.Log(
            $"announce: door 0x{doorId:X} transition beginning -> '{module}'"
            + (waypoint.Length > 0 ? $" at waypoint '{waypoint}'" : string.Empty));
    }

    /// <summary>
    /// The scripted-funnel sibling of <see cref="OnTransitionBegan"/> (docs/research/
    /// transition-smoothness.md, 2026-08-27): runs on the rising edge of
    /// <c>Odyssey.IsMoveToModulePending()</c> — the field EVERY <c>StartNewModule()</c> call
    /// writes, not just a door's — for a transition the door watch never saw (a galaxy-map
    /// departure, a Leviathan/Star Forge stage change, a native trigger-driven transition, or a
    /// debug verb). The caller (<c>PluginHost.OnFrame</c>) already guarantees this only runs
    /// when no door transition is in flight this epoch and this instance's own
    /// <see cref="TravelController"/> did not raise the flag itself — see that caller's own
    /// comments for exactly what guards it.
    ///
    /// UNLIKE the door watch, this cannot attempt a client-door-style refusal: a script-driven
    /// transition never raises <c>trans_pending</c>, so there is no fade window to intercept and
    /// no known engine call that cancels a <c>StartNewModule()</c> already mid-flight — refusing
    /// a scripted transition is out of scope for this pass (docs/research/
    /// transition-smoothness.md's dated section, "what remains").
    /// </summary>
    public static void OnScriptedTransitionBegan(SharedBlockView view)
    {
        byte* moduleBuf = stackalloc byte[SharedBlock.TravelNameLength];
        int moduleLength = Odyssey.ReadMoveToModuleDestination(moduleBuf, SharedBlock.TravelNameLength);
        if (moduleLength <= 0)
        {
            PluginHost.Log(
                "announce: scripted transition pending but the destination module could not be "
                + "read - the fallback module-change detection will still carry the follow");
            return;
        }

        byte* waypointBuf = stackalloc byte[SharedBlock.TravelNameLength];
        int waypointLength = Odyssey.ReadMoveToModuleStartWaypoint(waypointBuf, SharedBlock.TravelNameLength);

        byte* sourceBuf = stackalloc byte[SharedBlock.TravelNameLength];
        int sourceLength = Odyssey.ReadCurrentModuleName(sourceBuf, SharedBlock.TravelNameLength);

        string module = Encoding.ASCII.GetString(moduleBuf, moduleLength);
        string waypoint = waypointLength > 0 ? Encoding.ASCII.GetString(waypointBuf, waypointLength) : string.Empty;
        string source = sourceLength > 0 ? Encoding.ASCII.GetString(sourceBuf, sourceLength) : string.Empty;

        TransitionAnnounceKind kind = TransitionAnnounceKindClassifier.Classify(source);

        view.PublishTransitionAnnounce(module, waypoint);
        PluginHost.Log(
            $"announce: scripted transition beginning ({kind}) -> '{module}'"
            + (waypoint.Length > 0 ? $" at waypoint '{waypoint}'" : string.Empty));
    }

    /// <summary>Mirrors <c>KotorCoop.Protocol.ClientDoorRefusalReason.HostBusy</c> by value —
    /// see <see cref="SharedBlockView.ClientDoorRefusalReason"/> for why this cannot be a shared
    /// enum type.</summary>
    private const uint ReasonHostBusy = 2;

    /// <summary>
    /// The client-door-refusal path (task 3, extended 2026-08-22 for lead-swap): logs the
    /// required line, posts it to the local chat overlay immediately (no agent round trip fits
    /// inside a 0.5 s fade window), and attempts the best-effort engine cancellation. Does NOT
    /// publish a <c>TransitionAnnounce</c> — a refused transition has no destination worth
    /// relaying, and publishing one would tell peers this instance is headed somewhere it just
    /// refused to go.
    ///
    /// <paramref name="reason"/> picks the wording: under <c>follow_lead=host</c> the client
    /// never leads at all ("the host must lead the way"); under <c>follow_lead=any</c> it COULD
    /// lead, but the host cannot safely follow this exact moment ("the host cannot follow right
    /// now") — a transient state worth trying again in a few seconds, not a standing rule.
    ///
    /// The module names by its RESREF, not a pretty display name: the plugin has no assembly
    /// dependency on <c>KotorCoop.Memory.GameFiles.ModuleNames</c> (a NativeAOT x86 DLL loaded
    /// into the game has none on purpose) and no reader for <c>dialog.tlk</c> of its own. The
    /// AGENT-side "following X to Y" line (task 2a) is the one that pretty-prints, because the
    /// agent process already has the game directory and the reader.
    /// </summary>
    private static void Refuse(SharedBlockView view, uint doorId, string module, uint reason)
    {
        string line = reason == ReasonHostBusy
            ? $"the host cannot follow right now (busy) - door 0x{doorId:X} leads to {module} - try again shortly"
            : $"the host must lead the way - door 0x{doorId:X} leads to {module}";

        view.PostLocalChatLine(line);
        PluginHost.Log($"door-refusal: {line}");

        // Best-effort, and known to be exactly that — see EngineHandles.TryClearTransitionPending's
        // own doc. Whether or not it actually stops phase 2, nothing here is unsafe: this is the
        // identical write the engine's own transition makes on ordinary completion.
        bool cleared = EngineHandles.TryClearTransitionPending();
        PluginHost.Log(cleared
            ? "door-refusal: cleared trans_pending (INFERRED whether this stops the load - NEEDS-LIVE)"
            : "door-refusal: could not resolve the area to clear trans_pending");
    }

}
