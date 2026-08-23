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

            Assert.NotNull(c.ResolveEndPoint());
            Assert.Equal(27140, c.ResolveEndPoint()!.Port);
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
}
