namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- live executable-image digest (region 0x4720..0x4728, no TotalSize growth) --------
    //
    // docs/research/steam-support.md §8. The co-op compatibility handshake used to refuse a
    // session whose two peers' swkotor.exe FILES hashed differently. The Steam copy is the
    // same link as the DRM-free one under Valve's SteamStub v3 — .text encrypted at rest,
    // decrypted in place before the game's entry point runs — so the two files hash
    // differently and hook identically, and a Steam player and a GOG player were refused on
    // RefusalReason.Executable over nothing at all.
    //
    // The plugin is the only thing that can settle it: it lives INSIDE the decrypted image.
    // It hashes that image (ExeImageDigestRule, shared by source with the agent's protocol
    // assembly) and publishes the result here; the agent reads it and carries it as
    // ContentDigest.ExeDigest instead of the file hash.
    //
    // ⚠ COMPUTED BEFORE THE FIRST HOOK, in PluginHost.AllocateBlock, which runs before
    // InstallHooks. Our own inline detours rewrite .text, and WHICH ones get installed
    // depends on coop.ini — so a digest taken any later would differ between two peers whose
    // CONFIG differs and would refuse a session that works. One fixed point in startup is
    // what makes the two sides comparable.
    //
    // Rides in the free gap between the minigame peer lock (0x4704..0x4720) and the
    // new-character chargen handshake (0x4780), inside the 0xFC bytes TotalSize 0x4800
    // leaves after the item-template-spawn cursor. Uses 8 of them. No TotalSize growth, no
    // relayout of anything else.

    /// <summary>Plugin -&gt; agent. FNV-1a over the mapped image: the PE timestamp,
    /// <c>ImageBase</c>, every section header's name/RVA/virtual size, and the whole
    /// <c>.text</c> body. <c>AddressOfEntryPoint</c> is excluded on purpose — it is the one
    /// header field SteamStub changes, and it says nothing about the code that runs.</summary>
    public const int OffsetExeImageDigest = 0x4720;

    /// <summary>Plugin -&gt; agent. 1 once <see cref="OffsetExeImageDigest"/> holds a real
    /// value. Written AFTER the digest, so a reader that catches the block mid-write sees
    /// "unknown" rather than a half-written hash — and "unknown" only ever downgrades the
    /// executable check to a warning, never fabricates a match.</summary>
    public const int OffsetExeImageDigestKnown = 0x4724;
}
