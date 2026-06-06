using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Shared implementation for <see cref="ToHexFunction"/>,
/// <see cref="ToBinFunction"/>, and <see cref="ToOctFunction"/>. Converts an
/// integer <see cref="ValueRef"/> to a base-2/8/16 string using
/// two's-complement representation for negative values (matching PG
/// behaviour: the bit width tracks the input kind, so e.g.
/// <c>to_hex(-1::int)</c> returns <c>'ffffffff'</c> and
/// <c>to_hex(-1::bigint)</c> returns <c>'ffffffffffffffff'</c>).
/// </summary>
internal static class BaseConversion
{
    public static ValueRef Run(ValueRef value, int radix, string functionName)
    {
        if (value.IsNull)
        {
            return ValueRef.Null(DataKind.String);
        }

        // Width depends on the input kind: 8/16/32/64 bits. Convert.ToString
        // accepts radix 2/8/16 and treats Int64 as a 64-bit pattern when the
        // value is non-negative. For negative values we mask to the proper
        // width to emit unsigned two's-complement.
        return value.Kind switch
        {
            DataKind.Int8 => ValueRef.FromString(Format((byte)value.AsInt8(), radix)),
            DataKind.UInt8 => ValueRef.FromString(Format(value.AsUInt8(), radix)),
            DataKind.Int16 => ValueRef.FromString(Format((ushort)value.AsInt16(), radix)),
            DataKind.UInt16 => ValueRef.FromString(Format(value.AsUInt16(), radix)),
            DataKind.Int32 => ValueRef.FromString(Format((uint)value.AsInt32(), radix)),
            DataKind.UInt32 => ValueRef.FromString(Format(value.AsUInt32(), radix)),
            DataKind.Int64 => ValueRef.FromString(Format((ulong)value.AsInt64(), radix)),
            DataKind.UInt64 => ValueRef.FromString(Format(value.AsUInt64(), radix)),
            _ => throw new FunctionArgumentException(functionName, $"unsupported integer kind {value.Kind}."),
        };
    }

    private static string Format(ulong unsignedValue, int radix)
    {
        if (unsignedValue == 0)
        {
            return "0";
        }
        if (radix == 16)
        {
            return unsignedValue.ToString("x");
        }
        // For radix 2 and 8 there is no direct ulong overload; build manually.
        Span<char> buffer = stackalloc char[64];
        int i = buffer.Length;
        while (unsignedValue > 0)
        {
            int digit = (int)(unsignedValue % (ulong)radix);
            buffer[--i] = (char)('0' + digit);
            unsignedValue /= (ulong)radix;
        }
        return new string(buffer[i..]);
    }
}
