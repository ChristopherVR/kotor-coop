using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PersonalCaptureFingerprintTests
{
    private static ItemIdentity Stack(ushort n) => new(n, 0, 0, 0, ItemIdentity.FlagKnown);

    [Fact]
    public void ItemOrderAndCaseDoNotMatter()
    {
        string a = PersonalCaptureFingerprint.Compose(535, 100, [("g_i_medeqpmnt01", Stack(18)), ("g_w_fraggren01", Stack(8))]);
        string b = PersonalCaptureFingerprint.Compose(535, 100, [("G_W_FRAGGREN01", Stack(8)), ("g_i_medeqpmnt01", Stack(18))]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void LootMergedIntoAnExistingStackIsAChange()
    {
        // Picked-up medpacs join the stack the player already has: same item, bigger stack.
        string before = PersonalCaptureFingerprint.Compose(535, 100, [("g_i_medeqpmnt01", Stack(18))]);
        string after = PersonalCaptureFingerprint.Compose(535, 100, [("g_i_medeqpmnt01", Stack(25))]);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void CreditsExperienceAndUpgradesAreChanges()
    {
        string baseline = PersonalCaptureFingerprint.Compose(535, 100, [("g_w_blstrpstl001", Stack(1))]);
        Assert.NotEqual(baseline, PersonalCaptureFingerprint.Compose(1234, 100, [("g_w_blstrpstl001", Stack(1))]));
        Assert.NotEqual(baseline, PersonalCaptureFingerprint.Compose(535, 150, [("g_w_blstrpstl001", Stack(1))]));
        Assert.NotEqual(baseline, PersonalCaptureFingerprint.Compose(535, 100,
            [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0x4, ItemIdentity.FlagKnown))]));
    }
}
