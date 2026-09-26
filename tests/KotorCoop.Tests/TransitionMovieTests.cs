using System.Text.RegularExpressions;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Cinematic parity: both players should see the same film.
///
/// Measured on 2026-08-15 that they do not. The owner's transition parked its main thread for
/// 7,407 ms playing <c>01c</c>; the follower flushed, followed and logged no <c>cinematic:</c>
/// line at all. The follower was never GATED — its <c>move_to_module_movies</c> queue is empty,
/// and the engine plays what is in the queue — so the only thing that has to travel is the
/// names, and the receiving half was already proved live (run A: a queued movie played for
/// 15,531 ms on the co-op warp path).
///
/// These are the parts of that wire that can be asserted without a game.
/// </summary>
public class TransitionMovieTests
{
    // ---- the resref strip, which is load-bearing -----------------------------------

    /// <summary>
    /// ⚠ THE ONE THAT WOULD HAVE FAILED SILENTLY. The engine does not store the name a script
    /// passed to <c>StartNewModule</c> — <c>AddMovieToExoArrayList</c> resolves it to a path
    /// and stores THAT, so reading the queue back yields <c>.\movies\01c.bik</c>. Feed the
    /// stored form back in and the add takes the no-colon branch, builds
    /// <c>MOVIES:.\movies\01c.bik</c>, cannot open it, and refuses the movie — returning 0 and
    /// adding nothing. The follower would then transition with an empty queue, exactly as if
    /// the channel did not exist.
    /// </summary>
    [Theory]
    [InlineData(@".\movies\01c.bik", "01c")]
    [InlineData(@".\MOVIES\05_1c.BIK", "05_1c")]
    [InlineData("movies/01f.bik", "01f")]
    [InlineData("01c", "01c")]
    [InlineData("  01c  ", "01c")]
    public void TheEnginesStoredPathIsReducedToTheResRefItWillAccept(string stored, string expected)
    {
        Assert.True(MovieStaging.TryStripToResRef(stored, out string resref));
        Assert.Equal(expected, resref);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@".\movies\.bik")]                 // nothing left after stripping
    [InlineData(@".\movies\not a name.bik")]       // not a resref
    [InlineData("aaaaaaaaaaaaaaaaa")]              // 17 chars: past a resref
    public void AStoredNameThatCannotBecomeAResRefIsRefusedRatherThanForwarded(string? stored)
    {
        Assert.False(MovieStaging.TryStripToResRef(stored, out string resref));
        Assert.Equal(string.Empty, resref);
    }

    /// <summary>A name that survives the strip must still be one the engine would take —
    /// forwarding something we already know will be refused is just a slower refusal.</summary>
    [Fact]
    public void EveryStrippedNameIsAValidMovieName()
    {
        Assert.True(MovieStaging.TryStripToResRef(@".\movies\01c.bik", out string resref));
        Assert.True(MovieStaging.IsMovieName(resref));
    }

    // ---- the wire ------------------------------------------------------------------

    [Fact]
    public void TheAnnouncementRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        TransitionMoviePacket original = new(PeerId: 3, "end_m01ab", Serial: 9, ["01c", "01f"]);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TransitionMoviePacket? read = PacketCodec.ReadTransitionMovies(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(original.PeerId, read!.PeerId);
        Assert.Equal(original.Module, read.Module);
        Assert.Equal(original.Serial, read.Serial);
        Assert.Equal(original.Movies, read.Movies);
    }

    /// <summary>An announcement with no movies is still a well-formed packet — it is what a
    /// transition that carries no cinematic looks like, and it must not read as malformed.</summary>
    [Fact]
    public void AnEmptyAnnouncementRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        int length = PacketCodec.Write(buffer, new TransitionMoviePacket(1, "tar_m03aa", 2, []));
        Assert.True(length > 0);

        TransitionMoviePacket? read = PacketCodec.ReadTransitionMovies(buffer[..length]);
        Assert.NotNull(read);
        Assert.Empty(read!.Movies);
    }

    /// <summary>The full six the engine allows, at the resref cap, still fits a datagram.</summary>
    [Fact]
    public void AFullSixMovieAnnouncementFitsOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        string[] movies =
        [
            new('a', 16), new('b', 16), new('c', 16),
            new('d', 16), new('e', 16), new('f', 16),
        ];

        int length = PacketCodec.Write(buffer, new TransitionMoviePacket(8, new string('m', 32), 1, movies));

        Assert.True(length > 0, "a full announcement did not fit and was dropped");
        Assert.True(length <= ProtocolConstants.MaxPacketSize);
        Assert.Equal(movies, PacketCodec.ReadTransitionMovies(buffer[..length])!.Movies);
    }

    /// <summary>
    /// A count this side never writes is malformed, not an allocation instruction. The count
    /// is a byte off the wire and it decides an array size, so a peer choosing 200 must be
    /// refused rather than believed.
    /// </summary>
    [Fact]
    public void AnImpossibleMovieCountIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new TransitionMoviePacket(1, "end_m01aa", 1, ["01c"]));

        // The count byte sits after the header, peer id, module string and serial. Find it by
        // its value rather than by arithmetic over the header layout.
        int countIndex = -1;
        for (int i = length - 1; i >= 0; i--)
        {
            if (buffer[i] == 1 && i + 1 < length && buffer[i + 1] == 3)
            {
                countIndex = i; // the count, followed by the length byte of "01c"
                break;
            }
        }

        Assert.True(countIndex > 0, "could not locate the movie count in the encoded packet");

        buffer[countIndex] = (byte)(TransitionMoviePacket.MaxMovies + 1);
        Assert.Null(PacketCodec.ReadTransitionMovies(buffer[..length]));
    }

    // ---- the numbers that three agents have already collided on --------------------

    /// <summary>
    /// ⚠ Two packet types sharing a number is a wire-level catastrophe that no test caught
    /// before: the receiver dispatches one payload into the other's reader. Three agents in
    /// this tree have already claimed "the next free" cheat kind simultaneously, and packet
    /// types are the same hazard with a worse blast radius.
    /// </summary>
    [Fact]
    public void NoTwoPacketTypesShareANumber()
    {
        Dictionary<byte, List<string>> byValue = [];

        foreach (PacketType type in Enum.GetValues<PacketType>())
        {
            byte value = (byte)type;
            if (!byValue.TryGetValue(value, out List<string>? names))
            {
                names = [];
                byValue[value] = names;
            }

            names.Add(type.ToString());
        }

        List<string> clashes =
        [
            .. byValue.Where(entry => entry.Value.Count > 1)
                .Select(entry => $"{entry.Key} = {string.Join(" and ", entry.Value)}"),
        ];

        Assert.True(clashes.Count == 0, "two packet types share a number: " + string.Join("; ", clashes));
    }

    /// <summary>
    /// The channel added with protocol 25 must actually be on the enum at the number its
    /// changelog claims — and 39 must stay free, because network-topology.md reserves it on
    /// paper for a channel nobody has written yet.
    /// </summary>
    [Fact]
    public void TheTransitionMovieChannelIsWhereTheChangelogSaysItIs()
    {
        Assert.Equal(40, (byte)PacketType.TransitionMovies);
        Assert.DoesNotContain(Enum.GetValues<PacketType>(), type => (byte)type == 39);
    }

    /// <summary>
    /// The shared block version is one number in two files, and a half-bump does not fail
    /// loudly — it reads as "plugin shared block not found", which looks like a broken
    /// install. A guard exists; this asserts it covers the number this pass moved.
    /// </summary>
    [Fact]
    public void TheBlockVersionMovedInBothHalves()
    {
        string root = RepositoryRoot();

        string pluginSource = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Plugin", "PluginHost.cs"));
        Match version = Regex.Match(pluginSource, @"_block->Version\s*=\s*(\d+)\s*;");
        Assert.True(version.Success, "could not find _block->Version in PluginHost.cs");

        string bridgeSource = File.ReadAllText(
            Path.Combine(root, "src", "KotorCoop.Memory", "KotorGameBridge.cs"));
        Match signature = Regex.Match(
            bridgeSource, @"PluginSignature\s*=\s*""(?:[0-9A-F]{2} ){8}([0-9A-F]{2})");
        Assert.True(signature.Success, "could not find the version byte in PluginSignature");

        Assert.Equal(
            int.Parse(version.Groups[1].Value),
            Convert.ToInt32(signature.Groups[1].Value, 16));

        // And it really is the LATEST version shipped (not just some number both halves
        // happen to agree on), so a future edit that moves one half and not the other
        // cannot pass by moving both to the wrong place. This pass's own bump was 42 (the
        // transition-cinematic channel); it has moved again since (43 the agent-heartbeat
        // field, 44 the peer-effect channel, 47 the transition-announce channel, 48 the
        // mid-module movie relay region, 49 the world-load edge (roadmap item 14 phase 2),
        // 50 the container-lock target ids (InteractionKind.Container's object-id key,
        // docs/research/container-sync.md), 51 the local/remote dialogue-forced bit
        // (docs/research/dialogue-sync.md's forced-conversation section), 52 the
        // mid-session-load-request flag (the `loadgame` verb, docs/research/session-save.md's
        // dated entry), 53 the party-roster body identity region (two nine-slot object-id
        // arrays, docs/research/dialogue-consequences.md's 2026-08-24 "stale mirrored body"
        // entry), 54 the companion-equipment channel (block grown to 0x4100,
        // docs/research/companion-state-sync.md), 55 the effective-behaviour-override flags
        // (docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap), 56 the
        // solo-segment spectate flag (docs/research/star-forge-finale.md), 57 the container-lock
        // explicit arbitration region (docs/research/container-sync.md), 58 the
        // item-template-spawn ring (block grown to 0x4700, docs/research/item-identity.md §13),
        // 59 the item-template-spawn ring's own per-entry cheat flag (block grown to 0x4800,
        // docs/research/party-pool-sync.md §20), 60 the 2026-08-29 seamless wave (minigame peer
        // lock, new-character join handshake, cheat kinds 53/56/59/60), 61..63 the 2026-09-02
        // combat wave (block grown to 0x4B00: N-slot local_combat at 0x4800, per-attacker
        // damage attribution at 0x4900, the companion-damage channel at 0x4A00), 64 the
        // item-template-spawn ring's per-entry created stack count (flags bits 8..23, no
        // layout change, docs/research/shared-inventory-v2.md §17.7), 65 the same day's
        // materialised-owner-id table (block grown to 0x4C00, SharedBlock.
        // OffsetMaterialisedOwnerIds, docs/research/enemy-materialisation.md's 2026-09-02
        // section), 66 the 2026-09-03 threat-retarget diagnostics region (0x4B80 inside the
        // existing 0x4C00, docs/research/peer-damage-ledger.md §9; the branch bumped the plugin
        // half only and the rig refused to start with "plugin shared block not found" until
        // the signature byte followed) — see the changelog comments beside `_block->Version`
        // and `PluginSignature` for the full history. 67 = the 2026-09-03 galaxy-map departure
        // relay (block grown to 0x4D00, SharedBlock.OffsetGalaxyDeparture*, docs/research/
        // galaxy-map-and-party-select.md's 2026-09-03 relay addendum). 68 = the 2026-09-18
        // class-grant relay (0x4C20 inside the existing 0x4D00, SharedBlock.OffsetClassGrant*,
        // docs/research/class-and-power-grants.md); 69 = the player-count scaling words (0x4C38);
        // 70 = the client-cast enemy-effect relay (0x4C60..0x4C80, docs/research/enemy-effects.md).
        // 71 = companion presence publication sequences in the existing slot padding;
        // 72 = live runtime difficulty sample in the container-loot region's free tail;
        // 73 = agent-published local session role at 0x4E44; 74 = exact native load request
        // provenance at 0x4E50..0x4EA8; 75 adds resolved store prices at 0x4C40 and the
        // item-spawn generation/ticket/load-serial arrays at 0x4F00..0x5300.
        // 76-77 = the 2026-09-24 wave's other streams; 78 = party-placed mine replication at
        // 0x5A00..0x5BBC (block grown to 0x5C00 for the wave, docs/research/mines.md §14).
        // 79 = another in-flight stream's own reservation (0x5C00..0x6000, deliberately left
        // untouched by 80 below). 80 = the "no cap really" pass
        // (docs/research/creature-liveness.md, docs/review-seamless-2026-09-18.md): the
        // 32-entry enemy census/presence/materialised-owner/damage-ledger tables all widen to
        // 128 and relocate into a new region, block grown to 0x8600 — see
        // SharedBlock.Combat.cs and SharedBlock.TotalSize's own remarks.
        // 81 carries the caller's dialogue resource override for forced scene joining.
        Assert.Equal(81, int.Parse(version.Groups[1].Value));
    }

    private static string RepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
