namespace KotorCoop.Plugin;

/// <summary>
/// Hides the HUD's three party-member portrait widgets on a MIRRORING instance (a client).
///
/// A client's companions are the host's: the control lock already swallows Tab and
/// portrait clicks, so the portraits are dead UI that still invites the click — and worse,
/// they show health this instance does not own. Hiding them makes the split honest: your
/// portrait is yours, the party is the host's.
///
/// Mechanism — measured live, in this order:
/// - The +0x44 visible bit (the save/load panel's lever) is a dead end here: the engine's
///   per-frame portrait update calls CSWGuiMainInterfaceChar::Show 0x006867F0, which
///   re-asserts exactly that bit on every control, and it runs before the draw while this
///   controller runs after it (the frame hook is SwapBuffers). The clear lost the race
///   every single frame.
/// - Raw writes to the extent at control+0x04 move nothing either: each control's draw
///   uses screen rects CACHED in its params structs, rebuilt only when the extent is set
///   through the control's own (possibly overridden) SetExtent — vtable +0x04, the same
///   slot the minimap arrow is moved with every frame.
/// So the widgets are moved OFF-SCREEN through that slot, which the per-frame Show does
/// not fight (extents were measured to survive it), and moved back the same way. Off
/// screen also means un-clickable: hit-testing reads the same geometry.
///
/// Layout (symbol database, anchored by an exact vtable check on the main interface —
/// Odyssey.GetMainInterface): CSWGuiMainInterface embeds CSWGuiMainInterfaceChar
/// party_members[3] at +0x1F88, stride 0xEA8. ⚠ Measured live: widget 0 is the
/// CONTROLLED character's own slot (the large leftmost portrait — hiding all three
/// removed the client's own portrait too), so only widgets 1 and 2 are hidden. A client's
/// control is locked to their own PC, so widget 0 is always theirs.
/// Kill switch: coop.ini hide_party_portraits=0.
/// </summary>
internal sealed unsafe class PortraitController(SharedBlockView block)
{
    private const int PartyMembersOffset = 0x1F88;

    private const int CharWidgetStride = 0xEA8;

    private const int PartyWidgetCount = 3;

    /// <summary>Widget 0 is the controlled character's own portrait — never hidden.</summary>
    private const int FirstHiddenWidget = 1;

    /// <summary>Every GUI control embedded in one CSWGuiMainInterfaceChar (symbol
    /// database): back label, debilitated/disabled overlays, level-up label + background,
    /// the portrait image label, vitality and force bars, the effect labels, and the
    /// clickable char button.</summary>
    private static readonly int[] ControlOffsets =
        [0x028, 0x168, 0x2A8, 0x3E8, 0x528, 0x668, 0x7A8, 0x8F8, 0xA48, 0xB88, 0xCC8];

    /// <summary>CSWGuiControl + this = the extent rect, four ints (x, y, w, h).</summary>
    private const int ControlExtent = 0x04;

    /// <summary>CSWGuiObject vtable +0x04 = SetExtent(Extent*) — through the slot, never a
    /// raw field write, because subclasses rebuild their cached draw rects in overrides.</summary>
    private const int SetExtentSlot = 0x04;

    /// <summary>Far below any real screen. Added to the saved Y, never accumulated: the
    /// target is always computed from the snapshot, so re-applying is idempotent.</summary>
    private const int OffscreenShift = 20000;

    /// <summary>Frames between passes. The engine only re-lays-out the widgets on real
    /// events (resolution, party change), so this is reassertion cadence, not a race.</summary>
    private const int IntervalFrames = 15;

    private readonly bool _enabled = PluginConfig.ReadBool("hide_party_portraits", true);

    /// <summary>Each control's extent as it was before this activation moved it.</summary>
    private readonly int[,,] _savedExtent = new int[PartyWidgetCount, 16, 4];

    private bool _hiding;
    private int _countdown;
    private bool _announced;

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (!_enabled)
        {
            return;
        }

        // "This instance mirrors the world" — set on a client once the enemy channel is
        // live, never on the host. Sessions where it never rises (solo, host) never hide.
        bool shouldHide = block.Command == SharedBlock.CommandSpawnAndDrive && block.DamageDealtEnabled;

        nint mainInterface = Odyssey.GetMainInterface();
        if (mainInterface == 0)
        {
            // HUD gone (menu, transition). The widgets no longer exist; a rebuilt HUD
            // comes back with the engine's own layout, so start a fresh activation.
            _hiding = false;
            return;
        }

        if (shouldHide)
        {
            if (!_hiding)
            {
                _hiding = true;
                Snapshot(mainInterface);

                if (!_announced)
                {
                    _announced = true;
                    PluginHost.Log("portraits: hiding the party widgets (client; hide_party_portraits=0 opts out)");
                }
            }

            Apply(mainInterface, OffscreenShift);
        }
        else if (_hiding)
        {
            _hiding = false;
            Apply(mainInterface, 0);
            PluginHost.Log("portraits: restored the party widgets");
        }
    }

    private void Snapshot(nint mainInterface)
    {
        for (int w = FirstHiddenWidget; w < PartyWidgetCount; w++)
        {
            nint widget = mainInterface + PartyMembersOffset + (w * CharWidgetStride);
            for (int c = 0; c < ControlOffsets.Length; c++)
            {
                int* extent = (int*)(widget + ControlOffsets[c] + ControlExtent);

                // A widget the engine rebuilt while we were hidden would snapshot our own
                // shifted rect as "original" and never come back. Anything parked below
                // the shift line keeps its previous snapshot instead.
                if (extent[1] >= OffscreenShift / 2)
                {
                    continue;
                }

                for (int i = 0; i < 4; i++)
                {
                    _savedExtent[w, c, i] = extent[i];
                }
            }
        }
    }

    /// <summary>Moves every widget control to its saved rect shifted down by
    /// <paramref name="shift"/> (0 = restore), through each control's own SetExtent.</summary>
    private void Apply(nint mainInterface, int shift)
    {
        for (int w = FirstHiddenWidget; w < PartyWidgetCount; w++)
        {
            nint widget = mainInterface + PartyMembersOffset + (w * CharWidgetStride);
            for (int c = 0; c < ControlOffsets.Length; c++)
            {
                nint control = widget + ControlOffsets[c];

                nint vtable = *(nint*)control;
                if (!Odyssey.LooksLikeCodePointer(vtable))
                {
                    continue;
                }

                var setExtent = (delegate* unmanaged[Thiscall]<nint, int*, void>)(*(nint*)(vtable + SetExtentSlot));
                if (!Odyssey.LooksLikeCodePointer((nint)setExtent))
                {
                    continue;
                }

                int* rect = stackalloc int[4];
                rect[0] = _savedExtent[w, c, 0];
                rect[1] = _savedExtent[w, c, 1] + shift;
                rect[2] = _savedExtent[w, c, 2];
                rect[3] = _savedExtent[w, c, 3];
                setExtent(control, rect);
            }
        }
    }
}
