namespace KotorCoop.Plugin;

/// <summary>
/// One plugin-gated coop.ini boolean (effect_sync, trigger_relay, liveness_sync,
/// map_reveal_sync, dialogue_recruit_sync) that a host-authoritative Welcome comparison can
/// override for this session — see <see cref="BehaviourOverrideRule"/> for the pure decision
/// and SharedBlock.BehaviourOverrides.cs for the wire region. Wraps the "read the effective
/// value and log once when it changes" dance so the five consuming controllers each carry one
/// field instead of their own copy of this bookkeeping.
///
/// Constructed once, at the controller's own construction time, with the coop.ini value that
/// PluginConfig already reads once there — exactly what the controller used to store directly
/// as its `_enabled` bool. <see cref="Resolve"/> is called every frame the controller runs;
/// the coop.ini value itself never changes mid-process, only whether it is overridden.
/// </summary>
internal sealed class EffectiveBehaviourFlag
{
    // DIAGNOSTIC-only kill key (default ON) — added 2026-08-29 for the transition-crash DLL
    // bisect (docs/research/transition-crashes.md). This reader consults
    // SharedBlock.OffsetBehaviourOverrideMask/Values every frame for all five gated
    // controllers, unconditionally, with no prior escape hatch; setting this OFF makes
    // Resolve() behave exactly as if no session ever overrides anything — the five
    // controllers fall back to their own coop.ini value only, and the shared-block override
    // region is never read. Not meant to ship OFF.
    private static readonly bool ReaderEnabled = PluginConfig.ReadBool("behaviour_override_reader", true);

    private readonly string _key;
    private readonly uint _bit;
    private readonly bool _localValue;

    private bool _current;
    private bool _everResolved;

    public EffectiveBehaviourFlag(string key, uint bit, bool localValue)
    {
        _key = key;
        _bit = bit;
        _localValue = localValue;
        _current = localValue;
    }

    /// <summary>
    /// Resolves this frame's effective value from <paramref name="block"/> — two dword reads,
    /// as cheap as any other per-frame block read in this file — and logs once per genuine
    /// change: the first time an override takes hold ("adopted host's X=1 (local coop.ini
    /// says 0)"), and once more if it later reverts to the local value (the host stopped
    /// overriding it, or the session disconnected and <c>SessionScopedBlockLevels</c> cleared
    /// the mask). Returns <c>_localValue</c> immediately, with no block read at all, when
    /// <c>behaviour_override_reader</c> is off.
    /// </summary>
    public bool Resolve(SharedBlockView block)
    {
        if (!ReaderEnabled)
        {
            return _localValue;
        }

        uint mask = block.BehaviourOverrideMask;
        uint values = block.BehaviourOverrideValues;

        bool overridden = BehaviourOverrideRule.IsOverridden(mask, _bit);
        bool value = BehaviourOverrideRule.Resolve(mask, values, _bit, _localValue);

        if (!_everResolved || value != _current)
        {
            _everResolved = true;

            if (overridden)
            {
                PluginHost.Log(
                    $"behaviour: adopted host's {_key}={(value ? 1 : 0)} (local coop.ini says "
                    + $"{(_localValue ? 1 : 0)})");
            }
            else if (_current != _localValue)
            {
                PluginHost.Log($"behaviour: {_key} reverted to local coop.ini={(_localValue ? 1 : 0)}");
            }
        }

        _current = value;
        return value;
    }
}
