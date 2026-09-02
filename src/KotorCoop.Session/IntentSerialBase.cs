namespace KotorCoop.Session;

/// <summary>Where a client instance's intent serials start — see the remarks on
/// <c>CoopClient._nextPartyPoolIntentSerialV2</c> for why this is not simply 1.</summary>
internal static class IntentSerialBase
{
    /// <summary>A fresh base in [1, 2^31): never 0 (the "not published" answer), and with the
    /// whole upper half of the range left for the instance to count through.</summary>
    public static uint Mint() => (uint)Random.Shared.Next(1, int.MaxValue);
}
