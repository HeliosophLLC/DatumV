using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Explicit type conversion between DataKind types.
/// <c>cast(val, targetKind)</c> where targetKind is a string name of the target DataKind.
/// </summary>
public sealed class CastFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "cast";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("cast() requires exactly 2 arguments: value and target kind name (as String).");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException("cast() second argument must be String (the target kind name).");
        }

        // Cannot determine the output kind statically without the actual string value.
        // Return String as a placeholder; the actual kind is determined at runtime.
        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        string targetKindName = arguments[1].AsString();

        if (!Enum.TryParse<DataKind>(targetKindName, ignoreCase: true, out DataKind targetKind))
        {
            // Accept common aliases that don't match enum names.
            if (string.Equals(targetKindName, "bool", StringComparison.OrdinalIgnoreCase))
            {
                targetKind = DataKind.Boolean;
            }
            else if (string.Equals(targetKindName, "time", StringComparison.OrdinalIgnoreCase))
            {
                targetKind = DataKind.Time;
            }
            else if (string.Equals(targetKindName, "duration", StringComparison.OrdinalIgnoreCase))
            {
                targetKind = DataKind.Duration;
            }
            else if (string.Equals(targetKindName, "scalar", StringComparison.OrdinalIgnoreCase))
            {
                targetKind = DataKind.Float32;
            }
            else
            {
                throw new ArgumentException($"Unknown target kind '{targetKindName}'.");
            }
        }

        if (input.IsNull)
        {
            return DataValue.Null(targetKind);
        }

        // Same kind — return as-is.
        if (input.Kind == targetKind)
        {
            return input;
        }

        // ── Numeric ↔ numeric: use double as a common intermediate. ──────────
        // Covers Int8/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float32/Float64/UInt8
        // in any combination, including widening and narrowing casts.
        if (TryExtractAsDouble(input, out double inputAsDouble))
        {
            if (TryMakeNumeric(inputAsDouble, targetKind) is { } numericResult)
            {
                return numericResult;
            }

            // Numeric → Boolean (non-zero = true).
            if (targetKind == DataKind.Boolean)
            {
                return DataValue.FromBoolean(inputAsDouble != 0.0);
            }

            // Numeric → String: format via the native type to preserve integer precision.
            if (targetKind == DataKind.String)
            {
                return DataValue.FromString(FormatNumericAsString(input));
            }

            // Any remaining numeric-source cases (e.g. Float32 → Time) fall through
            // to the semantic-conversion switch below.
        }

        // Boolean → any numeric type (true=1, false=0).
        if (input.Kind == DataKind.Boolean && IsNumericKind(targetKind))
        {
            return TryMakeNumeric(input.AsBoolean() ? 1.0 : 0.0, targetKind)
                ?? throw new InvalidOperationException($"Internal error: TryMakeNumeric returned null for numeric kind {targetKind}.");
        }

        // String → any numeric type.
        if (input.Kind == DataKind.String && IsNumericKind(targetKind))
        {
            return ParseStringToNumeric(input.AsString(), targetKind);
        }

        return (input.Kind, targetKind) switch
        {
            // Date -> DateTime
            (DataKind.Date, DataKind.DateTime) => DataValue.FromDateTime(
                new DateTimeOffset(input.AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),

            // DateTime -> Date
            (DataKind.DateTime, DataKind.Date) => DataValue.FromDate(
                DateOnly.FromDateTime(input.AsDateTime().DateTime)),

            // String -> Date
            (DataKind.String, DataKind.Date) => DataValue.FromDate(
                DateOnly.Parse(input.AsString(), CultureInfo.InvariantCulture)),

            // String -> DateTime
            (DataKind.String, DataKind.DateTime) => DataValue.FromDateTime(
                DateTimeOffset.Parse(input.AsString(), CultureInfo.InvariantCulture)),

            // Date -> String
            (DataKind.Date, DataKind.String) => DataValue.FromString(
                input.AsDate().ToString("O", CultureInfo.InvariantCulture)),

            // DateTime -> String
            (DataKind.DateTime, DataKind.String) => DataValue.FromString(
                input.AsDateTime().ToString("O", CultureInfo.InvariantCulture)),

            // Date -> Scalar (epoch days since 1970-01-01)
            (DataKind.Date, DataKind.Float32) => DataValue.FromFloat32(
                input.AsDate().DayNumber - DateOnly.FromDateTime(DateTimeOffset.UnixEpoch.DateTime).DayNumber),

            // DateTime -> Scalar (epoch seconds since 1970-01-01T00:00:00Z)
            (DataKind.DateTime, DataKind.Float32) => DataValue.FromFloat32(
                (float)(input.AsDateTime().ToUniversalTime() - DateTimeOffset.UnixEpoch).TotalSeconds),

            // String -> JsonValue
            (DataKind.String, DataKind.JsonValue) => DataValue.FromJsonValue(input.AsString()),

            // JsonValue -> String
            (DataKind.JsonValue, DataKind.String) => DataValue.FromString(input.AsJsonValue()),

            // UInt8Array -> Image (reinterpret bytes as image)
            (DataKind.UInt8Array, DataKind.Image) => DataValue.FromImage(input.AsUInt8Array()),

            // Image -> UInt8Array (reinterpret image as bytes)
            (DataKind.Image, DataKind.UInt8Array) => DataValue.FromUInt8Array(input.AsImage()),

            // String -> Uuid
            (DataKind.String, DataKind.Uuid) => DataValue.FromUuid(
                Guid.Parse(input.AsString())),

            // Uuid -> String
            (DataKind.Uuid, DataKind.String) => DataValue.FromString(
                input.AsUuid().ToString("D")),

            // String -> Boolean
            (DataKind.String, DataKind.Boolean) => ParseStringToBoolean(input.AsString()),

            // Boolean -> String
            (DataKind.Boolean, DataKind.String) => DataValue.FromString(
                input.AsBoolean() ? "true" : "false"),

            // String -> Time
            (DataKind.String, DataKind.Time) => DataValue.FromTime(
                TimeOnly.Parse(input.AsString(), CultureInfo.InvariantCulture)),

            // Time -> String
            (DataKind.Time, DataKind.String) => DataValue.FromString(
                input.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),

            // DateTime -> Time (extract time component)
            (DataKind.DateTime, DataKind.Time) => DataValue.FromTime(
                TimeOnly.FromTimeSpan(input.AsDateTime().TimeOfDay)),

            // Time -> Scalar (seconds since midnight)
            (DataKind.Time, DataKind.Float32) => DataValue.FromFloat32(
                (float)(input.AsTime().Hour * 3600 + input.AsTime().Minute * 60 + input.AsTime().Second + input.AsTime().Millisecond / 1000.0)),

            // Scalar -> Time (seconds since midnight)
            (DataKind.Float32, DataKind.Time) => DataValue.FromTime(
                TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(input.AsFloat32()))),

            // String -> Duration
            (DataKind.String, DataKind.Duration) => DataValue.FromDuration(
                TimeSpan.Parse(input.AsString(), CultureInfo.InvariantCulture)),

            // Duration -> String
            (DataKind.Duration, DataKind.String) => DataValue.FromString(
                input.AsDuration().ToString("c")),

            // Duration -> Scalar (total seconds)
            (DataKind.Duration, DataKind.Float32) => DataValue.FromFloat32(
                (float)input.AsDuration().TotalSeconds),

            // Scalar -> Duration (seconds)
            (DataKind.Float32, DataKind.Duration) => DataValue.FromDuration(
                TimeSpan.FromSeconds(input.AsFloat32())),

            _ => throw new InvalidOperationException(
                $"cast() does not support conversion from {input.Kind} to {targetKind}."),
        };
    }

    private static DataValue ParseStringToBoolean(string value)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || value == "1")
        {
            return DataValue.FromBoolean(true);
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || value == "0")
        {
            return DataValue.FromBoolean(false);
        }

        throw new InvalidOperationException(
            $"Cannot convert string '{value}' to Boolean. Expected 'true', 'false', '1', or '0'.");
    }

    /// <summary>
    /// Returns true when <paramref name="kind"/> is one of the ten numeric DataKinds:
    /// Int8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, UInt8.
    /// </summary>
    private static bool IsNumericKind(DataKind kind)
    {
        return kind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64 or DataKind.UInt8;
    }

    private static bool TryExtractAsDouble(DataValue value, out double result) =>
        value.TryToDouble(out result);

    /// <summary>
    /// Creates a <see cref="DataValue"/> of the given numeric <paramref name="targetKind"/>
    /// from a double intermediate.  Returns null if <paramref name="targetKind"/> is not numeric.
    /// UInt8 saturates at [0, 255]; all other integer types use truncating cast.
    /// </summary>
    private static DataValue? TryMakeNumeric(double value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Int8 => DataValue.FromInt8((sbyte)value),
            DataKind.Int16 => DataValue.FromInt16((short)value),
            DataKind.UInt16 => DataValue.FromUInt16((ushort)value),
            DataKind.Int32 => DataValue.FromInt32((int)value),
            DataKind.UInt32 => DataValue.FromUInt32((uint)value),
            DataKind.Int64 => DataValue.FromInt64((long)value),
            DataKind.UInt64 => DataValue.FromUInt64((ulong)value),
            DataKind.Float32 => DataValue.FromFloat32((float)value),
            DataKind.Float64 => DataValue.FromFloat64(value),
            DataKind.UInt8 => DataValue.FromUInt8((byte)System.Math.Clamp(value, 0.0, 255.0)),
            _ => null,
        };
    }

    /// <summary>
    /// Formats a numeric <see cref="DataValue"/> as a string using its native type,
    /// so integers are not rendered with a decimal point.
    /// </summary>
    private static string FormatNumericAsString(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Int8 => value.AsInt8().ToString(CultureInfo.InvariantCulture),
            DataKind.Int16 => value.AsInt16().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt16 => value.AsUInt16().ToString(CultureInfo.InvariantCulture),
            DataKind.Int32 => value.AsInt32().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt32 => value.AsUInt32().ToString(CultureInfo.InvariantCulture),
            DataKind.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt64 => value.AsUInt64().ToString(CultureInfo.InvariantCulture),
            DataKind.Float32 => value.AsFloat32().ToString(CultureInfo.InvariantCulture),
            DataKind.Float64 => value.AsFloat64().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt8 => value.AsUInt8().ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Not a numeric kind: {value.Kind}."),
        };
    }

    /// <summary>
    /// Parses <paramref name="value"/> as the given numeric <paramref name="targetKind"/>
    /// using culture-invariant, type-specific parsing to preserve full precision.
    /// </summary>
    private static DataValue ParseStringToNumeric(string value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Int8 => DataValue.FromInt8(sbyte.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.Int16 => DataValue.FromInt16(short.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.UInt16 => DataValue.FromUInt16(ushort.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.Int32 => DataValue.FromInt32(int.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.UInt32 => DataValue.FromUInt32(uint.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.Int64 => DataValue.FromInt64(long.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.UInt64 => DataValue.FromUInt64(ulong.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.Float32 => DataValue.FromFloat32(float.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.Float64 => DataValue.FromFloat64(double.Parse(value, CultureInfo.InvariantCulture)),
            DataKind.UInt8 => DataValue.FromUInt8(byte.Parse(value, CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException($"Not a numeric kind: {targetKind}."),
        };
    }
}
