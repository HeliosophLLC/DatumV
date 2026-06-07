using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>parse_ident(text [, strict_mode]) → text[]</c>. Splits a
/// qualified SQL identifier into its component names. Double-quoted parts
/// preserve case and may contain any character (with internal <c>""</c>
/// representing a literal quote); unquoted parts are case-folded to
/// lowercase as PostgreSQL does at parse time. When <c>strict</c> is
/// <c>true</c> (the default), trailing garbage after the last identifier
/// part raises an error; passing <c>false</c> tolerates it. Null input
/// propagates to null.
/// </summary>
public sealed class ParseIdentFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "parse_ident";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Splits a qualified identifier into an Array, removing quotes (unquoted parts are folded to lowercase).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("strict", DataKindMatcher.Exact(DataKind.Boolean)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ParseIdentFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || (args.Length == 2 && args[1].IsNull))
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.String));
        }
        string input = args[0].AsString();
        bool strict = args.Length == 2 ? args[1].AsBoolean() : true;

        List<string> parts = [];
        int i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;

            string part;
            if (input[i] == '"')
            {
                // Quoted identifier: read until matching unescaped quote.
                StringBuilder sb = new();
                i++;
                while (i < input.Length)
                {
                    if (input[i] == '"')
                    {
                        if (i + 1 < input.Length && input[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(input[i]);
                    i++;
                }
                if (sb.Length == 0)
                {
                    throw new FunctionArgumentException(Name, "empty quoted identifier.");
                }
                part = sb.ToString();
            }
            else if (IsIdentStart(input[i]))
            {
                int start = i;
                while (i < input.Length && IsIdentContinue(input[i])) i++;
                part = input[start..i].ToLowerInvariant();
            }
            else
            {
                throw new FunctionArgumentException(Name, $"unexpected character '{input[i]}' in identifier.");
            }
            parts.Add(part);

            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;
            if (input[i] == '.')
            {
                i++;
                continue;
            }
            // Trailing junk after a complete identifier.
            if (strict)
            {
                throw new FunctionArgumentException(Name,
                    $"trailing characters after qualified identifier: '{input[i..]}'.");
            }
            break;
        }

        if (parts.Count == 0)
        {
            throw new FunctionArgumentException(Name, "string does not contain a qualified identifier.");
        }

        ValueRef[] elements = new ValueRef[parts.Count];
        for (int k = 0; k < parts.Count; k++)
        {
            elements[k] = ValueRef.FromString(parts[k]);
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.String, elements));
    }

    private static bool IsIdentStart(char c) =>
        c == '_' || char.IsLetter(c);

    private static bool IsIdentContinue(char c) =>
        c == '_' || c == '$' || char.IsLetterOrDigit(c);
}
