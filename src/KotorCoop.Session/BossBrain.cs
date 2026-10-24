using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>The numbers that go with each of the boss's powers (docs/research/chungus-boss.md).</summary>
public static class BossPowers
{
    /// <summary>How long after the power starts it lands: the moment in the animation the
    /// resonator discharges, the tool strikes, the slam hits the floor.</summary>
    public static int LandDelayMs(BossPower power) => power switch
    {
        BossPower.Snap => 1_300,
        BossPower.Blast => 900,
        BossPower.Gizka => 900,
        BossPower.Drain => 850,
        BossPower.Entrance => 1_500,
        BossPower.Defeat or BossPower.Blink => 0,
        _ => 700,
    };

    /// <summary>How hard it lands, as the power reads it: the share of maximum hit points a
    /// blast or a drain takes, or how much a slow slows.</summary>
    public static byte Strength(BossPower power) => power switch
    {
        BossPower.Snap => 50,
        BossPower.Blast => 30,
        BossPower.Drain => 15,
        _ => 0,
    };

    /// <summary>How long after the discharge lands its answering chime plays.</summary>
    public const int SnapLineDelayMs = 900;

    /// <summary>The sound of the line (k_coop_chline.wav).</summary>
    public const string SnapLine = "k_coop_chline";

    /// <summary>How long time stays stopped. KEEP IN SYNC with the plugin's ChungusRule.</summary>
    public const int TimeStopMs = 3_500;

    /// <summary>Aimed at one player; the others hit everyone.</summary>
    public static bool Targeted(BossPower power) =>
        power is BossPower.Blast or BossPower.Gizka or BossPower.Drain;
}

/// <summary>
/// Decides when the boss uses a power and which. Host only, and pure: the pump tells it the time,
/// how hurt the boss is and who is in the fight, and it answers with a power or nothing.
///
/// He works through the field effects in a shuffled order, one every seven to ten seconds (five to
/// seven once he is badly hurt). The snap is kept for the moments that matter: the first time
/// he drops below 60% of his health, and again below 25%.
/// </summary>
public sealed class BossBrain(uint seed)
{
    public const long FirstPowerDelayMs = 6_000;

    private static readonly BossPower[] Deck =
    [
        BossPower.Blast, BossPower.Lift, BossPower.Gizka, BossPower.Fright, BossPower.Drain, BossPower.Slow,
    ];

    private readonly Random _random = new(unchecked((int)seed));
    private readonly List<BossPower> _hand = [];
    private long _nextAtMs;
    private bool _started;
    private bool _snappedHurt;
    private bool _snappedDying;
    private int _used;

    /// <param name="healthPercent">The boss's health, 0 to 100, or -1 when it is not known.</param>
    /// <param name="players">The peer ids in the fight.</param>
    public BossCue? Next(long nowMs, int healthPercent, IReadOnlyList<uint> players)
    {
        if (!_started)
        {
            _started = true;
            _nextAtMs = nowMs + FirstPowerDelayMs;
            return null;
        }

        if (nowMs < _nextAtMs || players.Count == 0)
        {
            return null;
        }

        bool dying = healthPercent is >= 0 and <= 25;
        _nextAtMs = nowMs + (dying ? _random.Next(5_000, 7_001) : _random.Next(7_000, 10_001));

        BossPower power = Choose(healthPercent);
        _used++;

        ushort target = 0;
        if (BossPowers.Targeted(power))
        {
            uint picked = players[_random.Next(players.Count)];
            if (picked > ushort.MaxValue)
            {
                // A peer id that does not fit the cue's target field: use a power that needs none.
                power = BossPower.Lift;
            }
            else
            {
                target = (ushort)picked;
            }
        }

        return new BossCue(power, BossPowers.Strength(power), target);
    }

    private BossPower Choose(int healthPercent)
    {
        // With his health unknown the snap still comes: third, and every sixth after.
        bool unknown = healthPercent < 0;
        if (!_snappedHurt && (unknown ? _used == 2 : healthPercent <= 60))
        {
            _snappedHurt = true;
            return BossPower.Snap;
        }

        if (!_snappedDying && (unknown ? _used == 8 : healthPercent <= 25))
        {
            _snappedDying = true;
            return BossPower.Snap;
        }

        if (_hand.Count == 0)
        {
            _hand.AddRange(Deck);
            for (int i = _hand.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_hand[i], _hand[j]) = (_hand[j], _hand[i]);
            }
        }

        BossPower next = _hand[^1];
        _hand.RemoveAt(_hand.Count - 1);
        return next;
    }
}
