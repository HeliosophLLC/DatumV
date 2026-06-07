using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Arrow;

/// <summary>
/// Converts a per-batch Apache Arrow column array into an array of
/// <see cref="DataValue"/>s — one entry per row in the batch. Handles
/// primitive scalars and 1-D array shapes (<see cref="ListArray"/>,
/// <see cref="FixedSizeListArray"/>); deeper nesting is deferred.
/// </summary>
internal static class ArrowColumnReader
{
    /// <summary>
    /// Decodes <paramref name="array"/> into a per-row
    /// <see cref="DataValue"/> array. <paramref name="rowCount"/> is the
    /// batch length and must equal <c>array.Length</c>.
    /// </summary>
    public static DataValue[] ReadAsRows(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        if (!type.IsSupported)
        {
            throw new InvalidOperationException(
                $"Arrow column has unsupported element kind {type.ElementKind} " +
                $"({type.UnderlyingTypeId}).");
        }

        // Struct columns recurse into their child arrays — each child is
        // an independently-decoded column whose per-row DataValues stitch
        // together into the parent struct's field array.
        if (array is StructArray structArray)
        {
            return ReadStructColumn(structArray, type, rowCount, arena);
        }

        // Dictionary-encoded columns are the HuggingFace label-column shape
        // — low-cardinality strings (or ints) compressed as (Indices,
        // Dictionary). ArrowColumnType.UnwrapDictionary already surfaces
        // the value type at the schema layer; per-row decoding has to do
        // the index→value lookup explicitly because the underlying runtime
        // array is DictionaryArray, not the value-typed array the
        // BuildScalar arms cast to.
        if (array is DictionaryArray dict)
        {
            return ReadDictionaryColumn(dict, type, rowCount, arena);
        }

        DataValue[] raw = type.IsArray
            ? ReadArrayColumn(array, type, rowCount, arena)
            : ReadScalarColumn(array, type, rowCount, arena);

        // datumv.* metadata routing — lift each row from its on-disk Arrow
        // shape (byte-array / string) into the typed kind the column was
        // originally written as. Same convention open_parquet uses.
        if (type.MediaRouteKind is { } target)
        {
            return ApplyMediaRoute(raw, target, type.MediaRouteFormat, arena);
        }
        return raw;
    }

    /// <summary>
    /// Decodes a top-level Arrow <see cref="StructArray"/> into per-row
    /// <see cref="DataValue"/>s. Each child column is read independently
    /// (recursively, so a struct field that's itself a struct or list
    /// works); the per-row field arrays are then packaged via
    /// <see cref="DataValue.FromUntypedStruct"/>.
    /// </summary>
    private static DataValue[] ReadStructColumn(
        StructArray structArray, ArrowColumnType type, int rowCount, IValueStore arena)
    {
        if (type.StructChildren is not { } childFields)
        {
            throw new InvalidOperationException(
                "Arrow struct column has no child field metadata; " +
                "ArrowColumnType.From must populate StructChildren for Struct columns.");
        }

        int childCount = childFields.Count;
        DataValue[][] perChildRows = new DataValue[childCount][];
        for (int c = 0; c < childCount; c++)
        {
            // Each child's Apache.Arrow.Field carries its own datumv.*
            // metadata and shape, so we re-derive the ArrowColumnType per
            // child instead of inheriting from the parent.
            ArrowColumnType childType = ArrowColumnType.From(childFields[c]);
            if (!childType.IsSupported)
            {
                throw new InvalidOperationException(
                    $"Arrow struct field '{childFields[c].Name}' has unsupported type " +
                    $"({childType.LogicalTypeName}).");
            }
            perChildRows[c] = ReadAsRows(structArray.Fields[c], childType, rowCount, arena);
        }

        DataValue[] result = new DataValue[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            if (structArray.IsNull(r))
            {
                result[r] = DataValue.NullUntypedStruct();
                continue;
            }
            DataValue[] fields = new DataValue[childCount];
            for (int c = 0; c < childCount; c++)
            {
                fields[c] = perChildRows[c][r];
            }
            // FromUntypedStruct because the reader doesn't have a
            // TypeRegistry handle. SQL-level operators that need field
            // names recover them from the column's ColumnInfo.Fields
            // (set in OpenArrowFunction.BuildColumnInfo) rather than
            // from a per-value TypeId.
            result[r] = DataValue.FromUntypedStruct(fields, arena);
        }
        return result;
    }

    /// <summary>
    /// Decodes a dictionary-encoded column by resolving each row's index
    /// against the dictionary's value array, then routing the resolved
    /// value through the scalar decoder. Index nulls and dictionary-side
    /// nulls both surface as <see cref="DataValue.Null"/> of the surfaced
    /// kind. Dictionary-encoded array columns aren't surfaced here — the
    /// HF label-column case is scalar-typed and that's what v1 covers.
    /// </summary>
    private static DataValue[] ReadDictionaryColumn(
        DictionaryArray dict, ArrowColumnType type, int rowCount, IValueStore arena)
    {
        IArrowArray indices = dict.Indices;
        IArrowArray values = dict.Dictionary;
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (indices.IsNull(i))
            {
                result[i] = DataValue.Null(type.ElementKind);
                continue;
            }
            int valueIdx = ReadDictionaryIndex(indices, i);
            if (values.IsNull(valueIdx))
            {
                result[i] = DataValue.Null(type.ElementKind);
                continue;
            }
            result[i] = BuildScalar(values, valueIdx, type, arena);
        }
        // Run the same media route the non-dictionary path runs, so a
        // dictionary-encoded Json column (rare but valid) still gets
        // re-encoded to CBOR on import.
        if (type.MediaRouteKind is { } target)
        {
            return ApplyMediaRoute(result, target, type.MediaRouteFormat, arena);
        }
        return result;
    }

    /// <summary>
    /// Reads the dictionary index at row <paramref name="i"/> as an
    /// <see cref="int"/>. Arrow allows any signed/unsigned integer width
    /// for the index array; in practice HF datasets use Int32 / Int64 for
    /// label columns. UInt32 / UInt64 indices ≥ <see cref="int.MaxValue"/>
    /// would lose precision here — exceptional for dictionary columns
    /// (you'd need ≥ 2B unique values to hit it), so cast plainly rather
    /// than threading a long index everywhere.
    /// </summary>
    private static int ReadDictionaryIndex(IArrowArray indices, int i) => indices switch
    {
        Int8Array a => a.GetValue(i)!.Value,
        UInt8Array a => a.GetValue(i)!.Value,
        Int16Array a => a.GetValue(i)!.Value,
        UInt16Array a => a.GetValue(i)!.Value,
        Int32Array a => a.GetValue(i)!.Value,
        UInt32Array a => checked((int)a.GetValue(i)!.Value),
        Int64Array a => checked((int)a.GetValue(i)!.Value),
        UInt64Array a => checked((int)a.GetValue(i)!.Value),
        _ => throw new InvalidOperationException(
            $"Arrow dictionary column has unsupported index type {indices.GetType().Name}."),
    };

    private static DataValue[] ApplyMediaRoute(
        DataValue[] raw, DataKind target, string? format, IValueStore arena)
    {
        DataValue[] result = new DataValue[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i].IsNull)
            {
                result[i] = DataValue.Null(target);
                continue;
            }
            result[i] = target switch
            {
                // Byte-array carriers: the raw row is Array<UInt8> (Binary
                // on disk). Passthrough kinds retag without decoding; Mesh
                // and PointCloud round-trip through the importers that
                // mirror the writer's exporter conversion.
                DataKind.Image => DataValue.FromImage(raw[i].AsUInt8Array(arena), arena),
                DataKind.Audio => DataValue.FromAudio(raw[i].AsUInt8Array(arena), arena),
                DataKind.Video => DataValue.FromVideo(raw[i].AsUInt8Array(arena), arena),
                DataKind.Mesh => DataValue.FromMesh(GltfImporter.Import(raw[i].AsUInt8Array(arena)), arena),
                DataKind.PointCloud => DataValue.FromPointCloud(PlyImporter.Import(raw[i].AsUInt8Array(arena)), arena),

                // String-backed carriers. Each has no native Arrow type
                // in the .NET v23 builder surface (Arrow has DurationType
                // in the IPC spec but no .NET v23 builder; Uuid is an
                // extension type, not primitive; Int/UInt128 have no
                // Arrow primitive at all). The writer stringifies and
                // tags; we parse the string back here so the round-trip
                // restores the original kind, not "String".
                DataKind.Json => DataValue.FromJson(
                    CborJsonCodec.EncodeFromJsonText(raw[i].AsString(arena)), arena),
                DataKind.Uuid => DataValue.FromUuid(Guid.Parse(raw[i].AsString(arena))),
                DataKind.Duration => DataValue.FromDuration(
                    System.TimeSpan.Parse(raw[i].AsString(arena),
                        System.Globalization.CultureInfo.InvariantCulture)),
                // Interval rides Arrow's native MonthDayNanosecond type, so
                // raw[i] is already DataKind.Interval — passthrough. The
                // legacy string-tag form (from an older writer) would land
                // here as DataKind.String; parse it back to keep forward-
                // reading compatibility with files predating the native swap.
                DataKind.Interval => raw[i].Kind == DataKind.Interval
                    ? raw[i]
                    : DataValue.FromInterval(Interval.Parse(raw[i].AsString(arena))),
                DataKind.Int128 => DataValue.FromInt128(
                    System.Int128.Parse(raw[i].AsString(arena),
                        System.Globalization.CultureInfo.InvariantCulture)),
                DataKind.UInt128 => DataValue.FromUInt128(
                    System.UInt128.Parse(raw[i].AsString(arena),
                        System.Globalization.CultureInfo.InvariantCulture)),

                _ => raw[i],
            };
        }
        return result;
    }

    private static DataValue[] ReadScalarColumn(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (array.IsNull(i))
            {
                result[i] = DataValue.Null(type.ElementKind);
                continue;
            }
            result[i] = BuildScalar(array, i, type, arena);
        }
        return result;
    }

    private static DataValue[] ReadArrayColumn(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        // Array<Struct> needs the inner struct's child columns decoded
        // once for the whole batch rather than per-row, so dispatch
        // upfront. Both ListArray and FixedSizeListArray flavours
        // delegate to the same struct-aware helpers; the rest of the
        // element kinds (primitives, String) stay on the per-row
        // BuildArrayCell path that copies typed-span slices directly.
        if (type.ElementKind == DataKind.Struct)
        {
            if (array is ListArray listOfStruct)
            {
                return DecodeListOfStructRows(listOfStruct, type, rowCount, arena);
            }
            if (array is FixedSizeListArray fixedListOfStruct)
            {
                return DecodeFixedSizeListOfStructRows(fixedListOfStruct, type, rowCount, arena);
            }
        }

        if (array is ListArray listArray)
        {
            return DecodeListRows(listArray, type, rowCount, arena);
        }
        if (array is FixedSizeListArray fixedListArray)
        {
            return DecodeFixedSizeListRows(fixedListArray, type, rowCount, arena);
        }
        if (array is BinaryArray binaryArray)
        {
            // Binary at the top level holds per-row variable-length byte
            // buffers — the engine surfaces this as Array<UInt8>, the same
            // shape the columnar reader uses for ListArray<UInt8>.
            return DecodeBinaryRows(binaryArray, rowCount, arena);
        }
        throw new InvalidOperationException(
            $"Arrow array column has unexpected runtime type {array.GetType().Name}; " +
            "expected ListArray, FixedSizeListArray, or BinaryArray.");
    }

    /// <summary>
    /// Decodes a <c>ListArray&lt;StructArray&gt;</c> by reading each of
    /// the struct's child columns once for the whole batch, then slicing
    /// per-row into the per-element field arrays. Routing the work this
    /// way avoids the O(rows × elements × children) blowup the per-row
    /// BuildArrayCell path would have if it re-decoded every child for
    /// every row.
    /// </summary>
    private static DataValue[] DecodeListOfStructRows(
        ListArray listArray, ArrowColumnType type, int rowCount, IValueStore arena)
    {
        if (listArray.Values is not StructArray innerStruct)
        {
            throw new InvalidOperationException(
                $"Array<Struct> column expected StructArray inner values, got " +
                $"{listArray.Values.GetType().Name}.");
        }
        DataValue[][] perChildRows = PreDecodeStructChildren(innerStruct, type, arena);

        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (listArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(DataKind.Struct);
                continue;
            }
            int start = listArray.ValueOffsets[i];
            int end = listArray.ValueOffsets[i + 1];
            result[i] = BuildStructArrayCell(innerStruct, perChildRows, start, end - start, arena);
        }
        return result;
    }

    /// <summary>
    /// FixedSizeList variant of <see cref="DecodeListOfStructRows"/> —
    /// each row's per-element slice has constant length, so the offsets
    /// derive from <c>i × listSize</c> rather than from a value-offsets
    /// buffer.
    /// </summary>
    private static DataValue[] DecodeFixedSizeListOfStructRows(
        FixedSizeListArray fixedListArray, ArrowColumnType type, int rowCount, IValueStore arena)
    {
        if (fixedListArray.Values is not StructArray innerStruct)
        {
            throw new InvalidOperationException(
                $"Array<Struct> column expected StructArray inner values, got " +
                $"{fixedListArray.Values.GetType().Name}.");
        }
        int listSize = ((FixedSizeListType)fixedListArray.Data.DataType).ListSize;
        DataValue[][] perChildRows = PreDecodeStructChildren(innerStruct, type, arena);

        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (fixedListArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(DataKind.Struct);
                continue;
            }
            int start = i * listSize;
            result[i] = BuildStructArrayCell(innerStruct, perChildRows, start, listSize, arena);
        }
        return result;
    }

    /// <summary>
    /// Pre-decodes every child column of <paramref name="innerStruct"/>
    /// into a per-child <see cref="DataValue"/> array, indexed by the
    /// struct's flat element position. The list-row decoder then slices
    /// these per element rather than re-walking each child column per
    /// row.
    /// </summary>
    private static DataValue[][] PreDecodeStructChildren(
        StructArray innerStruct, ArrowColumnType type, IValueStore arena)
    {
        if (type.StructChildren is not { } childFields)
        {
            throw new InvalidOperationException(
                "Array<Struct> column has no child field metadata; " +
                "ArrowColumnType.From must populate StructChildren when the underlying " +
                "value type is StructType.");
        }
        DataValue[][] perChildRows = new DataValue[childFields.Count][];
        for (int c = 0; c < childFields.Count; c++)
        {
            ArrowColumnType childType = ArrowColumnType.From(childFields[c]);
            if (!childType.IsSupported)
            {
                throw new InvalidOperationException(
                    $"Array<Struct> child field '{childFields[c].Name}' has unsupported type " +
                    $"({childType.LogicalTypeName}).");
            }
            perChildRows[c] = ReadAsRows(innerStruct.Fields[c], childType, innerStruct.Length, arena);
        }
        return perChildRows;
    }

    /// <summary>
    /// Packages a per-row slice of pre-decoded struct child columns into
    /// an <c>Array&lt;Struct&gt;</c> <see cref="DataValue"/>. Each
    /// element's field array is built from the same slice index across
    /// every child, then the elements are emitted as an untyped struct
    /// array (no <see cref="Model.TypeRegistry"/> is available at the
    /// reader layer; SQL recovers field names from the column's
    /// <see cref="ColumnInfo.Fields"/>).
    /// </summary>
    private static DataValue BuildStructArrayCell(
        StructArray innerStruct,
        DataValue[][] perChildRows,
        int start,
        int length,
        IValueStore arena)
    {
        DataValue[][] elements = new DataValue[length][];
        int childCount = perChildRows.Length;
        for (int i = 0; i < length; i++)
        {
            int elementIdx = start + i;
            if (innerStruct.IsNull(elementIdx))
            {
                // List element NULL inside an Array<Struct>. The engine's
                // FromUntypedStructArray rejects nulls inside an array
                // today; surface a clear diagnostic so the issue points
                // at the right place instead of the deeper assertion.
                throw new InvalidOperationException(
                    $"Array<Struct> contains a NULL element at flat position {elementIdx}; " +
                    "per-element struct nulls inside an array aren't supported yet. " +
                    "Filter nulls out before exporting, or wrap into Struct{value, is_null}.");
            }
            DataValue[] fields = new DataValue[childCount];
            for (int c = 0; c < childCount; c++)
            {
                fields[c] = perChildRows[c][elementIdx];
            }
            elements[i] = fields;
        }
        return DataValue.FromUntypedStructArray(elements, arena);
    }

    private static DataValue[] DecodeBinaryRows(
        BinaryArray binaryArray, int rowCount, IValueStore arena)
    {
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (binaryArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(DataKind.UInt8);
                continue;
            }
            // GetBytes returns a span into the array's contiguous values
            // buffer; ToArray materialises the per-row slice into the
            // arena-tracked DataValue.
            result[i] = DataValue.FromByteArray(binaryArray.GetBytes(i).ToArray(), arena);
        }
        return result;
    }


    private static DataValue[] DecodeListRows(
        ListArray listArray,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (listArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(type.ElementKind);
                continue;
            }
            int start = listArray.ValueOffsets[i];
            int end = listArray.ValueOffsets[i + 1];
            result[i] = BuildArrayCell(listArray.Values, start, end - start, type, arena);
        }
        return result;
    }

    private static DataValue[] DecodeFixedSizeListRows(
        FixedSizeListArray fixedListArray,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        int listSize = ((FixedSizeListType)fixedListArray.Data.DataType).ListSize;
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (fixedListArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(type.ElementKind);
                continue;
            }
            int start = i * listSize;
            result[i] = BuildArrayCell(fixedListArray.Values, start, listSize, type, arena);
        }
        return result;
    }

    private static DataValue BuildScalar(IArrowArray array, int index, ArrowColumnType type, IValueStore arena)
    {
        return type.ElementKind switch
        {
            DataKind.Boolean => DataValue.FromBoolean(((BooleanArray)array).GetValue(index)!.Value),
            DataKind.Int8 => DataValue.FromInt8(((Int8Array)array).GetValue(index)!.Value),
            DataKind.UInt8 => DataValue.FromUInt8(((UInt8Array)array).GetValue(index)!.Value),
            DataKind.Int16 => DataValue.FromInt16(((Int16Array)array).GetValue(index)!.Value),
            DataKind.UInt16 => DataValue.FromUInt16(((UInt16Array)array).GetValue(index)!.Value),
            DataKind.Int32 => DataValue.FromInt32(((Int32Array)array).GetValue(index)!.Value),
            DataKind.UInt32 => DataValue.FromUInt32(((UInt32Array)array).GetValue(index)!.Value),
            DataKind.Int64 => DataValue.FromInt64(((Int64Array)array).GetValue(index)!.Value),
            DataKind.UInt64 => DataValue.FromUInt64(((UInt64Array)array).GetValue(index)!.Value),
            DataKind.Float32 when array is FloatArray fa => DataValue.FromFloat32(fa.GetValue(index)!.Value),
            DataKind.Float32 when array is HalfFloatArray hfa => DataValue.FromFloat32((float)hfa.GetValue(index)!.Value),
            DataKind.Float64 => DataValue.FromFloat64(((DoubleArray)array).GetValue(index)!.Value),
            DataKind.String => DataValue.FromString(((StringArray)array).GetString(index), arena),
            DataKind.Timestamp => DataValue.FromTimestamp(((TimestampArray)array).GetTimestamp(index)!.Value.UtcDateTime),
            DataKind.TimestampTz => DataValue.FromTimestampTz(((TimestampArray)array).GetTimestamp(index)!.Value),
            DataKind.Date when array is Date32Array d32 => DataValue.FromDate(DateOnly.FromDateTime(d32.GetDateTime(index)!.Value)),
            DataKind.Date when array is Date64Array d64 => DataValue.FromDate(DateOnly.FromDateTime(d64.GetDateTime(index)!.Value)),
            DataKind.Decimal when array is Decimal128Array dec => DataValue.FromDecimal(dec.GetValue(index)!.Value),
            DataKind.Interval when array is MonthDayNanosecondIntervalArray ia => BuildInterval(ia, index),
            _ => throw new InvalidOperationException(
                $"Arrow scalar element kind {type.ElementKind} not yet wired."),
        };
    }

    /// <summary>
    /// Decodes a native Arrow <c>MonthDayNanosecondInterval</c> value into
    /// our <see cref="Interval"/>. Nanoseconds widen down to microseconds
    /// (Arrow's wider precision is truncated — our payload is microsecond-
    /// granular).
    /// </summary>
    private static DataValue BuildInterval(MonthDayNanosecondIntervalArray array, int index)
    {
        Apache.Arrow.Scalars.MonthDayNanosecondInterval iv = array.GetValue(index)!.Value;
        long microseconds = iv.Nanoseconds / 1_000L;
        return DataValue.FromInterval(new Interval(iv.Months, iv.Days, microseconds));
    }

    private static DataValue BuildArrayCell(
        IArrowArray values,
        int start,
        int length,
        ArrowColumnType type,
        IValueStore arena)
    {
        switch (type.ElementKind)
        {
            case DataKind.Int8: return BuildPrimitive<sbyte>((Int8Array)values, start, length, DataKind.Int8, arena);
            case DataKind.UInt8: return BuildPrimitive<byte>((UInt8Array)values, start, length, DataKind.UInt8, arena);
            case DataKind.Int16: return BuildPrimitive<short>((Int16Array)values, start, length, DataKind.Int16, arena);
            case DataKind.UInt16: return BuildPrimitive<ushort>((UInt16Array)values, start, length, DataKind.UInt16, arena);
            case DataKind.Int32: return BuildPrimitive<int>((Int32Array)values, start, length, DataKind.Int32, arena);
            case DataKind.UInt32: return BuildPrimitive<uint>((UInt32Array)values, start, length, DataKind.UInt32, arena);
            case DataKind.Int64: return BuildPrimitive<long>((Int64Array)values, start, length, DataKind.Int64, arena);
            case DataKind.UInt64: return BuildPrimitive<ulong>((UInt64Array)values, start, length, DataKind.UInt64, arena);
            case DataKind.Float32 when values is FloatArray fa:
                return BuildPrimitive<float>(fa, start, length, DataKind.Float32, arena);
            case DataKind.Float64: return BuildPrimitive<double>((DoubleArray)values, start, length, DataKind.Float64, arena);
            case DataKind.String:
            {
                var strArr = (StringArray)values;
                string[] slice = new string[length];
                for (int i = 0; i < length; i++)
                {
                    slice[i] = strArr.GetString(start + i) ?? string.Empty;
                }
                return DataValue.FromStringArray(slice, arena);
            }
            case DataKind.Boolean:
            {
                var boolArr = (BooleanArray)values;
                byte[] packed = new byte[length];
                for (int i = 0; i < length; i++) packed[i] = boolArr.GetValue(start + i)!.Value ? (byte)1 : (byte)0;
                return DataValue.FromByteArray(packed, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Arrow array element kind {type.ElementKind} not yet wired.");
        }
    }

    private static DataValue BuildPrimitive<T>(
        PrimitiveArray<T> array,
        int start,
        int length,
        DataKind kind,
        IValueStore arena)
        where T : unmanaged, IEquatable<T>
    {
        T[] slice = new T[length];
        ReadOnlySpan<T> source = array.Values;
        source.Slice(start, length).CopyTo(slice);
        return DataValue.FromArenaArray<T>(slice, kind, arena);
    }
}
