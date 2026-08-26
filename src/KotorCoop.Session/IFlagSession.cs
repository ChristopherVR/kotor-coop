// World event flags, converged across every player through EventFlagStore.
namespace KotorCoop.Session;

public interface IFlagSession
{
    EventFlagStore Flags { get; }

    /// <summary>Requests a world flag change. Delivered reliably.</summary>
    void SetFlag(uint flagId, sbyte value);
}
