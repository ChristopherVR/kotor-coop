// Odyssey.StealthVisual.cs - the CLIENT-side stealth cosmetic (coop.ini stealth_partner_visual
// = translucent), added 2026-09-25 alongside StealthVisualMode.cs after docs/research/
// effect-sync.md §12.9's live screenshot showed the SERVER-side lever (Odyssey.Stealth.cs's
// SetStealthMode, stealth_partner_visual = hidden) fully hides a peer's stand-in instead of
// drawing the retail translucent shimmer. See effect-sync.md §12.10 for the full RE writeup
// this rests on.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CSWCCreature::SetStealthState(int mode, int arg2)</c> — thiscall, <c>ret 8</c>,
    /// VERIFIED by disassembly (2026-09-25, <c>tools/disasm.py</c>, GOG 1.0.3,
    /// <c>0x00613930</c>-<c>0x00613A60</c>):
    ///
    /// <code>
    /// 0x00613930  sub esp, 0xc
    /// 0x00613933  push esi
    /// 0x00613934  push edi
    /// 0x00613935  mov edi, [esp+0x18]        ; mode (first stack arg, entry+4)
    /// 0x00613939  cmp edi, 1
    /// 0x0061393E  mov esi, ecx               ; esi = this (CSWCCreature)
    /// ...                                    ; mode==1 AND old [esi+0x194]==0: pushes vfx row
    ///                                        ; 0x1F40 (8000) if a capability check passes, else
    ///                                        ; 0x1F42 (8002), color args 0x7F000000/0x7F000000
    ///                                        ; twice (~50% alpha) and 0/1, then falls into the
    ///                                        ; shared tail
    /// ...                                    ; mode==0 AND old [esi+0x194]==1: DeleteVisualEffect
    ///                                        ; (vtable+0xB8) on whichever of 0x1F40/0x1F42 is up,
    ///                                        ; then plays a one-shot "decloak" row 0x1F41 (8001)
    ///                                        ; with the same color args
    /// 0x00613A1A  call [vtable+0xB4]         ; AddVisualEffect(id, flag, color1, color2, 0, 0, &buf)
    /// 0x00613A55  mov [esi+0x194], edi       ; ALWAYS stores mode, whichever branch ran
    /// 0x00613A60  ret 8
    /// </code>
    ///
    /// <c>arg2</c> is read nowhere in the callee body — confirmed across the whole
    /// 0x613930-0x613A60 span — so it is passed as 0 below, matching every real call site.
    /// <c>+0x194</c> is the symbol DB's <c>CSWCCreature.stealth_state</c> (int); the setter is
    /// idempotent the same way <c>CSWSCreature::SetStealthMode</c> is (a call with the SAME
    /// mode the field already holds re-writes the field but skips the vfx add/remove).
    ///
    /// <c>tools/xrefs.py 0x613930</c> finds exactly TWO direct call sites in the whole image,
    /// BOTH inside <c>CSWCMessage::HandleServerToPlayerCreatureUpdate_Update</c>
    /// (<c>0x006655E0</c>) — the general per-creature network-update handler, not anything
    /// party-member-specific. Disassembly of the caller around <c>0x666700</c>-<c>0x666915</c>
    /// shows the actual gate: <c>cmp bl, 1</c> / <c>jne</c> selects mode==1 vs. mode==0 from a
    /// packet-carried flag (<c>bl</c>), landing on this call with mode = that flag either way —
    /// i.e. ANY creature whose client twin is told "your stealth flag changed" gets this call,
    /// the same way a real Sith stealth unit or a party member does. This is the retail stealth
    /// SHIMMER, and it is entirely independent of the SERVER field
    /// <c>CSWSCreature::SetStealthMode</c> writes (<see cref="SetStealthMode"/>,
    /// <c>CSWSCreature+0x4D1</c>) — different class, different offset, different call site.
    ///
    /// Why the SERVER field hides the stand-in instead: <c>CSWSCreature::
    /// BroadcastStealthDetectionData</c> (<c>0x004EC7A0</c>) is a per-observer loop (walks the
    /// player list, compares area ids at <c>+0x8C</c>, and only for a match sends a targeted
    /// network message) — the detection-relevance gate a real stealthed creature's EXISTENCE is
    /// broadcast through. Writing <c>+0x4D1</c> on the stand-in pushes it down that gate; an
    /// observer who has never "detected" it is simply never sent updates for it, so its client
    /// twin is never created/kept and nothing this function does could matter — full hide,
    /// exactly docs/research/effect-sync.md §12.9's screenshot. Calling THIS function directly,
    /// and leaving <c>+0x4D1</c> untouched, keeps the stand-in exactly as relevant/broadcast as
    /// it always was (nothing about detection changes) while wearing the same translucency vfx
    /// a real party member's own stealth draws.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetStealthStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x00613930;

    /// <summary>CSWCCreature + this = stealth_state (int) — the field the setter above always
    /// writes on return; read back so a call on a bad/torn-down twin is reported, not
    /// latched.</summary>
    private const int CreClientStealthState = 0x194;

    /// <summary>
    /// Puts a creature's CLIENT twin into (or takes it out of) the engine's own stealth
    /// COSMETIC — the translucent shimmer, never the server's detection-relevant mode
    /// (<see cref="SetStealthMode"/>). <paramref name="serverObjectId"/> is resolved to the
    /// client-side object through <c>CClientExoApp::GetGameObject</c> (<c>ClientGetGameObjectFn</c>,
    /// declared once in Odyssey.Dialogue.Bark.cs and reused here — private members of a partial
    /// class are visible from every file that makes up the type) via the documented
    /// <c>id | 0x80000000</c> server-to-client id conversion (PlayBark's own notes), then
    /// guarded against a vtable that is not <c>CSWCCreature</c>'s (<see
    /// cref="AttackClientCreatureVtable"/>, the same constant Odyssey.Combat.AttackClient.cs
    /// already verifies against) before the call — a body whose client twin has not
    /// materialised yet (or has torn down) answers false rather than dereferencing garbage.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetStealthVisualState(uint serverObjectId, bool on, out string diagnostic)
    {
        diagnostic = string.Empty;

        if (serverObjectId is 0 or ObjectInvalid)
        {
            diagnostic = "invalid object id";
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            diagnostic = "no client application object (menu, or not yet in-game)";
            return false;
        }

        uint clientObjectId = serverObjectId | 0x80000000;
        nint clientObj = ClientGetGameObjectFn(client, clientObjectId);
        if (clientObj == 0
            || !NativeMethods.IsReadable(clientObj, (nuint)(CreClientStealthState + 4))
            || *(nint*)clientObj != AttackClientCreatureVtable)
        {
            diagnostic = "no client-side creature twin yet";
            return false;
        }

        int mode = on ? 1 : 0;
        SetStealthStateFn(clientObj, mode, 0);
        int after = *(int*)(clientObj + CreClientStealthState);
        bool ok = on ? after != 0 : after == 0;
        if (!ok)
        {
            diagnostic = $"stealth_state field reads {after} after the call";
        }

        return ok;
    }
}
