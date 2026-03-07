namespace DatumIngest.Model;

/// <summary>
/// Resolves a textual SQL type annotation (as carried in AST <c>TypeName</c>
/// fields) into the runtime <c>(DataKind, IsArray)</c> pair. Annotations may
/// be a bare scalar name (<c>"Int32"</c>, <c>"String"</c>, <c>"bool"</c>),
/// the canonical array wrapper <c>"Array&lt;T&gt;"</c> produced by the parser
/// from either <c>Array&lt;T&gt;</c> or <c>T[]</c> source syntax, or a
/// named-type identifier from <see cref="NamedTypeRegistry.Entries"/>
/// (<c>"ScoredClass"</c>, <c>"BoundingBox"</c>, …) — those resolve to
/// <see cref="DataKind.Struct"/> with the corresponding TypeId from the
/// supplied registry.
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
    /// <remarks>
    /// This overload does not consult the named-type vocabulary — pass a
    /// <see cref="TypeRegistry"/> via <see cref="TryParse(string, TypeRegistry?, out DataKind, out bool, out int)"/>
    /// to additionally accept <c>RETURNS ScoredClass</c>-shaped names.
    /// </remarks>
    public static bool TryParse(string annotation, out DataKind kind, out bool isArray)
        => TryParse(annotation, types: null, out kind, out isArray, out _);

    /// <summary>
    /// True when <paramref name="annotation"/> matches an entry in the
    /// engine-defined named-type vocabulary (case-insensitive). Used by
    /// the 2-arg <see cref="TryParse(string, out DataKind, out bool)"/>
    /// fast path to recognize <c>RETURNS ScoredClass</c>-shaped
    /// annotations without a per-query TypeRegistry — the vocabulary
    /// itself is static.
    /// </summary>
    public static bool IsNamedType(string annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return false;
        foreach (NamedTypeRegistry.NamedTypeDefinition def in NamedTypeRegistry.Entries)
        {
            if (string.Equals(def.Name, annotation, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Registry-aware overload. Same contract as the 3-arg form plus:
    /// <list type="bullet">
    /// <item>When <paramref name="annotation"/> is a named-type identifier
    /// (or <c>Array&lt;NamedType&gt;</c>), the matching TypeId is written
    /// to <paramref name="typeId"/> and <paramref name="kind"/> is set to
    /// the named type's underlying kind (<see cref="DataKind.Struct"/>
    /// for every entry today).</item>
    /// <item>For scalar / primitive-array annotations <paramref name="typeId"/>
    /// is <see cref="TypeRegistry.NoType"/> (0) — there's no per-shape
    /// TypeId to surface.</item>
    /// </list>
    /// </summary>
    public static bool TryParse(
        string annotation,
        TypeRegistry? types,
        out DataKind kind,
        out bool isArray,
        out int typeId)
    {
        kind = default;
        isArray = false;
        typeId = TypeRegistry.NoType;

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

            if (TryResolveScalar(inner, out kind))
            {
                isArray = true;
                return true;
            }

            // Named-type inside Array<...>. Recognize the name from the
            // static vocabulary; if a per-query registry was supplied,
            // also resolve the element struct's TypeId. The array's own
            // TypeId would be a separate InternArrayType call, which the
            // *caller* makes if it needs the array-shape TypeId
            // (signature matching against RETURNS Array<ScoredClass>
            // typically compares the element TypeId, not the array
            // container's).
            if (IsNamedType(inner))
            {
                kind = DataKind.Struct; // every named-type entry is Struct-kinded today
                isArray = true;
                if (types is not null)
                {
                    int elementTypeId = types.GetTypeIdByName(inner);
                    if (elementTypeId != TypeRegistry.NoType)
                    {
                        typeId = elementTypeId;
                    }
                }
                return true;
            }

            return false;
        }

        if (TryResolveScalar(annotation, out kind))
        {
            return true;
        }

        // Bare named-type identifier — final fallback. Recognized purely
        // from the static vocabulary; per-query TypeId resolution only
        // happens when a registry was supplied.
        if (IsNamedType(annotation))
        {
            kind = DataKind.Struct;
            if (types is not null)
            {
                int namedTypeId = types.GetTypeIdByName(annotation);
                if (namedTypeId != TypeRegistry.NoType)
                {
                    typeId = namedTypeId;
                }
            }
            return true;
        }

        return false;
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
