using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The <c>TextWriter</c> that stands in for <c>Console.Out</c>/<c>Error</c> while the TUI owns
/// the screen: splits writes into complete lines, buffers a trailing fragment, and never loses
/// a partial write made right before shutdown. No console involved — everything here goes
/// through the writer's own <c>Write</c>/<c>WriteLine</c> surface exactly as
/// <c>Console.WriteLine</c> would use it once redirected.
/// </summary>
public class ConsoleCaptureWriterTests
{
    [Fact]
    public void WriteLineEmitsOneCompleteLine()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.WriteLine("hello");

        Assert.Equal(["hello"], lines);
    }

    [Fact]
    public void MultipleWriteLineCallsEmitSeparateLines()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.WriteLine("coop.ini: 3 developer switch(es) set - not player settings, see docs/CONFIG.md: a=1, b=2, c=3");
        writer.WriteLine("[census] gate: host authoritative");

        Assert.Equal(
        [
            "coop.ini: 3 developer switch(es) set - not player settings, see docs/CONFIG.md: a=1, b=2, c=3",
            "[census] gate: host authoritative",
        ], lines);
    }

    [Fact]
    public void PartialWritesJoinIntoOneLineOnceNewlineArrives()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.Write("part one, ");
        writer.Write("part two");
        writer.Write('\n');

        Assert.Equal(["part one, part two"], lines);
    }

    [Fact]
    public void CarriageReturnBeforeNewlineIsNotASecondLine()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.Write("hello\r\n");

        Assert.Equal(["hello"], lines);
    }

    [Fact]
    public void NothingIsEmittedUntilANewlineArrives()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.Write("no newline yet");

        Assert.Empty(lines);
    }

    [Fact]
    public void FlushPendingEmitsATrailingFragmentWithNoNewline()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.Write("shutdown fragment");
        writer.FlushPending();

        Assert.Equal(["shutdown fragment"], lines);
    }

    [Fact]
    public void FlushPendingIsANoOpWhenThereIsNothingPending()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.WriteLine("already emitted");
        writer.FlushPending();

        Assert.Equal(["already emitted"], lines);
    }

    [Fact]
    public void AnEmptyLineIsStillEmitted()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.WriteLine(string.Empty);

        Assert.Equal([string.Empty], lines);
    }

    [Fact]
    public void WriteLineWithNoArgumentEmitsJustTheBufferedText()
    {
        List<string> lines = [];
        ConsoleCaptureWriter writer = new(lines.Add);

        writer.Write("bare");
        writer.WriteLine();

        Assert.Equal(["bare"], lines);
    }

    [Fact]
    public void ConcurrentWritersDoNotInterleaveWithinALine()
    {
        // Several threads calling Console.WriteLine already happened unsynchronized relative
        // to each other against the real Console.Out; this only proves ONE writer's own
        // internal buffer state cannot be corrupted by concurrent callers of IT specifically.
        List<string> lines = [];
        object gate = new();
        void OnLine(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }
        }

        ConsoleCaptureWriter writer = new(OnLine);

        Parallel.For(0, 100, i => writer.WriteLine($"line {i}"));

        Assert.Equal(100, lines.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Contains($"line {i}", lines);
        }
    }
}
