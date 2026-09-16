namespace KotorCoop.Plugin;

/// <summary>
/// Whether a phantom/stand-in equip attempt's resref hash should be latched as "already
/// applied" (never tried again for this body) or left alone so the next opportunity retries
/// it — a pure decision so it can be regression-tested without a live game.
///
/// ⚠ THE BUG THIS CLOSES: <c>PhantomController.Drive.cs</c>'s <c>ApplyEquipment</c> used to
/// cache the hash BEFORE calling <see cref="Odyssey.EquipItemFromTemplate"/>, unconditionally
/// — a transient failure was latched exactly like a success, and the per-slot cache is keyed
/// by the RESREF's hash, so the same peer publishing the same gear (the ordinary case: nobody
/// changes clothes every tick) never got a second attempt for the rest of the module's life.
///
/// Measured live, 2026-09-16 (kotorcoop-plugin.log, client side, connect time): the very first
/// equip for a freshly spawned phantom failed with "failure 3" for BOTH the body slot
/// (<c>g_a_clothes01</c>) and the weapon slot (<c>g_w_shortswrd01</c>) — <see
/// cref="Odyssey.EquipItemFromTemplate"/>'s own routine-table lookup (<c>CreateItemOnObject</c>/
/// <c>ActionEquipItem</c>) had not resolved yet, a few hundred milliseconds after the module
/// finished loading. With the old caching, that phantom never got clothing for the rest of the
/// session: its client twin's animbase model resref read back completely EMPTY on live probe
/// (no body model to render at all) even though the appearance.2da row and name were both
/// correct on the server object and the twin — the two fields <see cref="PeerIdentityRule"/>
/// governs. A composite/clothing-dependent PC appearance row with no clothing equipped has
/// nothing to build a body model from.
///
/// Failure 7 is the one PERMANENT case (see <see cref="Odyssey.EquipItemFromTemplate"/>'s own
/// doc comment): the blueprint genuinely does not exist on this install (a peer has a mod this
/// side lacks), which will never succeed no matter how many times it is retried, so it is
/// still latched — the original, correct "soft failure when installs don't match" behaviour.
/// Every OTHER failure code (no implementer/routine table yet, the VM mid-call, a stack push
/// refusal, an equip call that itself returned non-zero, a stack-pointer imbalance) is a
/// transient engine-call condition, not a fact about the item, and must be retried.
/// </summary>
internal static class EquipRetryRule
{
    /// <summary>The one failure code that can never succeed on retry: the blueprint resref does
    /// not exist on this install. <c>PhantomController.ApplyEquipment</c> equips through
    /// <see cref="Odyssey.EquipItemFromTemplateDirect"/>, whose create step is
    /// <see cref="Odyssey.GiveItemToInventory"/>; that method reports a missing blueprint as
    /// failure 8 (its own doc: "The blueprint does not exist in this install"). The older VM
    /// path (<see cref="Odyssey.EquipItemFromTemplate"/>) used 7 for the same case, but the
    /// phantom path no longer calls it - see the 2026-09-16 note in ApplyEquipment.</summary>
    public const int BlueprintMissing = 8;

    /// <summary>
    /// Whether this equip attempt's resref hash should be cached as done. True on a genuine
    /// success, or on the one permanent failure (<see cref="BlueprintMissing"/>); false for
    /// every other failure, so the next opportunity tries the same resref again instead of
    /// silently giving up forever.
    /// </summary>
    public static bool ShouldLatch(bool ok, int failure) => ok || failure == BlueprintMissing;
}
