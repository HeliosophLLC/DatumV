using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Bit-flag groups of <see cref="DataKind"/> values used by
/// <see cref="DataKindMatcher.Family"/> and <see cref="ReturnTypeRule"/>
/// helpers. Lets a function's signature express "any integer", "any
/// numeric scalar", or "any kind" without enumerating individual kinds
/// at every call site.
/// </summary>
[Flags]
public enum DataKindFamily : ulong
{
    /// <summary>Empty set; matches no kind.</summary>
    None = 0,

    /// <summary>Signed and unsigned integer scalar kinds.</summary>
    IntegerFamily =
        (1UL << (int)DataKind.Int8) |
        (1UL << (int)DataKind.UInt8) |
        (1UL << (int)DataKind.Int16) |
        (1UL << (int)DataKind.UInt16) |
        (1UL << (int)DataKind.Int32) |
        (1UL << (int)DataKind.UInt32) |
        (1UL << (int)DataKind.Int64) |
        (1UL << (int)DataKind.UInt64),

    /// <summary>Floating-point scalar kinds.</summary>
    FloatFamily =
        (1UL << (int)DataKind.Float32) |
        (1UL << (int)DataKind.Float64),

    /// <summary>All numeric scalar kinds (integer + float).</summary>
    NumericScalar = IntegerFamily | FloatFamily,

    /// <summary>Temporal kinds (date / time / timestamp / timestamptz / duration).</summary>
    Temporal =
        (1UL << (int)DataKind.Date) |
        (1UL << (int)DataKind.Timestamp) |
        (1UL << (int)DataKind.TimestampTz) |
        (1UL << (int)DataKind.Time) |
        (1UL << (int)DataKind.Duration),

    /// <summary>Text-like kinds.</summary>
    TextLike =
        (1UL << (int)DataKind.String),

    /// <summary>Sentinel matching every kind.</summary>
    AnyKind = ulong.MaxValue,
}

/// <summary>Helpers for testing membership in a <see cref="DataKindFamily"/>.</summary>
internal static class DataKindFamilyExtensions
{
    /// <summary>Returns true when <paramref name="kind"/> is a member of the family.</summary>
    public static bool Contains(this DataKindFamily family, DataKind kind)
    {
        if (family == DataKindFamily.AnyKind)
        {
            return true;
        }
        int slot = (int)kind;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }
        return ((ulong)family & (1UL << slot)) != 0;
    }
}
