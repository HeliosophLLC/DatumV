namespace Heliosoph.DatumV.Serialization.MediaBag;

/// <summary>
/// Shared per-entry filter applied by <see cref="IMediaBagReader"/> implementations
/// to drop the OS/editor metadata that commonly appears in user-uploaded archives.
/// Keeping the rule in one place ensures ZIP and TAR readers reject the same noise.
/// </summary>
internal static class MediaBagFilter
{
    /// <summary>
    /// Returns <c>true</c> when the entry path should be skipped silently rather
    /// than treated as a data entry.
    /// </summary>
    public static bool IsIgnorableMetadata(string fullName)
    {
        if (fullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) return true;

        string name = Path.GetFileName(fullName);
        if (name.Length == 0) return false;

        if (name[0] == '.') return true;

        if (name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
