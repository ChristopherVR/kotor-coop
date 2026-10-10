namespace KotorCoop.Plugin;

/// <summary>
/// Dialogue stunt models: the canned animation tracks a cutscene conversation plays its
/// participants through (STUNT_55a's end_55a maps "PLAYER" to m44ac_c01_char02, STUNT_57's
/// m41ad_c01 maps the player and every companion). The engine does not move the participant's
/// object; it loads the stunt model onto the participant's client body as an add-in animation
/// source and plays stunt animation ids (0x578..0x6BF) from it, so the body walks a pre-authored
/// path relative to where it stands.
///
/// GOG 1.0.3, disassembled 2026-10-10: CSWSDialog::SendDialogModels 0x5A0080 resolves the dlg's
/// StuntList and hands it to CGuiInGame::HandleDialogModels 0x62E020, which stores the client ids
/// at CGuiInGame+0x190 and 16-byte resrefs at +0x194 (count +0x18C) and calls
/// LoadStuntModelsForDialog 0x62C620: for each id, CClientExoApp::GetGameObject 0x5ED580, object
/// vtable +0x0C to the client creature, creature vtable +0x98 GetModel(0xFF body / 0xFE head, 0),
/// then Gob vtable +0x20 (LoadAddInAnimations, the add-in lands at Gob+0x64) with the resref.
/// UnloadStuntModelsForDialog 0x62C6F0 (from CloseDialog) does Gob vtable +0x18
/// PlayAnimation(0, 1.0f, 0, 0) then +0x24 (RemoveAddInAnimations). Per node,
/// SetDialogAnimations 0x6313A0 plays creature vtable +0x9C SetAnimation(server+0xD4, 0, 0).
///
/// The co-op stand-in is never a participant, so these let the plugin give it the local
/// player's track and animation ids. MAIN THREAD ONLY.
/// </summary>
internal static unsafe partial class Odyssey
{
    private const int GuiStuntCount = 0x18C;
    private const int GuiStuntClientIds = 0x190;
    private const int GuiStuntResRefs = 0x194;
    private const int StuntResRefStride = 16;
    private const int GobAddInModel = 0x64;
    private const int ModelBody = 0xFF;
    private const int ModelHead = 0xFE;

    /// <summary>Cutscene animation ids a stunt-driven body plays. The dlg's own numbers start at
    /// 1200 (end_55a's PLAYER lines are 1200..1205, live on the player's body at +0x48,
    /// 2026-10-10); IsLoopingDialogStuntAnimation 0x62AB70 tests 0x578..0x6BF for the looping
    /// subset. The range covers both.</summary>
    public const ushort StuntAnimationFirst = 1200;
    public const ushort StuntAnimationLast = 0x6BF;

    /// <summary>True for an animation id a dialogue stunt model plays.</summary>
    public static bool IsStuntAnimation(ushort id) => id >= StuntAnimationFirst && id <= StuntAnimationLast;

    /// <summary>
    /// Copies the stunt model the running conversation loaded onto <paramref name="playerServer"/>'s
    /// client body (its StuntList entry), NUL-terminated, and returns its length; 0 when the
    /// conversation gave the player no stunt.
    /// </summary>
    public static int ReadStuntModelFor(nint playerServer, byte* destination, int capacity)
    {
        nint gui = GetInGameGui();
        nint twin = playerServer == 0 ? 0 : *(nint*)(playerServer + CreClientTwin);
        nint clientInternal = GetClient();
        if (gui == 0 || twin == 0 || clientInternal == 0 || capacity < StuntResRefStride + 1)
        {
            return 0;
        }

        int count = *(int*)(gui + GuiStuntCount);
        nint ids = *(nint*)(gui + GuiStuntClientIds);
        nint resrefs = *(nint*)(gui + GuiStuntResRefs);
        if (count <= 0 || count > 64 || ids == 0 || resrefs == 0)
        {
            return 0;
        }

        for (int i = 0; i < count; i++)
        {
            nint obj = ClientGetGameObjectFn(clientInternal, *(uint*)(ids + (i * 4)));
            if (obj == 0)
            {
                continue;
            }

            var asCreature = (delegate* unmanaged[Thiscall]<nint, nint>)(*(nint*)(*(nint*)obj + 0x0C));
            if (asCreature(obj) != twin)
            {
                continue;
            }

            byte* source = (byte*)(resrefs + (i * StuntResRefStride));
            int length = 0;
            while (length < StuntResRefStride && source[length] != 0)
            {
                destination[length] = source[length];
                length++;
            }

            destination[length] = 0;
            return length;
        }

        return 0;
    }

    /// <summary>Loads <paramref name="resref"/> (NUL-terminated) as the add-in animation source of
    /// <paramref name="creatureServer"/>'s client body and head, the way the engine loads a
    /// participant's. A part that already carries an add-in is left alone. True when any part
    /// took it.</summary>
    public static bool LoadStuntModel(nint creatureServer, byte* resref, out string diagnostic)
    {
        diagnostic = string.Empty;
        nint twin = creatureServer == 0 ? 0 : *(nint*)(creatureServer + CreClientTwin);
        if (twin == 0)
        {
            diagnostic = "no client body";
            return false;
        }

        bool loaded = false;
        foreach (int part in stackalloc int[] { ModelBody, ModelHead })
        {
            nint gob = GetModel(twin, part);
            nint before = gob == 0 ? 0 : *(nint*)(gob + GobAddInModel);
            if (gob != 0 && before == 0)
            {
                var load = (delegate* unmanaged[Thiscall]<nint, byte*, void>)(*(nint*)(*(nint*)gob + 0x20));
                load(gob, resref);
            }

            nint after = gob == 0 ? 0 : *(nint*)(gob + GobAddInModel);
            loaded |= after != 0;
            diagnostic += $"{(part == ModelBody ? "body" : "head")} gob 0x{gob:X} vt 0x{(gob == 0 ? 0 : *(nint*)gob):X} add-in 0x{before:X}->0x{after:X}; ";
        }

        return loaded;
    }

    /// <summary>Diagnostic: the add-in slots of a creature's body and head models.</summary>
    public static string DescribeStuntSlots(nint creatureServer)
    {
        nint twin = creatureServer == 0 ? 0 : *(nint*)(creatureServer + CreClientTwin);
        if (twin == 0)
        {
            return "no client body";
        }

        nint body = GetModel(twin, ModelBody);
        nint head = GetModel(twin, ModelHead);
        return $"body gob 0x{body:X} vt 0x{(body == 0 ? 0 : *(nint*)body):X} add-in 0x{(body == 0 ? 0 : *(nint*)(body + GobAddInModel)):X}, "
            + $"head gob 0x{head:X} add-in 0x{(head == 0 ? 0 : *(nint*)(head + GobAddInModel)):X}";
    }

    /// <summary>Undoes <see cref="LoadStuntModel"/> exactly as CloseDialog's unload does for a
    /// participant: reset the animation, then remove the add-in source.</summary>
    public static void UnloadStuntModel(nint creatureServer)
    {
        nint twin = creatureServer == 0 ? 0 : *(nint*)(creatureServer + CreClientTwin);
        if (twin == 0)
        {
            return;
        }

        foreach (int part in stackalloc int[] { ModelBody, ModelHead })
        {
            nint gob = GetModel(twin, part);
            if (gob == 0 || *(nint*)(gob + GobAddInModel) == 0)
            {
                continue;
            }

            var play = (delegate* unmanaged[Thiscall]<nint, nint, float, int, int, void>)(*(nint*)(*(nint*)gob + 0x18));
            play(gob, 0, 1.0f, 0, 0);
            var remove = (delegate* unmanaged[Thiscall]<nint, void>)(*(nint*)(*(nint*)gob + 0x24));
            remove(gob);
        }

        SetInCutscene(twin, false);
    }

    /// <summary>Plays animation <paramref name="id"/> on <paramref name="creatureServer"/>'s client
    /// body through the same call SetDialogAnimations makes for a participant.</summary>
    public static void PlayStuntAnimation(nint creatureServer, ushort id)
    {
        nint twin = creatureServer == 0 ? 0 : *(nint*)(creatureServer + CreClientTwin);
        if (twin == 0)
        {
            return;
        }

        // SetDialogAnimations' own per-node sequence (0x63187E..0x6318AE): the animbase into
        // cutscene mode, +0x54 cleared, back to idle if anything else is playing, then the id.
        // Without the idle step only the first id ever took (live 2026-10-10).
        SetInCutscene(twin, true);
        *(int*)(twin + 0x54) = 0;
        var setAnimation = (delegate* unmanaged[Thiscall]<nint, int, int, int, void>)(*(nint*)(*(nint*)twin + 0x9C));
        if (*(ushort*)(twin + TwinCurrentAnimation) != 0x2710)
        {
            setAnimation(twin, 0x2710, 0, 0);
        }

        setAnimation(twin, id, 0, 0);
    }

    /// <summary>The animbase's cutscene flag (animbase vtable +0x70), which SetDialogAnimations
    /// raises for a participant's node animation and lowers when the conversation ends.</summary>
    private static void SetInCutscene(nint twin, bool on)
    {
        nint animBase = *(nint*)(twin + 0x68);
        if (animBase == 0)
        {
            return;
        }

        var set = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)animBase + 0x70));
        set(animBase, on ? 1 : 0);
    }

    /// <summary>The animation a creature's client body is playing (CSWCCreature+0x48, the field
    /// SetDialogAnimations reads at 0x6314B9), or 0 without a body.</summary>
    public static ushort GetBodyAnimation(nint creatureServer)
    {
        nint twin = creatureServer == 0 ? 0 : *(nint*)(creatureServer + CreClientTwin);
        return twin == 0 ? (ushort)0 : *(ushort*)(twin + TwinCurrentAnimation);
    }

    private static nint GetModel(nint twin, int part)
    {
        var getModel = (delegate* unmanaged[Thiscall]<nint, int, int, nint>)(*(nint*)(*(nint*)twin + 0x98));
        return getModel(twin, part, 0);
    }
}
