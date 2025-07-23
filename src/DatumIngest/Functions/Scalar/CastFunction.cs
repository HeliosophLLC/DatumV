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

        return (input.Kind, targetKind) switch
        {
            // UInt8 -> Scalar
            (DataKind.UInt8, DataKind.Scalar) => DataValue.FromScalar(input.AsUInt8()),

            // Scalar -> UInt8 (truncate)
            (DataKind.Scalar, DataKind.UInt8) => DataValue.FromUInt8((byte)System.Math.Clamp(input.AsScalar(), 0, 255)),

            // String -> Scalar
            (DataKind.String, DataKind.Scalar) => DataValue.FromScalar(
                float.Parse(input.AsString(), CultureInfo.InvariantCulture)),

            // Scalar -> String
            (DataKind.Scalar, DataKind.String) => DataValue.FromString(
                input.AsScalar().ToString(CultureInfo.InvariantCulture)),

            // UInt8 -> String
            (DataKind.UInt8, DataKind.String) => DataValue.FromString(
                input.AsUInt8().ToString(CultureInfo.InvariantCulture)),

            // String -> UInt8
            (DataKind.String, DataKind.UInt8) => DataValue.FromUInt8(
                byte.Parse(input.AsString(), CultureInfo.InvariantCulture)),

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
            (DataKind.Date, DataKind.Scalar) => DataValue.FromScalar(
                input.AsDate().DayNumber - DateOnly.FromDateTime(DateTimeOffset.UnixEpoch.DateTime).DayNumber),

            // DateTime -> Scalar (epoch seconds since 1970-01-01T00:00:00Z)
            (DataKind.DateTime, DataKind.Scalar) => DataValue.FromScalar(
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

            // Boolean -> Scalar (true=1, false=0)
            (DataKind.Boolean, DataKind.Scalar) => DataValue.FromScalar(
                input.AsBoolean() ? 1f : 0f),

            // Scalar -> Boolean (nonzero=true)
            (DataKind.Scalar, DataKind.Boolean) => DataValue.FromBoolean(
                input.AsScalar() != 0f),

            // Boolean -> UInt8 (true=1, false=0)
            (DataKind.Boolean, DataKind.UInt8) => DataValue.FromUInt8(
                (byte)(input.AsBoolean() ? 1 : 0)),

            // UInt8 -> Boolean (nonzero=true)
            (DataKind.UInt8, DataKind.Boolean) => DataValue.FromBoolean(
                input.AsUInt8() != 0),

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
}
