using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Concatenates values with a separator, skipping null arguments.
/// <c>concat_ws(separator, v1, v2, …)</c> — null separator yields a null
/// result; null value arguments are silently skipped. Non-string value kinds
/// are automatically converted to their string representation.
/// </summary>
public sealed class ConcatWsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "concat_ws";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates values with a separator, skipping null arguments. " +
        "concat_ws(separator, v1, v2, …)";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("separator", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: new VariadicSpec("values", DataKindMatcher.Any, MinOccurrences: 0),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ConcatWsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef separatorArg = args[0];

        if (separatorArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));

        string separator = separatorArg.AsString();
        StringBuilder sb = new();
        bool first = true;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].IsNull) continue;

            if (!first) sb.Append(separator);
            sb.Append(ValueToString(args[i]));
            first = false;
        }

        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }

    private static string ValueToString(ValueRef v) => v.Kind switch
    {
        DataKind.String => v.AsString(),
        DataKind.Int8 => v.AsInt8().ToString(),
        DataKind.Int16 => v.AsInt16().ToString(),
        DataKind.Int32 => v.AsInt32().ToString(),
        DataKind.Int64 => v.AsInt64().ToString(),
        DataKind.UInt8 => v.AsUInt8().ToString(),
        DataKind.UInt16 => v.AsUInt16().ToString(),
        DataKind.UInt32 => v.AsUInt32().ToString(),
        DataKind.UInt64 => v.AsUInt64().ToString(),
        DataKind.Float16 => v.AsFloat16().ToString(),
        DataKind.Float32 => v.AsFloat32().ToString("G"),
        DataKind.Float64 => v.AsFloat64().ToString("G"),
        DataKind.Decimal => v.AsDecimal().ToString(),
        DataKind.Uuid => v.AsUuid().ToString("D"),
        _ => throw new FunctionArgumentException(Name,
            $"cannot implicitly convert kind {v.Kind} to string; use CAST(value AS String) explicitly."),
    };
}
