namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// Shared path-string helpers for the <c>File</c> function family. Treats
/// <c>/</c> and <c>\</c> as equivalent separators on every platform so SQL
/// behaves the same on Windows and Linux.
/// </summary>
internal static class PathOps
{
    /// <summary>
    /// Returns the index of the last <c>/</c> or <c>\</c> in <paramref name="s"/>,
    /// or <c>-1</c> when no separator is present.
    /// </summary>
    public static int LastSeparatorIndex(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            char c = s[i];
            if (c == '/' || c == '\\') return i;
        }
        return -1;
    }

    /// <summary>True when <paramref name="c"/> is a path separator (<c>/</c> or <c>\</c>).</summary>
    public static bool IsSeparator(char c) => c == '/' || c == '\\';
}
