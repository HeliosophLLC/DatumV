namespace DatumIngest.Model;

/// <summary>
/// Per-query intern table for <see cref="TypeDescriptor"/>s. Assigns a stable 16-bit type-id to
/// each structurally-distinct shape so that <see cref="DataValue"/>s carrying that id are
/// self-describing without threading <see cref="ColumnInfo"/> through every consumer.
/// </summary>
/// <remarks>
/// Lifetime matches the enclosing <see cref="DatumIngest.Execution.ExecutionContext"/>. Child
/// contexts share the same instance so type-ids are consistent across the operator tree.
/// Thread-safe: all mutations are guarded by a lock; reads of already-assigned ids are lock-free.
/// </remarks>
public sealed class TypeRegistry
{
    /// <summary>Reserved sentinel meaning "no type registered for this value."</summary>
    public const int NoType = 0;

    private readonly object _lock = new();
    private readonly Dictionary<TypeDescriptor, int> _intern = new();
    // Index 0 is the NoType sentinel (null). All registered descriptors start at index 1.
    private readonly List<TypeDescriptor?> _byId = [null];

    // ── public intern helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns the type-id for a struct with the given ordered field descriptors,
    /// registering a new id if the shape has not been seen before.
    /// </summary>
    public int InternStructType(IReadOnlyList<StructFieldDescriptor> fields, bool nullable = false)
        => Intern(new TypeDescriptor(DataKind.Struct, IsArray: false, nullable, fields, ElementTypeId: null));

    /// <summary>
    /// Returns the type-id for a typed array whose element kind is <paramref name="elementKind"/>.
    /// For arrays of struct or nested arrays supply the element's type-id via
    /// <paramref name="elementTypeId"/>.
    /// </summary>
    public int InternArrayType(DataKind elementKind, int? elementTypeId = null, bool nullable = false)
        => Intern(new TypeDescriptor(elementKind, IsArray: true, nullable, Fields: null, elementTypeId));

    /// <summary>Returns the type-id for a scalar value of the given kind.</summary>
    public int InternScalarType(DataKind kind, bool nullable = false)
        => Intern(new TypeDescriptor(kind, IsArray: false, nullable, Fields: null, ElementTypeId: null));

    // ── ColumnInfo convenience ─────────────────────────────────────────────

    /// <summary>
    /// Interns the shape described by <paramref name="col"/>, recursing into struct fields.
    /// </summary>
    public int InternFromColumnInfo(ColumnInfo col)
    {
        if (col.Kind == DataKind.Struct && col.Fields is { } fields)
            return InternStructFromColumnInfoFields(fields);

        if (col.IsArray)
        {
            int? elementTypeId = col.Kind == DataKind.Struct && col.Fields is { } ef
                ? InternStructFromColumnInfoFields(ef)
                : null;
            return InternArrayType(col.Kind, elementTypeId, col.Nullable);
        }

        return InternScalarType(col.Kind, col.Nullable);
    }

    /// <summary>
    /// Interns a struct type whose fields are described by a list of <see cref="ColumnInfo"/>s.
    /// Each field's type-id is recursively interned.
    /// </summary>
    public int InternStructFromColumnInfoFields(IReadOnlyList<ColumnInfo> fields)
    {
        var descriptors = new StructFieldDescriptor[fields.Count];
        for (int i = 0; i < fields.Count; i++)
            descriptors[i] = new StructFieldDescriptor(fields[i].Name, InternFromColumnInfo(fields[i]));
        return InternStructType(descriptors);
    }

    // ── lookup ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="TypeDescriptor"/> for <paramref name="typeId"/>,
    /// or <c>null</c> when <paramref name="typeId"/> is <see cref="NoType"/> (0).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="typeId"/> is positive but has not been registered.
    /// </exception>
    public TypeDescriptor? GetDescriptor(int typeId)
    {
        if (typeId == NoType) return null;
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _byId.Count)
                throw new ArgumentOutOfRangeException(nameof(typeId), typeId,
                    "Type-id has not been registered in this query's TypeRegistry.");
            return _byId[typeId];
        }
    }

    /// <summary>Total number of registered shapes (excluding the NoType sentinel).</summary>
    public int Count
    {
        get { lock (_lock) return _byId.Count - 1; }
    }

    // ── internals ──────────────────────────────────────────────────────────

    private int Intern(TypeDescriptor descriptor)
    {
        lock (_lock)
        {
            if (_intern.TryGetValue(descriptor, out int existing))
                return existing;
            int id = _byId.Count;
            _byId.Add(descriptor);
            _intern[descriptor] = id;
            return id;
        }
    }
}
