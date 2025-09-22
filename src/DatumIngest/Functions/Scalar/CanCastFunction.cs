using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns whether a value can be safely cast to a target type without data loss
/// or parse failure. <c>can_cast(value, TargetType)</c> where TargetType is a
/// <see cref="DataKind.Type"/> literal (e.g. <c>can_cast(x, Int32)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>typeof(x) = Int32</c> which checks type identity, <c>can_cast</c>
/// checks value representability: whether the specific value can be converted to
/// the target type without overflow, truncation, or parse failure.
/// </para>
/// <para>
/// Delegates range checking, string parsing, and semantic path validation to
/// <see cref="DataValueComparer"/> so all cast-compatibility logic is centralized.
/// </para>
/// </remarks>
internal sealed class CanCastFunction : IScalarFunction
{
    public string Name => "can_cast";

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("can_cast() requires exactly 2 arguments: value and target type.");
        }

        if (argumentKinds[1] != DataKind.Type)
        {
            throw new ArgumentException("can_cast() second argument must be a type literal (e.g. Int32, Float64).");
        }

        return DataKind.Boolean;
    }

    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataKind targetKind = arguments[1].AsType();

        if (input.IsNull) return DataValue.FromBoolean(true);
        if (input.Kind == targetKind) return DataValue.FromBoolean(true);

        bool result = CanConvert(input, targetKind);
        return DataValue.FromBoolean(result);
    }

    private static bool CanConvert(DataValue input, DataKind targetKind)
    {
        // Numeric → Numeric (range check via double intermediate).
        if (input.TryToDouble(out double asDouble))
        {
            if (DataValueComparer.CanFitNumeric(asDouble, targetKind)) return true;
            if (targetKind == DataKind.Boolean) return true;
            if (targetKind == DataKind.String) return true;
        }

        // Boolean → numeric: always valid (true=1, false=0).
        if (input.Kind == DataKind.Boolean && DataValueComparer.IsNumericScalar(targetKind))
        {
            return true;
        }

        // String parsing.
        if (input.Kind == DataKind.String)
        {
            return DataValueComparer.CanParseString(input.AsString(), targetKind);
        }

        // Semantic conversions (Date↔DateTime, Uuid↔String, temporal↔numeric, etc.).
        return DataValueComparer.HasSemanticCastPath(input.Kind, targetKind);
    }
}
