using System.Runtime.InteropServices;
using Heliosoph.DatumV.DatumFile.Sidecar;

namespace Heliosoph.DatumV.Model;

public readonly partial struct DataValue
{

    /// <summary>
    /// Creates an <c>Array&lt;Struct&gt;</c> value. Each element is a struct's
    /// <see cref="DataValue"/>[] of fields; each is stored via
    /// <see cref="IValueStore.StoreDataValues"/> and referenced from a slot. For
    /// N≥2 a slot block is also written. Field count per struct is implicit in
    /// the stored field-array length — the slot itself carries no field-count
    /// metadata, so heterogeneous-field-count elements are allowed at the value
    /// layer (the schema layer enforces uniformity when configured).
    /// </summary>
    public static DataValue FromStructArray(ReadOnlySpan<DataValue[]> elements, IValueStore store, ushort typeId)
    {
        // typeId is the *element struct's* TypeId — written into each slot's
        // reserved bytes so every element is self-describing on read. The array
        // container itself doesn't carry a TypeId; `_charCount` here only
        // distinguishes the inline N=0 / N=1 cases.
        if (elements.Length == 0)
        {
            return new(
                DataKind.Struct,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            var (elementP0, elementP1) = store.StoreDataValues(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0.Value, elementP1.Value, typeId);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                DataKind.Struct,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            var (elementP0, elementP1) = store.StoreDataValues(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value,
                typeId);
        }
        var (blockP0, blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.Struct,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            offset: blockP0.Value,
            length: blockP1.Value,
            charCount: 0);
    }

    /// <summary>
    /// Creates an <c>Array&lt;Struct&gt;</c> value <em>without</em> a registered TypeId.
    /// Explicit escape hatch for sites that don't have a <see cref="TypeRegistry"/> in
    /// scope. See <see cref="FromUntypedStruct"/> for the rationale.
    /// </summary>
    public static DataValue FromUntypedStructArray(ReadOnlySpan<DataValue[]> elements, IValueStore store) =>
        FromStructArray(elements, store, typeId: TypeRegistry.NoType);

    /// <summary>
    /// Creates an arena-backed multi-dimensional <c>Array&lt;Struct&gt;</c> value.
    /// Each element's fields are written to <paramref name="store"/>; a slot block
    /// of <c>elements.Length × 16 bytes</c> is prepended with an <c>int32 × ndim</c>
    /// shape prefix and stored as a single contiguous block. The per-element
    /// <paramref name="typeId"/> rides in each slot's reserved bytes (13-14) so
    /// elements stay self-describing on read; the array container itself carries
    /// no TypeId. <see cref="AsStructArray"/> transparently skips the shape prefix.
    /// </summary>
    public static DataValue FromArenaMultiDimStructArray(
        ReadOnlySpan<DataValue[]> elements,
        ReadOnlySpan<int> shape,
        IValueStore store,
        ushort typeId)
    {
        if (shape.Length < 2 || shape.Length > 255)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shape), shape.Length,
                "Multi-dim ndim must be in [2, 255].");
        }
        long product = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            int dim = shape[i];
            if (dim <= 0)
            {
                throw new ArgumentException(
                    $"Shape dimension {i} must be positive; got {dim}.", nameof(shape));
            }
            product *= dim;
            if (product > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shape), product,
                    "Product of shape dimensions overflows Int32.");
            }
        }
        if (product != elements.Length)
        {
            throw new ArgumentException(
                $"Product of shape dimensions ({product}) does not equal element count ({elements.Length}).",
                nameof(shape));
        }

        int shapeBytes = shape.Length * sizeof(int);
        int slotBlockBytes = elements.Length * ArraySlot.SizeBytes;
        byte[] buffer = new byte[shapeBytes + slotBlockBytes];
        MemoryMarshal.AsBytes(shape).CopyTo(buffer);

        for (int i = 0; i < elements.Length; i++)
        {
            DataValue[]? fields = elements[i];
            if (fields is null)
            {
                throw new ArgumentException(
                    $"Element {i} is null. Array<Struct> elements must be non-null; " +
                    "use a typed null DataValue for SQL NULL semantics at the column level.",
                    nameof(elements));
            }
            var (elementP0, elementP1) = store.StoreDataValues(fields);
            ArraySlot.Write(
                buffer.AsSpan(shapeBytes + i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value,
                typeId);
        }

        var (blockP0, blockP1) = store.StoreBytes(buffer);
        ushort cc = (ushort)(shape.Length << 8);
        return new(
            DataKind.Struct,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray | DataValueFlags.IsMultiDim,
            offset: blockP0.Value, length: blockP1.Value, charCount: cc);
    }

    /// <summary>
    /// Polymorphic typed-array factory. Accepts a span of element <see cref="DataValue"/>s
    /// and dispatches to the appropriate per-kind factory based on
    /// <paramref name="elementKind"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="DataKind.String"/> → <see cref="FromStringArray"/></description></item>
    ///   <item><description><see cref="DataKind.Image"/> → <see cref="FromImageArray"/></description></item>
    ///   <item><description><see cref="DataKind.Struct"/> → <see cref="FromStructArray"/></description></item>
    ///   <item><description>Fixed-width primitives → <see cref="FromArenaArrayBytes"/> (or inline when ≤16 bytes)</description></item>
    /// </list>
    /// Used by aggregate functions (<c>ARRAY_AGG</c>) and by any caller that
    /// has accumulated <see cref="DataValue"/> elements row-by-row and wants
    /// to materialise them as a typed array. The resulting value has
    /// <see cref="Kind"/> = <paramref name="elementKind"/> and
    /// <see cref="DataValueFlags.IsArray"/> set.
    /// </summary>
    /// <param name="elementKind">Per-element kind. All elements must match.</param>
    /// <param name="elements">
    /// Row-by-row accumulated values. For reference kinds (String/Image/Struct)
    /// elements may be inline-or-arena-backed in <paramref name="source"/>;
    /// payload bytes are re-stored into <paramref name="target"/>. For fixed-
    /// width primitives elements are read inline from each <see cref="DataValue"/>'s
    /// payload via <see cref="CopyInlineScalarBytes"/>.
    /// </param>
    /// <param name="source">
    /// Resolves arena-backed input element payloads. With one-arena-per-query,
    /// this is typically the same store as <paramref name="target"/>.
    /// </param>
    /// <param name="target">Where the resulting array's payload bytes land.</param>
    /// <param name="registry">Resolves sidecar-backed input element payloads.</param>
    /// <exception cref="NotSupportedException">
    /// <paramref name="elementKind"/> has no per-element representation.
    /// </exception>
    /// <exception cref="InvalidOperationException">An element is null (per-element nulls inside arrays not yet supported).</exception>
    public static DataValue FromTypedArray(
        DataKind elementKind,
        ReadOnlySpan<DataValue> elements,
        IValueStore source,
        IValueStore target,
        SidecarRegistry? registry = null)
    {
        return elementKind switch
        {
            DataKind.String => BuildStringArrayFromDataValues(elements, source, target, registry),
            DataKind.Image => BuildImageArrayFromDataValues(elements, source, target, registry),
            DataKind.Struct => BuildStructArrayFromDataValues(elements, source, target, registry),
            _ => BuildFixedWidthArrayFromDataValues(elementKind, elements, target),
        };
    }

    private static DataValue BuildStringArrayFromDataValues(
        ReadOnlySpan<DataValue> elements, IValueStore source, IValueStore target, SidecarRegistry? registry)
    {
        string[] strings = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullArrayElement(elements[i], i, DataKind.String);
            strings[i] = elements[i].AsString(source, registry);
        }
        return FromStringArray(strings, target);
    }

    private static DataValue BuildImageArrayFromDataValues(
        ReadOnlySpan<DataValue> elements, IValueStore source, IValueStore target, SidecarRegistry? registry)
    {
        byte[][] images = new byte[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullArrayElement(elements[i], i, DataKind.Image);
            images[i] = elements[i].AsByteSpan(source, registry).ToArray();
        }
        return FromImageArray(images, target);
    }

    private static DataValue BuildStructArrayFromDataValues(
        ReadOnlySpan<DataValue> elements, IValueStore source, IValueStore target, SidecarRegistry? registry)
    {
        _ = registry;
        DataValue[][] rows = new DataValue[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullArrayElement(elements[i], i, DataKind.Struct);
            rows[i] = elements[i].AsStruct(source);
        }
        // Aggregate-side construction (ARRAY_AGG of struct) doesn't have a
        // TypeRegistry in scope to resolve the element shape's TypeId — left
        // as untyped until the aggregate path is threaded through. Existing
        // ARRAY_AGG callers expected this behaviour.
        return FromUntypedStructArray(rows, target);
    }

    private static DataValue BuildFixedWidthArrayFromDataValues(
        DataKind elementKind, ReadOnlySpan<DataValue> elements, IValueStore target)
    {
        int elementSize = ScalarByteSize(elementKind);
        int totalBytes = elements.Length * elementSize;

        if (totalBytes == 0)
        {
            return FromInlineArrayBytes(ReadOnlySpan<byte>.Empty, elementKind);
        }

        byte[] buffer = new byte[totalBytes];
        Span<byte> span = buffer;
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullArrayElement(elements[i], i, elementKind);
            if (elements[i]._kind != elementKind)
            {
                throw new InvalidOperationException(
                    $"Array element [{i}] has kind {elements[i]._kind}; expected {elementKind}.");
            }
            elements[i].CopyInlineScalarBytes(span.Slice(i * elementSize, elementSize));
        }

        if (totalBytes <= InlineArrayMaxBytes)
        {
            return FromInlineArrayBytes(span, elementKind);
        }
        return FromArenaArrayBytes(span, elementKind, target);
    }

    private static void ThrowIfNullArrayElement(DataValue element, int index, DataKind elementKind)
    {
        if (element.IsNull)
        {
            throw new InvalidOperationException(
                $"Array<{elementKind}> element [{index}] is null; per-element nulls inside an array are "
                + "not yet supported. Filter nulls before aggregating, or wrap into Struct{value, is_null}.");
        }
    }

    /// <summary>
    /// Reads an <c>Array&lt;Struct&gt;</c> value as a flat array of
    /// self-describing <see cref="DataValue"/> elements. Each returned element is
    /// a <see cref="DataKind.Struct"/> <see cref="DataValue"/> carrying its own
    /// TypeId stamped at construction time; call <see cref="AsStruct(IValueStore)"/> on it to
    /// access the struct's fields. The per-element TypeId rides in the slot's
    /// reserved bytes — see <see cref="ArraySlot"/>.
    /// </summary>
    /// <remarks>
    /// The previous shape was <c>DataValue[][]</c> (outer = element, inner =
    /// fields). That representation couldn't carry a per-row TypeId, so
    /// <c>typeof(arr[0])</c> and field-name rendering on indexed elements lost
    /// shape information. The current shape preserves it: every element is a
    /// real Struct DataValue with its own <see cref="TypeId"/>.
    /// </remarks>
    public DataValue[] AsStructArray(
        IValueStore store,
        SidecarRegistry? registry = null,
        TypeIdTranslationTable? typeIdTranslations = null)
    {
        ThrowIfNotReferenceArray(DataKind.Struct);

        // Multi-dim values prepend an [int32 × ndim] shape prefix to the slot
        // block on both arena and sidecar tiers; element reads skip it
        // transparently.
        int shapePrefix = ShapePrefixByteCount;

        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry)[shapePrefix..];
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            DataValue[] result = new DataValue[elementCount];
            byte storeId = SidecarStoreId;
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out ushort elementTypeId,
                    out _);
                ReadOnlySpan<byte> structBytes = src.Read(elementOffset, elementLength);
                DataValue[] fields = DeserializeStructFields(structBytes, store);
                // Translate the slot's on-disk TypeId to the runtime registry id
                // for this query. When no translator is registered (in-memory
                // values, pre-v5 files) the id passes through unchanged. The
                // sidecar's struct bytes are wire-format; re-store the field
                // array into the in-memory arena so the synthesised Struct
                // DataValue has the standard arena-backed layout.
                ushort runtimeTypeId = typeIdTranslations is null
                    ? elementTypeId
                    : typeIdTranslations.Translate(storeId, elementTypeId);
                result[i] = FromStruct(fields, store, runtimeTypeId);
            }
            return result;
        }

        // N = 0 / N = 1 inline path. Multi-dim is unreachable here (min shape
        // product 2×2 = 4 slots × 16 bytes exceeds the 16-byte inline payload).
        if (IsInline)
        {
            if (_charCount == 0) return [];

            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(
                slotBytes,
                out long elementOffset,
                out long elementLength,
                out ushort elementTypeId,
                out _);
            return [SynthesiseArenaStruct(elementOffset, elementLength, elementTypeId)];
        }

        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength))[shapePrefix..];
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        DataValue[] arenaResult = new DataValue[n];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out ushort elementTypeId,
                out _);
            arenaResult[i] = SynthesiseArenaStruct(elementOffset, elementLength, elementTypeId);
        }
        return arenaResult;
    }

    /// <summary>
    /// Builds a <see cref="DataKind.Struct"/> <see cref="DataValue"/> that points
    /// at an already-stored field array at <c>(offset, count)</c> in some arena,
    /// stamped with <paramref name="typeId"/>. No fresh arena writes — used by
    /// <see cref="AsStructArray"/> to materialise per-element struct values from
    /// their slot data.
    /// </summary>
    private static DataValue SynthesiseArenaStruct(long fieldArrayOffset, long fieldCount, ushort typeId) =>
        new(DataKind.Struct, DataValueFlags.InArena, typeId: typeId, offset: fieldArrayOffset, length: fieldCount);

    /// <summary>
    /// Deserialises a single struct's field bytes (uint16 fieldCount + N
    /// self-describing records) — the inverse of the encoder's
    /// <c>SerializeStructFieldArray</c>. Reference-typed fields are written
    /// into <paramref name="store"/> as part of deserialisation.
    /// </summary>
    private static DataValue[] DeserializeStructFields(ReadOnlySpan<byte> bytes, IValueStore store)
    {
        byte[] copy = bytes.ToArray();
        using MemoryStream ms = new(copy, writable: false);
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        ushort fieldCount = br.ReadUInt16();
        DataValue[] fields = new DataValue[fieldCount];
        for (int j = 0; j < fieldCount; j++)
        {
            fields[j] = IO.DataValueReader.ReadDataValue(br, store);
        }
        return fields;
    }

    /// <param name="fields">Positional field values for the struct.</param>
    /// <param name="store">Value store that will hold the field array.</param>
    /// <param name="typeId">
    /// Required type-id from the query's <see cref="TypeRegistry"/>. The 0 sentinel
    /// (<see cref="TypeRegistry.NoType"/>) is allowed but should be explicit — prefer
    /// <see cref="FromUntypedStruct"/> for callers that genuinely have no shape to record,
    /// so the absence is searchable in code review.
    /// </param>
    public static DataValue FromStruct(DataValue[] fields, IValueStore store, ushort typeId)
    {
        var (p0, count) = store.StoreDataValues(fields);
        return new(DataKind.Struct, DataValueFlags.InArena, typeId: typeId, offset: p0.Value, length: count.Value);
    }

    /// <summary>
    /// Creates a struct value <em>without</em> a registered TypeId. Explicit escape
    /// hatch for sites that don't (yet) have a <see cref="TypeRegistry"/> in scope —
    /// test fixtures and low-level decoders that haven't been threaded through the
    /// registry. Production code should prefer <see cref="FromStruct"/> with a real
    /// TypeId so <c>typeof()</c> and field-name resolution keep working.
    /// </summary>
    public static DataValue FromUntypedStruct(DataValue[] fields, IValueStore store) =>
        FromStruct(fields, store, typeId: TypeRegistry.NoType);

    /// <summary>Creates a typed null struct that carries a <see cref="TypeRegistry"/> id.</summary>
    /// <param name="typeId">
    /// Required type-id; 0 is permitted but discouraged — use <see cref="NullUntypedStruct"/>
    /// when the call site genuinely has no shape (e.g. early null-propagation before any
    /// type has been resolved).
    /// </param>
    public static DataValue NullStruct(ushort typeId) =>
        new(DataKind.Struct, DataValueFlags.IsNull, typeId, p0: 0, p1: 0);

    /// <summary>
    /// Typed null struct with no registered TypeId. Explicit escape hatch — see
    /// <see cref="FromUntypedStruct"/>.
    /// </summary>
    public static DataValue NullUntypedStruct() => NullStruct(TypeRegistry.NoType);


    /// <summary>Returns the positional field-value array from an explicit <see cref="IValueStore"/>.</summary>
    public DataValue[] AsStruct(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Struct);
        return store.RetrieveDataValues(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }
}
