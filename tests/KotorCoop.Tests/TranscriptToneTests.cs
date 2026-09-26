using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The agent window's line colours: first matching rule wins, order matters.</summary>
public sealed class TranscriptToneTests
{
    [Theory]
    [InlineData("> who", Tone.Command)]
    [InlineData("connected - connected to 127.0.0.1:27140 as peer 2", Tone.Good)]
    [InlineData("disconnected", Tone.Bad)]
    [InlineData("not connected - nobody to follow", Tone.Bad)]
    [InlineData("rejoin failed after 1 attempt(s) - reconnecting as-is and trying again later", Tone.Bad)]
    [InlineData("co-op [W_SAVE] different save: enemies, doors, items and party are off", Tone.Warning)]
    [InlineData("autofollow holding: in a conversation", Tone.Warning)]
    [InlineData("rejoined 'danm14aa'", Tone.Good)]
    [InlineData("dropped Heavy Blaster in front of you - your partner can pick it up (F3 / click it)", Tone.Good)]
    [InlineData("perf: 30.5 ticks/s, tick 0.57/2.1 ms, 0 failed", Tone.Diagnostic)]
    [InlineData("party pool trace: 'dan13_plotcrys' actual=1 failed=0", Tone.Diagnostic)]
    [InlineData("commands: servers | connect <ip|#n>", Tone.Helpful)]
    [InlineData("  Lanik  danm14aa", Tone.Helpful)]
    [InlineData("Lanik joined the game", Tone.Good)]
    [InlineData("hello there", Tone.Plain)]
    [InlineData("saving and loading:", Tone.Helpful)]
    [InlineData("  effects                     the last 8 effects captured, forwarded and applied", Tone.Helpful)]
    [InlineData("unknown command - type `help` to list them, or press Tab to complete", Tone.Warning)]
    [InlineData("steam: not active - see kotorcoop-agent.log for why (no steam_api64.dll, Steam not running)", Tone.Warning)]
    [InlineData("coop.ini: 62 developer switch(es) set - testing options, not player settings (F1 lists them)", Tone.Diagnostic)]
    [InlineData("coop.ini: ignoring 1 setting(s) this version does not know: combat_state_sync", Tone.Warning)]
    public void ClassifiesByMeaning(string line, Tone expected)
    {
        Assert.Equal(expected, TranscriptTone.Classify(line));
    }
}
