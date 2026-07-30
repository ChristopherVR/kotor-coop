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
            Assert.False(c.MenuButton);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
