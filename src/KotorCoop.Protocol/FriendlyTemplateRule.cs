namespace KotorCoop.Protocol;

/// <summary>
/// Host spawns only, the friendly exception (ScriptCreateObjectHook, SharedBlock.ScriptSpawns.cs).
/// A joiner refuses every script CreateObject of a creature because the host's copy reaches it
/// through the enemy mirror, but the mirror only materialises HOSTILE creatures, so a friendly
/// story actor (Carth in the Star Forge's light/dark scene, blueprint sta_carth) would never
/// exist on the joiner. The joiner's agent therefore publishes which creature blueprints of the
/// current module are friendly, as hashes, and the plugin lets those spawn locally.
/// Pure decision logic shared by the agent (writer) and the plugin (reader).
/// </summary>
public static class FriendlyTemplateRule
{
    /// <summary>How many template hashes the block carries (0xCCA4 + 80 * 4 = 0xCDE4, inside
    /// the 0xCE00 block).</summary>
    public const int Capacity = 80;

    /// <summary>
    /// Friendly_1 (repute.2da row 2) only. Neutral (5) and Friendly_2 (4) looked safe from the
    /// table, but a blueprint's faction is only where a creature starts: the Star Forge's
    /// sta45a_assault*, sta45a_malak and sta_jedi00* are Neutral or Friendly_2 in their .utc and
    /// are turned hostile by script, and a joiner building them itself would duplicate the
    /// host's mirror. Friendly_1 holds the story actors (sta_carth, sta_bastila, the light Jedi).
    /// Anything else stays suppressed: the mirror owns it, and unknown fails towards "host spawns".
    /// </summary>
    public static bool IsFriendlyFaction(int factionId) => factionId == 2;

    /// <summary>Lowercase ASCII FNV-1a 32 of a blueprint resref, the value in the block.</summary>
    public static uint Hash(string template)
    {
        uint hash = 2166136261;
        foreach (char c in template.AsSpan().Trim('\0'))
        {
            hash ^= (byte)char.ToLowerInvariant(c);
            hash *= 16777619;
        }

        return hash;
    }

    /// <summary>The hashes to publish: distinct, non-empty, at most <see cref="Capacity"/> (the
    /// first ones in the order given; the rest simply stay suppressed).</summary>
    public static uint[] Publishable(IEnumerable<string> friendlyTemplates)
    {
        List<uint> hashes = [];
        foreach (string template in friendlyTemplates)
        {
            if (template.Trim('\0').Length == 0)
            {
                continue;
            }

            uint hash = Hash(template);
            if (!hashes.Contains(hash))
            {
                hashes.Add(hash);
                if (hashes.Count == Capacity)
                {
                    break;
                }
            }
        }

        return [.. hashes];
    }

    /// <summary>True when the script's requested blueprint is in the published set. An empty
    /// template or an empty set is never friendly: unknown stays suppressed.</summary>
    public static bool IsFriendlyTemplate(string template, ReadOnlySpan<uint> published)
    {
        if (published.IsEmpty || template.Trim('\0').Length == 0)
        {
            return false;
        }

        if (published.Length > Capacity)
        {
            published = published[..Capacity];
        }

        return published.Contains(Hash(template));
    }
}
