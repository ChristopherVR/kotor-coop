namespace KotorCoop.Plugin;

/// <summary>
/// Fog-of-war sync (roadmap item 19): clears fog around each remote peer's position on THIS
/// instance's map, so ground a partner has already walked does not stay black just because
/// this player never stood on it.
///
/// No new wire — <c>RemoteView.Position</c> is the exact position <c>MapMarkerController</c>
/// and <c>MinimapPeerHook</c> already read every frame, already filtered to peers in the
/// same module (see docs/research/map-reveal.md). The only new thing this controller adds is
/// calling <c>CSWSAreaMap::SetWorldPointExplored</c> at each peer's position, throttled by
/// <see cref="MapRevealRule"/> so a peer standing still does not re-fire the call forever.
///
/// The engine call is pinned VERIFIED by static RE (symbol database + disassembly + two real
/// callers agreeing on the signature — docs/research/map-reveal.md) and now also LIVE-VERIFIED
/// (2026-08-27, docs/research/map-reveal.md "Live pass"): fired correctly on connect, followed
/// a peer's real position change, survived a module transition cleanly (re-resolved the new
/// module's area map with no crash and no "no area map yet" fallback), and cost nothing
/// measurable in frame time. Default flipped ON — behind <c>coop.ini map_reveal_sync</c> still,
/// so a player can opt out. A wrong pointer chain remains a logged no-op
/// (<see cref="TryGetAreaMap"/> below), never a crash — deliberately re-derived independently
/// of <c>Odyssey.cs</c> rather than reusing its private <c>GetAreaMap()</c>, per this pass's
/// file ownership.
///
/// MAIN THREAD ONLY — <c>OnFrame</c> must run from the same frame hook every other engine-
/// calling controller here does.
/// </summary>
internal sealed unsafe class MapRevealController(SharedBlockView block)
{
    // ---- the one engine call this controller makes ---------------------------------
    //
    // Re-derived here rather than calling into Odyssey.cs (this pass does not own that
    // file): CAppManager -> +0x08 CServerExoApp -> CServerExoAppInternal::GetModule(this)
    // -> +0x218 CSWSAreaMap*, gated on +0x04 (HasData) != 0 — the identical chain
    // MinimapPeerHook and MapMarkerController already exercise live every frame via
    // Odyssey.GetAreaMap(), so only the final call below is unproven. See
    // docs/research/map-reveal.md.

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int ModuleAreaMapOffset = 0x218;
    private const int AreaMapHasData = 0x04;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;

    /// <summary>CSWSAreaMap::SetWorldPointExplored(Vector pos, int explored) — thiscall,
    /// ret 0x10 (three stack floats + one stack int, matching the symbol db's
    /// param_size_bytes=16 for this address). VERIFIED: the exact call
    /// CServerExoAppInternal::UpdateMapData makes for every party member's position each
    /// tick, and the whole body of CSWVirtualMachineCommands::ExecuteCommandRevealMap (the
    /// RevealMap() script command) — see docs/research/map-reveal.md.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, float, float, int, void>
        SetWorldPointExploredFn =
            (delegate* unmanaged[Thiscall]<nint, float, float, float, int, void>)0x005792D0;

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;

    /// <summary>The current module's CSWSAreaMap, or 0 when there is no world (menu,
    /// mid-transition) — never dereferenced further than the HasData check.</summary>
    private static nint TryGetAreaMap()
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return 0;
        }

        nint server = Deref(appManager + AppServerOffset);
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        if (module == 0)
        {
            return 0;
        }

        nint areaMap = Deref(module + ModuleAreaMapOffset);
        return LooksLikeHeapPointer(areaMap) && *(int*)(areaMap + AreaMapHasData) != 0
            ? areaMap
            : 0;
    }

    private static bool LooksLikeHeapPointer(nint p) => p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    /// <summary>
    /// Marks one world point explored on the current module's area map. False (a no-op, not
    /// a crash) when there is no loaded area map yet, or the position is not finite — the
    /// same "never hand the engine a torn cross-process read" screen
    /// <c>Odyssey.DrawMinimapPeers</c> applies, because this too reads a slot an entirely
    /// separate process writes.
    /// </summary>
    private static bool TryRevealWorldPoint(Vec3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            return false;
        }

        nint areaMap = TryGetAreaMap();
        if (areaMap == 0)
        {
            return false;
        }

        SetWorldPointExploredFn(areaMap, position.X, position.Y, position.Z, 1);
        return true;
    }

    // ---- per-slot throttle state -----------------------------------------------------

    private readonly bool[] _hasRevealed = new bool[SharedBlock.SlotCapacity];
    private readonly long[] _lastRevealedAtMs = new long[SharedBlock.SlotCapacity];
    private readonly Vec3[] _lastRevealedPosition = new Vec3[SharedBlock.SlotCapacity];

    private readonly bool _enabled = PluginConfig.ReadBool("map_reveal_sync", true);

    private int _loggedFailures;
    private int _loggedReveals;

    /// <summary>Forgets every slot's throttle state — a new module means every peer's next
    /// position is worth an immediate reveal rather than waiting on a stale comparison
    /// point from the module that was just left.</summary>
    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            _hasRevealed[i] = false;
            _lastRevealedAtMs[i] = 0;
            _lastRevealedPosition[i] = default;
        }
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        long now = Environment.TickCount64;
        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = i < wanted ? block.GetRemote(i) : default;

            if (!remote.Active)
            {
                // The peer left this slot (departed, or moved to a different module — the
                // block already filters to same-module peers): forget the comparison point
                // so a return later reveals immediately rather than waiting on a distance
                // computed against a stale, possibly cross-module, position.
                _hasRevealed[i] = false;
                continue;
            }

            if (!float.IsFinite(remote.Position.X)
                || !float.IsFinite(remote.Position.Y)
                || !float.IsFinite(remote.Position.Z))
            {
                continue;
            }

            bool should = MapRevealRule.ShouldReveal(
                _hasRevealed[i], _lastRevealedAtMs[i], now, _lastRevealedPosition[i], remote.Position);

            if (!should)
            {
                continue;
            }

            if (TryRevealWorldPoint(remote.Position))
            {
                _hasRevealed[i] = true;
                _lastRevealedAtMs[i] = now;
                _lastRevealedPosition[i] = remote.Position;

                if (_loggedReveals < 5)
                {
                    _loggedReveals++;
                    PluginHost.Log($"map reveal: slot {i} revealed at "
                        + $"({remote.Position.X:F1}, {remote.Position.Y:F1}, {remote.Position.Z:F1})");
                }
            }
            else if (_loggedFailures < 5)
            {
                _loggedFailures++;
                PluginHost.Log($"map reveal: slot {i} skipped — no area map yet");
            }
        }
    }
}
