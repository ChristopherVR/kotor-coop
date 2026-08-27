using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// KotorGameBridge.IsTransitionalModuleName: the pure "does this module name look like the
/// engine's own mid-transition placeholder" check, shared by DisableLocalEncounters (never
/// write into a world being freed) and TryReadLocalState (never adopt a transitional read as a
/// success — see docs/research/disconnect-reconnect.md §10, the 2026-08-27 live FAILURE this
/// was extracted to fix). Exposed `internal` for exactly this test via InternalsVisibleTo.
/// </summary>
public class TransitionalModuleNameTests
{
    [Theory]
    [InlineData("area1")]
    [InlineData("area1F")]
    [InlineData("AREA2A")]
    [InlineData("areaFF")]
    public void AreaPlusHexDigitsIsTransitional(string module)
    {
        Assert.True(KotorGameBridge.IsTransitionalModuleName(module));
    }

    [Theory]
    [InlineData("end_m01aa")]
    [InlineData("tar_m03aa")]
    [InlineData("")]
    [InlineData("area")]        // exactly the prefix, no digits: not what the fallback ever emits
    [InlineData("areaXYZ")]     // not hex
    [InlineData("area1 ")]      // trailing space is not a hex digit
    public void RealOrEmptyModuleNamesAreNotTransitional(string module)
    {
        Assert.False(KotorGameBridge.IsTransitionalModuleName(module));
    }
}
