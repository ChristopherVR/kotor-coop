namespace KotorCoop.Session.Pumps;

/// <summary>Object ids agreed by VALUE rather than by reference, because the plugin side
/// (<c>KotorCoop.Plugin</c>, a NativeAOT win-x86 library) cannot be referenced from here.
/// Shared by <see cref="LootPump"/> and <see cref="DialoguePump"/> so both sides of the
/// give-loot cheat channel agree on the same sentinel.</summary>
internal static class AgentWellKnownIds
{
    /// <summary>The well-known object id of THIS instance's own local player creature —
    /// <c>0x7FFFFFFF</c>, VERIFIED live and identical on every install
    /// (docs/re/object-model.md: "Player object id is exactly 0x7FFFFFFF"). Mirrors
    /// <c>SharedBlock.CheatTargetLocalPlayer</c> in the plugin.</summary>
    public const uint LocalPlayerObjectId = 0x7FFFFFFF;
}
