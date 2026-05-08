namespace DatumIngest.Manifest;

/// <summary>
/// Parser / inspector for the canonical <c>Struct&lt;name: Kind, …&gt;</c>
/// annotation string. Used in two directions:
/// <list type="bullet">
///   <item>Engine side — <c>CatalogManifestBuilder</c> reads parsed
///   field shapes off a <c>ModelDescriptor</c>'s return-type annotation and
///   emits them onto the manifest's <c>ModelEntry</c> /
///   <c>FunctionSignature</c>.</item>
///   <item>LanguageServer side — hover / completion read the stringified
///   shape back into <see cref="StructFieldShape"/> tuples so a field reference
///   like <c>curr_depth.depth</c> resolves to the field's declared
///   <c>Array&lt;Float32&gt;</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The canonical form is <c>"Struct&lt;name: Kind[, name: Kind, …]&gt;"</c>
/// where each <c>Kind</c> is itself a complete type annotation (scalar,
/// array, or another struct). The bare <c>"Struct"</c> form — used by
/// models that declare <c>RETURNS Struct</c> without an explicit field list
/// — is opaque and yields <see langword="null"/> from <see cref="TryParse"/>.
/// </para>
/// <para>
/// Parsing is hand-rolled rather than going through the full SQL parser
/// because (a) the LanguageServer assembly can't take a parser dependency
/// at hover-resolution time without pulling in Superpower, and (b) the
/// canonical form is fixed and trivial enough that a single-pass tokenizer
/// over angle-bracket depth + comma-at-depth-zero handles every case the
/// engine produces.
/// </para>
/// </remarks>
public static class StructTypeAnnotation
{
    private const string Prefix = "Struct<";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="annotation"/>
    /// starts with the canonical <c>Struct&lt;</c> prefix — useful as a
    /// cheap pre-flight check before paying the full parse cost.
    /// </summary>
    public static bool LooksLikeStructAnnotation(string? annotation) =>
        annotation is not null
        && annotation.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase)
        && annotation.EndsWith(">", System.StringComparison.Ordinal);

    /// <summary>
    /// Parses a canonical <c>Struct&lt;name: Kind, …&gt;</c> annotation into
    /// a list of <see cref="StructFieldShape"/> tuples. Returns
    /// <see langword="false"/> for the bare <c>"Struct"</c> form (no field
    /// list) and for malformed input — callers fall back to treating the
    /// value as an opaque struct.
    /// </summary>
    public static bool TryParse(string? annotation, out IReadOnlyList<StructFieldShape> fields)
    {
        fields = System.Array.Empty<StructFieldShape>();
        if (!LooksLikeStructAnnotation(annotation)) return false;

        string inner = annotation![Prefix.Length..^1].Trim();
        if (inner.Length == 0) return false;

        List<StructFieldShape> parsed = new();
        foreach (string fieldText in SplitTopLevelCommas(inner))
        {
            string entry = fieldText.Trim();
            if (entry.Length == 0) return false;

            // `name: Kind` — split on the first top-level colon (kinds
            // themselves may contain colons only inside nested struct
            // bodies, which are guarded by angle brackets).
            int colonIdx = FindTopLevelColon(entry);
            if (colonIdx <= 0 || colonIdx >= entry.Length - 1) return false;

            string name = entry[..colonIdx].Trim();
            string kind = entry[(colonIdx + 1)..].Trim();
            if (name.Length == 0 || kind.Length == 0) return false;

            parsed.Add(new StructFieldShape(name, kind));
        }

        if (parsed.Count == 0) return false;
        fields = parsed;
        return true;
    }

    /// <summary>
    /// Builds a canonical <c>Struct&lt;…&gt;</c> annotation string from a
    /// list of <see cref="StructFieldShape"/> tuples. Inverse of
    /// <see cref="TryParse"/>; used by <c>CatalogManifestBuilder</c>
    /// when emitting a struct shape derived from a model's
    /// <c>OutputFields</c> rather than from the textual annotation.
    /// </summary>
    public static string Format(IReadOnlyList<StructFieldShape> fields)
    {
        if (fields.Count == 0) return "Struct";
        System.Text.StringBuilder sb = new();
        sb.Append(Prefix);
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(fields[i].Name).Append(": ").Append(fields[i].Kind);
        }
        sb.Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// Splits <paramref name="text"/> on commas that sit at zero nesting
    /// depth. Tracks both angle brackets (for nested
    /// <c>Struct&lt;...&gt;</c> / <c>Array&lt;...&gt;</c> bodies) AND
    /// parens (for array shape suffixes like
    /// <c>Array&lt;Float32&gt;(518, 518)</c>). Without paren tracking, a
    /// field type with a multi-dim suffix would false-split between the
    /// dims and shred the field list.
    /// </summary>
    private static IEnumerable<string> SplitTopLevelCommas(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<' || c == '(') depth++;
            else if (c == '>' || c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>
    /// Returns the index of the first colon at zero nesting depth, or
    /// <c>-1</c> if none. Mirrors the comma splitter so an inner colon
    /// (nested struct field separator) or paren content doesn't get
    /// picked up as the name/kind boundary.
    /// </summary>
    private static int FindTopLevelColon(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<' || c == '(') depth++;
            else if (c == '>' || c == ')') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }
}

/// <summary>
/// A single field in a parsed struct annotation. <see cref="Kind"/> is the
/// canonical kind string for the field's type — <c>"Int32"</c>,
/// <c>"Array&lt;Float32&gt;"</c>, even a nested <c>"Struct&lt;…&gt;"</c> for
/// fields that are themselves structs. <see cref="EnumValues"/> carries an
/// optional enumerated string vocabulary attached via metadata (e.g. a
/// named type registry entry's per-field enum); the annotation string
/// itself doesn't encode it.
/// </summary>
public sealed record StructFieldShape(
    string Name,
    string Kind,
    IReadOnlyList<string>? EnumValues = null);
