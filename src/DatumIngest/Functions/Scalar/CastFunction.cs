using System.Globalization;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Explicit type conversion between <see cref="DataKind"/> values.
/// <c>cast(value, type)</c> where <c>type</c> is either a <see cref="DataKind.Type"/>
/// literal (e.g. <c>cast(x, Int32)</c>) or a <see cref="DataKind.String"/> name
/// (e.g. the desugared form of <c>CAST(x AS Int32)</c>).
/// </summary>
/// <remarks>
/// <para>
/// The starting set of supported pairs covers what the model-orchestration
/// demos need: numeric ↔ numeric (widening + narrowing), numeric ↔ string,
/// boolean ↔ numeric/string, and string ↔ Date/DateTime/Time/Duration/Uuid.
/// Add additional pairs demand-pulled when a query needs them; throw is the
/// signal a pair isn't yet supported.
/// </para>
/// </remarks>
public sealed class CastFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cast";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Converts a value to the given target kind. Throws on unsupported pairs; "
        + "use try_cast for a null-on-failure variant.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec(
                    "target",
                    DataKindMatcher.OneOf(DataKind.Type, DataKind.String)),
            ],
            VariadicTrailing: null,
            // Result kind is data-dependent — only known once we read the
            // target literal at runtime — so the static signature reports
            // String as a placeholder. The actual emitted DataValue carries
            // the runtime target kind regardless.
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CastFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        DataKind targetKind = ResolveTargetKind(arguments[1]);

        if (input.IsNull)
        {
            return ValueRef.Null(targetKind);
        }

        if (input.Kind == targetKind)
        {
            return input;
        }

        if (TryCastCore(input, targetKind, out ValueRef result))
        {
            return result;
        }

        throw new InvalidOperationException(
            $"cast() does not support conversion from {input.Kind} to {targetKind}.");
    }

    /// <summary>
    /// Reads the target kind from the second argument. Accepts either a
    /// <see cref="DataKind.Type"/> literal or a <see cref="DataKind.String"/>
    /// name (with a few common aliases like <c>bool</c>).
    /// </summary>
    internal static DataKind ResolveTargetKind(ValueRef target)
    {
        string targetName = target.Kind == DataKind.Type
            ? target.AsType().ToString()
            : target.AsString();

        if (Enum.TryParse(targetName, ignoreCase: true, out DataKind kind))
        {
            return kind;
        }

        return targetName.ToLowerInvariant() switch
        {
            "bool" => DataKind.Boolean,
            "scalar" => DataKind.Float32,
            _ => throw new ArgumentException($"Unknown target kind '{targetName}'."),
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> with a populated <paramref name="result"/>
    /// when the conversion succeeds. Returns <see langword="false"/> for
    /// kind pairs that aren't supported (the caller decides whether to throw
    /// or produce a typed null).
    /// </summary>
    internal static bool TryCastCore(ValueRef input, DataKind targetKind, out ValueRef result)
    {
        result = default;

        // Numeric ↔ numeric / Numeric ↔ Boolean / Numeric → String
        if (input.TryToDouble(out double inputDouble))
        {
            if (TryMakeNumeric(inputDouble, targetKind, out result))
            {
                return true;
            }

            if (targetKind == DataKind.Boolean)
            {
                result = ValueRef.FromBoolean(inputDouble != 0.0);
                return true;
            }

            if (targetKind == DataKind.String)
            {
                result = ValueRef.FromString(FormatNumericAsString(input));
                return true;
            }
        }

        // Boolean → numeric. (TryToDouble above handles Boolean as 0/1, so this
        // is currently unreachable, but kept for explicitness.) On failure we
        // fall through to the switch so e.g. (Boolean, String) below still wins.
        if (input.Kind == DataKind.Boolean
            && DataValueComparer.IsNumericScalar(targetKind)
            && TryMakeNumeric(input.AsBoolean() ? 1.0 : 0.0, targetKind, out result))
        {
            return true;
        }

        // String → numeric. Falls through to the switch on parse failure so the
        // explicit (String, Boolean) case below — which IsNumericScalar matches
        // but TryParseStringToNumeric does not — still wins.
        if (input.Kind == DataKind.String
            && DataValueComparer.IsNumericScalar(targetKind)
            && TryParseStringToNumeric(input.AsString(), targetKind, out result))
        {
            return true;
        }

        switch ((input.Kind, targetKind))
        {
            case (DataKind.Date, DataKind.DateTime):
                result = ValueRef.FromDateTime(
                    new DateTimeOffset(input.AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                return true;
            case (DataKind.DateTime, DataKind.Date):
                result = ValueRef.FromDate(DateOnly.FromDateTime(input.AsDateTime().DateTime));
                return true;
            case (DataKind.String, DataKind.Date):
                if (DateOnly.TryParse(input.AsString(), CultureInfo.InvariantCulture, out DateOnly d))
                {
                    result = ValueRef.FromDate(d);
                    return true;
                }
                return false;
            case (DataKind.String, DataKind.DateTime):
                if (DateTimeOffset.TryParse(input.AsString(), CultureInfo.InvariantCulture, out DateTimeOffset dto))
                {
                    result = ValueRef.FromDateTime(dto);
                    return true;
                }
                return false;
            case (DataKind.Date, DataKind.String):
                result = ValueRef.FromString(input.AsDate().ToString("O", CultureInfo.InvariantCulture));
                return true;
            case (DataKind.DateTime, DataKind.String):
                result = ValueRef.FromString(input.AsDateTime().ToString("O", CultureInfo.InvariantCulture));
                return true;
            case (DataKind.String, DataKind.Time):
                if (TimeOnly.TryParse(input.AsString(), CultureInfo.InvariantCulture, out TimeOnly t))
                {
                    result = ValueRef.FromTime(t);
                    return true;
                }
                return false;
            case (DataKind.Time, DataKind.String):
                result = ValueRef.FromString(input.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                return true;
            case (DataKind.String, DataKind.Duration):
                if (TimeSpan.TryParse(input.AsString(), CultureInfo.InvariantCulture, out TimeSpan ts))
                {
                    result = ValueRef.FromDuration(ts);
                    return true;
                }
                return false;
            case (DataKind.Duration, DataKind.String):
                result = ValueRef.FromString(input.AsDuration().ToString("c"));
                return true;
            case (DataKind.String, DataKind.Uuid):
                if (Guid.TryParse(input.AsString(), out Guid g))
                {
                    result = ValueRef.FromUuid(g);
                    return true;
                }
                return false;
            case (DataKind.Uuid, DataKind.String):
                result = ValueRef.FromString(input.AsUuid().ToString("D"));
                return true;
            case (DataKind.String, DataKind.Boolean):
                return TryParseStringToBoolean(input.AsString(), out result);
            case (DataKind.Boolean, DataKind.String):
                result = ValueRef.FromString(input.AsBoolean() ? "true" : "false");
                return true;
            case (DataKind.String, DataKind.Json):
                try
                {
                    byte[] cbor = DatumIngest.Functions.Json.CborJsonCodec.EncodeFromJsonText(input.AsString());
                    result = ValueRef.FromBytes(DataKind.Json, cbor);
                    return true;
                }
                catch (System.Text.Json.JsonException)
                {
                    return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            case (DataKind.Json, DataKind.String):
                result = ValueRef.FromString(
                    DatumIngest.Functions.Json.CborJsonCodec.DecodeToJsonText(input.AsByteSpan()));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Constructs a numeric <see cref="ValueRef"/> from a <see cref="double"/>
    /// intermediate via <see cref="DataValueComparer.MakeNumeric"/>. Returns
    /// <see langword="false"/> for non-numeric target kinds. Coverage is the
    /// same as <see cref="DataValueComparer.IsNumericScalar"/>.
    /// </summary>
    private static bool TryMakeNumeric(double value, DataKind targetKind, out ValueRef result)
    {
        DataValue? made = DataValueComparer.MakeNumeric(value, targetKind);
        if (made.HasValue)
        {
            result = ValueRef.FromInline(made.Value);
            return true;
        }
        result = default;
        return false;
    }

    private static bool TryParseStringToNumeric(string text, DataKind targetKind, out ValueRef result)
    {
        switch (targetKind)
        {
            case DataKind.Int8:
                if (sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte i8))
                { result = ValueRef.FromInt8(i8); return true; }
                break;
            case DataKind.UInt8:
                if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte u8))
                { result = ValueRef.FromUInt8(u8); return true; }
                break;
            case DataKind.Int16:
                if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16))
                { result = ValueRef.FromInt16(i16); return true; }
                break;
            case DataKind.UInt16:
                if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16))
                { result = ValueRef.FromUInt16(u16); return true; }
                break;
            case DataKind.Int32:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32))
                { result = ValueRef.FromInt32(i32); return true; }
                break;
            case DataKind.UInt32:
                if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32))
                { result = ValueRef.FromUInt32(u32); return true; }
                break;
            case DataKind.Int64:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
                { result = ValueRef.FromInt64(i64); return true; }
                break;
            case DataKind.UInt64:
                if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u64))
                { result = ValueRef.FromUInt64(u64); return true; }
                break;
            case DataKind.Int128:
                if (Int128.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int128 i128))
                { result = ValueRef.FromInt128(i128); return true; }
                break;
            case DataKind.UInt128:
                if (UInt128.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt128 u128))
                { result = ValueRef.FromUInt128(u128); return true; }
                break;
            case DataKind.Float16:
                if (Half.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out Half f16))
                { result = ValueRef.FromFloat16(f16); return true; }
                break;
            case DataKind.Float32:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f32))
                { result = ValueRef.FromFloat32(f32); return true; }
                break;
            case DataKind.Float64:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double f64))
                { result = ValueRef.FromFloat64(f64); return true; }
                break;
            case DataKind.Decimal:
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal dec))
                { result = ValueRef.FromDecimal(dec); return true; }
                break;
        }

        result = default;
        return false;
    }

    private static bool TryParseStringToBoolean(string text, out ValueRef result)
    {
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            result = ValueRef.FromBoolean(true);
            return true;
        }
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            result = ValueRef.FromBoolean(false);
            return true;
        }
        result = default;
        return false;
    }

    private static string FormatNumericAsString(ValueRef value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt8 => value.AsUInt8().ToString(CultureInfo.InvariantCulture),
        DataKind.Int16 => value.AsInt16().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt16 => value.AsUInt16().ToString(CultureInfo.InvariantCulture),
        DataKind.Int32 => value.AsInt32().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt32 => value.AsUInt32().ToString(CultureInfo.InvariantCulture),
        DataKind.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt64 => value.AsUInt64().ToString(CultureInfo.InvariantCulture),
        DataKind.Int128 => value.AsInt128().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt128 => value.AsUInt128().ToString(CultureInfo.InvariantCulture),
        DataKind.Float16 => value.AsFloat16().ToString(CultureInfo.InvariantCulture),
        DataKind.Float32 => value.AsFloat32().ToString(CultureInfo.InvariantCulture),
        DataKind.Float64 => value.AsFloat64().ToString(CultureInfo.InvariantCulture),
        DataKind.Decimal => value.AsDecimal().ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Not a numeric kind: {value.Kind}."),
    };
}
