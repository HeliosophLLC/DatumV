using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Resolves a parameter's declared type annotation into a list of struct
/// field shapes when the type names a struct (either inline
/// <c>Struct&lt;name: Kind, …&gt;</c> or a named <see cref="NamedTypeRegistry"/>
/// entry like <c>ChatMessage</c>). Used by the model registrar and by the
/// catalog manifest builder's UDF / procedure paths so a single
/// <c>messages Array&lt;ChatMessage&gt;</c> declaration surfaces field-name
/// completion across every callable kind that exposes positional parameters.
/// </summary>
internal static class ParameterStructFieldResolver
{
    /// <summary>
    /// Parses <paramref name="typeName"/> (the raw annotation as it appears
    /// in the AST) into a struct field list. Strips an outer
    /// <c>Array&lt;…&gt;</c> or <c>T[]</c> wrapper unconditionally so the
    /// same call works for scalar struct parameters and array-of-struct
    /// parameters. Returns <see langword="null"/> for non-struct
    /// annotations, opaque bare <c>Struct</c>, and unknown names.
    /// </summary>
    public static IReadOnlyList<StructFieldShape>? TryResolveShapes(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        string element = typeName.Trim();

        const string ArrayPrefix = "Array<";
        if (element.Length > ArrayPrefix.Length + 1
            && element.StartsWith(ArrayPrefix, StringComparison.OrdinalIgnoreCase)
            && element[^1] == '>')
        {
            element = element[ArrayPrefix.Length..^1].Trim();
        }
        else
        {
            // `T[]` shorthand. Strip a trailing `[…]` suffix (with or
            // without dim-args) so the element name is what survives.
            int bracket = element.IndexOf('[');
            if (bracket > 0 && element[^1] == ']')
            {
                element = element[..bracket].Trim();
            }
        }

        if (StructTypeAnnotation.TryParse(element, out IReadOnlyList<StructFieldShape> inline))
        {
            return inline;
        }

        foreach (NamedTypeRegistry.NamedTypeDefinition def in NamedTypeRegistry.Entries)
        {
            if (!string.Equals(def.Name, element, StringComparison.OrdinalIgnoreCase)) continue;
            if (!StructTypeAnnotation.TryParse(def.Description, out IReadOnlyList<StructFieldShape> named))
            {
                return null;
            }
            // Overlay per-field enum vocabulary from the named type's
            // metadata so the language server can suggest legal literals
            // inside the struct's field values. Annotation strings don't
            // encode enums; only the registry entry does.
            if (def.FieldEnumValues is { Count: > 0 } enumValues)
            {
                StructFieldShape[] enriched = new StructFieldShape[named.Count];
                for (int i = 0; i < named.Count; i++)
                {
                    StructFieldShape field = named[i];
                    enriched[i] = enumValues.TryGetValue(field.Name, out IReadOnlyList<string>? values)
                        ? new StructFieldShape(field.Name, field.Kind, values)
                        : field;
                }
                return enriched;
            }
            return named;
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="typeName"/> directly into the manifest's
    /// <see cref="StructFieldSignature"/> shape. Convenience for callers
    /// that emit straight into <see cref="ParameterSignature.StructFields"/>
    /// (UDFs, procedures) and don't need the intermediate
    /// <see cref="StructFieldShape"/> form.
    /// </summary>
    public static IReadOnlyList<StructFieldSignature>? TryResolveSignatures(string? typeName)
    {
        IReadOnlyList<StructFieldShape>? shapes = TryResolveShapes(typeName);
        if (shapes is null) return null;
        StructFieldSignature[] result = new StructFieldSignature[shapes.Count];
        for (int i = 0; i < shapes.Count; i++)
        {
            result[i] = new StructFieldSignature
            {
                Name = shapes[i].Name,
                Kind = shapes[i].Kind,
                EnumValues = shapes[i].EnumValues,
            };
        }
        return result;
    }
}
