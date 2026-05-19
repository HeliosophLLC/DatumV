using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Metadata-driven argument validator. Walks the static
/// <see cref="IFunction.Signatures"/> for the supplied function type,
/// finds the first variant matching the supplied argument kinds, and
/// returns the resolved result kind. Throws
/// <see cref="FunctionArgumentException"/> on mismatch.
/// </summary>
public static class FunctionMetadata
{
    /// <summary>
    /// Validates <paramref name="argumentKinds"/> against
    /// <typeparamref name="T"/>'s signatures. Returns the result kind from
    /// the first matching variant.
    /// </summary>
    public static DataKind Validate<T>(ReadOnlySpan<DataKind> argumentKinds)
        where T : IFunction
    {
        IReadOnlyList<FunctionSignatureVariant> signatures = T.Signatures;
        if (signatures.Count == 0)
        {
            throw new FunctionArgumentException(
                T.Name,
                "no signatures declared for this function.");
        }

        for (int i = 0; i < signatures.Count; i++)
        {
            if (TryMatch(signatures[i], argumentKinds))
            {
                return signatures[i].ReturnType.Resolve(argumentKinds);
            }
        }

        FunctionArgumentException.ThrowNoMatchingVariant(
            T.Name, argumentKinds, signatures);
        return default; // unreachable
    }

    /// <summary>
    /// Walks <paramref name="signatures"/> and returns the first variant that
    /// matches <paramref name="argumentKinds"/>, or <see langword="null"/> when
    /// no variant matches. Companion to <see cref="Validate{T}"/> for callers
    /// that need the matched <see cref="FunctionSignatureVariant"/> itself —
    /// e.g. to read its <see cref="FunctionSignatureVariant.ReturnType"/> and
    /// inspect <see cref="ReturnTypeRule.ProducesArray"/>.
    /// </summary>
    public static FunctionSignatureVariant? MatchVariant(
        IReadOnlyList<FunctionSignatureVariant> signatures,
        ReadOnlySpan<DataKind> argumentKinds)
    {
        for (int i = 0; i < signatures.Count; i++)
        {
            if (TryMatch(signatures[i], argumentKinds))
            {
                return signatures[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Array-aware variant of <see cref="MatchVariant"/>. Walks
    /// <paramref name="signatures"/> and returns the first variant that
    /// matches both <see cref="DataKind"/> and array-ness for each argument.
    /// Use this from the type resolver, where the per-arg <c>IsArray</c>
    /// flag is known. Falls back to <see cref="MatchVariant"/>-equivalent
    /// behaviour for parameters whose <see cref="ParameterSpec.IsArray"/>
    /// is <see cref="ArrayMatch.Either"/>.
    /// </summary>
    public static FunctionSignatureVariant? MatchVariantWithShape(
        IReadOnlyList<FunctionSignatureVariant> signatures,
        ReadOnlySpan<(DataKind Kind, bool IsArray, bool IsMultiDim)> argumentShapes)
    {
        for (int i = 0; i < signatures.Count; i++)
        {
            if (TryMatchWithShape(signatures[i], argumentShapes))
            {
                return signatures[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Array-aware version of <see cref="TryMatch"/>. Identical to
    /// <see cref="TryMatch"/> except each fixed parameter and the variadic
    /// also enforce <see cref="ParameterSpec.IsArray"/> /
    /// <see cref="VariadicSpec.IsArray"/> against the argument's actual
    /// array-ness (and multi-dim-ness for the <see cref="ArrayMatch.FlatArray"/>
    /// / <see cref="ArrayMatch.MultiDimArray"/> variants).
    /// </summary>
    public static bool TryMatchWithShape(
        FunctionSignatureVariant variant,
        ReadOnlySpan<(DataKind Kind, bool IsArray, bool IsMultiDim)> argumentShapes)
    {
        IReadOnlyList<ParameterSpec> parameters = variant.Parameters;
        int requiredCount = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (!parameters[i].IsOptional) requiredCount++;
        }

        VariadicSpec? variadic = variant.VariadicTrailing;
        int minTotal = requiredCount + (variadic?.MinOccurrences ?? 0);
        int maxFixed = parameters.Count;

        if (argumentShapes.Length < minTotal) return false;
        if (variadic is null && argumentShapes.Length > maxFixed) return false;

        int fixedToCheck = Math.Min(argumentShapes.Length, maxFixed);
        for (int i = 0; i < fixedToCheck; i++)
        {
            (DataKind kind, bool isArray, bool isMultiDim) = argumentShapes[i];
            if (!parameters[i].Kind.Matches(kind)) return false;
            if (!ArrayMatchSatisfied(parameters[i].IsArray, isArray, isMultiDim)) return false;
        }

        if (variadic is not null)
        {
            DataKind? firstVariadic = null;
            for (int i = maxFixed; i < argumentShapes.Length; i++)
            {
                (DataKind kind, bool isArray, bool isMultiDim) = argumentShapes[i];
                if (!variadic.Kind.Matches(kind)) return false;
                if (!ArrayMatchSatisfied(variadic.IsArray, isArray, isMultiDim)) return false;
                if (variadic.RequireSameKindAcrossArgs)
                {
                    if (firstVariadic is null)
                    {
                        firstVariadic = kind;
                    }
                    else if (firstVariadic.Value != kind)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool ArrayMatchSatisfied(ArrayMatch spec, bool actualIsArray, bool actualIsMultiDim) => spec switch
    {
        ArrayMatch.Either => true,
        ArrayMatch.Scalar => !actualIsArray,
        ArrayMatch.Array => actualIsArray,
        ArrayMatch.FlatArray => actualIsArray && !actualIsMultiDim,
        ArrayMatch.MultiDimArray => actualIsArray && actualIsMultiDim,
        _ => true,
    };

    /// <summary>
    /// True when <paramref name="argumentKinds"/> match
    /// <paramref name="variant"/>'s parameter list and trailing variadic.
    /// </summary>
    public static bool TryMatch(
        FunctionSignatureVariant variant,
        ReadOnlySpan<DataKind> argumentKinds)
    {
        IReadOnlyList<ParameterSpec> parameters = variant.Parameters;
        int requiredCount = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (!parameters[i].IsOptional) requiredCount++;
        }

        VariadicSpec? variadic = variant.VariadicTrailing;

        int minTotal = requiredCount + (variadic?.MinOccurrences ?? 0);
        int maxFixed = parameters.Count;

        if (argumentKinds.Length < minTotal)
        {
            return false;
        }
        if (variadic is null && argumentKinds.Length > maxFixed)
        {
            return false;
        }

        int fixedToCheck = Math.Min(argumentKinds.Length, maxFixed);
        for (int i = 0; i < fixedToCheck; i++)
        {
            if (!parameters[i].Kind.Matches(argumentKinds[i]))
            {
                return false;
            }
        }

        if (variadic is not null)
        {
            DataKind? firstVariadic = null;
            for (int i = maxFixed; i < argumentKinds.Length; i++)
            {
                DataKind kind = argumentKinds[i];
                if (!variadic.Kind.Matches(kind))
                {
                    return false;
                }
                if (variadic.RequireSameKindAcrossArgs)
                {
                    if (firstVariadic is null)
                    {
                        firstVariadic = kind;
                    }
                    else if (firstVariadic.Value != kind)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
