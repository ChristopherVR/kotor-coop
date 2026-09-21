using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class RejoinRetryRunnerTests
{
    [Fact]
    public async Task ATransientFailureRetriesAndStopsAfterSuccess()
    {
        List<int> attempts = [];
        bool ok = await RejoinRetryRunner.RunAsync(
            5, TimeSpan.Zero,
            (number, _) =>
            {
                attempts.Add(number);
                return Task.FromResult(number == 3);
            },
            () => true,
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal([1, 2, 3], attempts);
    }

    [Fact]
    public async Task ExhaustionIsBoundedAndReturnsFailure()
    {
        int attempts = 0;
        bool ok = await RejoinRetryRunner.RunAsync(
            3, TimeSpan.Zero,
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(false);
            },
            () => true,
            CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ResetDuringRetryDelayPreventsTheNextAttempt()
    {
        bool owns = true;
        int attempts = 0;
        Task<bool> first = RejoinRetryRunner.RunAsync(
            5, TimeSpan.FromMilliseconds(50),
            (_, _) =>
            {
                attempts++;
                owns = false;
                return Task.FromResult(false);
            },
            () => owns,
            CancellationToken.None);

        Assert.False(await first);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayPreventsLateFetchCompletion()
    {
        using CancellationTokenSource cts = new();
        int attempts = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = RejoinRetryRunner.RunAsync(
            5, TimeSpan.FromSeconds(10),
            (_, _) =>
            {
                attempts++;
                started.SetResult();
                return Task.FromResult(false);
            },
            () => true,
            cts.Token);

        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ResetBeforeLateAttemptCompletionCannotReportSuccess()
    {
        bool owns = true;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> fetch = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> run = RejoinRetryRunner.RunAsync(
            1, TimeSpan.Zero,
            async (_, _) =>
            {
                started.SetResult();
                return await fetch.Task;
            },
            () => owns,
            CancellationToken.None);

        await started.Task;
        owns = false;
        fetch.SetResult(true);

        Assert.False(await run);
    }

    [Fact]
    public async Task IntentionalDisconnectOwnedByAdoptionDoesNotCancelRetry()
    {
        // RejoinAfterLoadAsync disconnects before its transfer. A missing Current session is
        // therefore expected while the adoption generation still owns that disconnect.
        bool sessionMissing = true;
        bool adoptionOwnsDisconnect = true;
        int attempts = 0;

        bool ok = await RejoinRetryRunner.RunAsync(
            2, TimeSpan.Zero,
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            },
            () => !sessionMissing || adoptionOwnsDisconnect,
            CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, attempts);
    }
}
