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

    // Name → TypeId index for the pre-interned named-type vocabulary
    // (NamedTypeRegistry.Entries). Case-insensitive on lookup; the stored
    // keys are the canonical names from the static registry. Anonymous
    // struct values built at runtime don't appear here.
    private readonly Dictionary<string, int> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Constructs a fresh per-query registry, pre-interning every entry in
    /// <see cref="NamedTypeRegistry.Entries"/> in topological order so the
    /// vocabulary is immediately resolvable by name. TypeIds are
    /// deterministic per fresh registry — the i-th entry always lands at
    /// the i-th interned TypeId.
    /// </summary>
    public TypeRegistry()
    {
        foreach (NamedTypeRegistry.NamedTypeDefinition def in NamedTypeRegistry.Entries)
        {
            int typeId = def.Build(this, _byName);
            // Capture the resolved TypeId under the canonical name so
            // later entries' Build callbacks (and downstream callers) can
            // reference it. Names within a single vocabulary are unique;
            // a collision here means a programming error in NamedTypeRegistry.
            if (!_byName.TryAdd(def.Name, typeId))
            {
                throw new InvalidOperationException(
                    $"NamedTypeRegistry has duplicate entry '{def.Name}'.");
            }
        }
    }

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

    /// <summary>
    /// Interns a fully-formed <see cref="TypeDescriptor"/> directly. Used by
    /// the file-format reader, which deserializes descriptors recursively
    /// out of the on-disk type table and needs to register them as-is.
    /// </summary>
    public int InternDescriptor(TypeDescriptor descriptor) => Intern(descriptor);

    // ── ColumnInfo convenience ─────────────────────────────────────────────

    /// <summary>
    /// Interns the shape described by <paramref name="col"/>, recursing into struct fields.
    /// </summary>
    /// <remarks>
    /// IsArray takes precedence over Kind: an <c>Array&lt;Struct&gt;</c> column has both
    /// <c>Kind == Struct</c> and <c>IsArray == true</c>, and we need the array
    /// descriptor (<c>IsArray=true, ElementTypeId=structTypeId</c>) — not the bare
    /// element struct — registered for the column's TypeId. Without this ordering,
    /// downstream <c>BuildStructArray</c> hits a struct descriptor where it expects
    /// an array descriptor, fails to resolve <c>ElementTypeId</c>, and produces
    /// f0..fN rendering on what should be self-describing nested arrays.
    /// </remarks>
    public int InternFromColumnInfo(ColumnInfo col)
    {
        if (col.IsArray)
        {
            int? elementTypeId = col.Kind == DataKind.Struct && col.Fields is { } ef
                ? InternStructFromColumnInfoFields(ef)
                : null;
            return InternArrayType(col.Kind, elementTypeId, col.Nullable);
        }

        if (col.Kind == DataKind.Struct && col.Fields is { } fields)
            return InternStructFromColumnInfoFields(fields);

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

    /// <summary>
    /// Looks up a named-type entry by name (case-insensitive). Returns
    /// the pre-interned TypeId, or <see cref="NoType"/> when no entry
    /// matches. Anonymous struct shapes built at runtime are not reachable
    /// via this lookup — only the vocabulary in
    /// <see cref="NamedTypeRegistry.Entries"/>.
    /// </summary>
    public int GetTypeIdByName(string name)
        => _byName.TryGetValue(name, out int typeId) ? typeId : NoType;

    /// <summary>
    /// True when <paramref name="name"/> matches an entry in the
    /// named-type vocabulary. Used by the type-annotation resolver to
    /// disambiguate "this is a named type" from "this is an unknown
    /// identifier."
    /// </summary>
    public bool IsNamedType(string name) => _byName.ContainsKey(name);

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
