namespace DatumIngest.Model;

/// <summary>
/// Resolves a textual SQL type annotation (as carried in AST <c>TypeName</c>
/// fields) into the runtime <c>(DataKind, IsArray)</c> pair. Annotations may
/// be a bare scalar name (<c>"Int32"</c>, <c>"String"</c>, <c>"bool"</c>) or
/// the canonical array wrapper <c>"Array&lt;T&gt;"</c> produced by the parser
/// from either <c>Array&lt;T&gt;</c> or <c>T[]</c> source syntax.
/// </summary>
/// <remarks>
/// The runtime model is "one element kind plus an IsArray flag" — there is no
/// nested-array support and arrays do not carry per-element nullability.
/// Nested annotations (<c>Array&lt;Array&lt;T&gt;&gt;</c>) are rejected here.
/// </remarks>
public static class TypeAnnotationResolver
{
    /// <summary>
    /// Attempts to parse <paramref name="annotation"/> into a kind+IsArray
    /// pair. Returns <see langword="false"/> for unknown scalar names or for
    /// malformed array wrappers (including nested arrays).
    /// </summary>
    public static bool TryParse(string annotation, out DataKind kind, out bool isArray)
    {
        kind = default;
        isArray = false;

        if (string.IsNullOrEmpty(annotation))
        {
            return false;
        }

        if (TryStripArrayWrapper(annotation, out string inner))
        {
            // Nested arrays — Array<Array<T>>, Array<T[]>, T[][] — are
            // explicitly out of scope. The runtime is single-level only.
            if (TryStripArrayWrapper(inner, out _))
            {
                return false;
            }

            if (!TryResolveScalar(inner, out kind))
            {
                return false;
            }

            isArray = true;
            return true;
        }

        return TryResolveScalar(annotation, out kind);
    }

    /// <summary>
    /// Strips a single <c>Array&lt;...&gt;</c> wrapper from
    /// <paramref name="annotation"/>. Trims whitespace inside the angle
    /// brackets so e.g. <c>"Array&lt; String &gt;"</c> resolves cleanly.
    /// </summary>
    private static bool TryStripArrayWrapper(string annotation, out string inner)
    {
        const string Prefix = "Array<";
        if (annotation.Length > Prefix.Length + 1
            && annotation.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && annotation[^1] == '>')
        {
            inner = annotation[Prefix.Length..^1].Trim();
            return inner.Length > 0;
        }

        inner = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves a bare scalar type name. Accepts <see cref="DataKind"/> enum
    /// names case-insensitively plus the same alias set as the rest of the
    /// engine (<c>bool</c>, <c>scalar</c>).
    /// </summary>
    private static bool TryResolveScalar(string name, out DataKind kind)
    {
        if (Enum.TryParse(name, ignoreCase: true, out kind))
        {
            return true;
        }

        switch (name.ToLowerInvariant())
        {
            case "bool":
                kind = DataKind.Boolean;
                return true;
            case "scalar":
                kind = DataKind.Float32;
                return true;
        }

        kind = default;
        return false;
    }
}
