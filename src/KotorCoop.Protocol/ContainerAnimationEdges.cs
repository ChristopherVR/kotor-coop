namespace KotorCoop.Protocol;

/// <summary>
/// The pure diff behind "which containers need their OPEN animation played this pass, and which
/// need their CLOSE animation" — extracted out of
/// <c>KotorCoop.Plugin.InteractionLockController.ReconcileContainerAnimations</c> (the plugin
/// assembly is x86 NativeAOT and nothing references it — see
/// <c>DialogueAutoJoinRule</c>'s own remarks on why decisions like this live outside it) so the
/// "exactly once per edge" property is testable directly. See docs/research/container-sync.md.
///
/// A LEVEL diffed into edges, the same shape the interaction-lock broadcast itself already is:
/// <paramref name="currentlyHeld"/> is "who holds what right now" (this pass's Container-kind
/// locks), <paramref name="previouslyMirrored"/> is what this instance already believes it
/// opened on a peer's behalf. An id newly in <paramref name="currentlyHeld"/> needs its open
/// animation; an id that dropped out of it needs its close animation — there is no separate
/// release message to miss, exactly like the lock lease itself has none.
/// </summary>
public static class ContainerAnimationEdges
{
    /// <summary>Fills <paramref name="toOpen"/> and <paramref name="toClose"/> (both cleared
    /// first) with the ids that changed state this pass. Every id in either output list appears
    /// at most once, even if <paramref name="currentlyHeld"/> itself repeats one (a malformed or
    /// stale broadcast should not play an animation twice).</summary>
    public static void Diff(
        IReadOnlyList<uint> currentlyHeld,
        IReadOnlyList<uint> previouslyMirrored,
        List<uint> toOpen,
        List<uint> toClose)
    {
        toOpen.Clear();
        toClose.Clear();

        for (int i = 0; i < currentlyHeld.Count; i++)
        {
            uint id = currentlyHeld[i];
            if (id != 0 && !Contains(previouslyMirrored, id) && !Contains(toOpen, id))
            {
                toOpen.Add(id);
            }
        }

        for (int i = 0; i < previouslyMirrored.Count; i++)
        {
            uint id = previouslyMirrored[i];
            if (!Contains(currentlyHeld, id) && !Contains(toClose, id))
            {
                toClose.Add(id);
            }
        }
    }

    private static bool Contains(IReadOnlyList<uint> list, uint id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == id)
            {
                return true;
            }
        }

        return false;
    }
}
