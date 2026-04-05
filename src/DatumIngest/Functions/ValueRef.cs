using System.Numerics;

using DatumIngest.Functions.Audio;
using DatumIngest.Functions.Image;
using DatumIngest.Functions.Video;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using SkiaSharp;

namespace DatumIngest.Functions;

/// <summary>
/// Function-internal value type. Replaces store-aware <see cref="DataValue"/>
/// passing through scalar function bodies: a <see cref="ValueRef"/> either
/// carries an inline payload (small primitives, short strings, type tags)
/// or a managed reference (long strings, byte arrays, etc.). Lifetime is
/// GC-managed; functions never touch arenas, never thread an
/// <c>IValueStore</c>, never call <c>Stabilize</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExpressionEvaluator</c> converts inputs at the function
/// boundary: arena/sidecar-backed <see cref="DataValue"/>s are resolved
/// into managed payloads before <see cref="IScalarFunction.ExecuteAsync"/> is
/// called. The function then operates entirely on managed memory and
/// returns a <see cref="ValueRef"/>; the evaluator converts back to a
/// <see cref="DataValue"/> (writing managed payloads to the query arena
/// or inlining short strings) before handing the row out.
/// </para>
/// <para>
/// State invariants:
/// <list type="bullet">
///   <item><description>
///     <see cref="IsNull"/>: <c>_inline.IsNull == true</c> and
///     <c>Materialized == null</c>.
///   </description></item>
///   <item><description>
///     Inline non-null: <c>_inline</c> carries everything,
///     <c>Materialized == null</c>.
///   </description></item>
///   <item><description>
///     Materialized non-null: <c>_inline</c> is a typed null tag holding
///     <see cref="Kind"/> + <see cref="IsArray"/> only;
///     <c>Materialized</c> holds the real payload (e.g. a <see cref="string"/>
///     for <see cref="DataKind.String"/>, a <see cref="byte"/>[] for
///     byte arrays — <see cref="DataKind.UInt8"/> with the IsArray flag —
///     and <see cref="DataKind.Image"/>).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public readonly struct ValueRef
{
    private readonly DataValue _inline;
    private readonly object? _materialized;
    /// <summary>
    /// Per-dim shape for multi-dim primitive arrays. <see langword="null"/>
    /// for flat arrays / non-array values. When non-null, <c>_materialized</c>
    /// is still the typed <see cref="Array"/> (e.g. <c>float[]</c>), so
    /// existing <c>_materialized as float[]</c> consumers continue to see
    /// the flat element span — the shape is an additional, optional
    /// attachment that <see cref="ToDataValue"/> uses to construct a
    /// multi-dim <see cref="DataValue"/>.
    /// </summary>
    private readonly int[]? _shape;

    private ValueRef(DataValue inline, object? materialized)
    {
        _inline = inline;
        _materialized = materialized;
        _shape = null;
    }

    private ValueRef(DataValue inline, object? materialized, int[]? shape)
    {
        _inline = inline;
        _materialized = materialized;
        _shape = shape;
    }

    /// <summary>The kind of the value carried by this reference.</summary>
    public DataKind Kind => _inline.Kind;

    /// <summary>True when the value is the typed null for its kind.</summary>
    public bool IsNull => _materialized is null && _inline.IsNull;

    /// <summary>True when the value carries the IsArray flag.</summary>
    public bool IsArray => _inline.IsArray;

    /// <summary>
    /// Per-query <see cref="TypeRegistry"/> id of the underlying value's shape.
    /// Returns 0 when no type has been registered (scalar primitives, untyped
    /// in-flight intermediates). Stamped on materialisation by
    /// <see cref="ToDataValue(IValueStore, ushort, TypeRegistry?)"/> when the
    /// caller passes a registry.
    /// </summary>
    public ushort TypeId => _inline.TypeId;

    /// <summary>True when the value is a byte array (Kind=UInt8 + IsArray).</summary>
    public bool IsByteArrayKind => _inline.IsByteArrayKind;

    /// <summary>
    /// True when the value is a typed array carrying an explicit multi-dimensional
    /// shape — either as a scan-output ValueRef whose inline DataValue's
    /// <see cref="DataValue.IsMultiDim"/> is set, or as a function-output ValueRef
    /// carrying a managed shape attachment alongside its flat element payload.
    /// </summary>
    public bool IsMultiDim => _inline.IsMultiDim || _shape is not null;

    /// <summary>
    /// Number of dimensions for a multi-dim array value (≥2); <c>0</c> when
    /// <see cref="IsMultiDim"/> is <c>false</c>.
    /// </summary>
    public int Ndim => _shape?.Length ?? _inline.Ndim;

    /// <summary>
    /// The inline carrier. Only meaningful when <see cref="IsNull"/> is
    /// <c>false</c> and <see cref="Materialized"/> is <c>null</c>; in other
    /// states callers should use <see cref="Materialized"/> or
    /// <see cref="IsNull"/> instead.
    /// </summary>
    public DataValue InlineDataValue => _inline;

    /// <summary>
    /// The managed payload, populated for non-inline kinds (e.g. long
    /// strings, byte arrays). <c>null</c> for inline-or-null values.
    /// </summary>
    public object? Materialized => _materialized;

    /// <summary>
    /// Creates the typed null for the given <paramref name="kind"/>.
    /// </summary>
    public static ValueRef Null(DataKind kind) =>
        new(DataValue.Null(kind), null);

    /// <summary>
    /// Wraps a <see cref="DataValue"/> that is already inline-or-null. Use
    /// this when you have a <see cref="DataValue"/> in hand and know it
    /// doesn't reference an arena — e.g. the result of <see cref="DataValue.FromInt32"/>.
    /// Throws when the value is non-inline non-null because the caller
    /// would have to provide a managed payload to materialize.
    /// </summary>
    public static ValueRef FromInline(DataValue value)
    {
        if (!value.IsNull && !value.IsInline)
        {
            throw new ArgumentException(
                $"FromInline requires an inline or null DataValue; got {value.Kind} with non-inline storage. "
                + "Use the FromString / FromBytes / etc. helpers to wrap a materialized payload instead.",
                nameof(value));
        }
        return new(value, null);
    }

    /// <summary>
    /// Constructs a multi-dim typed-array <see cref="ValueRef"/> whose elements
    /// and shape live in managed memory. The multi-dim <see cref="DataValue"/>
    /// gets materialised into the target arena at <see cref="ToDataValue"/>
    /// time — the canonical shape for function outputs (e.g.
    /// <c>infer()</c>'s rank ≥ 2 ONNX tensors) so the resulting DataValue
    /// outlives the source arena's lifetime.
    /// </summary>
    /// <typeparam name="T">Unmanaged element type matching <paramref name="elementKind"/>.</typeparam>
    /// <param name="elements">Flat row-major element data; length must equal <c>product(shape)</c>.</param>
    /// <param name="shape">Per-dim sizes (ndim ≥ 2; all positive).</param>
    /// <param name="elementKind">Element kind; must not be byte / reference / blob.</param>
    public static ValueRef FromPrimitiveMultiDimArray<T>(T[] elements, int[] shape, DataKind elementKind)
        where T : unmanaged
    {
        if (shape.Length < 2)
        {
            throw new ArgumentException(
                "Multi-dim ValueRef requires ndim >= 2. Use FromPrimitiveArray for 1-D.",
                nameof(shape));
        }
        long product = 1;
        for (int i = 0; i < shape.Length; i++) product *= shape[i];
        if (product != elements.Length)
        {
            throw new ArgumentException(
                $"Product of shape ({product}) does not equal elements.Length ({elements.Length}).",
                nameof(shape));
        }

        // Inline + _materialized mirror the flat FromPrimitiveArray shape so
        // every existing `_materialized as T[]` / GetArrayLength / etc. reader
        // continues to see the elements as a 1-D array. The optional _shape
        // attachment is consulted only by multi-dim-aware paths (ToDataValue,
        // IsMultiDim/Ndim accessors, GetShape).
        ValueRef typed = FromPrimitiveArray(elements, elementKind);
        return new(typed._inline, typed._materialized, shape);
    }

    /// <summary>Boolean inline value.</summary>
    public static ValueRef FromBoolean(bool value) =>
        new(DataValue.FromBoolean(value), null);

    /// <summary>UInt8 inline value.</summary>
    public static ValueRef FromUInt8(byte value) =>
        new(DataValue.FromUInt8(value), null);

    /// <summary>Int8 inline value.</summary>
    public static ValueRef FromInt8(sbyte value) =>
        new(DataValue.FromInt8(value), null);

    /// <summary>Int16 inline value.</summary>
    public static ValueRef FromInt16(short value) =>
        new(DataValue.FromInt16(value), null);

    /// <summary>UInt16 inline value.</summary>
    public static ValueRef FromUInt16(ushort value) =>
        new(DataValue.FromUInt16(value), null);

    /// <summary>Int32 inline value.</summary>
    public static ValueRef FromInt32(int value) =>
        new(DataValue.FromInt32(value), null);

    /// <summary>UInt32 inline value.</summary>
    public static ValueRef FromUInt32(uint value) =>
        new(DataValue.FromUInt32(value), null);

    /// <summary>Int64 inline value.</summary>
    public static ValueRef FromInt64(long value) =>
        new(DataValue.FromInt64(value), null);

    /// <summary>UInt64 inline value.</summary>
    public static ValueRef FromUInt64(ulong value) =>
        new(DataValue.FromUInt64(value), null);

    /// <summary>Float16 inline value.</summary>
    public static ValueRef FromFloat16(Half value) =>
        new(DataValue.FromFloat16(value), null);

    /// <summary>Float32 inline value.</summary>
    public static ValueRef FromFloat32(float value) =>
        new(DataValue.FromFloat32(value), null);

    /// <summary>Float64 inline value.</summary>
    public static ValueRef FromFloat64(double value) =>
        new(DataValue.FromFloat64(value), null);

    /// <summary>Decimal inline value.</summary>
    public static ValueRef FromDecimal(decimal value) =>
        new(DataValue.FromDecimal(value), null);

    /// <summary>Int128 inline value.</summary>
    public static ValueRef FromInt128(Int128 value) =>
        new(DataValue.FromInt128(value), null);

    /// <summary>UInt128 inline value.</summary>
    public static ValueRef FromUInt128(UInt128 value) =>
        new(DataValue.FromUInt128(value), null);

    /// <summary>Date inline value.</summary>
    public static ValueRef FromDate(DateOnly value) =>
        new(DataValue.FromDate(value), null);

    /// <summary>TimestampTz inline value (PG <c>timestamptz</c>; UTC ticks, input offset discarded).</summary>
    public static ValueRef FromTimestampTz(DateTimeOffset value) =>
        new(DataValue.FromTimestampTz(value), null);

    /// <summary>Timestamp inline value (PG <c>timestamp</c>; naive ticks, no tz).</summary>
    public static ValueRef FromTimestamp(DateTime value) =>
        new(DataValue.FromTimestamp(value), null);

    /// <summary>Time inline value.</summary>
    public static ValueRef FromTime(TimeOnly value) =>
        new(DataValue.FromTime(value), null);

    /// <summary>Duration inline value.</summary>
    public static ValueRef FromDuration(TimeSpan value) =>
        new(DataValue.FromDuration(value), null);

    /// <summary>UUID inline value.</summary>
    public static ValueRef FromUuid(Guid value) =>
        new(DataValue.FromUuid(value), null);

    /// <summary>2D point inline value (8 bytes — two Float32 components).</summary>
    public static ValueRef FromPoint2D(System.Numerics.Vector2 value) =>
        new(DataValue.FromPoint2D(value), null);

    /// <summary>2D point inline value from explicit X / Y components.</summary>
    public static ValueRef FromPoint2D(float x, float y) =>
        new(DataValue.FromPoint2D(x, y), null);

    /// <summary>3D point inline value (12 bytes — three Float32 components).</summary>
    public static ValueRef FromPoint3D(System.Numerics.Vector3 value) =>
        new(DataValue.FromPoint3D(value), null);

    /// <summary>3D point inline value from explicit X / Y / Z components.</summary>
    public static ValueRef FromPoint3D(float x, float y, float z) =>
        new(DataValue.FromPoint3D(x, y, z), null);

    /// <summary>
    /// 32-bit RGBA colour inline value. Components are individual bytes
    /// in <c>[0, 255]</c>; alpha defaults to 255 (fully opaque) when not
    /// supplied.
    /// </summary>
    public static ValueRef FromColor(byte r, byte g, byte b, byte a = 255) =>
        new(DataValue.FromColor(r, g, b, a), null);

    /// <summary>
    /// Reads an RGBA colour value as <c>(r, g, b, a)</c> bytes. Throws when
    /// <see cref="Kind"/> is not <see cref="DataKind.Color"/>.
    /// </summary>
    public (byte R, byte G, byte B, byte A) AsColor() => _inline.AsColor();

    /// <summary>
    /// DataKind tag (the value of <c>typeof(x)</c>). When <paramref name="typeId"/>
    /// is non-zero, the tag carries a <see cref="TypeRegistry"/> id describing the
    /// rich shape (struct field names, nested array element types). For primitive
    /// arrays (no descriptor), <paramref name="describesArray"/> /
    /// <paramref name="describesMultiDim"/> let <c>FormatType</c> render
    /// <c>Array&lt;...&gt;</c> from the annotation.
    /// </summary>
    public static ValueRef FromType(DataKind value, ushort typeId = 0, bool describesArray = false, bool describesMultiDim = false) =>
        new(DataValue.FromType(value, typeId, describesArray, describesMultiDim), null);

    /// <summary>
    /// String value carried as a managed payload. The boundary conversion
    /// chooses whether to inline (UTF-8 ≤16 bytes) or write to the query
    /// arena when converting back to <see cref="DataValue"/>.
    /// </summary>
    public static ValueRef FromString(string value) =>
        new(DataValue.Null(DataKind.String), value);

    /// <summary>
    /// Byte-array payload. Pass <see cref="DataKind.Image"/>, <see cref="DataKind.Audio"/>,
    /// <see cref="DataKind.Video"/>, <see cref="DataKind.Json"/>, <see cref="DataKind.PointCloud"/>,
    /// or <see cref="DataKind.Mesh"/> for the corresponding encoded-blob kinds, or
    /// <see cref="DataKind.UInt8"/> with <paramref name="isArray"/> set to <c>true</c> for
    /// generic byte arrays. The DataValue tag carries the kind (and IsArray flag for byte
    /// arrays); the actual bytes live in <see cref="Materialized"/>.
    /// </summary>
    public static ValueRef FromBytes(DataKind kind, byte[] value, bool isArray = false)
    {
        if (kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json or DataKind.PointCloud or DataKind.Mesh)
        {
            return new(DataValue.Null(kind), value);
        }
        if (kind == DataKind.UInt8 && isArray)
        {
            return new(DataValue.NullByteArray(), value);
        }
        throw new ArgumentException(
            $"FromBytes is only valid for Image/Audio/Video/Json/PointCloud/Mesh or (UInt8 with IsArray=true); got {kind}, isArray={isArray}.",
            nameof(kind));
    }

    /// <summary>
    /// Decoded bitmap value. Produced by models and image functions that construct
    /// the output bitmap directly, avoiding an intermediate encode/decode round-trip.
    /// The bitmap is GC-managed; arena encoding (PNG, lossless) happens once at
    /// <see cref="ToDataValue"/>. Disposal in V1 relies on SkiaSharp's finalizer.
    /// </summary>
    public static ValueRef FromImage(SKBitmap bitmap) =>
        new(DataValue.Null(DataKind.Image), bitmap);

    /// <summary>
    /// Pre-encoded Image bytes (PNG/JPEG/WebP). Used by MIO's parallel
    /// scatter pre-encode pass: an <see cref="SKBitmap"/>-backed
    /// <see cref="ValueRef"/> is encoded to bytes on a worker thread, then
    /// rewrapped via this factory so the sequential
    /// <see cref="ToDataValue"/> path falls into the cheap byte[]
    /// arena-copy branch instead of paying the per-row PNG encode there.
    /// </summary>
    public static ValueRef FromImage(byte[] encodedBytes) =>
        new(DataValue.Null(DataKind.Image), encodedBytes);

    /// <summary>
    /// PointCloud value carried as a raw byte blob (40-byte header + interleaved
    /// per-point payload — see <see cref="DatumIngest.Model.Spatial.PointCloudHeader"/>).
    /// Producers build the full blob in managed memory; arena copy happens once at
    /// <see cref="ToDataValue"/>. No decoded form — PointCloud has no SKBitmap-equivalent
    /// because its on-wire layout is already the renderable form.
    /// </summary>
    public static ValueRef FromPointCloud(byte[] blob) =>
        new(DataValue.Null(DataKind.PointCloud), blob);

    /// <summary>
    /// Mesh value carried as a raw byte blob (48-byte header + interleaved per-vertex
    /// payload + triangle indices + optional embedded texture — see
    /// <see cref="DatumIngest.Model.Spatial.MeshHeader"/>). Producers build the full
    /// blob in managed memory; arena copy happens once at <see cref="ToDataValue"/>.
    /// </summary>
    public static ValueRef FromMesh(byte[] blob) =>
        new(DataValue.Null(DataKind.Mesh), blob);

    /// <summary>
    /// First-class lambda value: the AST body plus a snapshot of the row
    /// context at construction time. Lambdas are <em>row-scoped</em> — they
    /// flow as <see cref="ValueRef"/>s through function arguments, struct
    /// fields, and array elements but cannot be persisted; <see cref="ToDataValue"/>
    /// throws when the payload is a <see cref="LambdaValue"/>.
    /// </summary>
    public static ValueRef FromLambda(LambdaValue lambda) =>
        new(DataValue.Null(DataKind.Lambda), lambda
            ?? throw new ArgumentNullException(nameof(lambda)));

    /// <summary>
    /// First-class procedural-drawing value: a tree of geometric / text /
    /// image / group / transformed nodes consumed by the universal
    /// rasterizer (<c>render(drawing, size)</c>). Drawings are
    /// <em>row-scoped</em> in the current implementation —
    /// <see cref="ToDataValue"/> throws when the payload is a
    /// <c>DrawingPayload</c>. Persistence support is plausible (the
    /// payload tree is small + embeddable) but isn't needed for the
    /// animation / static-render workflows that motivate the kind.
    /// </summary>
    public static ValueRef FromDrawing(Scalar.Drawing.DrawingPayload drawing) =>
        new(DataValue.Null(DataKind.Drawing), drawing
            ?? throw new ArgumentNullException(nameof(drawing)));

    /// <summary>
    /// JSON payload as a byte slice over canonical CBOR bytes — used by
    /// <c>json_query</c> to return a subdocument view without copying. The
    /// segment's <c>(Array, Offset, Count)</c> identifies a window into a
    /// larger CBOR buffer; downstream <see cref="ToDataValue"/> copies only
    /// the slice's bytes into the target arena.
    /// </summary>
    /// <remarks>
    /// Boxes the <see cref="ArraySegment{T}"/> struct once when stored in
    /// <see cref="Materialized"/>. That single allocation replaces what
    /// would otherwise be a copy of the whole subdocument's bytes — a clear
    /// win as soon as the slice exceeds a few dozen bytes (which it always
    /// does for JSON).
    /// </remarks>
    public static ValueRef FromJsonSlice(ArraySegment<byte> cborSlice) =>
        new(DataValue.Null(DataKind.Json), cborSlice);

    /// <summary>
    /// Struct value carried as a recursive <see cref="ValueRef"/>[] payload —
    /// each field is itself a deferred-materialisation wrapper, so non-inline
    /// fields (long strings, nested arrays/structs, byte arrays) stay in
    /// managed memory until the outermost <see cref="ToDataValue"/> recurses
    /// through and writes everything to the target arena in one pass.
    /// </summary>
    public static ValueRef FromStruct(ValueRef[] fields) =>
        new(DataValue.NullUntypedStruct(), fields);

    /// <summary>
    /// Struct value with a registered <see cref="TypeRegistry"/> id pre-stamped
    /// on the inline placeholder. Use this overload when the producer already
    /// knows the struct's shape (e.g. a model emitting its declared output schema)
    /// so <c>typeof()</c> and downstream formatters can resolve field names without
    /// waiting for the materialisation boundary.
    /// </summary>
    public static ValueRef FromStruct(ValueRef[] fields, ushort typeId) =>
        new(DataValue.NullStruct(typeId), fields);

    /// <summary>
    /// Array value carried as a recursive <see cref="ValueRef"/>[] payload.
    /// Same deferred-materialisation contract as <see cref="FromStruct(ValueRef[])"/>:
    /// nested non-inline elements stay managed until the boundary recurses.
    /// The inline carrier uses the typed-array shape (<see cref="DataValue.Kind"/> =
    /// <paramref name="elementKind"/>, <see cref="DataValue.IsArray"/> = true).
    /// </summary>
    /// <remarks>
    /// Nested arrays (an element that is itself an array carrier) are rejected
    /// at construction. The carrier encodes only the leaf element kind, not
    /// nesting, so <c>Array&lt;Array&lt;X&gt;&gt;</c> can't round-trip through
    /// <see cref="ToDataValue"/>. Models needing an outer-of-inner shape
    /// should flatten or wrap the inner array in a <see cref="DataKind.Struct"/>.
    /// </remarks>
    public static ValueRef FromArray(DataKind elementKind, ValueRef[] elements)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].IsArray)
            {
                throw new NotSupportedException(
                    $"ValueRef.FromArray element [{i}] is itself an array (Kind={elements[i].Kind}, IsArray=true). "
                    + "Nested arrays (Array<Array<X>>) are not supported; the typed-array carrier encodes only "
                    + "the leaf element kind. Wrap the inner array in a Struct field instead.");
            }
        }
        return new(DataValue.NullArrayOf(elementKind), elements);
    }


    /// <summary>
    /// Bulk-constructor for a fixed-width primitive array. Stores the caller's
    /// <paramref name="values"/> array directly as the managed payload — zero
    /// per-element wrapping, zero copies until the boundary <see cref="ToDataValue"/>
    /// flushes the bytes into the target arena. The path that
    /// <c>ValueRef.FromArray(DataKind.Float32, [ValueRef.FromFloat32(…), …])</c>
    /// would have wrapped 1536 individual <see cref="ValueRef"/> structs collapses
    /// to a single reference here.
    /// </summary>
    /// <typeparam name="T">
    /// Element type. Must be unmanaged and must match the kind's
    /// <see cref="DataValue.ScalarByteSize"/> exactly. The size check at
    /// construction catches obvious mismatches (e.g. <c>FromPrimitiveArray&lt;double&gt;(values, DataKind.Float32)</c>);
    /// it does not validate that the bit-pattern of <typeparamref name="T"/>
    /// is the canonical encoding of <paramref name="elementKind"/> (e.g.
    /// passing a <c>long[]</c> for <see cref="DataKind.Int64"/> or for
    /// <see cref="DataKind.Time"/> both pass the size check; the latter is
    /// reasonable because Time stores ticks directly).
    /// </typeparam>
    /// <param name="values">Caller-owned array. Held by reference until materialise.</param>
    /// <param name="elementKind">Per-element kind. Must be a fixed-width primitive.</param>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/>'s size doesn't match <paramref name="elementKind"/>'s
    /// <see cref="DataValue.ScalarByteSize"/>.
    /// </exception>
    public static ValueRef FromPrimitiveArray<T>(T[] values, DataKind elementKind) where T : unmanaged
    {
        int sizeT = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        int kindSize = DataValue.ScalarByteSize(elementKind);
        if (sizeT != kindSize)
        {
            throw new ArgumentException(
                $"Type {typeof(T).Name} (size={sizeT}) doesn't match DataKind.{elementKind} "
                + $"(size={kindSize}). Pass a T whose size matches the kind's ScalarByteSize.",
                nameof(elementKind));
        }

        return new(DataValue.NullArrayOf(elementKind), values);
    }

    /// <summary>
    /// Typed null struct with the given <see cref="TypeRegistry"/> id. No
    /// payload — boundary materialisation produces <see cref="DataValue.NullStruct"/>.
    /// Required: pass a real TypeId so <c>typeof()</c> stays meaningful, or use
    /// <see cref="NullUntypedStruct"/> when the call site genuinely has no shape.
    /// </summary>
    public static ValueRef NullStruct(ushort typeId) =>
        new(DataValue.NullStruct(typeId), null);

    /// <summary>
    /// Typed null struct with no registered TypeId. Explicit escape hatch —
    /// see <see cref="DataValue.FromUntypedStruct"/>.
    /// </summary>
    public static ValueRef NullUntypedStruct() =>
        new(DataValue.NullUntypedStruct(), null);

    /// <summary>
    /// Typed null array of the given element kind. No payload — boundary
    /// materialisation passes the typed-null carrier through unchanged.
    /// The carrier uses the typed-array shape (<see cref="DataValue.Kind"/> =
    /// <paramref name="elementKind"/> with <see cref="DataValue.IsArray"/> and
    /// <see cref="DataValue.IsNull"/> both set).
    /// </summary>
    public static ValueRef NullArray(DataKind elementKind) =>
        new(DataValue.NullArrayOf(elementKind), null);

    /// <summary>
    /// Reads the value as a <see cref="string"/>. For inline strings this
    /// reads the UTF-8 payload from the carrier; for materialized strings
    /// this returns the managed payload directly.
    /// </summary>
    public string AsString()
    {
        if (_materialized is string materialized)
        {
            return materialized;
        }
        if (_inline.IsInline && _inline.Kind == DataKind.String)
        {
            return _inline.AsString();
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} cannot be read as String. Materialized payload type: "
            + (_materialized?.GetType().Name ?? "<none>"));
    }

    /// <summary>Reads as a UTF-8 byte span where applicable.</summary>
    public byte[] AsBytes()
    {
        if (_materialized is byte[] bytes)
        {
            return bytes;
        }
        if (_materialized is ArraySegment<byte> slice)
        {
            // Materialise the slice once when callers ask for an owned byte[].
            // Hot paths should prefer AsByteSpan() to avoid this allocation.
            return slice.ToArray();
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} does not carry a byte payload.");
    }

    /// <summary>
    /// Reads the byte payload as a <see cref="ReadOnlySpan{T}"/> without
    /// allocating. Handles both whole-buffer payloads (<see cref="byte"/>[])
    /// and slice payloads (<see cref="ArraySegment{T}"/>) — the latter from
    /// <see cref="FromJsonSlice"/>. Use this on JSON / Image / Audio / Video
    /// codec hot paths to skip the <see cref="AsBytes"/> allocation.
    /// </summary>
    public ReadOnlySpan<byte> AsByteSpan()
    {
        if (_materialized is byte[] bytes)
        {
            return bytes;
        }
        if (_materialized is ArraySegment<byte> slice)
        {
            return slice;
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} does not carry a byte payload.");
    }

    /// <summary>
    /// Returns the byte payload as an <see cref="ArraySegment{T}"/> over the
    /// underlying buffer — no copy. Used by <c>json_query</c> to compose a
    /// new slice over the source's backing array without re-materialising the
    /// bytes. The returned segment shares the source array; subsequent
    /// mutations to either side would alias.
    /// </summary>
    public ArraySegment<byte> AsByteSegment()
    {
        if (_materialized is byte[] bytes)
        {
            return new ArraySegment<byte>(bytes);
        }
        if (_materialized is ArraySegment<byte> slice)
        {
            return slice;
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} does not carry a byte payload.");
    }

    /// <summary>
    /// Returns the value as a decoded <see cref="SKBitmap"/>. If the payload is
    /// already an <see cref="SKBitmap"/> (produced by a prior function or model),
    /// it is returned directly. If it is a raw-encoded <see cref="byte"/>[] (from
    /// the arena/sidecar boundary), the bytes are decoded on demand. No caching —
    /// hold the result in a local if the same function decodes more than once.
    /// </summary>
    public SKBitmap AsImage()
    {
        if (_materialized is SKBitmap bitmap)
        {
            return bitmap;
        }
        if (_materialized is byte[] bytes)
        {
            SKBitmap? decoded = SKBitmap.Decode(bytes);
            return decoded ?? throw new InvalidOperationException(
                "Failed to decode Image bytes into an SKBitmap. "
                + "The bytes may be corrupt or in an unsupported format.");
        }
        throw new InvalidOperationException(
            $"ValueRef of kind {Kind} does not carry an Image payload "
            + $"(Materialized: {_materialized?.GetType().Name ?? "<none>"}).");
    }

    /// <summary>
    /// Returns the <see cref="LambdaValue"/> backing a
    /// <see cref="DataKind.Lambda"/> <see cref="ValueRef"/>. Throws when the
    /// kind is wrong or the value carries no managed payload (which would
    /// indicate a misuse — Lambda values must be constructed with
    /// <see cref="FromLambda"/>).
    /// </summary>
    public LambdaValue AsLambda()
    {
        if (_inline.Kind != DataKind.Lambda)
        {
            throw new InvalidOperationException(
                $"AsLambda called on a {_inline.Kind} value (expected Lambda).");
        }
        if (_materialized is LambdaValue lambda)
        {
            return lambda;
        }
        throw new InvalidOperationException(
            $"ValueRef of kind Lambda does not carry a LambdaValue payload "
            + $"(Materialized: {_materialized?.GetType().Name ?? "<none>"}).");
    }

    /// <summary>
    /// Returns the <c>DrawingPayload</c> backing a
    /// <see cref="DataKind.Drawing"/> <see cref="ValueRef"/>. Throws when
    /// the kind is wrong or the value carries no managed payload.
    /// </summary>
    public Scalar.Drawing.DrawingPayload AsDrawing()
    {
        if (_inline.Kind != DataKind.Drawing)
        {
            throw new InvalidOperationException(
                $"AsDrawing called on a {_inline.Kind} value (expected Drawing).");
        }
        if (_materialized is Scalar.Drawing.DrawingPayload drawing)
        {
            return drawing;
        }
        throw new InvalidOperationException(
            $"ValueRef of kind Drawing does not carry a DrawingPayload "
            + $"(Materialized: {_materialized?.GetType().Name ?? "<none>"}).");
    }

    /// <summary>
    /// Returns the raw PointCloud blob (40-byte header + interleaved per-point
    /// payload). Asserts <see cref="Kind"/> is <see cref="DataKind.PointCloud"/>.
    /// Callers parse the header via <see cref="DatumIngest.Model.Spatial.PointCloudHeader.Read"/>.
    /// </summary>
    public byte[] AsPointCloud()
    {
        if (_inline.Kind != DataKind.PointCloud)
        {
            throw new InvalidOperationException(
                $"AsPointCloud called on a {_inline.Kind} value (expected PointCloud).");
        }
        if (_materialized is byte[] bytes)
        {
            return bytes;
        }
        throw new InvalidOperationException(
            "PointCloud ValueRef does not carry a byte[] payload "
            + $"(Materialized: {_materialized?.GetType().Name ?? "<none>"}).");
    }

    /// <summary>
    /// Returns the raw Mesh blob (48-byte header + interleaved per-vertex
    /// payload + triangle indices + optional embedded texture). Asserts
    /// <see cref="Kind"/> is <see cref="DataKind.Mesh"/>. Callers parse the
    /// header via <see cref="DatumIngest.Model.Spatial.MeshHeader.Read"/>.
    /// </summary>
    public byte[] AsMesh()
    {
        if (_inline.Kind != DataKind.Mesh)
        {
            throw new InvalidOperationException(
                $"AsMesh called on a {_inline.Kind} value (expected Mesh).");
        }
        if (_materialized is byte[] bytes)
        {
            return bytes;
        }
        throw new InvalidOperationException(
            "Mesh ValueRef does not carry a byte[] payload "
            + $"(Materialized: {_materialized?.GetType().Name ?? "<none>"}).");
    }

    /// <summary>
    /// Returns the field <see cref="ValueRef"/>s of a struct value without
    /// materialising into the arena. Each element is itself a ValueRef so
    /// callers can recurse into nested structures (or read leaf values via
    /// the inline accessors) without ever invoking <see cref="ToDataValue"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not a struct, or it is a struct null with no payload.
    /// </exception>
    public ReadOnlySpan<ValueRef> GetStructFields()
    {
        if (_inline.Kind != DataKind.Struct)
        {
            throw new InvalidOperationException(
                $"GetStructFields called on a {_inline.Kind} value (expected Struct).");
        }
        if (_materialized is ValueRef[] fields)
        {
            return fields;
        }
        if (IsNull)
        {
            throw new InvalidOperationException(
                "GetStructFields called on a null struct; check IsNull first.");
        }
        throw new InvalidOperationException(
            "Struct ValueRef does not carry a ValueRef[] payload.");
    }

    /// <summary>
    /// Returns the element <see cref="ValueRef"/>s of an array value without
    /// materialising into the arena. Same contract as <see cref="GetStructFields"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not an array, or it is an array null with no payload.
    /// </exception>
    public ReadOnlySpan<ValueRef> GetArrayElements()
    {
        if (!_inline.IsArray)
        {
            throw new InvalidOperationException(
                $"GetArrayElements called on a {_inline.Kind} value (expected an array).");
        }
        if (_materialized is ValueRef[] elements)
        {
            return elements;
        }
        if (IsNull)
        {
            throw new InvalidOperationException(
                "GetArrayElements called on a null array; check IsNull first.");
        }
        throw new InvalidOperationException(
            "Array ValueRef does not carry a ValueRef[] payload.");
    }

    /// <summary>
    /// Returns the element count of an array value across both array
    /// carrier shapes: the <c>ValueRef[]</c> form built by SQL array
    /// literals and the typed primitive form (e.g. <c>long[]</c>,
    /// <c>float[]</c>) built by <see cref="FromPrimitiveArray{T}"/>.
    /// Callers that don't actually need element access (e.g. array_length)
    /// should prefer this over <see cref="GetArrayElements"/>, which only
    /// works for the ValueRef[] form.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not an array.</exception>
    public int GetArrayLength()
    {
        if (!_inline.IsArray)
        {
            throw new InvalidOperationException(
                $"GetArrayLength called on a {_inline.Kind} value (expected an array).");
        }
        return _materialized switch
        {
            ValueRef[] refs => refs.Length,
            byte[] bytes => bytes.Length,
            sbyte[] sbytes => sbytes.Length,
            short[] shorts => shorts.Length,
            ushort[] ushorts => ushorts.Length,
            int[] ints => ints.Length,
            uint[] uints => uints.Length,
            long[] longs => longs.Length,
            ulong[] ulongs => ulongs.Length,
            float[] floats => floats.Length,
            double[] doubles => doubles.Length,
            bool[] bools => bools.Length,
            string[] strings => strings.Length,
            null when IsNull => throw new InvalidOperationException(
                "GetArrayLength called on a null array; check IsNull first."),
            _ => throw new InvalidOperationException(
                $"Array ValueRef payload is of unsupported runtime type "
                + $"'{_materialized?.GetType().Name ?? "<null>"}'."),
        };
    }

    /// <summary>
    /// The element kind for an array value. With the typed-array carrier
    /// (<see cref="Kind"/> = <c>elementKind</c>, <see cref="IsArray"/> = true),
    /// the element kind is simply <see cref="Kind"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not an array.</exception>
    public DataKind ArrayElementKind
    {
        get
        {
            if (!_inline.IsArray)
            {
                throw new InvalidOperationException(
                    $"Cannot read ArrayElementKind on a {_inline.Kind} value (expected an array).");
            }
            return _inline.Kind;
        }
    }

    /// <summary>Boolean accessor (inline only).</summary>
    public bool AsBoolean() => _inline.AsBoolean();
    /// <summary>UInt8 accessor (inline only).</summary>
    public byte AsUInt8() => _inline.AsUInt8();
    /// <summary>Int8 accessor (inline only).</summary>
    public sbyte AsInt8() => _inline.AsInt8();
    /// <summary>Int16 accessor (inline only).</summary>
    public short AsInt16() => _inline.AsInt16();
    /// <summary>UInt16 accessor (inline only).</summary>
    public ushort AsUInt16() => _inline.AsUInt16();
    /// <summary>Int32 accessor (inline only).</summary>
    public int AsInt32() => _inline.AsInt32();
    /// <summary>UInt32 accessor (inline only).</summary>
    public uint AsUInt32() => _inline.AsUInt32();
    /// <summary>Int64 accessor (inline only).</summary>
    public long AsInt64() => _inline.AsInt64();
    /// <summary>UInt64 accessor (inline only).</summary>
    public ulong AsUInt64() => _inline.AsUInt64();
    /// <summary>Float16 accessor (inline only).</summary>
    public Half AsFloat16() => _inline.AsFloat16();
    /// <summary>Float32 accessor (inline only).</summary>
    public float AsFloat32() => _inline.AsFloat32();

    /// <summary>2D point payload as a <see cref="System.Numerics.Vector2"/>.</summary>
    public System.Numerics.Vector2 AsPoint2D() => _inline.AsPoint2D();

    /// <summary>3D point payload as a <see cref="System.Numerics.Vector3"/>.</summary>
    public System.Numerics.Vector3 AsPoint3D() => _inline.AsPoint3D();
    /// <summary>Float64 accessor (inline only).</summary>
    public double AsFloat64() => _inline.AsFloat64();
    /// <summary>Decimal accessor (inline only).</summary>
    public decimal AsDecimal() => _inline.AsDecimal();
    /// <summary>Int128 accessor (inline only).</summary>
    public Int128 AsInt128() => _inline.AsInt128();
    /// <summary>UInt128 accessor (inline only).</summary>
    public UInt128 AsUInt128() => _inline.AsUInt128();

    /// <summary>
    /// Widens any numeric scalar (integer, float, boolean) to <see cref="double"/>.
    /// Returns <see langword="false"/> for non-numeric kinds or null values.
    /// Mirrors <see cref="DataValue.TryToDouble"/>.
    /// </summary>
    public bool TryToDouble(out double result) => _inline.TryToDouble(out result);

    /// <summary>
    /// Widens any numeric scalar to <see cref="float"/>. Returns
    /// <see langword="false"/> for non-numeric kinds or null values.
    /// Mirrors <see cref="DataValue.TryToFloat"/>.
    /// </summary>
    public bool TryToFloat(out float result) => _inline.TryToFloat(out result);

    /// <summary>
    /// Coerces any numeric scalar to <see cref="long"/>; floats truncate.
    /// Returns <see langword="false"/> for non-numeric kinds or null values.
    /// Mirrors <see cref="DataValue.TryToInt64"/>.
    /// </summary>
    public bool TryToInt64(out long result) => _inline.TryToInt64(out result);

    /// <summary>
    /// Coerces any numeric scalar to <see cref="int"/>; floats truncate and
    /// out-of-range values overflow silently.
    /// Returns <see langword="false"/> for non-numeric kinds or null values.
    /// Mirrors <see cref="DataValue.TryToInt32"/>.
    /// </summary>
    public bool TryToInt32(out int result) => _inline.TryToInt32(out result);

    /// <summary>
    /// Coerces any numeric scalar to <see cref="float"/>. Throws for null or
    /// non-numeric kinds. Mirrors <see cref="DataValue.ToFloat"/>.
    /// </summary>
    public float ToFloat() => _inline.ToFloat();

    /// <summary>
    /// Coerces any numeric scalar to <see cref="double"/>. Throws for null or
    /// non-numeric kinds. Mirrors <see cref="DataValue.ToDouble"/>.
    /// </summary>
    public double ToDouble() => _inline.ToDouble();

    /// <summary>
    /// Coerces any numeric scalar to <see cref="int"/>. Throws for null or
    /// non-numeric kinds; floats truncate. Mirrors <see cref="DataValue.ToInt32"/>.
    /// </summary>
    public int ToInt32() => _inline.ToInt32();

    /// <summary>
    /// Coerces any numeric scalar to <see cref="long"/>. Throws for null or
    /// non-numeric kinds; floats truncate. Mirrors <see cref="DataValue.ToInt64"/>.
    /// </summary>
    public long ToInt64() => _inline.ToInt64();

    /// <summary>Date accessor (inline only).</summary>
    public DateOnly AsDate() => _inline.AsDate();
    /// <summary>TimestampTz accessor (inline only). Returned offset is always <see cref="TimeSpan.Zero"/>.</summary>
    public DateTimeOffset AsTimestampTz() => _inline.AsTimestampTz();

    /// <summary>Timestamp accessor (inline only). Returned <see cref="DateTime"/> has <see cref="DateTimeKind.Unspecified"/>.</summary>
    public DateTime AsTimestamp() => _inline.AsTimestamp();
    /// <summary>Time accessor (inline only).</summary>
    public TimeOnly AsTime() => _inline.AsTime();
    /// <summary>Duration accessor (inline only).</summary>
    public TimeSpan AsDuration() => _inline.AsDuration();
    /// <summary>UUID accessor (inline only).</summary>
    public Guid AsUuid() => _inline.AsUuid();
    /// <summary>DataKind tag accessor (inline only).</summary>
    public DataKind AsType() => _inline.AsType();

    /// <summary>
    /// Returns the value as its natural boxed CLR type — the mirror of
    /// <see cref="DataValue.ToObject(IValueStore?, DatumFile.Sidecar.SidecarRegistry?)"/> for the ValueRef
    /// shape. Reference kinds (<see cref="DataKind.String"/>) read from the
    /// in-struct materialized payload, so no store is needed at this
    /// boundary. Null values return <see langword="null"/>.
    /// </summary>
    /// <returns>
    /// The boxed primitive (<see cref="float"/>, <see cref="int"/>,
    /// <see cref="bool"/>, etc.), a <see cref="string"/> for
    /// <see cref="DataKind.String"/>, or <see langword="null"/> when
    /// <see cref="IsNull"/> is <see langword="true"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value is an array, a struct, or a blob kind
    /// (<see cref="DataKind.Image"/>, <see cref="DataKind.Audio"/>,
    /// <see cref="DataKind.Video"/>, <see cref="DataKind.Json"/>). Callers
    /// that need composite or blob conversion should branch on
    /// <see cref="IsArray"/> / <see cref="Kind"/> and use
    /// <see cref="GetStructFields"/> / <see cref="GetArrayElements"/> /
    /// <see cref="ToDataValue(IValueStore, ushort, TypeRegistry?)"/> directly.
    /// </exception>
    public object? ToObject()
    {
        if (IsNull) return null;
        if (IsArray)
        {
            throw new InvalidOperationException(
                $"ValueRef.ToObject(): cannot box array values; recurse via GetArrayElements().");
        }
        return Kind switch
        {
            DataKind.String => AsString(),
            DataKind.Boolean => AsBoolean(),
            DataKind.Uuid => AsUuid(),
            DataKind.Int8 => AsInt8(),
            DataKind.Int16 => AsInt16(),
            DataKind.Int32 => AsInt32(),
            DataKind.Int64 => AsInt64(),
            DataKind.UInt8 => AsUInt8(),
            DataKind.UInt16 => AsUInt16(),
            DataKind.UInt32 => AsUInt32(),
            DataKind.UInt64 => AsUInt64(),
            DataKind.Int128 => AsInt128(),
            DataKind.UInt128 => AsUInt128(),
            DataKind.Float16 => AsFloat16(),
            DataKind.Float32 => AsFloat32(),
            DataKind.Float64 => AsFloat64(),
            DataKind.Decimal => AsDecimal(),
            DataKind.Date => AsDate(),
            DataKind.Timestamp => AsTimestamp(),
            DataKind.TimestampTz => AsTimestampTz(),
            DataKind.Time => AsTime(),
            DataKind.Duration => AsDuration(),
            DataKind.Point2D => AsPoint2D(),
            DataKind.Point3D => AsPoint3D(),
            _ => throw new InvalidOperationException(
                $"ValueRef.ToObject(): cannot box kind {Kind}. Composite and blob kinds " +
                "(Struct, Image, Audio, Video, Json) must be handled by the caller."),
        };
    }

    /// <summary>
    /// Materialises this <see cref="ValueRef"/> back into a <see cref="DataValue"/>
    /// against <paramref name="targetStore"/>. Inline and null values pass
    /// through unchanged; managed payloads (strings, byte arrays, recursive
    /// struct/array trees) are written to the target arena. The recursion
    /// for struct/array values is single-pass — every nested non-inline leaf
    /// writes exactly once at the boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the universal "ValueRef → DataValue" boundary used by the
    /// expression evaluator at expression-result emission and by the model
    /// invocation operator at model-output scatter. Lifting it onto
    /// <see cref="ValueRef"/> means any consumer that has a ValueRef in hand
    /// can materialise without dragging an evaluator dependency.
    /// </para>
    /// <para>
    /// For terminal sinks that don't need a <see cref="DataValue"/> — display,
    /// hash-based caching, export — recurse via
    /// <see cref="GetStructFields"/> / <see cref="GetArrayElements"/> directly
    /// and skip this method entirely. The arena stays cold.
    /// </para>
    /// </remarks>
    public DataValue ToDataValue(IValueStore targetStore, ushort typeId = 0, TypeRegistry? types = null)
    {
        if (IsNull)
        {
            return _inline;
        }

        if (_materialized is null)
        {
            return _inline;
        }

        return _materialized switch
        {
            // Lambdas are row-scoped — they exist only as intra-query intermediate
            // values. Hitting this branch means a caller tried to materialise a
            // Lambda into an arena-backed DataValue (e.g. as a SELECT-output column,
            // an INSERT row, or any other arena-write path). Refusal is correct:
            // a serialised lambda would have a dangling closure capture.
            LambdaValue when _inline.Kind == DataKind.Lambda =>
                throw new InvalidOperationException(
                    "Lambda values cannot be persisted to a column, output row, "
                    + "or any other arena-write boundary. They exist only as "
                    + "intra-query intermediate values flowing between higher-order "
                    + "functions and their consumers."),
            Scalar.Drawing.DrawingPayload when _inline.Kind == DataKind.Drawing =>
                throw new InvalidOperationException(
                    "Drawing values cannot be persisted to a column, output row, "
                    + "or any other arena-write boundary today. They exist only as "
                    + "intra-query intermediate values; render(drawing, size) "
                    + "rasterises a Drawing into a persistable Image."),
            string s when _inline.Kind == DataKind.String && !_inline.IsArray =>
                DataValue.FromString(s, targetStore),
            byte[] bytes when IsByteArrayKind => DataValue.FromByteArray(bytes, targetStore),
            byte[] bytes when _inline.Kind == DataKind.Image && !_inline.IsArray =>
                ImageDataValueFactory.FromEncodedBytes(bytes, targetStore),
            SKBitmap bmp when _inline.Kind == DataKind.Image && !_inline.IsArray =>
                ImageDataValueFactory.FromBitmap(bmp, targetStore),
            byte[] bytes when _inline.Kind == DataKind.Audio && !_inline.IsArray =>
                AudioDataValueFactory.FromEncodedBytes(bytes, targetStore),
            byte[] bytes when _inline.Kind == DataKind.Video && !_inline.IsArray =>
                VideoDataValueFactory.FromEncodedBytes(bytes, targetStore),
            byte[] bytes when _inline.Kind == DataKind.Json && !_inline.IsArray =>
                DataValue.FromJson(bytes, targetStore),
            byte[] bytes when _inline.Kind == DataKind.PointCloud && !_inline.IsArray =>
                MaterializePointCloudWithMetadata(bytes, targetStore),
            byte[] bytes when _inline.Kind == DataKind.Mesh && !_inline.IsArray =>
                MaterializeMeshWithMetadata(bytes, targetStore),
            // Slice form (json_query subdocument): copy only the slice's bytes into the arena.
            ArraySegment<byte> slice when _inline.Kind == DataKind.Json && !_inline.IsArray =>
                DataValue.FromJson((ReadOnlySpan<byte>)slice, targetStore),
            ValueRef[] elements when _inline.IsArray =>
                BuildTypedArray(_inline.Kind, elements, targetStore, typeId, types),
            // typeId stamped here so model outputs and other top-level struct ValueRefs
            // get a registered type-id at the single arena-write boundary. When `types` is
            // provided, field TypeIds are looked up from the descriptor and propagated
            // recursively so nested struct fields stay self-describing.
            ValueRef[] fields when _inline.Kind == DataKind.Struct =>
                DataValue.FromStruct(MaterialiseEach(fields, targetStore, typeId, types), targetStore, typeId),
            // Bulk primitive-array path: a typed T[] payload (Float32, Int32, …)
            // produced by FromPrimitiveArray<T> / FromPrimitiveMultiDimArray<T>.
            // When _shape is non-null, the array gets a shape prefix attached at
            // materialisation; otherwise it lands as a flat 1-D arena array.
            Array primitiveArray when _inline.IsArray && _shape is not null =>
                BuildMultiDimPrimitiveArray(_inline.Kind, primitiveArray, _shape, targetStore),
            Array primitiveArray when _inline.IsArray =>
                BuildPrimitiveArray(_inline.Kind, primitiveArray, targetStore),
            _ => throw new InvalidOperationException(
                $"Cannot lower ValueRef with managed payload of type {_materialized.GetType().Name} "
                + $"and kind {Kind} into a DataValue. Add a ToDataValue arm for this combination."),
        };
    }

    /// <summary>
    /// Recursively materialises an array of child <see cref="ValueRef"/>s into
    /// a <see cref="DataValue"/>[] against <paramref name="target"/>. Used by
    /// <see cref="ToDataValue"/> for the struct recursive arm; each leaf's
    /// arena write happens exactly once during the descent. When
    /// <paramref name="types"/> and <paramref name="parentStructTypeId"/> are
    /// provided, each child's TypeId is looked up from the parent's descriptor
    /// and propagated, keeping nested struct/array fields self-describing.
    /// </summary>
    private static DataValue[] MaterialiseEach(
        ValueRef[] children,
        IValueStore target,
        ushort parentStructTypeId = 0,
        TypeRegistry? types = null)
    {
        TypeDescriptor? parentDesc = parentStructTypeId != 0 && types is not null
            ? types.GetDescriptor(parentStructTypeId)
            : null;
        IReadOnlyList<StructFieldDescriptor>? fields = parentDesc?.Fields;
        DataValue[] resolved = new DataValue[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            ushort fieldTypeId = fields is not null && i < fields.Count
                ? (ushort)fields[i].TypeId
                : (ushort)0;
            resolved[i] = children[i].ToDataValue(target, fieldTypeId, types);
        }
        return resolved;
    }

    /// <summary>
    /// Materialises an array <see cref="ValueRef"/> into a typed-array
    /// <see cref="DataValue"/> of the appropriate per-element-kind layout.
    /// Dispatches by element kind: reference kinds (String, Image, Struct)
    /// route to their slot-block factories; fixed-width primitives pack their
    /// inline payload bytes contiguously via <see cref="BuildFixedWidthArray"/>.
    /// </summary>
    /// <summary>
    /// Parses a PointCloud blob header at the <c>ValueRef → DataValue</c> materialization
    /// boundary and stamps inline metadata (point count + attribute flags) onto the
    /// resulting DataValue. <see cref="DataValue.PointCloudCount"/> and the SQL accessor
    /// functions (<c>point_cloud_count</c>, <c>point_cloud_has_color</c>) skip a full
    /// blob materialization when the inline metadata is populated.
    /// </summary>
    private static DataValue MaterializePointCloudWithMetadata(byte[] bytes, IValueStore target)
    {
        if (bytes.Length < 40)
        {
            return DataValue.FromPointCloud(bytes, target);
        }
        try
        {
            PointCloudHeader header = PointCloudHeader.Read(bytes);
            return DataValue.FromPointCloud(bytes, target,
                pointCount: header.PointCount,
                attributeFlags: (byte)header.Flags);
        }
        catch
        {
            return DataValue.FromPointCloud(bytes, target);
        }
    }

    /// <summary>
    /// Parses a Mesh blob header at the <c>ValueRef → DataValue</c> materialization
    /// boundary and stamps inline metadata (vertex/triangle counts + attribute flags).
    /// </summary>
    private static DataValue MaterializeMeshWithMetadata(byte[] bytes, IValueStore target)
    {
        if (bytes.Length < 48)
        {
            return DataValue.FromMesh(bytes, target);
        }
        try
        {
            MeshHeader header = MeshHeader.Read(bytes);
            return DataValue.FromMesh(bytes, target,
                vertexCount: header.VertexCount,
                triangleCount: header.TriangleCount,
                attributeFlags: (byte)header.Flags);
        }
        catch
        {
            return DataValue.FromMesh(bytes, target);
        }
    }

    private static DataValue BuildTypedArray(
        DataKind elementKind,
        ValueRef[] elements,
        IValueStore target,
        ushort typeId = 0,
        TypeRegistry? types = null)
    {
        return elementKind switch
        {
            DataKind.String => BuildStringArray(elements, target),
            DataKind.Image => BuildImageArray(elements, target),
            DataKind.Struct => BuildStructArray(elements, target, typeId, types),
            _ => BuildFixedWidthArray(elementKind, elements, target),
        };
    }

    private static byte[] GetImageBytes(ValueRef v) => v._materialized switch
    {
        byte[] bytes => bytes,
        SKBitmap bmp => ImageEncoder.Encode(bmp, SKEncodedImageFormat.Png, 100),
        _ => throw new InvalidOperationException(
            $"ValueRef of kind {v.Kind} does not carry an Image payload "
            + $"(Materialized: {v._materialized?.GetType().Name ?? "<none>"})."),
    };

    private static DataValue BuildStringArray(ValueRef[] elements, IValueStore target)
    {
        string[] strings = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullElement(elements[i], i, DataKind.String);
            strings[i] = elements[i].AsString();
        }
        return DataValue.FromStringArray(strings, target);
    }

    private static DataValue BuildImageArray(ValueRef[] elements, IValueStore target)
    {
        byte[][] images = new byte[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullElement(elements[i], i, DataKind.Image);
            images[i] = GetImageBytes(elements[i]);
        }
        return DataValue.FromImageArray(images, target);
    }

    private static DataValue BuildStructArray(
        ValueRef[] elements,
        IValueStore target,
        ushort typeId = 0,
        TypeRegistry? types = null)
    {
        // The incoming `typeId` is the Array<Struct> descriptor's TypeId. Hop to
        // its ElementTypeId — that's the per-element struct's TypeId, the value
        // that gets stamped into every slot's reserved bytes by FromStructArray.
        // The array container itself no longer carries a TypeId; each element is
        // self-describing via its slot's TypeId field.
        ushort elementStructTypeId = 0;
        IReadOnlyList<StructFieldDescriptor>? elementFields = null;
        if (typeId != 0 && types is not null && types.GetDescriptor(typeId) is { } arrayDesc
            && arrayDesc.ElementTypeId is { } eid)
        {
            elementStructTypeId = (ushort)eid;
            elementFields = types.GetDescriptor(elementStructTypeId)?.Fields;
        }
        else if (elements.Length > 0 && elements[0]._inline.TypeId != 0)
        {
            // No Array<Struct> typeId was supplied (the caller is a dynamic-
            // shape producer like a SQL-defined model body whose result
            // ValueRef wraps an inline carrier without a TypeId — by design,
            // since the inline-array TypeId getter is gated to arena-backed
            // Array<Struct>). The per-element struct ValueRefs DO carry their
            // shape TypeId via `FromStruct(fields, typeId)`, so peek at the
            // first element's _inline.TypeId and use that as the element
            // struct's stamp. Without this peek, dynamic-shape Array<Struct>
            // outputs lose field names on the way out of MIO scatter.
            elementStructTypeId = elements[0]._inline.TypeId;
            elementFields = types?.GetDescriptor(elementStructTypeId)?.Fields;
        }

        DataValue[][] rows = new DataValue[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullElement(elements[i], i, DataKind.Struct);
            ReadOnlySpan<ValueRef> fields = elements[i].GetStructFields();
            DataValue[] row = new DataValue[fields.Length];
            for (int j = 0; j < fields.Length; j++)
            {
                ushort fieldTypeId = elementFields is not null && j < elementFields.Count
                    ? (ushort)elementFields[j].TypeId
                    : (ushort)0;
                row[j] = fields[j].ToDataValue(target, fieldTypeId, types);
            }
            rows[i] = row;
        }
        return DataValue.FromStructArray(rows, target, elementStructTypeId);
    }

    /// <summary>
    /// Guard for inner-element nulls inside an array. Whole-array nulls are
    /// handled at the carrier level by <see cref="ToDataValue"/>; per-element
    /// nulls would need either a per-element null bitmap (none yet) or a
    /// sentinel encoding per kind (none yet either). Until that lands,
    /// fail loudly rather than corrupt the slot block / silently zero-fill.
    /// </summary>
    private static void ThrowIfNullElement(ValueRef element, int index, DataKind elementKind)
    {
        if (element.IsNull)
        {
            throw new NotSupportedException(
                $"Array<{elementKind}> element [{index}] is null; per-element nulls inside an array are "
                + "not yet supported. Wrap into Struct{{value, is_null}} or filter nulls before aggregating.");
        }
    }

    /// <summary>
    /// Packs a fixed-width primitive array's element bytes contiguously and
    /// hands the buffer to the typed-array factory. Each element's inline
    /// payload bytes are copied via <see cref="DataValue.CopyInlineScalarBytes"/>
    /// using the per-kind <see cref="DataValue.ScalarByteSize"/> convention.
    /// Routes through the inline-array factory when the total fits in the
    /// 16-byte struct payload, otherwise through the arena-byte factory.
    /// </summary>
    /// <remarks>
    /// Each element ValueRef must be inline-or-null and of <paramref name="elementKind"/>.
    /// Null elements pack as zeroed bytes (no per-element null bitmap yet) —
    /// matches the existing fixed-width array convention; callers wanting null
    /// preservation must wrap into a Struct with a separate null flag.
    /// </remarks>
    private static DataValue BuildFixedWidthArray(
        DataKind elementKind,
        ValueRef[] elements,
        IValueStore target)
    {
        int elementSize = DataValue.ScalarByteSize(elementKind);
        int totalBytes = elements.Length * elementSize;

        // Empty arrays match the inline N=0 convention used by reference
        // typed-array factories. The factory itself enforces the byte cap.
        if (totalBytes == 0)
        {
            return DataValue.FromInlineArrayBytes(ReadOnlySpan<byte>.Empty, elementKind);
        }

        byte[] buffer = new byte[totalBytes];
        Span<byte> span = buffer;
        for (int i = 0; i < elements.Length; i++)
        {
            ThrowIfNullElement(elements[i], i, elementKind);
            DataValue inline = elements[i].InlineDataValue;
            if (inline.Kind != elementKind)
            {
                throw new InvalidOperationException(
                    $"Array element [{i}] has kind {inline.Kind}; expected {elementKind}.");
            }
            inline.CopyInlineScalarBytes(span.Slice(i * elementSize, elementSize));
        }

        // Inline if it fits; otherwise arena. The 16-byte cap matches
        // FromInlineArrayBytes' InlineArrayMaxBytes contract.
        if (totalBytes <= 16)
        {
            return DataValue.FromInlineArrayBytes(span, elementKind);
        }
        return DataValue.FromArenaArrayBytes(span, elementKind, target);
    }

    /// <summary>
    /// Materialises a <c>FromPrimitiveArray&lt;T&gt;</c> payload into a typed
    /// <see cref="DataValue"/>. The payload is a primitive <c>T[]</c>; we cast
    /// to the right element type and call <see cref="DataValue.FromArenaArray{T}"/>,
    /// which copies the bytes contiguously into the target arena in one
    /// <c>StoreBytes</c> call. Always arena-backed — the bulk constructor is
    /// targeted at long arrays (embeddings) where inline doesn't apply.
    /// </summary>
    /// <remarks>
    /// The cast set covers every fixed-width primitive kind. The 8-byte temporal
    /// kinds (<see cref="DataKind.Timestamp"/>, <see cref="DataKind.TimestampTz"/>,
    /// <see cref="DataKind.Time"/>, <see cref="DataKind.Duration"/>) accept
    /// <c>long[]</c> ticks.
    /// </remarks>
    /// <summary>
    /// Returns the approximate size in bytes of the GC-resident payload behind
    /// this <see cref="ValueRef"/>. Used by <see cref="DatumIngest.Execution.VariableScope"/>
    /// (and other holders of long-lived ValueRefs) to report bytes into the
    /// shared <see cref="DatumIngest.Execution.MemoryAccountant"/> on bind/release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts the payload bytes only — strings as <c>2 × Length</c>, primitive
    /// arrays as <c>Length × sizeof(element)</c>, <see cref="SKBitmap"/> via
    /// <see cref="SKBitmap.ByteCount"/>. Object headers and array headers are
    /// not added — they're a small constant-per-allocation overhead that
    /// doesn't materially affect the spill threshold, and counting them
    /// across nested <c>ValueRef[]</c> trees would over-attribute.
    /// </para>
    /// <para>
    /// Returns zero for inline-or-null carriers (no managed payload to track),
    /// for arena-backed values (their bytes live in the file-backed mmap,
    /// not in the GC heap), and for unrecognised payload shapes. Add a new
    /// arm to the switch when a new payload kind ships rather than letting
    /// it silently account as zero in a steady-state path.
    /// </para>
    /// </remarks>
    public static long ManagedPayloadBytes(ValueRef value)
    {
        object? mat = value.Materialized;
        if (mat is null) return 0;
        return mat switch
        {
            string s => 2L * s.Length,
            byte[] arr => arr.Length,
            sbyte[] arr => arr.Length,
            bool[] arr => arr.Length,
            short[] arr => arr.Length * 2L,
            ushort[] arr => arr.Length * 2L,
            Half[] arr => arr.Length * 2L,
            int[] arr => arr.Length * 4L,
            uint[] arr => arr.Length * 4L,
            long[] arr => arr.Length * 8L,
            ulong[] arr => arr.Length * 8L,
            float[] arr => arr.Length * 4L,
            double[] arr => arr.Length * 8L,
            decimal[] arr => arr.Length * 16L,
            Int128[] arr => arr.Length * 16L,
            UInt128[] arr => arr.Length * 16L,
            Guid[] arr => arr.Length * 16L,
            Vector2[] arr => arr.Length * 8L,
            Vector3[] arr => arr.Length * 12L,
            string[] arr => SumStringBytes(arr),
            ArraySegment<byte> seg => seg.Count,
            SKBitmap bmp => bmp.ByteCount,
            ValueRef[] children => SumChildPayloadBytes(children),
            _ => 0,
        };
    }

    private static long SumStringBytes(string[] arr)
    {
        long total = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            string? s = arr[i];
            if (s is not null) total += 2L * s.Length;
        }
        return total;
    }

    private static long SumChildPayloadBytes(ValueRef[] children)
    {
        long total = 0;
        for (int i = 0; i < children.Length; i++)
        {
            total += ManagedPayloadBytes(children[i]);
        }
        return total;
    }

    private static DataValue BuildPrimitiveArray(DataKind elementKind, Array values, IValueStore target)
    {
        return elementKind switch
        {
            DataKind.Boolean  => DataValue.FromArenaArray<byte>((byte[])values, elementKind, target),
            DataKind.UInt8    => DataValue.FromArenaArray<byte>((byte[])values, elementKind, target),
            DataKind.Int8     => DataValue.FromArenaArray<sbyte>((sbyte[])values, elementKind, target),
            DataKind.UInt16   => DataValue.FromArenaArray<ushort>((ushort[])values, elementKind, target),
            DataKind.Int16    => DataValue.FromArenaArray<short>((short[])values, elementKind, target),
            DataKind.Float16  => DataValue.FromArenaArray<Half>((Half[])values, elementKind, target),
            DataKind.UInt32   => DataValue.FromArenaArray<uint>((uint[])values, elementKind, target),
            DataKind.Int32    => DataValue.FromArenaArray<int>((int[])values, elementKind, target),
            DataKind.Float32  => DataValue.FromArenaArray<float>((float[])values, elementKind, target),
            DataKind.Date     => DataValue.FromArenaArray<int>((int[])values, elementKind, target),
            DataKind.UInt64   => DataValue.FromArenaArray<ulong>((ulong[])values, elementKind, target),
            DataKind.Int64    => DataValue.FromArenaArray<long>((long[])values, elementKind, target),
            DataKind.Float64  => DataValue.FromArenaArray<double>((double[])values, elementKind, target),
            DataKind.Time     => DataValue.FromArenaArray<long>((long[])values, elementKind, target),
            DataKind.Duration => DataValue.FromArenaArray<long>((long[])values, elementKind, target),
            DataKind.Timestamp   => DataValue.FromArenaArray<long>((long[])values, elementKind, target),
            DataKind.TimestampTz => DataValue.FromArenaArray<long>((long[])values, elementKind, target),
            DataKind.Decimal  => DataValue.FromArenaArray<decimal>((decimal[])values, elementKind, target),
            DataKind.UInt128  => DataValue.FromArenaArray<UInt128>((UInt128[])values, elementKind, target),
            DataKind.Int128   => DataValue.FromArenaArray<Int128>((Int128[])values, elementKind, target),
            DataKind.Uuid     => DataValue.FromArenaArray<Guid>((Guid[])values, elementKind, target),
            // Spatial: Point2D packs two floats inline (Vector2 is 8 bytes,
            // matching DataValue.ScalarByteSize(Point2D)). Used by
            // mask_to_polygon and any future polygon / polyline / contour
            // returning function.
            DataKind.Point2D  => DataValue.FromArenaArray<Vector2>((Vector2[])values, elementKind, target),
            _ => throw new NotSupportedException(
                $"FromPrimitiveArray materialisation for element kind {elementKind} is not supported. "
                + "Add a case here when the kind has a fixed-width primitive representation."),
        };
    }

    /// <summary>
    /// Multi-dim counterpart to <see cref="BuildPrimitiveArray"/>: takes the
    /// flat elements (typed managed array) and per-dim shape and writes a
    /// multi-dim <see cref="DataValue"/> (with shape prefix) into
    /// <paramref name="target"/>. Element-kind dispatch mirrors the flat
    /// primitive array path one-for-one so the supported kinds stay aligned.
    /// </summary>
    private static DataValue BuildMultiDimPrimitiveArray(DataKind elementKind, Array elements, int[] shape, IValueStore target)
    {
        ReadOnlySpan<byte> bytes = elementKind switch
        {
            DataKind.Boolean  => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<byte>((byte[])elements)),
            DataKind.UInt8    => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<byte>((byte[])elements)),
            DataKind.Int8     => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<sbyte>((sbyte[])elements)),
            DataKind.UInt16   => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>((ushort[])elements)),
            DataKind.Int16    => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<short>((short[])elements)),
            DataKind.Float16  => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Half>((Half[])elements)),
            DataKind.UInt32   => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<uint>((uint[])elements)),
            DataKind.Int32    => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<int>((int[])elements)),
            DataKind.Float32  => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<float>((float[])elements)),
            DataKind.UInt64   => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>((ulong[])elements)),
            DataKind.Int64    => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<long>((long[])elements)),
            DataKind.Float64  => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<double>((double[])elements)),
            _ => throw new NotSupportedException(
                $"Multi-dim ValueRef materialisation for element kind {elementKind} is not supported. "
                + "Add a case here when the kind has a fixed-width primitive representation."),
        };
        return DataValue.FromArenaMultiDimArrayBytes(bytes, shape, elementKind, target);
    }
}
