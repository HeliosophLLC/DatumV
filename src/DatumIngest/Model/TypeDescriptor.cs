namespace Heliosoph.DatumV.Model;

/// <summary>
/// Describes a single named field within a <see cref="TypeDescriptor"/> for a struct type.
/// </summary>
/// <param name="Name">Case-sensitive field name as declared at the construction site.</param>
/// <param name="TypeId">Type registry id of this field's value type. 0 = no type registered.</param>
public sealed record StructFieldDescriptor(string Name, int TypeId);

/// <summary>
/// Structural description of a value's shape stored in a per-query <see cref="TypeRegistry"/>.
/// Identified by a 16-bit type-id on <see cref="DataValue"/>. Enables self-describing struct
/// and typed-array values without threading <see cref="ColumnInfo"/> through every consumer.
/// </summary>
/// <param name="Kind">Underlying <see cref="DataKind"/> of the value.</param>
/// <param name="IsArray">True when the value is a typed array with element kind <see cref="Kind"/>.</param>
/// <param name="Nullable">True when null values are permitted at this position.</param>
/// <param name="Fields">
/// Ordered field descriptors for <see cref="DataKind.Struct"/> types; null for all other kinds.
/// </param>
/// <param name="ElementTypeId">
/// For typed arrays whose element is itself a struct or nested array, the type-id of the element
/// shape in the same registry. Null for primitive-element arrays (Int32[], Float32[], etc.).
/// </param>
public sealed record TypeDescriptor(
    DataKind Kind,
    bool IsArray,
    bool Nullable,
    IReadOnlyList<StructFieldDescriptor>? Fields,
    int? ElementTypeId)
{
    /// <summary>
    /// Returns the zero-based index of <paramref name="name"/> in <see cref="Fields"/>,
    /// using a case-insensitive comparison. Returns -1 when the field is not found or
    /// <see cref="Fields"/> is null.
    /// </summary>
    public int FindFieldIndex(string name)
    {
        if (Fields is null) return -1;
        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // Records use reference equality for IReadOnlyList; override to use sequence equality.

    /// <inheritdoc/>
    public bool Equals(TypeDescriptor? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Kind == other.Kind
            && IsArray == other.IsArray
            && Nullable == other.Nullable
            && ElementTypeId == other.ElementTypeId
            && FieldsEqual(Fields, other.Fields);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Kind);
        hc.Add(IsArray);
        hc.Add(Nullable);
        hc.Add(ElementTypeId);
        if (Fields is not null)
            foreach (var f in Fields)
                hc.Add(f);
        return hc.ToHashCode();
    }

    private static bool FieldsEqual(IReadOnlyList<StructFieldDescriptor>? a, IReadOnlyList<StructFieldDescriptor>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
