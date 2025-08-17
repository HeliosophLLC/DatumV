using System.Globalization;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Shared type inference and conversion logic for JSON-based providers.
/// Used by <see cref="JsonTableProvider"/> for DOM-based JSON files and by
/// <see cref="JsonlTableProvider"/> for line-streaming newline-delimited JSON.
/// </summary>
internal static class JsonTypeInference
{
    /// <summary>
    /// Infers the <see cref="DataKind"/> for a JSON element value.
    /// Number values that represent integers are inferred as <see cref="DataKind.Int64"/>;
    /// numbers with fractional parts are inferred as <see cref="DataKind.Float64"/>.
    /// Boolean values are inferred as <see cref="DataKind.Boolean"/>.
    /// String values that parse as ISO 8601 dates are inferred as Date or DateTime.
    /// </summary>
    internal static DataKind InferKind(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out _) ? DataKind.Int64 : DataKind.Float64,
            JsonValueKind.String => InferStringKind(element.GetString()),
            JsonValueKind.True or JsonValueKind.False => DataKind.Boolean,
            JsonValueKind.Null or JsonValueKind.Undefined => DataKind.String,
            JsonValueKind.Array or JsonValueKind.Object => DataKind.JsonValue,
            _ => DataKind.String
        };
    }

    /// <summary>
    /// Attempts to narrow a string value to Date or DateTime via ISO 8601 parsing.
    /// </summary>
    private static DataKind InferStringKind(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DataKind.String;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.TimeOfDay != TimeSpan.Zero ? DataKind.DateTime : DataKind.Date;
        }

        return DataKind.String;
    }

    /// <summary>
    /// Converts a JSON element to a <see cref="DataValue"/> based on the target column kind.
    /// </summary>
    internal static DataValue ConvertElement(JsonElement element, DataKind targetKind)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return DataValue.Null(targetKind);
        }

        return targetKind switch
        {
            DataKind.Float32 => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromFloat32((float)element.GetDouble()),
                JsonValueKind.True => DataValue.FromFloat32(1f),
                JsonValueKind.False => DataValue.FromFloat32(0f),
                _ => DataValue.Null(DataKind.Float32)
            },
            DataKind.Float64 => element.ValueKind switch
            {
                JsonValueKind.Number => DataValue.FromFloat64(element.GetDouble()),
                JsonValueKind.True => DataValue.FromFloat64(1.0),
                JsonValueKind.False => DataValue.FromFloat64(0.0),
                _ => DataValue.Null(DataKind.Float64)
            },
            DataKind.Int64 => element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out long int64Value)
                    ? DataValue.FromInt64(int64Value)
                    : DataValue.FromInt64((long)element.GetDouble()),
                JsonValueKind.True => DataValue.FromInt64(1),
                JsonValueKind.False => DataValue.FromInt64(0),
                _ => DataValue.Null(DataKind.Int64)
            },
            DataKind.Boolean => element.ValueKind switch
            {
                JsonValueKind.True => DataValue.FromBoolean(true),
                JsonValueKind.False => DataValue.FromBoolean(false),
                JsonValueKind.Number => DataValue.FromBoolean(element.GetDouble() != 0),
                _ => DataValue.Null(DataKind.Boolean)
            },
            DataKind.Date when element.ValueKind == JsonValueKind.String =>
                DateOnly.TryParse(element.GetString(), CultureInfo.InvariantCulture, out DateOnly date)
                    ? DataValue.FromDate(date)
                    : DataValue.Null(DataKind.Date),
            DataKind.DateTime when element.ValueKind == JsonValueKind.String =>
                DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset dateTime)
                    ? DataValue.FromDateTime(dateTime)
                    : DataValue.Null(DataKind.DateTime),
            DataKind.String => DataValue.FromString(element.GetString() ?? element.GetRawText()),
            DataKind.JsonValue => DataValue.FromJsonValue(element.GetRawText()),
            _ => DataValue.FromString(element.GetRawText())
        };
    }

    /// <summary>
    /// Resolves a type conflict between two <see cref="DataKind"/> values during
    /// schema inference. Numeric kinds widen: Int64 + Float64 → Float64, Boolean + numeric → numeric.
    /// Date + DateTime widens to DateTime. If either kind is
    /// <see cref="DataKind.JsonValue"/>, the result is <see cref="DataKind.JsonValue"/>;
    /// otherwise, widens to <see cref="DataKind.String"/>.
    /// </summary>
    internal static DataKind WidenKind(DataKind existingKind, DataKind detectedKind)
    {
        if (existingKind == detectedKind)
        {
            return existingKind;
        }

        // Numeric widening: Int64 + Float64 → Float64, Boolean + numeric → numeric.
        if (IsNumericOrBoolean(existingKind) && IsNumericOrBoolean(detectedKind))
        {
            if (existingKind is DataKind.Float64 || detectedKind is DataKind.Float64)
            {
                return DataKind.Float64;
            }

            if (existingKind is DataKind.Float32 || detectedKind is DataKind.Float32)
            {
                return DataKind.Float64;
            }

            // Boolean + Int64 → Int64, Int64 + Int64 already handled above.
            return DataKind.Int64;
        }

        // Date + DateTime → DateTime (widen to include time component).
        if ((existingKind is DataKind.Date && detectedKind is DataKind.DateTime) ||
            (existingKind is DataKind.DateTime && detectedKind is DataKind.Date))
        {
            return DataKind.DateTime;
        }

        if (existingKind == DataKind.JsonValue || detectedKind == DataKind.JsonValue)
        {
            return DataKind.JsonValue;
        }

        return DataKind.String;
    }

    /// <summary>
    /// Returns whether the kind is a numeric type or boolean (which can widen to numeric).
    /// </summary>
    private static bool IsNumericOrBoolean(DataKind kind)
    {
        return kind is DataKind.Boolean or DataKind.Int64 or DataKind.Float32 or DataKind.Float64;
    }
}
