using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class CoopConfigTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"coop-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParsesAJoinConfig()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            host = 10.0.0.5
            port = 27140
            password = swordfish
            name = Malak
            menu_button = 1
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);

            Assert.NotNull(c);
            Assert.False(c.Host);
            Assert.Equal("10.0.0.5", c.HostAddress);
            Assert.Equal(27140, c.Port);
            Assert.Equal("swordfish", c.Password);
            Assert.Equal("Malak", c.Name);
            Assert.True(c.MenuButton);

            // Absent keys: the join path this config describes is the one that already
            // shipped, and adoption is on. Pinned so a change to either default is caught as a
            // behaviour change needing a docs/CONFIG.md update, not silent drift.
            Assert.Equal(JoinMode.Save, c.JoinMode);
            Assert.True(c.AutoAdoptWorld);

            Assert.NotNull(c.ResolveEndPoint());
            Assert.Equal(27140, c.ResolveEndPoint()!.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The first-time joiner's config: no save on this install, so the game's own character
    /// creation supplies the character. Also pins that <c>auto_adopt_world = 0</c> is honoured
    /// — the one lever that restores the pre-adoption "play degraded" behaviour exactly.
    /// </summary>
    [Fact]
    public void ParsesJoinModeAndAutoAdoptWorld()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            host = 10.0.0.5
            join_mode = newcharacter
            auto_adopt_world = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);

            Assert.NotNull(c);
            Assert.Equal(JoinMode.NewCharacter, c.JoinMode);
            Assert.False(c.AutoAdoptWorld);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsesAHostConfigAndTolerantSyntax()
    {
        // No section header, comments, blank lines, inline comment, mixed case key.
        string path = WriteTemp("""
            # my host
            Mode=host

            PORT = 27200   ; the port

            menu_button = yes
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);

            Assert.NotNull(c);
            Assert.True(c.Host);
            Assert.Equal(27200, c.Port);
            Assert.True(c.MenuButton);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileIsNullNotAnError()
    {
        Assert.Null(CoopConfig.TryLoad(Path.Combine(Path.GetTempPath(), "definitely-not-there.ini")));
    }

    // ---- roadmap item 24: public address discovery / UPnP -------------------------------

    [Fact]
    public void AbsentStunServersKeyKeepsTheDefaultTwoServerList()
    {
        string path = WriteTemp("mode = host");
        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.Equal(PublicAddressDiscovery.DefaultServers, c.StunServers);

            // And that default actually parses to two servers - the whole point of shipping
            // a redundant pair rather than one.
            Assert.Equal(2, PublicAddressDiscovery.ParseServers(c.StunServers).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnExplicitlyBlankStunServersLineDisablesDiscovery()
    {
        string path = WriteTemp("""
            mode = host
            stun_servers =
            """);
        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.Equal(string.Empty, c.StunServers);
            Assert.Empty(PublicAddressDiscovery.ParseServers(c.StunServers));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AConfiguredStunServersListIsReadVerbatim()
    {
        string path = WriteTemp("stun_servers = stun.example.org:3478,stun2.example.org:3478");
        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.Equal<string[]>(
                ["stun.example.org:3478", "stun2.example.org:3478"],
                [.. PublicAddressDiscovery.ParseServers(c.StunServers)]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UpnpDefaultsOnAndCanBeDisabled()
    {
        string onPath = WriteTemp("mode = host");
        string offPath = WriteTemp("""
            mode = host
            upnp = 0
            """);
        try
        {
            Assert.True(CoopConfig.TryLoad(onPath)!.Upnp);
            Assert.False(CoopConfig.TryLoad(offPath)!.Upnp);
        }
        finally
        {
            File.Delete(onPath);
            File.Delete(offPath);
        }
    }

    [Fact]
    public void DefaultsAreSaneWhenKeysAreAbsent()
    {
        string path = WriteTemp("mode = join");
        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.Equal(KotorCoop.Protocol.ProtocolConstants.DefaultPort, c.Port);
            Assert.Equal(string.Empty, c.Password);

            // The Co-op button is part of the install: an absent key means ON, and only an
            // explicit menu_button = 0 hides it (covered below).
            Assert.True(c.MenuButton);

            // Chat overlay defaults on when the file says nothing about it.
            Assert.True(c.ChatOverlay);
            Assert.Equal(2u, c.ChatOverlayAnchorCode); // bottom-left

            // The persistent connection indicator (item 18) defaults on too, the same rule
            // menu_button and chat_overlay already follow: absent key means ON.
            Assert.True(c.StatusToasts);

            // companion_xp_sync defaults ON (docs/research/companion-state-sync.md §6,
            // live-verified 2026-08-27) — through TryLoad's real ini-parsing path, not just
            // the CoopConfig record's own bare-constructor default: an existing player's
            // coop.ini from before this flag existed has no line for it at all (the installer
            // never rewrites an existing coop.ini), so an absent key is the common case this
            // must get right, not an edge case.
            Assert.True(c.CompanionXpSync);

            // script_item_sync defaults ON the same way (c3c50b8 flipped the property default
            // but this line's ini-parse still read a missing key as OFF — the identical bug
            // class companion_xp_sync above was fixed for). An existing player's coop.ini from
            // before this flag existed has no line for it at all, so absent-means-ON is the
            // common case here too, not an edge case.
            Assert.True(c.ScriptItemSync);

            // Same bug class again, found while fixing the two above: TransitionAnnounceEnabled/
            // ClientDoorRefusalEnabled/MovieRelayEnabled are all documented "ON by default (an
            // absent key enables it)" and their ini-parse here already used the correct
            // absent-key-means-ON idiom, but AgentComposition.cs's own `config?.X ?? false`
            // fallback for "no coop.ini file at all" still landed on OFF — the identical
            // documented-vs-actual mismatch, one layer further out. Fixed alongside the record's
            // own bare-constructor default (both now `= true` / `?? true`).
            Assert.True(c.TransitionAnnounceEnabled);
            Assert.True(c.ClientDoorRefusalEnabled);
            Assert.True(c.MovieRelayEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MenuButtonHidesOnlyWhenExplicitlyTurnedOff()
    {
        string path = WriteTemp("""
            mode = join
            menu_button = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.MenuButton);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MenuButtonDefaultsOnWithNoConfigAtAll()
    {
        // No coop.ini is the fresh-install case: TryLoad returns null and the agent's
        // `config?.MenuButton ?? true` must land on the same answer as the record default.
        Assert.True(new CoopConfig().MenuButton);
    }

    [Fact]
    public void ParsesChatOverlaySettings()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            chat_overlay = 0
            chat_overlay_opacity = 70
            chat_overlay_anchor = top-right
            chat_overlay_lines = 8
            chat_overlay_fade_ms = 4000
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.ChatOverlay);
            Assert.Equal(70, c.ChatOverlayOpacity);
            Assert.Equal(1u, c.ChatOverlayAnchorCode); // top-right
            Assert.Equal(8, c.ChatOverlayLines);
            Assert.Equal(4000, c.ChatOverlayFadeMs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StatusToastsCanBeTurnedOff()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            status_toasts = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.StatusToasts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SessionSaveHostRefusalDefaultsOff()
    {
        // Round 53's live-verified false positive: an ordinary module transition (not just a
        // genuine different-save load) rewrites the watched autosave and would otherwise
        // disconnect the host. Absent key must default to NOT refusing.
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.SessionSaveHostRefusal);
            Assert.False(new CoopConfig().SessionSaveHostRefusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SessionSaveHostRefusalCanBeTurnedOn()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            session_save_host_refusal = 1
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.SessionSaveHostRefusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClientLoadRejoinDefaultsOn()
    {
        // Roadmap item 14 phase 2 — see CoopConfig.ClientLoadRejoin's own doc comment: live-
        // verified 2026-08-24 (docs/research/session-save.md round 87/88), a direct, decisive,
        // zero-regression result, the same bar DialogueFastJoin cleared earlier.
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.ClientLoadRejoin);
            Assert.True(new CoopConfig().ClientLoadRejoin);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClientLoadRejoinCanBeTurnedOff()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            client_load_rejoin = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.ClientLoadRejoin);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HostLoadReadoptDefaultsOn()
    {
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.HostLoadReadopt);
            Assert.True(new CoopConfig().HostLoadReadopt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HostLoadReadoptCanBeTurnedOff()
    {
        string path = WriteTemp("""
            [coop]
            mode = host
            host_load_readopt = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.HostLoadReadopt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PartyFastPumpDefaultsOn()
    {
        // CoopConfig.PartyFastPump's own doc comment: two earlier live A/Bs (2026-08-23) found
        // no benefit, but both were confounded by the companion follow-AI's permanent stall bug
        // of the time (a companion that never moves has no position lag to reduce). With that
        // bug fixed (rounds 80/84/88), a third A/B (2026-08-24, docs/research/lag-audit.md)
        // against a companion actually walking/following measured a real, reproduced ~45-55%
        // cut in companion-position lag with no frame-time regression — the same bar
        // DialogueFastJoin and the rejoin gates cleared earlier.
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.PartyFastPump);
            Assert.True(new CoopConfig().PartyFastPump);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PartyFastPumpCanBeTurnedOff()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            party_fast_pump = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.PartyFastPump);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MovieRelayDefaultsOnWhenKeyIsAbsent()
    {
        // Matches the plugin's own copy of this flag (MidModuleMovieController's
        // PluginConfig.ReadBool("movie_relay", true)) — an absent key must land on the same
        // answer on both sides, following the same "!ContainsKey || IsTruthy" rule
        // client_door_refusal and transition_announce already use.
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.MovieRelayEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MovieRelayCanBeTurnedOff()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            movie_relay = 0
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.False(c.MovieRelayEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- script_item_sync / companion_xp_sync: default ON when the key is absent from an existing file (fixed 2026-08-28; docs/CONFIG.md
    // footnote 3) ----------------------------------------------------------------------------
    //
    // Both properties' own doc comments (and their `= true` record-property initializers) say
    // "ON by default". That default is only reachable through `new CoopConfig()` directly - the
    // real construction path, CoopConfig.TryLoad, parses both with a plain `IsTruthy(...)` (no
    // `!ContainsKey(...) ||` guard the genuinely-ON-by-default keys like DialogueFastJoin use),
    // so an absent key resolves OFF. AgentComposition.Compose then reads them as
    // `config?.ScriptItemSync ?? false` / `config?.CompanionXpSync ?? false` - explicitly
    // `?? false`, not `?? true` - so a missing coop.ini gives the same OFF answer. The shipped
    // profiles/coop.ini.example sets both to `1` explicitly, so a packaged install ships ON
    // either way; only a hand-edited coop.ini with the line deleted (or none at all) falls back
    // to OFF, silently, contradicting the "ON by default" comments. These tests pin the ACTUAL
    // behaviour so a future change to either half (the parse rule or the `?? false` fallback)
    // is caught as a behaviour change that needs a docs/CONFIG.md update, not a silent drift.

    [Fact]
    public void ScriptItemSyncDefaultsOnWhenTheKeyIsAbsentFromAnExistingFile()
    {
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.ScriptItemSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptItemSyncCanBeTurnedOn()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            script_item_sync = 1
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.ScriptItemSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompanionXpSyncDefaultsOnWhenTheKeyIsAbsentFromAnExistingFile()
    {
        string path = WriteTemp("mode = join");

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.CompanionXpSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompanionXpSyncCanBeTurnedOn()
    {
        string path = WriteTemp("""
            [coop]
            mode = join
            companion_xp_sync = 1
            """);

        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.CompanionXpSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- player-facing vs. developer-switch classification (see CoopConfig.cs's own remarks
    // and docs/CONFIG.md's "Developer switches" table) --------------------------------------

    [Theory]
    [InlineData("mode")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("chat_overlay_fade_ms")]
    [InlineData("dinput8_next")]
    [InlineData("MENU_BUTTON")] // case-insensitive
    public void PlayerFacingKeysAreRecognisedAsPlayerFacing(string key)
    {
        Assert.True(CoopConfig.IsPlayerFacingKey(key));
        Assert.False(CoopConfig.IsKnownDeveloperKey(key));
    }

    [Theory]
    [InlineData("local_combat")]
    [InlineData("party_pool_sync")]
    [InlineData("threat_retarget")]
    [InlineData("party_follow_stall_recovery_method")]
    [InlineData("DEJITTER")] // case-insensitive
    public void KnownDeveloperKeysAreRecognisedAsDeveloperSwitches(string key)
    {
        Assert.False(CoopConfig.IsPlayerFacingKey(key));
        Assert.True(CoopConfig.IsKnownDeveloperKey(key));
    }

    [Fact]
    public void CombatStateSyncIsNoLongerKnownToEitherSet()
    {
        // Retired 2026-08-27, removed entirely during the player-settings cleanup: a coop.ini
        // that still has this line (an old hand-edited or pre-cleanup file) must classify as
        // "unknown", not "developer switch" — nothing reads it any more.
        Assert.False(CoopConfig.IsPlayerFacingKey("combat_state_sync"));
        Assert.False(CoopConfig.IsKnownDeveloperKey("combat_state_sync"));
    }

    [Fact]
    public void ClassifyNonPlayerKeysSplitsDeveloperSwitchesFromUnknownKeys()
    {
        (List<string> developer, List<string> unknown) = CoopConfig.ClassifyNonPlayerKeys(
            ["mode", "local_combat", "not_a_real_key", "chat_overlay", "threat_retarget"]);

        // Player-facing keys (mode, chat_overlay) are silently skipped by the classifier —
        // they show up in neither list.
        Assert.Equal(["local_combat", "threat_retarget"], developer);
        Assert.Equal(["not_a_real_key"], unknown);
    }
}
