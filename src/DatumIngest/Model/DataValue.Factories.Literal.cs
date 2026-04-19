namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    // ───────────────────────── Literal conversion ─────────────────────────

    /// <summary>
    /// Converts a CLR literal value (typically from an AST <see cref="Parsing.Ast.LiteralExpression"/>)
    /// to a <see cref="DataValue"/> using the natural type mapping.
    /// </summary>
    /// <param name="rawLiteral">
    /// A boxed CLR value: <see cref="double"/> (from the SQL parser), <see cref="int"/>,
    /// <see cref="long"/>, <see cref="float"/> (from rewriters), <see cref="string"/>,
    /// <see cref="bool"/>, or an existing <see cref="DataValue"/>.
    /// </param>
    /// <param name="store">Store for reference-type payloads (strings, etc.).</param>
    /// <returns>A <see cref="DataValue"/> preserving the CLR type's natural precision.</returns>
    /// <exception cref="ArgumentException">The literal type is not supported.</exception>
    public static DataValue FromLiteral(object rawLiteral, IValueStore store)
    {
        return rawLiteral switch
        {
            DataValue dataValue => dataValue,
            sbyte int8Value => FromInt8(int8Value),
            short int16Value => FromInt16(int16Value),
            int intValue => FromInt32(intValue),
            long longValue => FromInt64(longValue),
            float floatValue => FromFloat32(floatValue),
            double doubleValue => FromFloat64(doubleValue),
            decimal decimalValue => FromFloat64((double)decimalValue),
            string stringValue => FromString(stringValue, store),
            bool boolValue => FromBoolean(boolValue),
            _ => throw new ArgumentException(
                $"Unsupported literal type: {rawLiteral.GetType().Name}.", nameof(rawLiteral)),
        };
    }

    // /// <summary>
    // /// Converts a CLR literal value to a <see cref="DataValue"/>.
    // /// </summary>
    // /// <remarks>Note: string literals require a store. Use <see cref="FromLiteral(object, IValueStore)"/> for string literals.</remarks>
    // public static DataValue FromLiteral(object rawLiteral)
    // {
    //     throw new InvalidOperationException(
    //         "Use FromLiteral(rawLiteral, store) for string literals. ReferenceStore is no longer available.");
    // }

    /// <summary>
    /// Maps a CLR <see cref="Type"/> to the corresponding <see cref="DataKind"/>.
    /// Unwraps <see cref="Nullable{T}"/> automatically. Falls back to
    /// <see cref="DataKind.String"/> for unrecognised types.
    /// </summary>
    public static DataKind MapClrType(Type clrType) => DataValueComparer.MapClrType(clrType);

    /// <summary>
    /// Coerces this value to a different <see cref="DataKind"/>. Used when the column's
    /// stored type differs from the literal's natural type (e.g. a <see cref="DataKind.Float64"/>
    /// literal compared against a <see cref="DataKind.Int32"/> bitmap index key).
    /// </summary>
    /// <remarks>
    /// Numeric and boolean values are coerced via a <see cref="double"/> intermediary.
    /// Non-numeric/non-boolean values, or values whose kind already matches
    /// <paramref name="targetKind"/>, are returned unchanged.
    /// </remarks>
    /// <param name="targetKind">The desired <see cref="DataKind"/>.</param>
    /// <returns>A new value of the target kind, or this value unchanged if coercion is not applicable.</returns>
    public DataValue CoerceToKind(DataKind targetKind)
    {
        if (_kind == targetKind)
        {
            return this;
        }

        if (!IsCoercibleKind(_kind) || !IsCoercibleKind(targetKind))
        {
            return this;
        }

        double intermediate = ToDoubleRaw();
        return FromDoubleRaw(intermediate, targetKind);
    }

    /// <summary>
    /// Returns whether the given kind participates in numeric/boolean coercion.
    /// </summary>
    private static bool IsCoercibleKind(DataKind kind)
    {
        return kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Decimal
            or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Int128 or DataKind.UInt128
            or DataKind.Boolean;
    }

    /// <summary>
    /// Extracts the numeric payload as a <see cref="double"/> for coercion purposes.
    /// </summary>
    private double ToDoubleRaw()
    {
        return _kind switch
        {
            DataKind.Float16 => (double)BitConverter.UInt16BitsToHalf((ushort)_p0),
            DataKind.Float32 => BitConverter.Int32BitsToSingle(_p0),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(ReadLong()),
            DataKind.Decimal => (double)AsDecimal(),
            DataKind.UInt8 => (byte)_p0,
            DataKind.Int8 => (sbyte)_p0,
            DataKind.Int16 => (short)_p0,
            DataKind.UInt16 => (ushort)_p0,
            DataKind.Int32 => _p0,
            DataKind.UInt32 => (uint)_p0,
            DataKind.Int64 => ReadLong(),
            DataKind.UInt64 => (ulong)ReadLong(),
            DataKind.Int128 => (double)AsInt128(),
            DataKind.UInt128 => (double)AsUInt128(),
            DataKind.Boolean => _p0 != 0 ? 1d : 0d,
            _ => 0d,
        };
    }
    /// <summary>
    /// Creates a <see cref="DataValue"/> of the specified kind from a <see cref="double"/> value.
    /// </summary>
    private static DataValue FromDoubleRaw(double value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Float16 => FromFloat16((Half)value),
            DataKind.Float32 => FromFloat32((float)value),
            DataKind.Float64 => FromFloat64(value),
            DataKind.Decimal => FromDecimal((decimal)value),
            DataKind.UInt8 => FromUInt8((byte)value),
            DataKind.Int8 => FromInt8((sbyte)value),
            DataKind.Int16 => FromInt16((short)value),
            DataKind.UInt16 => FromUInt16((ushort)value),
            DataKind.Int32 => FromInt32((int)value),
            DataKind.UInt32 => FromUInt32((uint)value),
            DataKind.Int64 => FromInt64((long)value),
            DataKind.UInt64 => FromUInt64((ulong)value),
            DataKind.Int128 => FromInt128((Int128)value),
            DataKind.UInt128 => FromUInt128((UInt128)value),
            DataKind.Boolean => FromBoolean(value != 0d),
            _ => throw new ArgumentException(
                $"Cannot coerce to non-numeric kind {targetKind}.", nameof(targetKind)),
        };
    }
}
