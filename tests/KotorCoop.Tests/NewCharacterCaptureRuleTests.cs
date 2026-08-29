using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="NewCharacterCaptureRule"/> — when the character retail chargen writes to
/// <c>Temp\pifo.ifo</c> is safe to copy.
///
/// Each of the three conditions guards a failure that is SILENT in the game rather than loud:
/// the engine's login ignores <c>LoadCharacterFromIFO</c>'s return value, so a stale, empty or
/// half-written pifo does not produce an error — it produces a blank level-1 stranger standing
/// at the module entry point, which is exactly the failure docs/research/world-adoption.md §7
/// spent a whole live round diagnosing.
/// </summary>
public class NewCharacterCaptureRuleTests
{
    private const long Arm = 1_000_000;
    private const long Big = 11_918; // a real chargen pifo, measured

    [Fact]
    public void AFreshBigStableFileIsCaptured() =>
        Assert.True(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: Big, previousBytes: Big,
            writtenAtTicksUtc: Arm + 1, armedAtTicksUtc: Arm));

    [Fact]
    public void NoFileIsNotACapture() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: false, bytes: 0, previousBytes: 0,
            writtenAtTicksUtc: 0, armedAtTicksUtc: Arm));

    /// <summary>A zero-byte pifo is not "no file" — it is what a source-less
    /// <c>CExoResMan::FileCopy</c> leaves behind, and the engine reads it as a character.
    /// </summary>
    [Fact]
    public void AnEmptyFileIsNeverACapture() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: 0, previousBytes: 0,
            writtenAtTicksUtc: Arm + 1, armedAtTicksUtc: Arm));

    [Fact]
    public void AFileJustUnderTheFloorIsNeverACapture() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true,
            bytes: NewCharacterCaptureRule.MinimumBytes - 1,
            previousBytes: NewCharacterCaptureRule.MinimumBytes - 1,
            writtenAtTicksUtc: Arm + 1,
            armedAtTicksUtc: Arm));

    /// <summary>THE ONE THAT WOULD HAVE SHIPPED A DIFFERENT PERSON. <c>Temp\pifo.ifo</c>
    /// survives across runs, so an install that has ever loaded an autosave has one on disk
    /// from a previous session.</summary>
    [Fact]
    public void AFileOlderThanTheArmIsALeftoverAndIsRefused() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: Big, previousBytes: Big,
            writtenAtTicksUtc: Arm - 1, armedAtTicksUtc: Arm));

    /// <summary>A file written at exactly the arming instant still counts: the comparison is
    /// "not older than", because tick resolution is not the thing being tested here.</summary>
    [Fact]
    public void AFileWrittenAtTheArmingInstantCounts() =>
        Assert.True(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: Big, previousBytes: Big,
            writtenAtTicksUtc: Arm, armedAtTicksUtc: Arm));

    /// <summary>The engine writes the file from its own thread; a length that is still growing
    /// is a truncated character.</summary>
    [Fact]
    public void AGrowingFileWaitsForTheNextPoll() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: Big, previousBytes: Big / 2,
            writtenAtTicksUtc: Arm + 1, armedAtTicksUtc: Arm));

    /// <summary>The very first poll after arming has no previous length (-1), so it can never
    /// capture — one extra poll interval, against a character that would be wrong forever.
    /// </summary>
    [Fact]
    public void TheFirstPollNeverCaptures() =>
        Assert.False(NewCharacterCaptureRule.ShouldCapture(
            fileExists: true, bytes: Big, previousBytes: -1,
            writtenAtTicksUtc: Arm + 1, armedAtTicksUtc: Arm));
}
