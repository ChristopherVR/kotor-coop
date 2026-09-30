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
        const string usage = "hurt <amount> [slot|0xid]  amount: real damage through the engine's "
            + "own path (shields and both 1-hp floors apply); slot: party-roster 0-8, 0xid: any creature, "
            + "blank = yourself";

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
        if (rest.Length == 2 && rest[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(rest[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint objectId)
                || objectId is 0 or >= 0x20000000)
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a creature object id - {usage}");
            }

            return new CoopCommand(CoopCommandKind.Hurt, Amount: amount, X: -1f, Address: rest[1]);
        }

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
    // save [name]  - a bare `save` is the engine quicksave; `save <name>` also keeps a copy under a
    // name (NamedSaveRule), carried in Address, so `loadgame <name>` can return to it.
    private static CoopCommand ParseSave(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Save);
        }

        string name = NamedSaveRule.SanitizeName(string.Join(' ', rest));
        return name.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "save <name>: the name needs letters or digits")
            : new CoopCommand(CoopCommandKind.Save, Address: name);
    }

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

    // galaxymap open|close|stats|select <n>|unlock <n>|depart <n>  - LIVE-TEST PRIMITIVE, see
    // CoopCommandKind.GalaxyMapOpen/GalaxyMapClose/GalaxyMapStats/GalaxyMapSelect/GalaxyMapUnlock/
    // GalaxyMapDepart. The
    // multi-way parse itself is GalaxyMapVerbRule (pure, unit-tested) so it does not need
    // re-testing through this dispatcher's own plumbing.
    private static CoopCommand ParseGalaxyMap(string[] rest)
    {
        GalaxyMapVerbRule.Verb verb = GalaxyMapVerbRule.Parse(rest, out int planet);
        return verb switch
        {
            GalaxyMapVerbRule.Verb.Open => new CoopCommand(CoopCommandKind.GalaxyMapOpen),
            GalaxyMapVerbRule.Verb.Close => new CoopCommand(CoopCommandKind.GalaxyMapClose),
            GalaxyMapVerbRule.Verb.Stats => new CoopCommand(CoopCommandKind.GalaxyMapStats),
            GalaxyMapVerbRule.Verb.Select => new CoopCommand(CoopCommandKind.GalaxyMapSelect, Amount: planet),
            GalaxyMapVerbRule.Verb.Unlock => new CoopCommand(CoopCommandKind.GalaxyMapUnlock, Amount: planet),
            GalaxyMapVerbRule.Verb.Depart => new CoopCommand(CoopCommandKind.GalaxyMapDepart, Amount: planet),
            _ => new CoopCommand(CoopCommandKind.Invalid, Error: GalaxyMapVerbRule.Usage),
        };
    }

    // gamepad [status|on|off|cursor|sim <...>|sim off]  - see CoopCommandKind.Gamepad. The
    // subcommand grammar lives in the plugin (GamepadController.HandleConsole /
    // VirtualPadGrammar, unit-tested there); this only carries the text across, refusing a
    // line the 32-byte cheat-text slot could not hold rather than silently truncating it.
    private static CoopCommand ParseGamepad(string[] rest)
    {
        string text = rest.Length == 0 ? "status" : string.Join(' ', rest);
        return text.Length > GamepadTextMaxLength
            ? new CoopCommand(CoopCommandKind.Invalid, Error: $"gamepad: arguments too long (max {GamepadTextMaxLength} characters)")
            : new CoopCommand(CoopCommandKind.Gamepad, Address: text);
    }

    /// <summary>Mirrors the plugin's <c>SharedBlock.CheatTextLength</c> (32) less the null.</summary>
    public const int GamepadTextMaxLength = 31;

    // input click <x> <y> [left|right] | input move <x> <y> | input key <dik> [down|up|tap] -
    // see CoopCommandKind.Input. Same shape as ParseGamepad above: the plugin's
    // InputInjectionController.HandleConsole owns the subcommand grammar, this just carries
    // the text across the same 31-byte cheat-text slot.
    private static CoopCommand ParseFireTrigger(string[] rest) =>
        rest.Length == 1 && rest[0].Length is > 0 and <= 30
            ? new CoopCommand(CoopCommandKind.FireTrigger, Address: rest[0])
            : new CoopCommand(CoopCommandKind.Invalid, Error: "usage: firetrigger <tag|0xid>  e.g. firetrigger ebn12_ebonexit");

    private static CoopCommand ParseRunScript(string[] rest) =>
        rest.Length is 1 or 2 && rest[0].Length is > 0 and <= 16
            && (rest.Length == 1 || TryParseItemId(rest[1], out _))
            ? new CoopCommand(CoopCommandKind.RunScript, Address: rest[0], Password: rest.Length == 2 ? rest[1] : null)
            : new CoopCommand(CoopCommandKind.Invalid, Error: "usage: runscript <resref> [0xid]  e.g. runscript k_pdan_elise_06 0x2");

    private static CoopCommand ParseStealth(string[] rest) =>
        rest.Length == 1 && (rest[0].Equals("on", StringComparison.OrdinalIgnoreCase) || rest[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            ? new CoopCommand(CoopCommandKind.Stealth, Address: rest[0].ToLowerInvariant())
            : new CoopCommand(CoopCommandKind.Invalid, Error: "usage: stealth on|off");

    private static CoopCommand ParseInput(string[] rest)
    {
        string text = rest.Length == 0 ? "status" : string.Join(' ', rest);
        return text.Length > GamepadTextMaxLength
            ? new CoopCommand(CoopCommandKind.Invalid, Error: $"input: arguments too long (max {GamepadTextMaxLength} characters)")
            : new CoopCommand(CoopCommandKind.Input, Address: text);
    }

    // screenshot <name> [front] | screenshot status  - see CoopCommandKind.Screenshot. Same
    // shape as ParseGamepad/ParseInput above: the plugin's ScreenshotController.HandleConsole
    // owns the grammar and the file-name safety check; this only carries the text across the
    // same 31-byte cheat-text slot (which is also why the path itself can never travel here -
    // see docs/research/isolated-rig.md for the fixed-directory transport this uses instead).
    private static CoopCommand ParseScreenshot(string[] rest)
    {
        string text = rest.Length == 0 ? "status" : string.Join(' ', rest);
        return text.Length > GamepadTextMaxLength
            ? new CoopCommand(CoopCommandKind.Invalid, Error: $"screenshot: arguments too long (max {GamepadTextMaxLength} characters)")
            : new CoopCommand(CoopCommandKind.Screenshot, Address: text);
    }

    // galaxyaccept  - LIVE-TEST PRIMITIVE, see CoopCommandKind.GalaxyAccept. A TOP-LEVEL verb,
    // deliberately not a `galaxymap` subcommand: it drives HandleInputEvent directly (the same
    // shape skipkey uses), not GalaxyMapVerbRule's ShowGalaxyMapGui/HideGalaxyMapGui/
    // SetSelectedPlanet trio, so it does not belong in that pure rule.
    private static CoopCommand ParseGalaxyAccept(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "galaxyaccept takes no arguments")
            : new CoopCommand(CoopCommandKind.GalaxyAccept);

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
        // A number is a slot; anything else is a save NAME (`loadgame manaan`), resolved by the
        // dispatcher against the saves folder with the same ordering the loader uses.
        if (rest.Length == 1 && int.TryParse(rest[0], out int loadSlot) && loadSlot >= 0)
        {
            return new CoopCommand(CoopCommandKind.LoadGame, Amount: loadSlot);
        }

        string wanted = string.Join(' ', rest).Trim();
        if (wanted.Length == 0 || (rest.Length == 1 && int.TryParse(rest[0], out _)))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "loadgame needs a save slot (0-based, most-recent-first) or a save name, "
                    + "e.g. loadgame 0 or loadgame manaan");
        }

        return new CoopCommand(CoopCommandKind.LoadGame, Address: wanted);
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

        if (rest.Length == 2 && !TryParseObjectId(rest[0], out _)
            && rest[0].Length + 1 + rest[1].Length > 31)
        {
            return new CoopCommand(CoopCommandKind.Invalid,
                Error: "The tag and dialog resref exceed the command buffer; use the object's numeric id.");
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
        // Grouped by what a command is FOR (2026-09-26): the flat list mixed real cheats with
        // saving, dialogue control, movement and developer probes under one "cheats" heading.
        "co-op commands beyond the basics, by group (`help` has connect/host/say/who):",
        "moving around:",
        "  tp <x> <y> [z]              move yourself inside this area",
        "  walkto <x> <y> [z]          walk there for real (crosses area triggers, unlike tp)",
        "  goto <player>               move yourself to a partner (same area)",
        "  autofollow [player|off|status]  keep walking after a partner (default: the host);",
        "                              joins their conversations, snaps back if left behind",
        "  usedoor [tag|id]            walk through a transition door (nearest by default)",
        "  crossdoor [tag|id]          cross a transition door without walking (when usedoor stalls)",
        "  opendoor [tag|id]           open an ordinary door (nearest by default)",
        "  closedoor [tag|id]          dev: close an ordinary door through the engine",
        "  setlock <id> on|off         dev: change a real door/container lock",
        "items:",
        "  drop <0xid|resref> [n]      drop one of YOUR items (or n of a stack) for your partner;",
        "                              quest items stay shared (F2 in the agent window picks)",
        "  items [n]                   list items here: id, name, holder, stack/charges/upgrades",
        "  item 0x131                  what one item's own state reads",
        "  useplaceable [tag|id]       open a container/corpse/switch (nearest container by default)",
        "  closeplaceable [tag|id]     close a container's loot panel",
        "saving and loading:",
        "  save [name]                 quicksave; with a name, also keep it as 'NNNNNN - name'",
        "  loadgame <slot|name>        load a save mid-session (slot 0 = most recent)",
        "conversations:",
        "  skipline                    finish the current line of a conversation you own",
        "  skipkey                     press skip in the dialogue panel (also works as a spectator)",
        "  selectreply <slot>          choose a reply in a conversation you own",
        "cheats (only your own game unless noted):",
        "  god [on|off]                your damage floors at 1 hp",
        "  topup [n]                   your hit points to n, or to full",
        "  standup [n]                 stand yourself up out of the downed state",
        "  slay [tag|id]               kill an enemy (a joiner's request goes to the host)",
        "  grantxp <id|me> <amount>    add experience to a creature",
        "  grantgold <amount>          add credits to the party",
        "  equip <id|me> <resref> [slot]  put an item on a creature (1 body, 4 right, 5 left hand)",
        "  cast <spell|name> <me|0xid> [metamagic]  cast a Force power for real (heal, cure,",
        "                              valor, stun, fear, forcepush, stasis)",
        "  grantpower <spell|name> [classSlot]  learn a Force power",
        "  retail cheats (co-op-safe): giveitem <resref> | givecredits <n> | addexp <n> |",
        "                              addlevel | heal | revealmap | infiniteuses |",
        "                              invulnerability | whereami | bright | turbo",
        "party tools (developer, this game only):",
        "  leader <slot|pc>            switch your control to companion slot 0-8, or back",
        "  recruit <slot>              add roster slot 0-8 to your party without dialogue",
        "  removeparty <slot>          remove roster slot 0-8 through the game's own call",
        "  followkick <slot>           dump and kick a companion's follow state",
        "  followstall <slot> [method] dump and apply one follow-stall recovery (1-6)",
        "testing and diagnostics:",
        "  hurt <n> [slot]             real damage to yourself or party slot 0-8 (shields apply)",
        "  down [slot]                 force a genuine knock-down on yourself or party slot 0-8",
        "  destroyobject <id>          destroy a creature outright (host only)",
        "  dropitem <resref|0x131>     spawn a blueprint (or a copy of a live item) on the floor",
        "  setitem 0x131 stack=20 charges=3 upgrades=0x5 identified=0   change an item's state",
        "  giveloot <owner id> <resref> [stack]   give a stand-in its real loot",
        "  pooladd <resref> [stack]    add a stack to the shared party item pool",
        "  poolremove <resref> [stack] remove a stack from the shared party item pool",
        "  applyeffect <kind> [target] [ms] [magnitude]  stage a stun/slow/choke/... effect",
        "  effects                     the last 8 effects captured, forwarded and applied",
        "  census [id]                 what the enemy census holds, and what the game says of each",
        "  resolve <id>                what the game knows about one object id",
        "  runscript <resref> [0xid]   run a named script with that object (or you) as its caller",
        "  netsim <off|status|loss <pct>|latency <ms> <jitter>|blackout <s> [in|out|both]>",
        "                              simulate a bad network",
        "  moviewarp <module> <movie...>   warp with a cinematic (the plugin pauses for it)",
        "  moviewarp queue <movie...> | clear | show",
        "  movieplay <resref>          play a movie now (the game's main thread stops for it)",
    ];
}
