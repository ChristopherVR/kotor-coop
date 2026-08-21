using System.Net.Sockets;

namespace KotorCoop.Session;

/// <summary>
/// Thrown by <see cref="UdpChannel"/> when its bind fails because something else already
/// holds the port — almost always a second co-op agent instance still running (a crashed
/// previous session that never released it, or the player launching two games on the same
/// machine). Distinct from a bare <see cref="SocketException"/> so <see cref="SessionManager"/>
/// can turn it into one specific, actionable in-game line instead of a generic failure, and
/// so hosting fails cleanly back to idle rather than leaving a half-started session behind.
/// </summary>
public sealed class PortInUseException(int port, SocketException inner)
    : Exception($"port {port} is already in use", inner)
{
    /// <summary>The port the bind was attempted on.</summary>
    public int Port { get; } = port;
}
