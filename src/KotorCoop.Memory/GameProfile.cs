using System.Text.Json;
using System.Text.Json.Serialization;

namespace KotorCoop.Memory;

public enum AnchorMode
{
    /// <summary>Sweep the live process for a signature. Required when the image is packed.</summary>
    PatternScan,

    /// <summary>Module base plus a fixed offset. Safe when the image has no ASLR.</summary>
    ModuleOffset,

    /// <summary>A literal address. Only valid for a non-ASLR target such as swkotor.exe.</summary>
    Absolute,
}

/// <summary>How to locate the root of the game's player state each session.</summary>
public sealed class AnchorSpec
{
    public AnchorMode Mode { get; set; } = AnchorMode.PatternScan;

    /// <summary>AOB signature with '??' wildcards. Used when Mode is PatternScan.</summary>
    public string? Pattern { get; set; }

    public ScanScope Scope { get; set; } = ScanScope.PrivateCommitted;

    /// <summary>Module to anchor against. Defaults to the main executable.</summary>
    public string? Module { get; set; }

    public long ModuleOffset { get; set; }

    public long AbsoluteAddress { get; set; }

    /// <summary>Added to the resolved address before anything else.</summary>
    public int Displacement { get; set; }

    /// <summary>
    /// When true the anchor is treated as holding a pointer, and is dereferenced once.
    /// This is the normal shape for a game global such as "pointer to the player object".
    /// </summary>
    public bool DereferenceAnchor { get; set; }

    /// <summary>Further Cheat Engine style offsets applied after the anchor.</summary>
    public int[] PointerChain { get; set; } = [];
}

/// <summary>Field offsets, relative to the resolved anchor, for the local player.</summary>
public sealed class StateLayout
{
    /// <summary>Three consecutive floats: x, y, z.</summary>
    public int? Position { get; set; }

    /// <summary>Single float, radians.</summary>
    public int? Facing { get; set; }

    /// <summary>Fixed-width null-padded ASCII field holding the current module/area name.</summary>
    public int? ModuleName { get; set; }

    public int ModuleNameLength { get; set; } = 32;

    public int? Health { get; set; }

    public int? Animation { get; set; }
}

/// <summary>
/// Where to write remote players back into the game. Absent means the bridge is
/// read-only: the agent can observe and broadcast the local player but has nowhere to
/// put a remote avatar. That is the honest state for stock KOTOR, which has no spare
/// creature slots reserved for phantoms.
/// </summary>
public sealed class RemoteLayout
{
    public int Count { get; set; }

    public int Slots { get; set; }

    public int Stride { get; set; }

    public int Capacity { get; set; }

    public int Active { get; set; }

    public int PeerId { get; set; }

    public int Position { get; set; }

    public int Facing { get; set; }

    public int Health { get; set; }

    public int Animation { get; set; }

    public int Name { get; set; }

    public int NameLength { get; set; } = 32;
}

public sealed class GameProfile
{
    public string Name { get; set; } = "unnamed";

    public string? Notes { get; set; }

    /// <summary>Process names (without .exe) this profile applies to.</summary>
    public string[] ProcessNames { get; set; } = [];

    public AnchorSpec Anchor { get; set; } = new();

    public StateLayout Layout { get; set; } = new();

    public RemoteLayout? Remotes { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GameProfile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Profile not found: {path}", path);
        }

        string json = File.ReadAllText(path);
        GameProfile? profile = JsonSerializer.Deserialize<GameProfile>(json, SerializerOptions)
            ?? throw new InvalidDataException($"Profile '{path}' deserialised to null.");

        profile.Validate(path);
        return profile;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    private void Validate(string path)
    {
        if (Layout.Position is null)
        {
            throw new InvalidDataException(
                $"Profile '{path}' has no layout.position offset, so the local player cannot be read. "
                + "Run 'KotorCoop.Agent scan' to discover it.");
        }

        if (Anchor.Mode == AnchorMode.PatternScan && string.IsNullOrWhiteSpace(Anchor.Pattern))
        {
            throw new InvalidDataException($"Profile '{path}' uses PatternScan but defines no anchor.pattern.");
        }

        if (Anchor.Mode == AnchorMode.PatternScan
            && !BytePattern.TryParse(Anchor.Pattern!, out _, out string? error))
        {
            throw new InvalidDataException($"Profile '{path}' has an invalid anchor.pattern: {error}");
        }

        if (Remotes is { } remotes && (remotes.Stride <= 0 || remotes.Capacity <= 0))
        {
            throw new InvalidDataException(
                $"Profile '{path}' defines remotes but stride/capacity are not positive.");
        }
    }
}
