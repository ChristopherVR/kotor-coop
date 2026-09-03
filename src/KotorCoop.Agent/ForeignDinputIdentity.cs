using System.Text;

namespace KotorCoop.Agent;

/// <summary>
/// Pure identification of a foreign (non-kotor-coop) <c>dinput8.dll</c>, for the friendly
/// name that goes into the install summary ("found another mod in dinput8.dll (ReShade);
/// installing as a chain..."). Never touches disk itself — <see cref="PackageInstaller"/>
/// reads the file's bytes and calls in here, so this decision logic is unit-testable without
/// a real PE file on disk.
/// </summary>
public static class ForeignDinputIdentity
{
    /// <summary>Whether the foreign file is a mod worth chaining, or just Windows' own DLL.</summary>
    public enum Kind
    {
        /// <summary>A plain copy of Windows' own dinput8.dll — not a mod. Safe to overwrite
        /// outright; there is nothing to chain.</summary>
        SystemFile,

        /// <summary>A recognised mod (ReShade, Ultimate ASI Loader, ...) or an unrecognised
        /// one — either way, something the player installed on purpose and would want kept.</summary>
        Mod,
    }

    public readonly record struct Identity(Kind Kind, string FriendlyName);

    // A Win32 version resource stores its strings (CompanyName, FileDescription, ...) as
    // UTF-16LE key/value pairs inside the file, so a plain byte search for the value finds it
    // without needing a full PE resource-table parser — the same trick PackageInstaller.Identify
    // already uses for our own markers. Windows' real dinput8.dll carries this exact string.
    private static readonly byte[] MicrosoftCompanyMarker = Encoding.Unicode.GetBytes("Microsoft Corporation");

    // ReShade exports a "ReShadeVersion" symbol and its own name appears in its strings either
    // way; Ultimate ASI Loader's name appears verbatim, and "UltimateASI" is the shorter form
    // some redistributions use. Plain ASCII: these are narrow strings in both mods' binaries.
    private static readonly byte[] ReShadeVersionMarker = Encoding.ASCII.GetBytes("ReShadeVersion");
    private static readonly byte[] ReShadeMarker = Encoding.ASCII.GetBytes("ReShade");
    private static readonly byte[] UltimateAsiLongMarker = Encoding.ASCII.GetBytes("Ultimate ASI Loader");
    private static readonly byte[] UltimateAsiShortMarker = Encoding.ASCII.GetBytes("UltimateASI");

    /// <summary>
    /// <paramref name="image"/> is the raw file bytes. <paramref name="fileDescription"/> is
    /// the version resource's FileDescription string when one could be read (a real file may
    /// carry no version resource at all, or one FileVersionInfo could not parse) — used only
    /// for the last-resort fallback name, so callers with no version-info reader can pass null.
    /// </summary>
    public static Identity Identify(ReadOnlySpan<byte> image, string? fileDescription)
    {
        if (image.IndexOf(MicrosoftCompanyMarker) >= 0)
        {
            return new Identity(Kind.SystemFile, "the system dinput8.dll (Microsoft Corporation)");
        }

        if (image.IndexOf(ReShadeVersionMarker) >= 0 || image.IndexOf(ReShadeMarker) >= 0)
        {
            return new Identity(Kind.Mod, "ReShade");
        }

        if (image.IndexOf(UltimateAsiLongMarker) >= 0 || image.IndexOf(UltimateAsiShortMarker) >= 0)
        {
            return new Identity(Kind.Mod, "Ultimate ASI Loader");
        }

        string description = fileDescription?.Trim() ?? string.Empty;
        return new Identity(Kind.Mod,
            description.Length > 0 ? $"another dinput8 mod ({description})" : "another dinput8 mod");
    }
}
