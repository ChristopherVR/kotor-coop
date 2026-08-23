using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The slot -&gt; story-companion-blueprint mapping <see cref="Odyssey.AddAvailableNpcByTemplate"/>
/// uses, split out so the data itself is unit-tested (the engine call around it is not — see
/// <see cref="PartyRosterRecruitRule"/> for the same split applied to the decision half of the
/// same feature). Every value here was independently re-derived by disassembling the GOG
/// install's own shipped recruit scripts (round 2026-08-24,
/// docs/research/dialogue-consequences.md) — not merely asserted from "standard nwscript.nss
/// order" — and slot 2 = Carth is additionally LIVE-confirmed
/// (docs/research/party-reconciliation.md round 75).
/// </summary>
public class PartyRosterTemplateTableTests
{
    [Theory]
    [InlineData(0, "p_bastilla")]
    [InlineData(1, "p_cand")]
    [InlineData(2, "p_carth")] // live-confirmed, party-reconciliation.md round 75
    [InlineData(3, "p_hk47")]
    [InlineData(4, "p_jolee")]
    [InlineData(5, "p_juhani")]
    [InlineData(6, "p_mission")]
    [InlineData(7, "p_t3m4")]
    [InlineData(8, "p_zaalbar")]
    public void EachSlotResolvesToItsDisassemblyConfirmedTemplate(int slot, string expectedResref)
    {
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(slot, out string resref));
        Assert.Equal(expectedResref, resref);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(100)]
    public void AnOutOfRangeSlotIsRefused(int slot)
    {
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(slot, out string resref));
        Assert.Equal(string.Empty, resref);
    }

    [Fact]
    public void SlotCountMatchesTheEnginesFixedNineSlotRoster()
    {
        Assert.Equal(9, PartyRosterTemplateTable.SlotCount);
    }

    [Fact]
    public void NoTwoSlotsShareATemplate()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int slot = 0; slot < PartyRosterTemplateTable.SlotCount; slot++)
        {
            Assert.True(PartyRosterTemplateTable.TryGetTemplate(slot, out string resref));
            Assert.True(seen.Add(resref), $"slot {slot}'s template '{resref}' is a duplicate");
        }
    }
}
