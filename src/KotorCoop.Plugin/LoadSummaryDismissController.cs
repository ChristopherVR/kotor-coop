namespace KotorCoop.Plugin;

/// <summary>
/// Clicks OK on a joiner's Status Summary ("Item(s) Received") after a world change the way the box's own
/// button does. When and why: <see cref="LoadSummaryDismissRule"/>. The world change is the
/// engine's world epoch moving (the first epoch seen counts too: the joiner's own adopt load);
/// the dismissal is <see cref="Odyssey.DismissStatusSummary"/>, which also unpauses and gives
/// input back. Behind coop.ini <c>dismiss_load_summary</c> (default ON).
/// </summary>
internal sealed class LoadSummaryDismissController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("dismiss_load_summary", true);

    private ulong? _epoch;
    private long _worldChangedMs;
    private long _shownSinceMs = -1;
    private long _worldChangeToShownMs;
    private bool _handled;

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (Odyssey.TryGetWorldEpoch(out ulong epoch) && epoch != _epoch)
        {
            _epoch = epoch;
            _worldChangedMs = nowMs;
            _shownSinceMs = -1;
            _handled = false;
        }

        bool onTop = Odyssey.IsTopRegisteredStatusSummary();
        if (!onTop)
        {
            _shownSinceMs = -1;
            return;
        }

        bool isJoiner = block.TryGetLocalSessionRole(out bool isHost) && !isHost;
        if (_shownSinceMs < 0)
        {
            _shownSinceMs = nowMs;
            _worldChangeToShownMs = nowMs - _worldChangedMs;
            PluginHost.Log(
                $"load summary: the Status Summary is up, {_worldChangeToShownMs} ms after the world changed "
                + $"({(isJoiner ? "joiner" : "not a joiner")})");
        }

        if (_handled
            || !LoadSummaryDismissRule.ShouldDismiss(isJoiner, onTop, _worldChangeToShownMs, nowMs - _shownSinceMs))
        {
            return;
        }

        // One try per world: a box that will not go is left for the player, not hammered.
        _handled = true;
        bool closed = Odyssey.DismissStatusSummary(out string detail);
        PluginHost.Log(
            $"load summary: the joiner's post-load Status Summary came up {_worldChangeToShownMs} ms "
            + $"after the world changed - {(closed ? "dismissed" : "left for the player")}: {detail}");
    }
}
