namespace KotorCoop.Plugin;

// World-load edge, menu save/load, shared pause, journal apply and the in-game Connect
// flow. See SharedBlockView.cs for the class declaration and shared helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- world-load edge (roadmap item 14 phase 2) ----------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Bumps <see cref="SharedBlock.OffsetWorldLoadSerial"/> — called once
    /// per world teardown <c>PluginHost</c> judged unannounced (<c>WorldLoadRule.IsUnannouncedTeardown</c>).
    /// See that field's own doc comment in <c>SharedBlock</c> for why a plain counter, no
    /// payload, is all that crosses here.
    /// </summary>
    public void PublishWorldLoadObserved() =>
        *U32(SharedBlock.OffsetWorldLoadSerial) = *U32(SharedBlock.OffsetWorldLoadSerial) + 1;

    /// <summary>Plugin -&gt; agent. The raw counter, for a caller that wants the current value
    /// rather than to bump it (tests; a future diagnostic line).</summary>
    public uint WorldLoadSerial => *U32(SharedBlock.OffsetWorldLoadSerial);

    /// <summary>Agent -&gt; plugin. The request serial; a request is pending while it is ahead
    /// of <see cref="MidModulePlayApplied"/>.</summary>
    public uint MidModulePlayRequestSerial => *U32(SharedBlock.OffsetMidModulePlayRequestSerial);

    /// <summary>Agent -&gt; plugin. Pointer to the null-terminated resref (16 bytes) the
    /// pending request names.</summary>
    public byte* MidModulePlayRequestResRef => baseAddress + SharedBlock.OffsetMidModulePlayRequestResRef;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="MidModulePlayRequestSerial"/> once this
    /// instance has decided what to do with the request.</summary>
    public uint MidModulePlayApplied
    {
        get => *U32(SharedBlock.OffsetMidModulePlayApplied);
        set => *U32(SharedBlock.OffsetMidModulePlayApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. The verdict for the log (<c>MidModulePlayStatusCode</c>).
    /// </summary>
    public uint MidModulePlayStatus
    {
        set => *U32(SharedBlock.OffsetMidModulePlayStatus) = value;
    }

    /// <summary>Agent -&gt; plugin. True while the client-door-refusal watch should refuse a
    /// CLIENT-initiated real door transition, for whatever reason — see
    /// <see cref="ClientDoorRefusalReason"/> for which.</summary>
    public bool ClientDoorRefusalArmed => ClientDoorRefusalReason != 0;

    /// <summary>
    /// Posts one line to the always-on chat overlay WITHOUT going through the agent —
    /// <see cref="ChatOverlayController"/> reads <see cref="ChatLine"/>/<see cref="ChatSerial"/>
    /// from this same block, so a plugin-side write is visible to it exactly like an
    /// agent-side one. Exists for messages the plugin needs to show IMMEDIATELY, from code
    /// that has no round trip to spare — the client-door refusal fires and expires inside a
    /// single 0.5 s fade window, far too little time to wait for the agent's own tick.
    /// </summary>
    public void PostLocalChatLine(string text)
    {
        WriteFixedAscii(SharedBlock.OffsetChatLine, text, SharedBlock.ChatLineLength);
        *U32(SharedBlock.OffsetChatSerial) = *U32(SharedBlock.OffsetChatSerial) + 1;
    }

    /// <summary>Agent -&gt; plugin. How many movies the pending travel should queue.</summary>
    public int TravelMovieCount
    {
        get
        {
            int count = *I32(SharedBlock.OffsetTravelMovieCount);
            return count is > 0 and <= SharedBlock.CinematicCapacity ? count : 0;
        }
    }

    /// <summary>Agent -&gt; plugin. Pointer to travel movie <paramref name="index"/>, 16 bytes
    /// of null-padded ASCII.</summary>
    public byte* TravelMovie(int index) =>
        baseAddress + SharedBlock.OffsetTravelMovieNames + (index * SharedBlock.CinematicNameLength);

    public uint TravelApplied
    {
        get => *U32(SharedBlock.OffsetTravelApplied);
        set => *U32(SharedBlock.OffsetTravelApplied) = value;
    }

    public int TravelStatus
    {
        get => *I32(SharedBlock.OffsetTravelStatus);
        set => *I32(SharedBlock.OffsetTravelStatus) = value;
    }


    // ---- menu save load ---------------------------------------------------------

    /// <summary>Agent-written request serial; pending while ahead of MenuLoadApplied.</summary>
    public uint MenuLoadSerial => *U32(SharedBlock.OffsetMenuLoadSerial);

    public uint MenuLoadApplied
    {
        get => *U32(SharedBlock.OffsetMenuLoadApplied);
        set => *U32(SharedBlock.OffsetMenuLoadApplied) = value;
    }

    public int MenuLoadStatus
    {
        get => *I32(SharedBlock.OffsetMenuLoadStatus);
        set => *I32(SharedBlock.OffsetMenuLoadStatus) = value;
    }

    /// <summary>Pointer to the null-terminated module name for the menu load (16 bytes).</summary>
    public byte* MenuLoadModule => baseAddress + SharedBlock.OffsetMenuLoadModule;

    /// <summary>Agent -&gt; plugin. See <see cref="SharedBlock.OffsetMenuLoadInSession"/>.</summary>
    public bool MenuLoadInSession => *U32(SharedBlock.OffsetMenuLoadInSession) != 0;

    /// <summary>The details record for browser row <paramref name="index"/> — the world the
    /// host stands in and its party's portrait resrefs. Parallel to
    /// <see cref="BrowserEntry"/>, indexed identically.</summary>
    public byte* BrowserDetail(int index) =>
        baseAddress + SharedBlock.OffsetBrowserDetails + (index * SharedBlock.BrowserDetailStride);

    /// <summary>Agent-written: the next menu load adopts somebody else's world, so the
    /// character must come from Temp\pifo.ifo rather than the staged world's own
    /// module.ifo. See <see cref="SharedBlock.OffsetAdoptWorldLogin"/>.</summary>
    public bool AdoptWorldLogin => *U32(SharedBlock.OffsetAdoptWorldLogin) != 0;

    /// <summary>Plugin-written: which login flavour the last load actually took.</summary>
    public uint AdoptWorldApplied
    {
        get => *U32(SharedBlock.OffsetAdoptWorldApplied);
        set => *U32(SharedBlock.OffsetAdoptWorldApplied) = value;
    }

    /// <summary>Agent-written: a REMOTE peer standing in this module has its engine in
    /// combat. See <see cref="SharedBlock.OffsetSessionInCombat"/> for why the client cannot
    /// determine this locally, and why the local player is excluded.</summary>
    public bool SessionInCombat => *U32(SharedBlock.OffsetSessionInCombat) != 0;

    /// <summary>Plugin-written: bumped when this player releases a pause mirrored from a
    /// peer, so the agent can announce that the shared pause is over.</summary>
    public uint LocalPauseReleaseSerial
    {
        get => *U32(SharedBlock.OffsetLocalPauseReleaseSerial);
        set => *U32(SharedBlock.OffsetLocalPauseReleaseSerial) = value;
    }

    /// <summary>Agent-written: bumped when a PEER released the shared pause, so this instance
    /// drops the pause it started.</summary>
    public uint RemotePauseReleaseSerial => *U32(SharedBlock.OffsetRemotePauseReleaseSerial);


    // ---- shared pause -----------------------------------------------------------

    /// <summary>Agent-written: nonzero while any remote peer holds a pause.</summary>
    public bool RemotePauseDesired => *U32(SharedBlock.OffsetRemotePauseDesired) != 0;

    /// <summary>Plugin-written: nonzero while the pause bit is held on the peers' behalf.
    /// The agent reads this to avoid republishing a mirrored pause as this player's own.</summary>
    public uint RemotePauseApplied
    {
        get => *U32(SharedBlock.OffsetRemotePauseApplied);
        set => *U32(SharedBlock.OffsetRemotePauseApplied) = value;
    }


    // ---- journal ----------------------------------------------------------------

    public uint JournalSerial => *U32(SharedBlock.OffsetJournalSerial);

    /// <summary>Pointer to the null-terminated quest tag (32 bytes).</summary>
    public byte* JournalTag => baseAddress + SharedBlock.OffsetJournalTag;

    public int JournalState => *I32(SharedBlock.OffsetJournalState);

    public uint JournalApplied
    {
        get => *U32(SharedBlock.OffsetJournalApplied);
        set => *U32(SharedBlock.OffsetJournalApplied) = value;
    }

    public int JournalStatus
    {
        get => *I32(SharedBlock.OffsetJournalStatus);
        set => *I32(SharedBlock.OffsetJournalStatus) = value;
    }


    // ---- in-game connect --------------------------------------------------------

    public uint ConnectSerial
    {
        get => *U32(SharedBlock.OffsetConnectSerial);
        set => *U32(SharedBlock.OffsetConnectSerial) = value;
    }

    /// <summary>Pointer to the null-terminated typed host IP (40 bytes).</summary>
    public byte* ConnectHost => baseAddress + SharedBlock.OffsetConnectHost;

    public uint ConnectStatus
    {
        get => *U32(SharedBlock.OffsetConnectStatus);
        set => *U32(SharedBlock.OffsetConnectStatus) = value;
    }

    /// <summary>Agent-written: nonzero FORCES the in-game Co-op menu button on. The button
    /// defaults on plugin-side (coop.ini menu_button); this is the diagnostics override.</summary>
    public bool ConnectMenuEnabled => *U32(SharedBlock.OffsetConnectEnable) != 0;


    // ---- effective behaviour-override flags --------------------------------------

    /// <summary>Agent-written: bit N set = a host-authoritative comparison overrode key N for
    /// this session. See SharedBlock.BehaviourOverrides.cs and <see cref="EffectiveBehaviourFlag"/>.</summary>
    public uint BehaviourOverrideMask => *U32(SharedBlock.OffsetBehaviourOverrideMask);

    /// <summary>Agent-written: the value each overridden key was overridden to, meaningful
    /// only where the paired bit of <see cref="BehaviourOverrideMask"/> is set.</summary>
    public uint BehaviourOverrideValues => *U32(SharedBlock.OffsetBehaviourOverrideValues);

}
