using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Per-kind sign predicates shared by <see cref="AssertPositiveFunction"/>
/// and <see cref="AssertNonNegativeFunction"/>. Dispatches per
/// <see cref="DataKind"/> so the comparison runs at full precision —
/// folding through <see cref="double"/> would lose precision for the wide
/// integer kinds (Int128/UInt128) and Decimal.
/// </summary>
internal static class NumericSign
{
    /// <summary>True when <paramref name="value"/> is strictly &gt; 0 at its native precision.</summary>
    internal static bool IsPositive(ValueRef value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8() > 0,
        DataKind.UInt8 => value.AsUInt8() > 0,
        DataKind.Int16 => value.AsInt16() > 0,
        DataKind.UInt16 => value.AsUInt16() > 0,
        DataKind.Int32 => value.AsInt32() > 0,
        DataKind.UInt32 => value.AsUInt32() > 0,
        DataKind.Int64 => value.AsInt64() > 0,
        DataKind.UInt64 => value.AsUInt64() > 0,
        DataKind.Int128 => value.AsInt128() > 0,
        DataKind.UInt128 => value.AsUInt128() > 0,
        DataKind.Float16 => value.AsFloat16() > Half.Zero,
        DataKind.Float32 => value.AsFloat32() > 0f,
        DataKind.Float64 => value.AsFloat64() > 0d,
        DataKind.Decimal => value.AsDecimal() > 0m,
        _ => false,
    };

    /// <summary>True when <paramref name="value"/> is &gt;= 0 at its native precision.</summary>
    internal static bool IsNonNegative(ValueRef value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8() >= 0,
        DataKind.UInt8 => true,
        DataKind.Int16 => value.AsInt16() >= 0,
        DataKind.UInt16 => true,
        DataKind.Int32 => value.AsInt32() >= 0,
        DataKind.UInt32 => true,
        DataKind.Int64 => value.AsInt64() >= 0,
        DataKind.UInt64 => true,
        DataKind.Int128 => value.AsInt128() >= 0,
        DataKind.UInt128 => true,
        DataKind.Float16 => value.AsFloat16() >= Half.Zero,
        DataKind.Float32 => value.AsFloat32() >= 0f,
        DataKind.Float64 => value.AsFloat64() >= 0d,
        DataKind.Decimal => value.AsDecimal() >= 0m,
        _ => false,
    };
}
