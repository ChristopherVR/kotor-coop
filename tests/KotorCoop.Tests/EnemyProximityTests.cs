using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What the enemy channel does when the two players are NOT standing together — the case the
/// whole design is quietest about, because everything was built and measured with both
/// characters in the same corridor.
///
/// Two rules are asserted here:
///
///   * an enemy snapshot only applies in the module its object ids were READ in, because ids
///     are assigned per module load and the same number names a different creature elsewhere;
///   * the census, which is capped, sheds by distance to the NEAREST player rather than to
///     whoever is publishing — otherwise a partner across the module loses their fight to the
///     cap at the moment it starts to matter.
/// </summary>
public class EnemyProximityTests
{
    private const string Here = "end_m01aa";
    private const string Elsewhere = "tar_m02aa";

    // ---- the module gate -------------------------------------------------------------

    [Fact]
    public void AClientAppliesTheOwnersEnemiesWhileTheyShareAModule()
    {
        ClientEnemyView view = new();
        view.Receive(Here, [new EnemyState(0x14, 9, 15, false)]);

        Assert.Single(view.GetEnemies(Here));
    }

    [Fact]
    public void AClientDropsTheOwnersEnemiesWhileItIsSomewhereElse()
    {
        ClientEnemyView view = new();
        view.Receive(Elsewhere, [new EnemyState(0x14, 9, 15, false)]);

        // 0x14 exists in this module too, and is a completely different creature.
        Assert.Empty(view.GetEnemies(Here));
    }

    [Fact]
    public void TheGateReopensOnceTheOwnerIsBackInOurModule()
    {
        ClientEnemyView view = new();
        view.Receive(Elsewhere, [new EnemyState(0x14, 9, 15, false)]);
        Assert.Empty(view.GetEnemies(Here));

        view.Receive(Here, [new EnemyState(0x14, 4, 15, false)]);
        Assert.Single(view.GetEnemies(Here));
    }

    [Fact]
    public void AnUnknownModuleOnEitherSideIsNotTreatedAsSomewhereElse()
    {
        // A profile that cannot read the module name reports an empty string. Treating
        // "unknown" as "not here" would silently disable the whole channel, which reads
        // exactly like a broken network layer — the same trap the peer filter documents.
        ClientEnemyView view = new();

        view.Receive(string.Empty, [new EnemyState(0x14, 9, 15, false)]);
        Assert.Single(view.GetEnemies(Here));

        view.Receive(Here, [new EnemyState(0x14, 9, 15, false)]);
        Assert.Single(view.GetEnemies(string.Empty));
    }

    /// <summary>The client's half of the enemy channel, in isolation: what it stores when a
    /// snapshot arrives and what it hands back for a given module. Mirrors CoopClient.</summary>
    private sealed class ClientEnemyView
    {
        private IReadOnlyList<EnemyState> _enemies = [];
        private string _module = string.Empty;

        public void Receive(string module, IReadOnlyList<EnemyState> enemies)
        {
            _module = module;
            _enemies = enemies;
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) =>
            _module.Length == 0
            || module.Length == 0
            || string.Equals(_module, module, StringComparison.OrdinalIgnoreCase)
                ? _enemies
                : [];
    }

    // ---- the census centre -----------------------------------------------------------

    [Fact]
    public void TheCensusKeepsTheCreaturesAroundADistantPartner()
    {
        // Three more creatures than the cap, all of them nearer the publisher than the
        // partner's one is. Measured from the publisher alone, the partner's creature is
        // last and falls off; measured from the nearest player, it is the closest thing
        // in the module to somebody.
        Vector3f publisher = new(0f, 0f, 0f);
        Vector3f partner = new(200f, 0f, 0f);

        List<(uint Id, Vector3f At)> creatures = [];
        for (uint i = 0; i < Cap + 3; i++)
        {
            creatures.Add((i, new Vector3f(i + 1, 0f, 0f)));
        }

        creatures.Add((999, new Vector3f(201f, 0f, 0f))); // beside the partner

        Assert.DoesNotContain(999u, Shed(creatures, publisher, []));
        Assert.Contains(999u, Shed(creatures, publisher, [partner]));
    }

    [Fact]
    public void APartnerInAnotherModuleDoesNotPullTheCensusAcrossTheMap()
    {
        // Peers are filtered to this module before they reach the census, so "no peers
        // here" must behave exactly like solo — this pins that the empty list is the
        // signal rather than a special case somewhere upstream.
        Vector3f publisher = new(0f, 0f, 0f);

        List<(uint Id, Vector3f At)> creatures = [];
        for (uint i = 0; i < Cap + 1; i++)
        {
            creatures.Add((i, new Vector3f(i + 1, 0f, 0f)));
        }

        uint[] kept = Shed(creatures, publisher, []);
        Assert.Equal(Cap, kept.Length);
        Assert.DoesNotContain((uint)Cap, kept); // the farthest one, and only that one, is shed
    }

    // ---- the killing blow ------------------------------------------------------------

    [Fact]
    public void APeersKillIsHonouredOnceWeHaveAlreadyFlooredThatEnemy()
    {
        // The peer fought it down through the floor, so our copy sits at 1 and alive. That
        // is the whole precondition: only then can their "it is dead" be the last point of
        // damage rather than a deletion.
        Assert.True(ShouldExecutePeerKill(reportedDead: true, ourHealth: 1, plot: false));
    }

    [Fact]
    public void APeersKillIsRefusedForSomethingNobodyHasBeatenDown()
    {
        // A stray or stale report can shave health through the floor rule; it must never be
        // able to delete a healthy creature.
        Assert.False(ShouldExecutePeerKill(reportedDead: true, ourHealth: 12, plot: false));
    }

    [Fact]
    public void AlreadyDeadIsNotKilledAgain()
    {
        // Our copy at or below zero means the death already ran here — most likely because
        // WE killed it and the peer is echoing that back.
        Assert.False(ShouldExecutePeerKill(reportedDead: true, ourHealth: 0, plot: false));
        Assert.False(ShouldExecutePeerKill(reportedDead: true, ourHealth: -3, plot: false));
    }

    [Fact]
    public void APlotCreatureIsNeverKilledByAPeersReport()
    {
        // Un-killable is the module's rule and our own census decides it, never the report.
        Assert.False(ShouldExecutePeerKill(reportedDead: true, ourHealth: 1, plot: true));
    }

    [Fact]
    public void ALivingReportNeverKills()
    {
        Assert.False(ShouldExecutePeerKill(reportedDead: false, ourHealth: 1, plot: false));
    }

    /// <summary>The owner's decision rule, mirroring KotorGameBridge.TryApplyEnemyFloors.</summary>
    private static bool ShouldExecutePeerKill(bool reportedDead, int ourHealth, bool plot) =>
        !plot && reportedDead && ourHealth is > 0 and <= 1;

    private const int Cap = 32;

    /// <summary>The census's own keep-or-shed rule: order by distance to the nearest player,
    /// then take the cap. (The live version sorts in-combat creatures first; that leg is
    /// unchanged and orthogonal to which player the distance is measured from.)</summary>
    private static uint[] Shed(
        List<(uint Id, Vector3f At)> creatures, Vector3f publisher, Vector3f[] otherPlayers)
    {
        return [.. creatures
            .OrderBy(c => NearestSquared(c.At, publisher, otherPlayers))
            .Take(Cap)
            .Select(c => c.Id)];
    }

    private static float NearestSquared(Vector3f at, Vector3f publisher, Vector3f[] others)
    {
        float best = Planar(at, publisher);
        foreach (Vector3f other in others)
        {
            float squared = Planar(at, other);
            if (squared < best)
            {
                best = squared;
            }
        }

        return best;
    }

    private static float Planar(Vector3f a, Vector3f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
