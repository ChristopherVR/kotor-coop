namespace KotorCoop.Protocol;

/// <summary>
/// The bookkeeping behind "drive the follower to the owner's node": which of the owner's
/// positions we have already been sent to, and how many times the engine has deferred the
/// current request.
///
/// Split out of the controller because it is arithmetic over two integers and a serial, and
/// because two of its rules are past defects that cost live runs to find:
///
/// ⚠ A SUCCESSFUL CALL IS NOT AN ARRIVAL. <c>CSWSObject::SendDialogEntry 0x004CD460</c>
/// returns 1 from its DEFER path as well as its send path: when <c>IsDialogDelay 0x004CD2D0</c>
/// says wait, it stores the entry at +0x4C, raises +0x48 and reports success having sent
/// nothing. That gate is shut exactly when a follower first tries to use it, because a
/// conversation opening is one of the things that stamps the one-second timer behind
/// <c>GetCanSendDialog</c>. Measured: a client joined, drove 19 -> 29 three milliseconds after
/// its conversation delivered content, was told "success", and sat on 19 for the next
/// seventeen seconds with the serial already banked so it never tried again. So the NODE is
/// the acknowledgement — nothing is banked until a re-read shows the engine actually moved,
/// and the whole thing simply runs again next frame.
///
/// ⚠ A ZERO applied-serial means "drive me wherever the owner is", which is exactly what a
/// fresh follow wants. Under the pick replay this replaced, a joiner had to skip the owner's
/// history by hand or it fired every pick already made, back to back, and walked through
/// several nodes in a second (the conversation "skipping"). Position has no history to skip.
/// </summary>
public sealed class DialogueNodeFollower
{
    /// <summary>The owner's node serial we have already been driven to. Zero is "none yet".</summary>
    private uint _appliedSerial;

    private int _deferrals;

    /// <summary>Consecutive frames the engine has deferred the CURRENT request.</summary>
    public int Deferrals => _deferrals;

    /// <summary>The serial banked so far — diagnostic, and what a test asserts on.</summary>
    public uint AppliedSerial => _appliedSerial;

    /// <summary>Forget the position. Called whenever a follow is not live, so every new follow
    /// starts from "wherever the owner is" and no deferral count is inherited from the last
    /// one. The old controller reset neither on the give-up path, so a follow could begin
    /// already believing it had been driven to the owner's current serial — and then sit
    /// still until the owner moved again.</summary>
    public void Reset()
    {
        _appliedSerial = 0;
        _deferrals = 0;
    }

    /// <summary>
    /// The cheap gate, before any engine read. False means there is nothing to do this frame:
    /// the owner has not said where it is (-1 is never driven — a follower that cannot be told
    /// where to go waits where it is, rather than being sent to node 0, the top of the
    /// conversation), or we have already been sent there, or our own conversation has not
    /// produced a line yet. Driving a shell that has delivered nothing races the open, and the
    /// open is what decides which panel the conversation is even running on.
    /// </summary>
    public bool NeedsNode(int peerNode, uint peerSerial, bool hasContent) =>
        peerNode >= 0 && hasContent && peerSerial != _appliedSerial;

    /// <summary>A local pending entry may move the follower after it acknowledged the
    /// owner's serial. The owner remains authoritative until a new serial arrives.</summary>
    public bool NeedsNode(int peerNode, uint peerSerial, bool hasContent, int localNode) =>
        peerNode >= 0 && peerSerial != 0 && hasContent
        && (peerSerial != _appliedSerial || localNode != peerNode);

    /// <summary>
    /// Whether the engine is on the wanted node, banking the owner's serial if it is. Called
    /// twice per drive: once BEFORE, because entries auto-advance identically on both instances
    /// through any stretch without a choice in it and that is the common case (bank it, or
    /// every such node is re-examined on every frame for as long as the owner sits on it), and
    /// once AFTER, where it is the acknowledgement the note above insists on.
    /// </summary>
    public bool Arrived(uint peerSerial, int localNode, int wantedNode)
    {
        if (localNode != wantedNode)
        {
            return false;
        }

        _appliedSerial = peerSerial;
        _deferrals = 0;
        return true;
    }

    /// <summary>The engine took the request and did not move. Returns the running count so the
    /// caller can complain exactly once, at a threshold.</summary>
    public int NoteDeferral() => ++_deferrals;
}
