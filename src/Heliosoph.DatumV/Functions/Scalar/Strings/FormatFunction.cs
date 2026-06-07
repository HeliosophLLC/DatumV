using System.Globalization;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>format(formatstr, formatarg, ...) → text</c>. Renders a
/// format string with sprintf-style placeholders. Supported conversions:
/// <list type="bullet">
/// <item><description><c>%s</c> — argument as plain text (null becomes the empty string).</description></item>
/// <item><description><c>%I</c> — argument as SQL identifier (<see cref="QuoteIdentFunction"/>); null is an error.</description></item>
/// <item><description><c>%L</c> — argument as SQL literal (<see cref="QuoteLiteralFunction"/>); null becomes the bare word <c>NULL</c>.</description></item>
/// <item><description><c>%%</c> — a literal <c>%</c>.</description></item>
/// </list>
/// Each placeholder may include a positional reference, <c>%n$type</c>,
/// where <c>n</c> is a 1-based index into the variadic arguments. Width
/// flags are not yet supported.
/// </summary>
public sealed class FormatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "format";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Renders a format string with sprintf-style placeholders (%s, %I, %L, %%, positional %n$type).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("format", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: new VariadicSpec("args", DataKindMatcher.Any, MinOccurrences: 0),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FormatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        string format = args[0].AsString();

        // PG: positional refs share the same numbering space as auto-numbered
        // placeholders. Auto-numbering picks the next variadic arg each time
        // a placeholder without an explicit position is encountered.
        StringBuilder sb = new(format.Length + 16);
        int autoIndex = 1;
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 >= format.Length)
            {
                throw new FunctionArgumentException(Name, "trailing '%' in format string.");
            }
            int cursor = i + 1;

            // %% → literal '%'
            if (format[cursor] == '%')
            {
                sb.Append('%');
                i = cursor + 1;
                continue;
            }

            // Optional positional reference: digits + '$'
            int? position = null;
            int digitStart = cursor;
            while (cursor < format.Length && char.IsDigit(format[cursor])) cursor++;
            if (cursor > digitStart && cursor < format.Length && format[cursor] == '$')
            {
                if (!int.TryParse(format.AsSpan(digitStart, cursor - digitStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) || p < 1)
                {
                    throw new FunctionArgumentException(Name, "invalid positional reference in format string.");
                }
                position = p;
                cursor++;
            }
            else
            {
                cursor = digitStart;
            }

            if (cursor >= format.Length)
            {
                throw new FunctionArgumentException(Name, "format string ends inside a placeholder.");
            }
            char type = format[cursor];
            int variadicIndex = position ?? autoIndex;
            if (position is null) autoIndex++;

            // Map 1-based variadic index → absolute arg index (args[0] is the format string).
            int absoluteIndex = variadicIndex; // args[1] = variadic[1]
            if (absoluteIndex < 1 || absoluteIndex >= args.Length)
            {
                throw new FunctionArgumentException(Name, $"format argument #{variadicIndex} is missing.");
            }
            ValueRef value = args[absoluteIndex];

            switch (type)
            {
                case 's':
                    sb.Append(value.IsNull ? "" : RenderAsText(value));
                    break;
                case 'I':
                    if (value.IsNull)
                    {
                        throw new FunctionArgumentException(Name, "null cannot be formatted as a SQL identifier (%I).");
                    }
                    sb.Append(SqlQuoting.QuoteIdentifier(RenderAsText(value)));
                    break;
                case 'L':
                    if (value.IsNull) sb.Append("NULL");
                    else sb.Append(SqlQuoting.QuoteLiteral(RenderAsText(value)));
                    break;
                default:
                    throw new FunctionArgumentException(Name, $"unsupported format conversion '%{type}'.");
            }
            i = cursor + 1;
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }

    private static string RenderAsText(ValueRef v) => v.Kind switch
    {
        DataKind.String => v.AsString(),
        DataKind.Boolean => v.AsBoolean() ? "true" : "false",
        DataKind.Int8 => v.AsInt8().ToString(CultureInfo.InvariantCulture),
        DataKind.Int16 => v.AsInt16().ToString(CultureInfo.InvariantCulture),
        DataKind.Int32 => v.AsInt32().ToString(CultureInfo.InvariantCulture),
        DataKind.Int64 => v.AsInt64().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt8 => v.AsUInt8().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt16 => v.AsUInt16().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt32 => v.AsUInt32().ToString(CultureInfo.InvariantCulture),
        DataKind.UInt64 => v.AsUInt64().ToString(CultureInfo.InvariantCulture),
        DataKind.Float16 => v.AsFloat16().ToString(CultureInfo.InvariantCulture),
        DataKind.Float32 => v.AsFloat32().ToString("G", CultureInfo.InvariantCulture),
        DataKind.Float64 => v.AsFloat64().ToString("G", CultureInfo.InvariantCulture),
        DataKind.Decimal => v.AsDecimal().ToString(CultureInfo.InvariantCulture),
        DataKind.Uuid => v.AsUuid().ToString("D"),
        _ => throw new FunctionArgumentException(Name,
            $"cannot implicitly format kind {v.Kind}; use CAST(value AS String) first."),
    };
}
