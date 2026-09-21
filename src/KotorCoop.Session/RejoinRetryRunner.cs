namespace KotorCoop.Session;

/// <summary>Bounded, cancellable retry orchestration for world adoption. The owner predicate
/// is checked before every attempt and during the delay so an old task cannot resume after a
/// session reset or newer rejoin generation.</summary>
public static class RejoinRetryRunner
{
    public static async Task<bool> RunAsync(
        int maxAttempts,
        TimeSpan retryDelay,
        Func<int, CancellationToken, Task<bool>> attempt,
        Func<bool> ownsAttempt,
        CancellationToken cancellationToken)
    {
        for (int number = 1; number <= maxAttempts; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownsAttempt())
            {
                return false;
            }

            bool succeeded = await attempt(number, cancellationToken).ConfigureAwait(false);

            // The operation may ignore cancellation or finish concurrently with a reset.
            // Never let that late completion adopt a world for a dead generation.
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownsAttempt())
            {
                return false;
            }

            if (succeeded)
            {
                return true;
            }

            if (number == maxAttempts)
            {
                return false;
            }

            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
