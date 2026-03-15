namespace DatumIngest.Model;

/// <summary>
/// Resolves a textual SQL type annotation (as carried in AST <c>TypeName</c>
/// fields) into the runtime <c>(DataKind, IsArray)</c> pair, plus optional
/// width / dimensionality. Annotations may be a bare scalar name
/// (<c>"Int32"</c>, <c>"String"</c>, <c>"bool"</c>, the PG aliases
/// <c>"VARCHAR"</c> / <c>"TEXT"</c>), the canonical array wrapper
/// <c>"Array&lt;T&gt;"</c> produced by the parser from either
/// <c>Array&lt;T&gt;</c> or <c>T[]</c> source syntax, or a named-type
/// identifier from <see cref="NamedTypeRegistry.Entries"/>. Any of those
/// may carry a trailing paren-arg list:
/// <list type="bullet">
/// <item><c>"String(N)"</c> / <c>"VARCHAR(N)"</c> — character-max-length
/// for the column (returned via <c>maxLength</c>).</item>
/// <item><c>"Array&lt;Float32&gt;(N)"</c> / <c>"Array&lt;Float32&gt;(N,M,…)"</c>
/// — fixed dimensionality (returned via <c>fixedShape</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// The runtime model is "one element kind plus an IsArray flag" — there is no
/// nested-array support and arrays do not carry per-element nullability.
/// Nested annotations (<c>Array&lt;Array&lt;T&gt;&gt;</c>) and inner-wrapper
/// suffixes (<c>Array&lt;Float32(10)&gt;</c>) are rejected here.
/// </remarks>
public static class TypeAnnotationResolver
{
    /// <summary>
    /// Attempts to parse <paramref name="annotation"/> into a kind+IsArray
    /// pair. Returns <see langword="false"/> for unknown scalar names or for
    /// malformed array wrappers (including nested arrays).
    /// </summary>
    /// <remarks>
    /// This overload does not consult the named-type vocabulary or surface
    /// width / shape — pass through <see cref="TryParse(string, TypeRegistry?, out DataKind, out bool, out int)"/>
    /// for named types or the seven-arg overload for width / shape.
    /// </remarks>
    public static bool TryParse(string annotation, out DataKind kind, out bool isArray)
        => TryParse(annotation, types: null, out kind, out isArray, out _, out _, out _);

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
    /// Registry-aware overload. Same contract as the 2-arg form plus
    /// named-type resolution; does not surface width / shape — use the
    /// seven-arg overload for those.
    /// </summary>
    public static bool TryParse(
        string annotation,
        TypeRegistry? types,
        out DataKind kind,
        out bool isArray,
        out int typeId)
        => TryParse(annotation, types, out kind, out isArray, out typeId, out _, out _, out _);

    /// <summary>
    /// Width / shape-aware overload that does not surface the blank-padded
    /// distinction. Convenience for callers that only need MaxLength
    /// (e.g. coercion paths that treat VARCHAR and CHAR identically).
    /// </summary>
    public static bool TryParse(
        string annotation,
        TypeRegistry? types,
        out DataKind kind,
        out bool isArray,
        out int typeId,
        out int? maxLength,
        out int[]? fixedShape)
        => TryParse(annotation, types, out kind, out isArray, out typeId, out maxLength, out fixedShape, out _);

    /// <summary>
    /// Full-fidelity overload. Same contract as the registry-aware form plus:
    /// <list type="bullet">
    /// <item><paramref name="maxLength"/> — set when the annotation is a
    /// scalar <see cref="DataKind.String"/> with a single paren-arg
    /// (<c>VARCHAR(10)</c> / <c>CHAR(10)</c>); the value is the declared
    /// character maximum. Bare <c>CHAR</c> alone yields <c>1</c> per PG
    /// semantics. <see langword="null"/> for bare strings and all
    /// non-string kinds.</item>
    /// <item><paramref name="fixedShape"/> — set when the annotation is an
    /// array wrapper with a paren-arg list (<c>Array&lt;Float32&gt;(384)</c>,
    /// <c>Float32[384]</c>, <c>Array&lt;Float32&gt;(3,3)</c>); each entry is
    /// a positive dimension. <see langword="null"/> for variable-length
    /// arrays and non-array kinds.</item>
    /// <item><paramref name="isBlankPadded"/> — true when the annotation
    /// used the <c>CHAR</c> alias (blank-padded fixed-length string).
    /// Always false for <c>VARCHAR</c> / <c>TEXT</c> / <c>String</c>.</item>
    /// </list>
    /// </summary>
    public static bool TryParse(
        string annotation,
        TypeRegistry? types,
        out DataKind kind,
        out bool isArray,
        out int typeId,
        out int? maxLength,
        out int[]? fixedShape,
        out bool isBlankPadded)
    {
        isBlankPadded = false;
        kind = default;
        isArray = false;
        typeId = TypeRegistry.NoType;
        maxLength = null;
        fixedShape = null;

        if (string.IsNullOrEmpty(annotation))
        {
            return false;
        }

        // Strip an outermost paren-arg list, if any. Inner wrappers may not
        // carry one (we reject "Array<Float32(10)>" below); the only legal
        // place for it is at the very end of the annotation string.
        int[]? args = null;
        string stripped = annotation;
        if (TryStripParenSuffix(annotation, out string before, out int[]? parsedArgs))
        {
            // All declared dimensions / widths must be positive.
            foreach (int v in parsedArgs!)
            {
                if (v <= 0) return false;
            }
            stripped = before;
            args = parsedArgs;
        }

        if (TryStripArrayWrapper(stripped, out string inner))
        {
            // Nested arrays — Array<Array<T>>, Array<T[]>, T[][] — are
            // explicitly out of scope. The runtime is single-level only.
            if (TryStripArrayWrapper(inner, out _))
            {
                return false;
            }

            // Inner-wrapper paren suffixes (e.g. "Array<Float32(10)>") are
            // also rejected — the dimensionality belongs on the array, not
            // on the element kind.
            if (TryStripParenSuffix(inner, out _, out _))
            {
                return false;
            }

            if (TryResolveScalar(inner, out kind, out _))
            {
                isArray = true;
                if (args is not null) fixedShape = args;
                return true;
            }

            // Named-type inside Array<...>. Recognize the name from the
            // static vocabulary; if a per-query registry was supplied,
            // also resolve the element struct's TypeId. The array's own
            // TypeId would be a separate InternArrayType call, which the
            // *caller* makes if it needs the array-shape TypeId.
            if (IsNamedType(inner))
            {
                kind = DataKind.Struct;
                isArray = true;
                if (args is not null) fixedShape = args;
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

        if (TryResolveScalar(stripped, out kind, out bool isCharAlias))
        {
            isBlankPadded = isCharAlias;
            if (args is not null)
            {
                // Paren-args on a bare scalar only make sense as a
                // character-max-length on String; everything else is
                // either meaningless (Int32(10)) or wants the array form
                // for shape.
                if (kind != DataKind.String || args.Length != 1) return false;
                maxLength = args[0];
            }
            else if (isCharAlias)
            {
                // PG semantics: bare CHAR == CHAR(1).
                maxLength = 1;
            }
            return true;
        }

        // Bare named-type identifier — final fallback. Recognized purely
        // from the static vocabulary; per-query TypeId resolution only
        // happens when a registry was supplied. Named types don't take
        // paren suffixes today.
        if (IsNamedType(stripped))
        {
            if (args is not null) return false;
            kind = DataKind.Struct;
            if (types is not null)
            {
                int namedTypeId = types.GetTypeIdByName(stripped);
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
    /// Strips a trailing <c>(N[, N…])</c> from <paramref name="annotation"/>
    /// if present. The args must be non-empty and parse as positive integers
    /// (sign validation is done by the caller; here we only enforce digit
    /// parseability so the caller can return a clean false on bad input).
    /// </summary>
    private static bool TryStripParenSuffix(string annotation, out string before, out int[]? args)
    {
        before = annotation;
        args = null;
        if (annotation.Length < 3 || annotation[^1] != ')') return false;

        int openIdx = annotation.LastIndexOf('(');
        if (openIdx < 1) return false;

        string inside = annotation[(openIdx + 1)..^1];
        if (inside.Length == 0) return false;

        string[] parts = inside.Split(',');
        int[] parsed = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(
                    parts[i].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int v))
            {
                return false;
            }
            parsed[i] = v;
        }

        before = annotation[..openIdx];
        args = parsed;
        return true;
    }

    /// <summary>
    /// Resolves a bare scalar type name. Accepts <see cref="DataKind"/> enum
    /// names case-insensitively plus the alias set: <c>bool</c>,
    /// <c>scalar</c>, the PG string aliases <c>varchar</c>/<c>text</c>, and
    /// the blank-padded alias <c>char</c>.
    /// </summary>
    /// <param name="name">The scalar type name to resolve.</param>
    /// <param name="kind">On success, the resolved <see cref="DataKind"/>.</param>
    /// <param name="isCharAlias">
    /// True only when <paramref name="name"/> matched the <c>char</c>
    /// alias — i.e. the caller should mark the column as blank-padded.
    /// False for every other resolved scalar (including <c>varchar</c> /
    /// <c>text</c> / direct <c>String</c>).
    /// </param>
    private static bool TryResolveScalar(string name, out DataKind kind, out bool isCharAlias)
    {
        isCharAlias = false;
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
            case "varchar":
            case "text":
                kind = DataKind.String;
                return true;
            case "char":
                kind = DataKind.String;
                isCharAlias = true;
                return true;
        }

        kind = default;
        return false;
    }
}
