namespace KotorCoop.Plugin;

// The pre-store flush (FlushOneBody, one body per call) and full-session teardown
// (ReleaseSession, a complete sweep) — the two places outside the per-frame loop that
// destroy phantom bodies through the engine. See PhantomController.cs for shared state.
internal sealed unsafe partial class PhantomController
{
    /// <summary>
    /// Destroys one live phantom body through the engine, for the pre-store flush window:
    /// a phantom left standing when the area is stored gets serialised into the saved
    /// module as a permanent NPC. One per call keeps the flush allocation-free and cheap.
    /// Returns false once nothing is left to destroy — a refused destroy still returns true,
    /// because the body IS still there and the caller must come back for it.
    /// </summary>
    public bool FlushOneBody()
    {
        // ⚠ Every slot, not the first _phantomCount of them. Slots are stable per peer
        // now, so a live body can sit in slot 2 with slots 0 and 1 empty; the old bound left
        // it standing and StoreCurrentModule wrote it into the save as a permanent NPC.
        for (int i = 0; i < _phantoms.Length; i++)
        {
            if (_phantoms[i] == 0)
            {
                continue;
            }

            DestroySlot(i, "flush, before the module is stored");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Destroys every body this controller still holds and reports how many are STILL
    /// standing afterwards, so the caller can come back for the refusals.
    ///
    /// ⚠ A FULL SWEEP, not the one-per-frame the flush window uses. Teardown has nothing
    /// waiting on it — no store about to serialise the area, no warp queued behind it — and
    /// a peer's body left standing in somebody's single-player game is the whole defect.
    /// </summary>
    public int ReleaseSession()
    {
        for (int i = 0; i < _phantoms.Length; i++)
        {
            if (_phantoms[i] != 0)
            {
                DestroySlot(i, "session released");
            }
        }

        // ⚠ THE COUNT IS DERIVED AND MUST BE REPUBLISHED HERE. The idle branch of OnFrame
        // destroyed bodies without ever recomputing it, so `phantoms N` in the agent's
        // readout stayed at whatever the live session left — which is how a disconnected
        // game reported one phantom for forty seconds with the controller holding none.
        PublishPhantomCount();
        return _phantomCount;
    }
}
