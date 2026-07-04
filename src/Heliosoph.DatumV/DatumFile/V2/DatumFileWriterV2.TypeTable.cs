using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Type-table machinery for <see cref="DatumFileWriterV2"/>: the in-flight
/// allocator that maps runtime <see cref="TypeRegistry"/> ids to stable
/// on-disk ids, the per-column homogeneous-shape capture, and the
/// finalize-time flush that writes descriptor blobs to the sidecar +
/// fills the footer's <c>TypeTable</c>.
/// </summary>
public sealed partial class DatumFileWriterV2
{
    /// <summary>
    /// Per-query <see cref="TypeRegistry"/> the writer reads descriptors
    /// from at finalize. Set explicitly via <see cref="SetTypeRegistry"/>;
    /// when <see langword="null"/> the writer falls back to the legacy
    /// untyped path (no <see cref="DatumFileFlagsV2.HasTypeTable"/> flag,
    /// no <c>StructTypeId</c> stamping). Existing v4-style consumers that
    /// don't yet thread a registry stay byte-identical to pre-v5.
    /// </summary>
    private TypeRegistry? _typeRegistry;

    /// <summary>
    /// Runtime → on-disk type-id allocator. Owned by the writer for its
    /// lifetime; passed into VariableSlot encoders that handle struct /
    /// Array&lt;Struct&gt; columns. Always non-null after Initialize so
    /// encoders can call <see cref="ITypeIdAllocator.AllocateOrLookup"/>
    /// without null-guarding; the allocator returns the runtime id
    /// unchanged (no flush) when <see cref="_typeRegistry"/> is null.
    /// </summary>
    private TypeIdAllocator _allocator = new();

    /// <summary>
    /// Per-Struct-column homogeneous-shape capture. Index parallel to
    /// <see cref="_columns"/>; entries left null for non-Struct or
    /// Array&lt;Struct&gt; columns. Captured on first non-null value;
    /// every subsequent value's TypeId is validated against the first
    /// — mixing two shapes in one column throws.
    /// </summary>
    private ushort?[]? _columnStructTypeIds;

    /// <summary>
    /// Append mode: the existing file's TypeTable entries (with their
    /// descriptor blob bytes, read during rehydrate while the read-side
    /// sidecar was open). Re-emitted verbatim at finalize so an append
    /// session never strips the table; the blobs seed the allocator when
    /// a registry arrives so same-shape values reuse existing on-disk
    /// ids. Null for fresh writes and files without a type table.
    /// </summary>
    private List<(TypeTableEntryV5 Entry, byte[] Blob)>? _existingTypeTableEntries;
    private bool _allocatorSeededFromExisting;

    /// <summary>
    /// Append mode: per-column <c>StructTypeId</c>s from the existing
    /// footer, carried forward so a session that writes no struct values
    /// (or has no registry) doesn't strip them. Index parallel to
    /// <see cref="_columns"/>.
    /// </summary>
    private ushort?[]? _existingColumnOnDiskStructTypeIds;

    /// <summary>
    /// Sets the per-query <see cref="TypeRegistry"/> the writer uses to
    /// resolve descriptors at finalize. Must be called before the first
    /// <see cref="WriteRowBatch"/> for the file to receive a TypeTable.
    /// Callers that omit this stay on the legacy untyped path.
    /// </summary>
    public void SetTypeRegistry(TypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _typeRegistry = registry;
        SeedAllocatorFromExistingTypeTable();
    }

    /// <summary>
    /// Interns the existing file's descriptor blobs into
    /// <see cref="_typeRegistry"/> and seeds the allocator's
    /// runtime → on-disk map, so this session's values that match an
    /// existing shape resolve to the id already on disk instead of
    /// allocating a duplicate entry. No-op until both the registry and
    /// the rehydrated entries are present; idempotent thereafter.
    /// </summary>
    private void SeedAllocatorFromExistingTypeTable()
    {
        if (_allocatorSeededFromExisting) return;
        if (_typeRegistry is null || _existingTypeTableEntries is null) return;

        foreach ((TypeTableEntryV5 entry, byte[] blob) in _existingTypeTableEntries)
        {
            ushort runtimeId = checked((ushort)TypeDescriptorSerializer.DeserializeAndIntern(blob, _typeRegistry));
            _allocator.SeedExisting(runtimeId, entry.OnDiskTypeId);
        }
        _allocatorSeededFromExisting = true;
    }

    /// <summary>
    /// Declares a struct column's shape up front from its schema-level
    /// field list, independent of any row values. Used at CREATE TABLE
    /// time so the empty file already carries the declared shape in its
    /// type table and column footer — the first INSERT (and every cold
    /// reopen) then reads the shape off the file instead of needing the
    /// original DDL. Requires <see cref="SetTypeRegistry"/> and
    /// <c>Initialize</c> to have run. Covers scalar Struct and
    /// Array&lt;Struct&gt; columns alike (for arrays the recorded id is
    /// the ELEMENT shape).
    /// </summary>
    public void DeclareStructColumnShape(int columnIndex, IReadOnlyList<ColumnInfo> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (_typeRegistry is null)
        {
            throw new InvalidOperationException(
                "DeclareStructColumnShape requires SetTypeRegistry to be called first.");
        }
        if (_columns is null)
        {
            throw new InvalidOperationException(
                "DeclareStructColumnShape requires Initialize to be called first.");
        }

        ushort runtimeId = checked((ushort)_typeRegistry.InternStructFromColumnInfoFields(fields));
        _columnStructTypeIds ??= new ushort?[_columns.Length];
        _columnStructTypeIds[columnIndex] = runtimeId;
        _allocator.AllocateOrLookup(runtimeId);
    }

    /// <summary>
    /// Captures the homogeneous-shape struct TypeId for non-array Struct
    /// columns. Called per row from <see cref="WriteRowBatch"/> when the
    /// column kind is <see cref="DataKind.Struct"/> without
    /// <see cref="DataValue.IsArray"/>. Validates uniformity; the column's
    /// declared shape must match every value's runtime TypeId.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Two different runtime TypeIds appear in the same column — violates
    /// the homogeneous-column invariant the format relies on.
    /// </exception>
    private void CaptureStructColumnTypeId(int columnIndex, ushort runtimeTypeId)
    {
        if (_columnStructTypeIds is null)
        {
            _columnStructTypeIds = new ushort?[_columns!.Length];
        }
        ushort? captured = _columnStructTypeIds[columnIndex];
        if (captured is null)
        {
            _columnStructTypeIds[columnIndex] = runtimeTypeId;
            // Force the allocator to register the column-level shape so
            // the TypeTable carries it even if no row reaches the
            // Array<Struct> path that would otherwise allocate it.
            _allocator.AllocateOrLookup(runtimeTypeId);
            return;
        }
        if (captured.Value != runtimeTypeId)
        {
            throw new InvalidOperationException(
                $"Column '{_columns![columnIndex].Name}' received two different struct TypeIds " +
                $"({captured.Value} and {runtimeTypeId}) in the same write session. The .datum " +
                "format requires homogeneous Struct columns — every value in a Struct column " +
                "must share one shape. Use a separate column for the alternative shape.");
        }
    }

    /// <summary>
    /// Returns the on-disk struct TypeId to stamp on a non-array Struct
    /// column's footer, or <see langword="null"/> when the column was
    /// never populated (zero rows, or the writer is on the legacy path
    /// without a registry).
    /// </summary>
    private ushort? ResolveColumnStructTypeIdForFooter(int columnIndex)
    {
        // Columns added mid-session (ALTER ADD COLUMN) sit past the end of
        // the rehydrated array — they have no pre-append id by definition.
        ushort? existing = _existingColumnOnDiskStructTypeIds is { } ids && columnIndex < ids.Length
            ? ids[columnIndex]
            : null;

        if (_typeRegistry is null || _columnStructTypeIds?[columnIndex] is not { } runtimeId)
        {
            // Session wrote no struct values into this column (or has no
            // registry): the pre-append id, if any, carries forward so
            // existing rows keep resolving.
            return existing;
        }

        ushort resolved = _allocator.AllocateOrLookup(runtimeId);
        if (existing is { } onDisk && onDisk != resolved)
        {
            throw new InvalidOperationException(
                $"Column '{_columns![columnIndex].Name}' stores struct shape id {onDisk} on disk but " +
                $"this append session wrote values of shape id {resolved}. Struct columns are " +
                "homogeneous across appends — the incoming values must match the column's shape.");
        }
        return resolved;
    }

    /// <summary>
    /// Builds the TypeTable entries by serializing every emitted runtime
    /// TypeId's descriptor (recursively inlined) into the sidecar and
    /// recording each entry's offset/length. Returns an empty list when
    /// no struct types were seen — the writer leaves the
    /// <see cref="DatumFileFlagsV2.HasTypeTable"/> flag clear in that
    /// case so the file stays byte-identical to v4.
    /// </summary>
    private IReadOnlyList<TypeTableEntryV5> EmitTypeTable()
    {
        // Existing entries carry forward verbatim — their descriptor blobs
        // are already in the sidecar at the recorded offsets. New shapes
        // allocated this session (Snapshot excludes seeded mappings) get
        // fresh blobs appended behind them.
        int existingCount = _existingTypeTableEntries?.Count ?? 0;
        IReadOnlyList<(ushort runtimeId, ushort onDiskId)> mappings = _typeRegistry is null
            ? Array.Empty<(ushort, ushort)>()
            : _allocator.Snapshot();
        if (existingCount == 0 && mappings.Count == 0) return Array.Empty<TypeTableEntryV5>();

        TypeTableEntryV5[] entries = new TypeTableEntryV5[existingCount + mappings.Count];
        for (int i = 0; i < existingCount; i++)
        {
            entries[i] = _existingTypeTableEntries![i].Entry;
        }

        if (mappings.Count > 0 && _sidecar is null)
        {
            throw new InvalidOperationException(
                "DatumFileWriterV2 produced struct values but has no sidecar to hold their " +
                "TypeDescriptor blobs. Open the writer with a sidecar path or skip the registry.");
        }

        for (int i = 0; i < mappings.Count; i++)
        {
            (ushort runtimeId, ushort onDiskId) = mappings[i];
            TypeDescriptor? descriptor = _typeRegistry!.GetDescriptor(runtimeId);
            if (descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Allocator recorded runtime TypeId {runtimeId} but the TypeRegistry " +
                    "has no descriptor for it. Every emitted TypeId must be live in the " +
                    "registry at finalize time.");
            }
            byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(descriptor, _typeRegistry);
            (long offset, long length) = _sidecar!.Append(blob);
            entries[existingCount + i] = new TypeTableEntryV5(onDiskId, offset, checked((int)length));
        }
        return entries;
    }

    /// <summary>
    /// Records a runtime → on-disk id mapping per writer instance.
    /// Allocates dense on-disk ids in first-sight order so the file's
    /// TypeTable is compact. Snapshot returns mappings in allocation
    /// order — the order the entries are written to disk.
    /// </summary>
    private sealed class TypeIdAllocator : ITypeIdAllocator
    {
        private readonly Dictionary<ushort, ushort> _runtimeToOnDisk = new();
        private readonly List<(ushort runtimeId, ushort onDiskId)> _emissionOrder = new();
        private ushort _nextOnDiskId = 1;  // 0 reserved for "no type"

        public ushort AllocateOrLookup(ushort runtimeTypeId)
        {
            if (runtimeTypeId == 0) return 0;
            if (_runtimeToOnDisk.TryGetValue(runtimeTypeId, out ushort onDisk)) return onDisk;
            if (_nextOnDiskId == 0)
            {
                throw new InvalidOperationException(
                    "DatumFileWriterV2 has emitted more than 65,535 distinct struct shapes " +
                    "into one file. The on-disk TypeId field is 16 bits; exceeding that limit " +
                    "is far beyond any realistic schema cardinality and indicates a runaway loop.");
            }
            onDisk = _nextOnDiskId++;
            _runtimeToOnDisk[runtimeTypeId] = onDisk;
            _emissionOrder.Add((runtimeTypeId, onDisk));
            return onDisk;
        }

        /// <summary>
        /// Records a runtime → on-disk mapping for a shape that ALREADY has
        /// a TypeTable entry on disk (append mode). Unlike
        /// <see cref="AllocateOrLookup"/>, seeded mappings don't join the
        /// emission order — their entries carry forward verbatim at
        /// finalize rather than re-serializing. First seed wins if the
        /// existing table happens to carry duplicate shapes.
        /// </summary>
        public void SeedExisting(ushort runtimeTypeId, ushort onDiskTypeId)
        {
            if (runtimeTypeId == 0 || onDiskTypeId == 0) return;
            _runtimeToOnDisk.TryAdd(runtimeTypeId, onDiskTypeId);
            EnsureNextOnDiskIdAtLeast((ushort)(onDiskTypeId + 1));
        }

        /// <summary>
        /// Raises the next-id floor so fresh allocations never collide with
        /// on-disk ids carried forward from the existing file's TypeTable.
        /// </summary>
        public void EnsureNextOnDiskIdAtLeast(ushort floor)
        {
            if (floor > _nextOnDiskId) _nextOnDiskId = floor;
        }

        /// <summary>
        /// Returns the recorded mappings in allocation order. The list is
        /// owned by the allocator; callers must not mutate it.
        /// </summary>
        public IReadOnlyList<(ushort runtimeId, ushort onDiskId)> Snapshot() => _emissionOrder;
    }
}
