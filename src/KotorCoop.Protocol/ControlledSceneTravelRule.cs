namespace KotorCoop.Protocol;

/// <summary>A vision's controlled actor cannot be recreated by a bare follower warp.
/// BoS bos_ebovis was observed selecting an absent actor and unloading the joiner.
/// Ordinary Leviathan entry remains allowed until its actual controlled-solo flag.</summary>
public static class ControlledSceneTravelRule
{
    public static bool IsPrivateVision(string module) =>
        string.Equals(module, "bos_ebovis", StringComparison.OrdinalIgnoreCase);

    public static bool RefuseFollow(bool enabled, string destination, bool ownerInSolo) => enabled
        && (IsPrivateVision(destination)
            || ownerInSolo && SoloSegmentCatalogue.ContainsControlledNpcModule(destination));

    public static bool HoldAdoption(PlayerState? host) => host is { IsInSoloSegment: true } state
        && SoloSegmentCatalogue.ContainsControlledNpcModule(state.Module);

    public static bool ParkPeer(bool enabled, PlayerState local, PlayerState owner) => enabled
        && !local.IsInSoloSegment && !owner.IsAway && owner.IsInSoloSegment
        && SoloSegmentCatalogue.ContainsControlledNpcModule(owner.Module)
        && !string.Equals(local.Module, owner.Module, StringComparison.OrdinalIgnoreCase);
}
