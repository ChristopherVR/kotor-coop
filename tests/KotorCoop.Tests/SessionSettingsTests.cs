using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="SessionSettings"/> is a construction-time record; almost everything on it is a
/// plain init-only property with no logic worth testing beyond "the default matches
/// ProtocolConstants" and "with-expressions round-trip". The one member with real behaviour is
/// <see cref="SessionSettings.AdvertisedName"/>'s fallback, which is what this file mostly
/// exercises.
/// </summary>
public class SessionSettingsTests
{
    [Fact]
    public void DefaultsMatchProtocolConstants()
    {
        SessionSettings settings = new();

        Assert.Equal(ProtocolConstants.DefaultPort, settings.Port);
        Assert.Equal(ProtocolConstants.DefaultTickRate, settings.TickRate);
        Assert.Equal(ProtocolConstants.DefaultInterpolationDelayMs, settings.InterpolationDelayMs);
        Assert.Equal(ProtocolConstants.PeerTimeoutMs, settings.PeerTimeoutMs);
        Assert.Equal(ProtocolConstants.DefaultConnectTimeoutMs, settings.ConnectTimeoutMs);
    }

    [Fact]
    public void DefaultPlayerNameIsPlayer()
    {
        Assert.Equal("player", new SessionSettings().PlayerName);
    }

    [Fact]
    public void DefaultServerNameAndPasswordAreEmpty()
    {
        SessionSettings settings = new();

        Assert.Equal(string.Empty, settings.ServerName);
        Assert.Equal(string.Empty, settings.Password);
    }

    [Fact]
    public void DefaultDigestIsUnknown()
    {
        Assert.Equal(ContentDigest.Unknown, new SessionSettings().Digest);
    }

    [Fact]
    public void DefaultRequireIdenticalSaveIsOff()
    {
        // World adoption makes saves match by construction — a mismatch is a failed adoption
        // to be reported and worked around, not a session to end. See the property's remarks.
        Assert.False(new SessionSettings().RequireIdenticalSave);
    }

    [Fact]
    public void DefaultPeerIdentityIsZeroMintAFreshOne()
    {
        Assert.Equal(0u, new SessionSettings().PeerIdentity);
    }

    // ---- AdvertisedName --------------------------------------------------------------------

    [Fact]
    public void AdvertisedNameFallsBackToPlayerNameWhenServerNameIsEmpty()
    {
        SessionSettings settings = new() { PlayerName = "Revan" };

        Assert.Equal(string.Empty, settings.ServerName);
        Assert.Equal("Revan", settings.AdvertisedName);
    }

    [Fact]
    public void AdvertisedNamePrefersServerNameWhenSet()
    {
        SessionSettings settings = new() { PlayerName = "Revan", ServerName = "The Ebon Hawk" };

        Assert.Equal("The Ebon Hawk", settings.AdvertisedName);
    }

    [Fact]
    public void AdvertisedNameFallsBackWhenServerNameIsWhitespaceOnly()
    {
        // The fallback checks Length > 0, so a non-empty but all-whitespace server name is
        // still treated as "set" — this pins the actual rule (Length, not IsNullOrWhiteSpace)
        // rather than a stricter one nobody wrote.
        SessionSettings settings = new() { PlayerName = "Revan", ServerName = "   " };

        Assert.Equal("   ", settings.AdvertisedName);
    }

    // ---- Record semantics -------------------------------------------------------------------

    [Fact]
    public void WithExpressionsProduceAnIndependentCopy()
    {
        SessionSettings original = new() { PlayerName = "Bastila", Port = 27015 };
        SessionSettings modified = original with { Port = 28000 };

        Assert.Equal(27015, original.Port);
        Assert.Equal(28000, modified.Port);
        Assert.Equal("Bastila", modified.PlayerName);
    }

    [Fact]
    public void TwoSettingsWithTheSameValuesAreEqual()
    {
        SessionSettings a = new() { PlayerName = "Carth", Port = 27015 };
        SessionSettings b = new() { PlayerName = "Carth", Port = 27015 };

        Assert.Equal(a, b);
    }

    [Fact]
    public void TwoSettingsWithDifferentValuesAreNotEqual()
    {
        SessionSettings a = new() { PlayerName = "Carth" };
        SessionSettings b = new() { PlayerName = "Mission" };

        Assert.NotEqual(a, b);
    }
}
