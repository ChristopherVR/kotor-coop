using KotorCoop.Protocol;

namespace KotorCoop.MockGame;

/// <summary>
/// A tiny top-down world standing in for a KOTOR module. Two "areas" exist so module
/// filtering (a peer in a different area must not be drawn) can actually be exercised.
/// </summary>
/// <param name="originX">
/// Centre of the roaming area. Defaults to the middle of the mock world, but can be set to
/// a real game's coordinates so the mock shares a coordinate space with it. Without that,
/// a mock peer bridged into KOTOR lands ~50 units from the player, out of sight.
/// </param>
public sealed class MockWorld(string startingModule, float originX = MockWorld.Size / 2f, float originY = MockWorld.Size / 2f)
{
    public const float Size = 100f;
    public const float MoveSpeed = 22f;
    public const float TurnSpeed = 3.2f;

    public static readonly string[] Modules = ["end_m01aa", "tar_m02aa"];

    public float OriginX { get; } = originX;

    public float OriginY { get; } = originY;

    public Vector3f Position { get; private set; } = new(originX, originY, 0f);

    public float Facing { get; private set; }

    public string Module { get; private set; } = startingModule;

    public int Health { get; private set; } = 100;

    public uint Animation { get; private set; }

    public bool Moving { get; private set; }

    public void Step(float deltaSeconds, MoveInput input)
    {
        Moving = input.Forward != 0 || input.Strafe != 0;

        if (input.Turn != 0)
        {
            Facing = Angles.Normalize(Facing + (input.Turn * TurnSpeed * deltaSeconds));
        }

        if (Moving)
        {
            float distance = MoveSpeed * deltaSeconds;
            float forwardX = MathF.Cos(Facing);
            float forwardY = MathF.Sin(Facing);

            float dx = ((forwardX * input.Forward) + (forwardY * -input.Strafe)) * distance;
            float dy = ((forwardY * input.Forward) + (forwardX * input.Strafe)) * distance;

            float half = Size / 2f;
            Position = new Vector3f(
                Math.Clamp(Position.X + dx, OriginX - half, OriginX + half),
                Math.Clamp(Position.Y + dy, OriginY - half, OriginY + half),
                Position.Z);
        }

        // Animation id stands in for the game's current animation, which a real bridge
        // would read so remote avatars are not all frozen in an idle pose.
        Animation = Moving ? 2u : 1u;
    }

    public void CycleModule()
    {
        int index = Array.IndexOf(Modules, Module);
        Module = Modules[(index + 1) % Modules.Length];
        Position = new Vector3f(OriginX, OriginY, Position.Z);
    }

    public void Damage(int amount) => Health = Math.Clamp(Health - amount, 0, 100);
}

public readonly record struct MoveInput(float Forward, float Strafe, float Turn)
{
    public static readonly MoveInput None = new(0f, 0f, 0f);
}
