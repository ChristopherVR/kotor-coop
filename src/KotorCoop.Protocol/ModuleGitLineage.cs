namespace KotorCoop.Protocol;

/// <summary>
/// Tells a module-GIT digest difference that is only an artifact of WHERE each side took its
/// baseline from apart from a genuinely different object mapping — the GIT counterpart of
/// <see cref="SaveLineage"/>.
///
/// A module's GIT digest is baselined once per loaded world, from whichever save was newest on
/// disk at that moment: the module's saved GIT when that save visited it, the pristine module
/// file otherwise. Two peers in one world can therefore hold different digests for the same
/// module — live 2026-09-26, a joiner re-adopted the host's fresh save of danm14aa (explicit
/// saved ids) while the host's own baseline came from an older save that predated the module
/// (pristine), and <c>W_GIT</c> switched enemies, doors and items off right after a
/// successful adoption.
///
/// When both save identities are resolved, EQUAL, and that identity is a save one side
/// actually ADOPTED from the other (the host served it / the joiner loaded it), the joiner's
/// world was built from the host's own saved GIT, so any digest difference in that module is
/// a baseline artifact. Equal identities alone are not enough: a mod that edits a module's
/// placements on one install leaves the identities equal while the mapping really differs
/// (SessionIntegrationTests.AModuleGitMismatchDropsIdChannelsThereAndRestoresInTheNextModule).
/// That exact digest pair is remembered and absorbed for the module from then on — baselines do not move within a
/// loaded world, so a later ordinary save (which drifts the identities apart again, see
/// <see cref="SaveLineage"/>) must not bring the warning back. A different pair (a new
/// baseline after a load) is judged afresh.
/// </summary>
public sealed class ModuleGitLineage
{
    private string _module = string.Empty;
    private uint _localGit;
    private uint _remoteGit;

    /// <summary>True while the most recent comparison had a GIT mismatch this absorbed.</summary>
    public bool Absorbing { get; private set; }

    /// <param name="isAdoptedSave">Whether <paramref name="local"/>'s save identity is one this
    /// side knows was adopted between the two (served by this host, or loaded by this joiner).</param>
    public WorldDigestComparison Apply(
        WorldDigestComparison raw, in WorldDigest local, in WorldDigest remote, Func<uint, bool> isAdoptedSave)
    {
        ArgumentNullException.ThrowIfNull(isAdoptedSave);
        Absorbing = false;
        if (!raw.ModuleGitMismatch)
        {
            return raw;
        }

        if (local.SaveIdentity != 0 && local.SaveIdentity == remote.SaveIdentity
            && isAdoptedSave(local.SaveIdentity))
        {
            _module = raw.Module;
            _localGit = local.ModuleGit;
            _remoteGit = remote.ModuleGit;
        }

        if (_module.Length > 0
            && string.Equals(_module, raw.Module, StringComparison.OrdinalIgnoreCase)
            && _localGit == local.ModuleGit
            && _remoteGit == remote.ModuleGit)
        {
            Absorbing = true;
            return raw with { ModuleGitMismatch = false };
        }

        return raw;
    }
}
