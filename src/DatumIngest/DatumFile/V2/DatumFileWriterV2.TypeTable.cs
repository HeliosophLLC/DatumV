using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

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
    /// Sets the per-query <see cref="TypeRegistry"/> the writer uses to
    /// resolve descriptors at finalize. Must be called before the first
    /// <see cref="WriteRowBatch"/> for the file to receive a TypeTable.
    /// Callers that omit this stay on the legacy untyped path.
    /// </summary>
    public void SetTypeRegistry(TypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _typeRegistry = registry;
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
        if (_typeRegistry is null) return null;
        if (_columnStructTypeIds is null) return null;
        ushort? runtimeId = _columnStructTypeIds[columnIndex];
        if (runtimeId is null) return null;
        return _allocator.AllocateOrLookup(runtimeId.Value);
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
        if (_typeRegistry is null) return Array.Empty<TypeTableEntryV5>();
        IReadOnlyList<(ushort runtimeId, ushort onDiskId)> mappings = _allocator.Snapshot();
        if (mappings.Count == 0) return Array.Empty<TypeTableEntryV5>();
        if (_sidecar is null)
        {
            throw new InvalidOperationException(
                "DatumFileWriterV2 produced struct values but has no sidecar to hold their " +
                "TypeDescriptor blobs. Open the writer with a sidecar path or skip the registry.");
        }

        TypeTableEntryV5[] entries = new TypeTableEntryV5[mappings.Count];
        for (int i = 0; i < mappings.Count; i++)
        {
            (ushort runtimeId, ushort onDiskId) = mappings[i];
            TypeDescriptor? descriptor = _typeRegistry.GetDescriptor(runtimeId);
            if (descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Allocator recorded runtime TypeId {runtimeId} but the TypeRegistry " +
                    "has no descriptor for it. Every emitted TypeId must be live in the " +
                    "registry at finalize time.");
            }
            byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(descriptor, _typeRegistry);
            (long offset, long length) = _sidecar.Append(blob);
            entries[i] = new TypeTableEntryV5(onDiskId, offset, checked((int)length));
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
        /// Returns the recorded mappings in allocation order. The list is
        /// owned by the allocator; callers must not mutate it.
        /// </summary>
        public IReadOnlyList<(ushort runtimeId, ushort onDiskId)> Snapshot() => _emissionOrder;
    }
}
