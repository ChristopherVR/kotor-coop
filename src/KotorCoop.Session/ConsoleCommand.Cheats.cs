namespace KotorCoop.Session;

/// <summary>
/// The staging-cheat verbs: god, topup, standup, slay, tp, walkto, destroyobject, leader,
/// recruit, skipline, selectreply, loadgame, and the `cheats` listing itself
/// (<see cref="CheatsText"/>). See docs/research/console-cheats.md for why this list is short —
/// KOTOR's own console already ships giveitem/givecredits/addexp/addlevel/revealmap/heal, all
/// already co-op-correct because they touch only the typist's single-player state.
/// </summary>
public static partial class ConsoleCommand
{
    // A cheat that only ever applies to your own character REFUSES A TARGET rather than
    // ignoring one. "god malak" is a reasonable thing for a player to try, and silently
    // arming their own invulnerability instead would be a lie about what just happened — so
    // the argument is validated and the message names the rule.
    private static CoopCommand ParseGod(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.God, Amount: -1);
        }

        return rest[0].ToLowerInvariant() switch
        {
            "on" or "1" or "true" or "yes" => new CoopCommand(CoopCommandKind.God, Amount: 1),
            "off" or "0" or "false" or "no" => new CoopCommand(CoopCommandKind.God, Amount: 0),
            _ => new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "god applies to your own character only - use: god [on|off]"),
        };
    }

    private static CoopCommand ParseTopUp(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.TopUp);
        }

        if (!int.TryParse(rest[0], out int hitPoints) || hitPoints <= 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a hit-point count - use: topup [n], or topup for full");
        }

        return new CoopCommand(CoopCommandKind.TopUp, Amount: hitPoints);
    }

    private static CoopCommand ParseStandUp(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.StandUp);
        }

        if (!int.TryParse(rest[0], out int reviveHitPoints) || reviveHitPoints <= 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a hit-point count - use: standup [n]");
        }

        return new CoopCommand(CoopCommandKind.StandUp, Amount: reviveHitPoints);
    }

    /// <summary>hurt &lt;amount&gt; [slot] — DEV-ONLY LIVE-TEST PRIMITIVE, see
    /// <see cref="CoopCommandKind.Hurt"/>. <c>X</c> carries the slot as a float (this record
    /// has no spare int field once <c>Amount</c> is taken); -1 is the "blank = yourself"
    /// sentinel, matching <c>ParseDown</c> below.</summary>
    private static CoopCommand ParseHurt(string[] rest)
    {
        const string usage = "hurt <amount> [slot]  amount: real damage through the engine's "
            + "own path (shields and both 1-hp floors apply); slot: party-roster 0-8, blank = yourself";

        if (rest.Length is < 1 or > 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!int.TryParse(rest[0], out int amount) || amount <= 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not a positive damage amount - {usage}");
        }

        float slot = -1f;
        if (rest.Length == 2)
        {
            if (!int.TryParse(rest[1], out int parsedSlot) || parsedSlot is < 0 or > 8)
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{rest[1]}' is not a party-roster slot (0-8) - {usage}");
            }

            slot = parsedSlot;
        }

        return new CoopCommand(CoopCommandKind.Hurt, Amount: amount, X: slot);
    }

    /// <summary>down [slot] — DEV-ONLY LIVE-TEST PRIMITIVE, see
    /// <see cref="CoopCommandKind.Down"/>.</summary>
    private static CoopCommand ParseDown(string[] rest)
    {
        const string usage = "down [slot]  slot: party-roster 0-8, blank = yourself";

        if (rest.Length > 1)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        float slot = -1f;
        if (rest.Length == 1)
        {
            if (!int.TryParse(rest[0], out int parsedSlot) || parsedSlot is < 0 or > 8)
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{rest[0]}' is not a party-roster slot (0-8) - {usage}");
            }

            slot = parsedSlot;
        }

        return new CoopCommand(CoopCommandKind.Down, X: slot);
    }

    private static CoopCommand ParseSlay(string[] rest)
    {
        if (rest.Length == 0)
        {
            // No target: the agent picks the nearest hostile from its own census.
            return new CoopCommand(CoopCommandKind.Slay);
        }

        // Aiming this at a person is the mistake worth catching in the parser: the plugin
        // refuses it too (a phantom and a companion are both recognised there), but a player
        // who types "slay me" deserves the reason immediately rather than a round trip.
        if (IsSelfWord(rest[0]))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "slay is for enemies - your own character is not a target");
        }

        if (!IsTargetToken(rest[0]))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a creature tag or object id");
        }

        return new CoopCommand(CoopCommandKind.Slay, Address: rest[0]);
    }

    private static CoopCommand ParseTeleport(string[] rest)
    {
        if (rest.Length < 2)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "tp needs coordinates, e.g. tp 12.5 -30.0 (z optional)");
        }

        if (!TryParseCoordinate(rest[0], out float x) || !TryParseCoordinate(rest[1], out float y))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]} {rest[1]}' is not a coordinate pair");
        }

        float z = 0f;
        if (rest.Length > 2 && !TryParseCoordinate(rest[2], out z))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a height");
        }

        return new CoopCommand(CoopCommandKind.Teleport, X: x, Y: y, Z: z);
    }

    // walkto <x> <y> [z] - LIVE-TEST PRIMITIVE, see CoopCommandKind.WalkTo. Same argument
    // shape as tp, deliberately, so a live pass can try both at one spot.
    private static CoopCommand ParseWalkTo(string[] rest)
    {
        if (rest.Length < 2)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "walkto needs coordinates, e.g. walkto 12.5 -30.0 (z optional)");
        }

        if (!TryParseCoordinate(rest[0], out float walkX) || !TryParseCoordinate(rest[1], out float walkY))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]} {rest[1]}' is not a coordinate pair");
        }

        float walkZ = 0f;
        if (rest.Length > 2 && !TryParseCoordinate(rest[2], out walkZ))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a height");
        }

        return new CoopCommand(CoopCommandKind.WalkTo, X: walkX, Y: walkY, Z: walkZ);
    }

    // destroyobject <id>  - STAGING VERB, see CoopCommandKind.DestroyObject. Object id only,
    // deliberately: this stages a SPECIFIC creature `census` already named, not a sweep.
    private static CoopCommand ParseDestroyObject(string[] rest)
    {
        if (rest.Length != 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "destroyobject needs exactly one object id, e.g. destroyobject 0x35");
        }

        if (IsSelfWord(rest[0]))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "destroyobject is for enemies - your own character is not a target");
        }

        if (!TryParseObjectId(rest[0], out _))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not an object id (try 0x35, or '#35')");
        }

        return new CoopCommand(CoopCommandKind.DestroyObject, Address: rest[0]);
    }

    // leader <slot|pc>  - DEV-ONLY TEST PRIMITIVE, see CoopCommandKind.Leader.
    /// <summary>
    /// Parses <c>leader &lt;slot|pc&gt;</c> — the dev-only control-switch test primitive (see
    /// <see cref="CoopCommandKind.Leader"/>). <c>slot</c> is a party-roster index 0-8;
    /// <c>pc</c>/<c>me</c>/<c>self</c> means "back to your own character".
    /// </summary>
    private static CoopCommand ParseLeader(string[] rest)
    {
        const string usage =
            "leader <slot|pc>  (slot: party-roster index 0-8, e.g. Trask is slot 0 in "
            + "end_m01aa; pc: switch back to your own character)";

        if (rest.Length != 1)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (IsSelfWord(rest[0]) || rest[0].Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            return new CoopCommand(CoopCommandKind.Leader, Amount: -1);
        }

        if (!int.TryParse(rest[0], out int slot) || slot < 0 || slot > 8)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a party-roster slot (0-8) or 'pc' - {usage}");
        }

        return new CoopCommand(CoopCommandKind.Leader, Amount: slot);
    }

    // recruit <slot>  - DEV-ONLY TEST PRIMITIVE, see CoopCommandKind.Recruit.
    /// <summary>
    /// Parses <c>recruit &lt;slot&gt; [tag]</c> — the dev-only force-into-party test primitive
    /// (see <see cref="CoopCommandKind.Recruit"/>). <c>slot</c> is a party-roster index 0-8.
    /// The optional <c>tag</c> is a fallback: if the slot has no availability-linked body
    /// (<c>SpawnAvailableNPC</c> needs a blueprint association a raw availability poke does not
    /// carry — see <c>Odyssey.TrySetSlotAvailable</c>'s own NEEDS-LIVE note), the plugin looks
    /// up an already-placed live creature by this tag instead and adds THAT body.
    /// </summary>
    private static CoopCommand ParseRecruit(string[] rest)
    {
        const string usage = "recruit <slot> [tag]  (party-roster index 0-8, e.g. Carth is slot 2; "
            + "tag is a fallback lookup for a body already placed in the module, e.g. p_carth001)";

        if (rest.Length is not (1 or 2))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!int.TryParse(rest[0], out int slot) || slot < 0 || slot > 8)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a party-roster slot (0-8) - {usage}");
        }

        string tag = rest.Length == 2 ? rest[1] : string.Empty;
        return new CoopCommand(CoopCommandKind.Recruit, Amount: slot, Address: tag);
    }

    // skipline  - LIVE-TEST PRIMITIVE, see CoopCommandKind.SkipLine.
    private static CoopCommand ParseSkipLine(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "skipline takes no arguments")
            : new CoopCommand(CoopCommandKind.SkipLine);

    // skipkey  - LIVE-TEST PRIMITIVE, see CoopCommandKind.SkipKey. NOT a synonym of skipline:
    // this one goes through the engine's input dispatch, which is where DialogueSkipHook lives.
    private static CoopCommand ParseSkipKey(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "skipkey takes no arguments")
            : new CoopCommand(CoopCommandKind.SkipKey);

    // save  - see CoopCommandKind.Save.
    private static CoopCommand ParseSave(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "save takes no arguments")
            : new CoopCommand(CoopCommandKind.Save);

    // quittomenu  - LIVE-TEST PRIMITIVE, see CoopCommandKind.QuitToMenu.
    private static CoopCommand ParseQuitToMenu(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "quittomenu takes no arguments")
            : new CoopCommand(CoopCommandKind.QuitToMenu);

    // partyselect ok  - LIVE-TEST PRIMITIVE, see CoopCommandKind.PartySelectOk. "ok" is the
    // only recognised subcommand for now (confirm with the current selection); nothing else
    // (cancel, pick a specific slot) is wired up.
    private static CoopCommand ParsePartySelect(string[] rest) =>
        rest.Length == 1 && string.Equals(rest[0], "ok", StringComparison.OrdinalIgnoreCase)
            ? new CoopCommand(CoopCommandKind.PartySelectOk)
            : new CoopCommand(CoopCommandKind.Invalid, Error: "usage: partyselect ok");

    // galaxymap open|close|stats  - LIVE-TEST PRIMITIVE, see CoopCommandKind.GalaxyMapOpen/
    // GalaxyMapClose/GalaxyMapStats. The three-way parse itself is GalaxyMapVerbRule (pure,
    // unit-tested) so it does not need re-testing through this dispatcher's own plumbing.
    private static CoopCommand ParseGalaxyMap(string[] rest) => GalaxyMapVerbRule.Parse(rest) switch
    {
        GalaxyMapVerbRule.Verb.Open => new CoopCommand(CoopCommandKind.GalaxyMapOpen),
        GalaxyMapVerbRule.Verb.Close => new CoopCommand(CoopCommandKind.GalaxyMapClose),
        GalaxyMapVerbRule.Verb.Stats => new CoopCommand(CoopCommandKind.GalaxyMapStats),
        _ => new CoopCommand(CoopCommandKind.Invalid, Error: GalaxyMapVerbRule.Usage),
    };

    // selectreply <slot>  - LIVE-TEST PRIMITIVE, see CoopCommandKind.SelectReply.
    private static CoopCommand ParseSelectReply(string[] rest)
    {
        if (rest.Length != 1 || !int.TryParse(rest[0], out int replySlot) || replySlot < 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "selectreply needs one reply slot (0-based), e.g. selectreply 0");
        }

        return new CoopCommand(CoopCommandKind.SelectReply, Amount: replySlot);
    }

    // loadgame <slot>  - LIVE-TEST PRIMITIVE, see CoopCommandKind.LoadGame.
    private static CoopCommand ParseLoadGame(string[] rest)
    {
        if (rest.Length != 1 || !int.TryParse(rest[0], out int loadSlot) || loadSlot < 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "loadgame needs one save slot (0-based, most-recent-first), "
                    + "e.g. loadgame 0");
        }

        return new CoopCommand(CoopCommandKind.LoadGame, Amount: loadSlot);
    }

    // newgame  - opens the game's own character-creation screens. See CoopCommandKind.NewGame.
    private static CoopCommand ParseNewGame(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "newgame takes no arguments")
            : new CoopCommand(CoopCommandKind.NewGame);

    // chargen quick [class 0-5] | chargen accept  - see CoopCommandKind.ChargenQuick/ChargenAccept.
    private static CoopCommand ParseChargen(string[] rest)
    {
        const string usage = "chargen quick [class 0-5] | chargen accept";

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (rest[0].Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            return rest.Length > 1
                ? new CoopCommand(CoopCommandKind.Invalid, Error: "chargen accept takes no arguments")
                : new CoopCommand(CoopCommandKind.ChargenAccept);
        }

        if (!rest[0].Equals("quick", StringComparison.OrdinalIgnoreCase))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        int classIndex = 0;
        if (rest.Length > 1 && (!int.TryParse(rest[1], out classIndex) || classIndex is < 0 or > 5))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "chargen quick takes a class index 0-5 (the six portraits, left to right)");
        }

        return rest.Length > 2
            ? new CoopCommand(CoopCommandKind.Invalid, Error: usage)
            : new CoopCommand(CoopCommandKind.ChargenQuick, Amount: classIndex);
    }

    // startdialog <npc-tag|id> [dlg-resref]  - LIVE-TEST PRIMITIVE, see
    // CoopCommandKind.StartDialog.
    private static CoopCommand ParseStartDialog(string[] rest)
    {
        const string usage = "startdialog <npc-tag|id> [dlg-resref]  e.g. startdialog "
            + "sta_invis_bast_conv k_sta_bastila";

        if (rest.Length is < 1 or > 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!IsTargetToken(rest[0]))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not an npc tag or object id - {usage}");
        }

        if (rest.Length == 2 && !IsResRef(rest[1], maxLength: 16))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a dialog resref - {usage}");
        }

        return new CoopCommand(
            CoopCommandKind.StartDialog, Address: rest[0], Password: rest.Length == 2 ? rest[1] : string.Empty);
    }

    // removeparty <slot>  - DEV-ONLY LIVE-TEST PRIMITIVE, see CoopCommandKind.RemoveParty.
    private static CoopCommand ParseRemoveParty(string[] rest)
    {
        if (rest.Length != 1 || !int.TryParse(rest[0], out int slot) || slot < 0 || slot > 8)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "removeparty needs one party-roster slot (0-8), e.g. removeparty 0");
        }

        return new CoopCommand(CoopCommandKind.RemoveParty, Amount: slot);
    }

    // followkick <slot>  - DEV-ONLY LIVE-TEST PRIMITIVE, see CoopCommandKind.FollowKick.
    private static CoopCommand ParseFollowKick(string[] rest)
    {
        if (rest.Length != 1 || !int.TryParse(rest[0], out int slot) || slot < 0 || slot > 8)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "followkick needs one party-roster slot (0-8), e.g. followkick 0");
        }

        return new CoopCommand(CoopCommandKind.FollowKick, Amount: slot);
    }

    // followstall <slot> [method]  - DEV-ONLY LIVE-TEST PRIMITIVE, see
    // CoopCommandKind.FollowStall (including why Amount carries method*100+slot).
    private static CoopCommand ParseFollowStall(string[] rest)
    {
        const string usage =
            "followstall needs a party-roster slot (0-8) and an optional recovery method "
            + "(1 phasereset, 2 followleader, 3 positionparty, 4 priortie, 5 destnode, "
            + "6 priorpush), e.g. followstall 0 6";

        if (rest.Length is not (1 or 2) || !int.TryParse(rest[0], out int slot) || slot < 0 || slot > 8)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        int method = 0;
        if (rest.Length == 2 && (!int.TryParse(rest[1], out method) || method < 1 || method > 6))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        return new CoopCommand(CoopCommandKind.FollowStall, Amount: (method * 100) + slot);
    }

    private static CoopCommand ParseCheats(string[] rest) => new(CoopCommandKind.Cheats);

    /// <summary>
    /// The staging-cheat listing. Two halves on purpose: what co-op adds, and what the
    /// retail console already does correctly in co-op — because the second half is the part
    /// a player cannot discover on their own and the part we deliberately did not rewrite.
    /// </summary>
    public static readonly string[] CheatsText =
    [
        "co-op staging cheats (authority-aware):",
        "  god [on|off]        your damage floors at 1 hp; yours only",
        "  topup [n]           your hit points to n, or to full",
        "  standup [n]         stand yourself up out of the downed state",
        "  hurt <n> [slot]     dev-only: real damage through the engine's own path (shields",
        "                      and both 1-hp floors apply) to yourself or party slot (0-8)",
        "  down [slot]         dev-only: force the SAME genuine temp-death a real lethal hit",
        "                      produces on yourself or party slot (0-8); bypasses min1hp",
        "  slay [tag|id]       kill an enemy; a client routes it to the host",
        "  destroyobject <id>  destroy a creature outright (Odyssey.DestroyObjectById);",
        "                      HOST-ONLY, no forward path - refused on a mirroring instance",
        "  tp <x> <y> [z]      move yourself inside this area",
        "  walkto <x> <y> [z]  queue a real walk (not a snap) - crosses subarea triggers",
        "  goto <player>       move yourself to a peer (same module)",
        "  dropitem <resref>   drop a blueprint; the item channel shares it",
        "  dropitem 0x131      drop a copy of a LIVE item, carrying its real state",
        "  items [n]           list live items here: id, resref, holder, stack/charges/upgrades",
        "  item 0x131          what one item's per-instance state actually reads",
        "  setitem 0x131 stack=20 charges=3 upgrades=0x5 identified=0",
        "                      poke those fields, so there is something worth sending",
        "  giveloot <owner id> <resref> [stack]   give a materialised stand-in its real loot",
        "  applyeffect <kind> [target] [durationMs] [magnitude]",
        "                      run a peer-effect constructor (stun/paralyze/knockdown/slow/",
        "                      frightened/sleep/choke/forcepush/whirlwind); blank target =",
        "                      nearest phantom. STAGING ONLY, LOCAL APPLY - what the hook",
        "                      forwards from a peer's phantom is the actual test",
        "  effects             last 8 captured/forwarded/applied peer effects, with ages",
        // Listed here because it is the same family — a verb that exists to stage a test —
        // and discoverability is the point of this listing. It is owned by the network
        // fault-injection work, which registers its own catalog entry and parser case; this
        // is only the line that tells a player it is there.
        "test harness:",
        "  netsim <off|status|loss <pct>|latency <ms> <jitter>|blackout <s> [in|out|both]>",
        // ⚠ Listed with the warning attached, because the thing it arranges is not "a video":
        // a movie parks the game's main thread for its whole length, and for those seconds
        // the co-op plugin is unreachable rather than idle.
        "  moviewarp <module> <movie...>   warp WITH a cinematic (the plugin stops for it)",
        "  moviewarp queue <movie...> | clear | show",
        // moviewarp only ever writes the TRANSITION queue - a different engine path entirely.
        // This is the one that fires MoviePlayHook / the mid-module movie relay on demand.
        "  movieplay <resref>              play a movie mid-module RIGHT NOW (blocks main",
        "                      thread); fires the mid-module movie relay for a connected peer",
        // The pair: queue the movie, then make the ENGINE start the transition. Only this
        // order opens the pre-store flush window a movie might swallow.
        "  usedoor [tag|id]                walk through a transition door (nearest by default)",
        "  crossdoor [tag|id]              post the door-crossing event directly, no walk",
        "                      (nearest by default) - for when usedoor's walk stalls short",
        "  opendoor [tag|id]               open an ORDINARY door (no transition; nearest by default)",
        "  useplaceable [tag|id]           use a container/corpse/switch (nearest container by default)",
        "  closeplaceable [tag|id]         close a container's loot panel (nearest by default)",
        "  census [id]         what my enemy census holds, and what my own engine says of each id",
        "  resolve <id>        what the ENGINE knows about one id, in one frame (plugin log has the chain)",
        "  leader <slot|pc>    dev-only: switch YOUR control to a party-roster companion (0-8)",
        "                      or back to 'pc'; local only, never crosses the network",
        "  recruit <slot>      dev-only: force NPC roster slot (0-8) into the local active",
        "                      party, no dialogue needed; local only, never crosses the network",
        "  removeparty <slot>  dev-only: call the REAL RemovePartyMember(nNPC) on roster slot",
        "                      (0-8) - exercises the solo-segment detector's script-agnostic",
        "                      hook without reaching the Bastila dialogue; local only",
        "  followkick <slot>   dev-only: dump roster slot (0-8)'s follow-record fields, then",
        "                      force record+4=10 (TeleportForward) unconditionally; local only",
        "  followstall <slot> [method]  dev-only: dump roster slot (0-8)'s follow-record wedge",
        "                      signature, then apply one follow-stall recovery unconditionally",
        "                      (1 phasereset, 2 followleader, 3 positionparty, 4 priortie,",
        "                      5 destnode, 6 priorpush;",
        "                      omitted = coop.ini's party_follow_stall_recovery_method); local only",
        "  skipline            finish your OWNED conversation's current line/VO right now,",
        "                      in-process (no OS input); no-op if nothing is playing",
        "  skipkey             send a skip KEYPRESS into the dialogue panel's own input",
        "                      dispatch - the only route through the spectator skip gag;",
        "                      answers with the gag's swallow count before -> after",
        "  selectreply <slot>  commit a reply slot on your OWNED conversation, in-process",
        "                      (no OS input); test-only navigation helper for skipline",
        "  save                quicksave through the engine's own DoQuickSave (asynchronous:",
        "                      the directory appears a tick or two later)",
        "  loadgame <slot>     fire a GENUINE mid-session Load Game action, in-process (no",
        "                      clicks); slot is 0-based, most-recent-first",
        "  pooladd <resref> [stack]     add a resref stack to the shared party item pool",
        "  poolremove <resref> [stack]  remove a resref stack from the shared party item pool",
        "  grantxp <id|me> <amount>     add a signed xp delta to a creature (script-grant sim)",
        "  equip <id|me> <resref> [slot]  re-equip a creature (1 body, 4 right weapon, 5 left)",
        "  grantgold <amount>           add a signed gold delta to the party wallet (trade sim)",
        "retail console cheats, already co-op-safe (they touch only your game):",
        "  giveitem <resref> | givecredits <n> | addexp <n> | addlevel | heal",
        "  revealmap | infiniteuses | invulnerability | whereami | bright | turbo",
    ];
}
