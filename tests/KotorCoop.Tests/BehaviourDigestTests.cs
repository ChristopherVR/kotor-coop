using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="BehaviourDigest"/> construction and comparison — the negotiation layer
/// docs/roadmap-seamless.md Tier 1 item 7 asks for. Wire round-trip (Hello/Welcome carrying
/// it) is covered separately in <see cref="PacketCodecTests"/>; this file is the pure data
/// model: the digest itself, and <see cref="BehaviourCompatibility.Compare"/>.
/// </summary>
public class BehaviourDigestTests
{
    private static BehaviourDigest AllOn(int interpMs = 80, string followLead = "any") =>
        BehaviourDigest.Compute(
            localCombat: true, companionLocalCombat: true, partyFastPump: true, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
            mapRevealSync: true, requireIdenticalSave: true, interpolationMs: interpMs, followLead: followLead);

    [Fact]
    public void ComputeSetsExactlyTheFlagsPassedTrue()
    {
        BehaviourDigest digest = BehaviourDigest.Compute(
            localCombat: true, companionLocalCombat: false, partyFastPump: true, partySync: false,
            clientDoorRefusal: false, transitionAnnounce: false, clientLoadRejoin: false, hostLoadReadopt: false,
            effectSync: false, triggerRelay: false, clientEncounters: false, dialogueItemSync: false,
            dialogueRecruitSync: false, scriptItemSync: false, movieRelay: false, livenessSync: false,
            mapRevealSync: false, requireIdenticalSave: false, interpolationMs: 40, followLead: "host");

        Assert.True(digest.Has(BehaviourFlags.LocalCombat));
        Assert.True(digest.Has(BehaviourFlags.PartyFastPump));
        Assert.False(digest.Has(BehaviourFlags.PartySync));
        Assert.False(digest.Has(BehaviourFlags.EffectSync));
        Assert.Equal(40, digest.InterpolationMs);
        Assert.Equal("host", digest.FollowLead);
    }

    [Fact]
    public void EmptyOrNullFollowLeadNormalisesToAny()
    {
        BehaviourDigest fromCompute = BehaviourDigest.Compute(
            localCombat: false, companionLocalCombat: false, partyFastPump: false, partySync: false,
            clientDoorRefusal: false, transitionAnnounce: false, clientLoadRejoin: false, hostLoadReadopt: false,
            effectSync: false, triggerRelay: false, clientEncounters: false, dialogueItemSync: false,
            dialogueRecruitSync: false, scriptItemSync: false, movieRelay: false, livenessSync: false,
            mapRevealSync: false, requireIdenticalSave: false, interpolationMs: 0, followLead: "");

        Assert.Equal("any", fromCompute.FollowLead);

        BehaviourDigest fromFields = BehaviourDigest.FromFields(0, 0, null);
        Assert.Equal("any", fromFields.FollowLead);
    }

    [Fact]
    public void HashIsStableAcrossEqualDigestsAndDiffersWhenAFlagChanges()
    {
        BehaviourDigest a = AllOn();
        BehaviourDigest b = AllOn();
        Assert.Equal(a.Hash, b.Hash);

        BehaviourDigest c = BehaviourDigest.Compute(
            localCombat: false, companionLocalCombat: true, partyFastPump: true, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
            mapRevealSync: true, requireIdenticalSave: true, interpolationMs: 80, followLead: "any");

        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Fact]
    public void FromFieldsRecomputesTheHashRatherThanTrustingAWireValue()
    {
        BehaviourDigest computed = AllOn();

        // FromFields never takes a Hash parameter — a wire reader has no way to lie about it,
        // which is the whole point (PacketCodec never sends Hash at all).
        BehaviourDigest rebuilt = BehaviourDigest.FromFields(computed.Flags, computed.InterpolationMs, computed.FollowLead);

        Assert.Equal(computed.Hash, rebuilt.Hash);
    }

    [Fact]
    public void UnrecognisedSchemeIsHandledLikeContentDigestUnknown()
    {
        // Mirrors ContentDigest's own "unknown scheme -> treat as no information" rule —
        // PacketCodecTests exercises the actual wire path; this just documents the same
        // contract the default(BehaviourDigest) value already satisfies (everything off).
        BehaviourDigest unknown = default;
        Assert.Equal(0u, unknown.Flags);
        Assert.False(unknown.Has(BehaviourFlags.PartySync));
    }

    [Fact]
    public void IdenticalDigestsCompareWithNoDifferences()
    {
        BehaviourComparisonResult result = BehaviourCompatibility.Compare(AllOn(), AllOn());
        Assert.True(result.IsEmpty);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ADifferingHostAuthoritativeKeyIsReportedAsSuch()
    {
        BehaviourDigest host = AllOn();
        BehaviourDigest peer = BehaviourDigest.Compute(
            localCombat: true, companionLocalCombat: true, partyFastPump: true, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: false /* differs */, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
            mapRevealSync: true, requireIdenticalSave: true, interpolationMs: 80, followLead: "any");

        BehaviourComparisonResult result = BehaviourCompatibility.Compare(host, peer);

        BehaviourDifference diff = Assert.Single(result.Differences);
        Assert.Equal("effect_sync", diff.Key);
        Assert.True(diff.HostAuthoritative);
        Assert.Equal("1", diff.HostValue);
        Assert.Equal("0", diff.YourValue);
        Assert.Equal("effect_sync host=1 you=0", diff.Text);
    }

    [Fact]
    public void ADifferingNonHostAuthoritativeKeyIsReportedButNotFlagged()
    {
        BehaviourDigest host = AllOn();
        BehaviourDigest peer = BehaviourDigest.Compute(
            localCombat: false /* differs, not host-authoritative */, companionLocalCombat: true,
            partyFastPump: true, partySync: true, clientDoorRefusal: true, transitionAnnounce: true,
            clientLoadRejoin: true, hostLoadReadopt: true, effectSync: true, triggerRelay: true,
            clientEncounters: true, dialogueItemSync: true, dialogueRecruitSync: true, scriptItemSync: true,
            movieRelay: true, livenessSync: true, mapRevealSync: true, requireIdenticalSave: true,
            interpolationMs: 80, followLead: "any");

        BehaviourComparisonResult result = BehaviourCompatibility.Compare(host, peer);

        BehaviourDifference diff = Assert.Single(result.Differences);
        Assert.Equal("local_combat", diff.Key);
        Assert.False(diff.HostAuthoritative);
    }

    [Fact]
    public void InterpMsAndFollowLeadDifferencesAreReportedAsText()
    {
        BehaviourDigest host = AllOn(interpMs: 80, followLead: "any");
        BehaviourDigest peer = AllOn(interpMs: 150, followLead: "host");

        BehaviourComparisonResult result = BehaviourCompatibility.Compare(host, peer);

        Assert.Equal(2, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.Key == "interp_ms" && d.HostValue == "80" && d.YourValue == "150");
        Assert.Contains(result.Differences, d => d.Key == "follow_lead" && d.HostValue == "any" && d.YourValue == "host");
        Assert.All(result.Differences, d => Assert.False(d.HostAuthoritative));
    }

    [Fact]
    public void EveryHostAuthoritativeKeyFromTheDesignIsMarkedAsSuch()
    {
        // Pins the exact set docs/research/mod-compatibility.md's "Behaviour-flag negotiation"
        // names as host-authoritative — a change here should be a deliberate design decision,
        // not an accidental drop.
        string[] expected =
        [
            "party_sync", "effect_sync", "trigger_relay", "liveness_sync", "map_reveal_sync",
            "script_item_sync", "dialogue_item_sync", "dialogue_recruit_sync", "movie_relay",
        ];

        BehaviourDigest host = AllOn();
        BehaviourDigest peer = BehaviourDigest.Compute(
            localCombat: false, companionLocalCombat: false, partyFastPump: false, partySync: false,
            clientDoorRefusal: false, transitionAnnounce: false, clientLoadRejoin: false, hostLoadReadopt: false,
            effectSync: false, triggerRelay: false, clientEncounters: false, dialogueItemSync: false,
            dialogueRecruitSync: false, scriptItemSync: false, movieRelay: false, livenessSync: false,
            mapRevealSync: false, requireIdenticalSave: false, interpolationMs: 80, followLead: "any");

        BehaviourComparisonResult result = BehaviourCompatibility.Compare(host, peer);

        foreach (string key in expected)
        {
            BehaviourDifference diff = Assert.Single(result.Differences, d => d.Key == key);
            Assert.True(diff.HostAuthoritative, $"{key} should be host-authoritative");
        }

        string[] notExpected =
        [
            "local_combat", "companion_local_combat", "party_fast_pump", "client_door_refusal",
            "transition_announce", "client_load_rejoin", "host_load_readopt", "client_encounters",
            "require_identical_save",
        ];
        foreach (string key in notExpected)
        {
            BehaviourDifference diff = Assert.Single(result.Differences, d => d.Key == key);
            Assert.False(diff.HostAuthoritative, $"{key} should NOT be host-authoritative");
        }
    }
}
