namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Canonical identity for a table in the catalog: a <c>(schema, name)</c>
/// pair. Case-insensitive on both fields (matches SQL identifier
/// semantics). <see cref="ToString"/> renders as <c>schema.name</c> — used
/// for diagnostics, EXPLAIN output, and the temporary string-keyed
/// catalog indexer.
/// </summary>
/// <param name="Schema">
/// Schema this table lives in. Conventional values: <c>public</c> for
/// user tables, <c>system</c> for engine-projected views (udfs,
/// procedures, models), <c>information_schema</c> and
/// <c>datum_catalog</c> for the SQL standard / engine catalog views.
/// </param>
/// <param name="Name">Unqualified table name within the schema.</param>
public readonly record struct QualifiedName(string Schema, string Name)
{
    /// <summary>Case-insensitive equality on both <see cref="Schema"/> and <see cref="Name"/>.</summary>
    public bool Equals(QualifiedName other) =>
        StringComparer.OrdinalIgnoreCase.Equals(Schema, other.Schema)
        && StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Schema ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name ?? string.Empty));

    /// <summary>Renders as <c>schema.name</c>.</summary>
    public override string ToString() => $"{Schema}.{Name}";

    /// <summary>
    /// Parses a string of the form <c>schema.name</c> or <c>name</c> into
    /// a <see cref="QualifiedName"/>. Unqualified names default to
    /// <paramref name="defaultSchema"/> (typically <c>public</c>). Splits
    /// on the first <c>.</c> only — names containing more dots end up in
    /// the <see cref="Name"/> field.
    /// </summary>
    public static QualifiedName Parse(string flat, string defaultSchema = "public")
    {
        ArgumentNullException.ThrowIfNull(flat);
        int dot = flat.IndexOf('.');
        return dot < 0
            ? new QualifiedName(defaultSchema, flat)
            : new QualifiedName(flat[..dot], flat[(dot + 1)..]);
    }
}
