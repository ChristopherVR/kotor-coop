// Odyssey.StealthVisual.cs - the CLIENT-side stealth cosmetic (coop.ini stealth_partner_visual
// = translucent), added 2026-09-25 alongside StealthVisualMode.cs after docs/research/
// effect-sync.md §12.9's live screenshot showed the SERVER-side lever (Odyssey.Stealth.cs's
// SetStealthMode, stealth_partner_visual = hidden) fully hides a peer's stand-in instead of
// drawing the retail translucent shimmer. §12.10's first attempt (SetStealthState's own
// AddVisualEffect(0x1F40/0x1F42) call) ran live and read back its own field correctly but drew
// the stand-in FULLY OPAQUE (§12.12) - visualeffects.2da only has 144 rows, so 8000/8002 do not
// resolve to a usable effect and AddVisualEffect's own Init silently fails (its return value is
// never even checked by SetStealthState). §12.13 replaces the lever with a direct push to the
// client model's own alpha field (CSWCAnimBase::SetAlpha, the SAME terminal call retail's own
// vfx-alpha and door/area-fade systems both use), which live-testing then confirmed draws the
// stand-in translucent. See effect-sync.md §12.10/§12.13 for the full RE writeup this rests on.

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
    /// it always was (nothing about detection changes).
    ///
    /// <b>NOT the source of the translucency, live 2026-09-25 (§12.12/§12.13).</b> This still
    /// runs (kept for whatever other client bookkeeping <c>+0x194</c> feeds — the field name
    /// alone, <c>stealth_state</c>, suggests other systems may read it) but its own
    /// <c>AddVisualEffect(0x1F40/0x1F42, ...)</c> call draws NOTHING live: <c>visualeffects.2da</c>
    /// has only 144 rows (<c>tools/kotorfiles.py 2da visualeffects.2da</c>), so 8000/8002/8001
    /// do not resolve to a usable effect there, and <c>AddVisualEffect</c>'s own success/failure
    /// (<c>eax</c>) is never even tested by this function — a failed add is silently swallowed.
    /// The actual translucency lever is <see cref="SetAlphaFn"/> below.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetStealthStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x00613930;

    /// <summary>CSWCCreature + this = stealth_state (int) — the field the setter above always
    /// writes on return; read back so a call on a bad/torn-down twin is reported, not
    /// latched.</summary>
    private const int CreClientStealthState = 0x194;

    /// <summary>
    /// <c>CSWCObject::SetAlpha(float alpha, byte channel)</c> — thiscall, <c>ret 8</c>, VERIFIED
    /// by disassembly (2026-09-25, <c>0x0063CB50</c>):
    ///
    /// <code>
    /// 0x0063CB50  mov ecx, [ecx+0x68]     ; ecx = this-&gt;anim_base (CSWCAnimBase*, TwinAnimBase
    ///                                     ; in Odyssey.CreatureAnim.cs - CSWCObject::GetAnimBase
    ///                                     ; 0x0063C070 is the same [ecx+0x68], cross-verified)
    /// 0x0063CB53  test ecx, ecx
    /// 0x0063CB55  jne 0x63cb5c
    /// 0x0063CB57  xor eax, eax            ; no anim_base yet -&gt; return false, no write
    /// 0x0063CB59  ret 8
    /// 0x0063CB5C  mov eax, [ecx]
    /// 0x0063CB5E  jmp [eax+0x94]          ; TAIL JMP into the anim_base's OWN vtable slot 0x94 -
    ///                                     ; the adjustor-thunk pattern this project's vtable-
    ///                                     ; slot-dispatch memory note describes. Args/return
    ///                                     ; address are untouched by the jmp, so calling THIS
    ///                                     ; address behaves like an ordinary thiscall from the
    ///                                     ; caller's side.
    /// </code>
    ///
    /// <c>CSWCAnimBase</c>'s own vtable (<c>0x00754F60</c>) slot <c>+0x94</c> (<c>0x00754FF4</c>,
    /// one of <c>tools/xrefs.py 0x69EB20</c>'s ten address-literal hits) holds
    /// <c>CSWCAnimBase::SetAlpha</c> (<c>0x0069EB20</c>) — the base-class default, not
    /// overridden for a creature. Disassembled in full: when <c>channel == 0xFF</c> it stores
    /// the raw float bits into <c>CSWCAnimBase+0xC0</c>, then walks up to 5 material-channel
    /// pointers at <c>+0x90..+0xA3</c> and, for each non-null one, calls a per-material virtual
    /// function (color white + this alpha + a flag) — an IMMEDIATE push to the rendered
    /// materials, not a value merely stored for something else to pick up later. Returns 1 (in
    /// <c>eax</c>) when it actually walked the channel-0xFF path, 0 on the early exits (no
    /// anim_base, or a specific single-channel byte other than 0xFF — this codebase always
    /// passes 0xFF).
    ///
    /// <c>tools/xrefs.py 0x63CB50</c> finds exactly six direct callers, ALL inside
    /// <c>CSWCVisualEffectOnObject::ApplyAlpha</c>/<c>RemoveAlpha</c>/<c>UpdateAlpha</c>
    /// (<c>0x006A3E80</c>/<c>0x006A4920</c>/<c>0x006A4C00</c>) and <c>CSWCObject::FadeObject</c>/
    /// <c>SetFadeState</c> (the door/area-transition and vfx-alpha families,
    /// <c>GetVisualEffectAlpha</c>/<c>GetFinalFadeAlpha</c> at <c>0x0063DE50</c>/<c>0x0063DEE0</c>)
    /// — an EVENT-DRIVEN "recompute once, push once" system, never a per-frame poller, so a
    /// direct call here is not fighting anything that re-derives and overwrites it every frame
    /// (this project's per-frame-write rule is about the CALL SITE's discipline, not this
    /// setter's own cost — it is cheap and idempotent either way, same as every other Odyssey
    /// setter). LIVE-VERIFIED 2026-09-25 (§12.13): the stand-in renders translucent.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, byte, int> SetAlphaFn =
        (delegate* unmanaged[Thiscall]<nint, float, byte, int>)0x0063CB50;

    /// <summary>CSWCAnimBase + this = the "all channels" alpha this setter stores when
    /// <c>channel == 0xFF</c> — read back to confirm a call actually landed (distinct object
    /// from <see cref="CreClientStealthState"/>, which is the CSWCCreature-level field the OTHER
    /// setter above writes).</summary>
    private const int AnimBaseAlpha = 0xC0;

    /// <summary>The translucency level this project ships: roughly what the old vfx's
    /// <c>0x7F000000</c> argument implied (~50%) before it turned out not to fire at all.</summary>
    public const float TranslucentAlpha = 0.5f;

    public const float OpaqueAlpha = 1.0f;

    /// <summary>
    /// Puts a creature's CLIENT twin into (or takes it out of) the engine's own stealth
    /// COSMETIC, never the server's detection-relevant mode (<see cref="SetStealthMode"/>).
    /// <paramref name="serverObjectId"/> is resolved to the client-side object through
    /// <c>CClientExoApp::GetGameObject</c> (<c>ClientGetGameObjectFn</c>, declared once in
    /// Odyssey.Dialogue.Bark.cs and reused here — private members of a partial class are
    /// visible from every file that makes up the type) via the documented
    /// <c>id | 0x80000000</c> server-to-client id conversion (PlayBark's own notes), then
    /// guarded against a vtable that is not <c>CSWCCreature</c>'s (<see
    /// cref="AttackClientCreatureVtable"/>, the same constant Odyssey.Combat.AttackClient.cs
    /// already verifies against) before either call — a body whose client twin has not
    /// materialised yet (or has torn down) answers false rather than dereferencing garbage.
    ///
    /// Calls <see cref="SetStealthStateFn"/> first (harmless bookkeeping; see its own doc
    /// comment for why its visual effect does not fire), then pushes
    /// <see cref="TranslucentAlpha"/>/<see cref="OpaqueAlpha"/> through <see cref="SetAlphaFn"/>
    /// — THIS is the call that actually changes what gets drawn. Success requires the alpha
    /// call to both return true AND read back the value it just wrote from
    /// <c>anim_base+0xC0</c>.
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
        SetStealthStateFn(clientObj, mode, 0); // harmless; see its own doc comment

        nint animBase = *(nint*)(clientObj + TwinAnimBase);
        if (animBase == 0 || !NativeMethods.IsReadable(animBase, (nuint)(AnimBaseAlpha + 4)))
        {
            diagnostic = "client twin has no anim_base yet (model not built)";
            return false;
        }

        float alpha = on ? TranslucentAlpha : OpaqueAlpha;
        int applied = SetAlphaFn(clientObj, alpha, 0xFF);
        float after = *(float*)(animBase + AnimBaseAlpha);
        bool ok = applied != 0 && after == alpha;
        if (!ok)
        {
            diagnostic = $"SetAlpha returned {applied}, anim_base+0xC0 reads {after} " +
                $"(wanted {alpha})";
        }

        return ok;
    }
}
