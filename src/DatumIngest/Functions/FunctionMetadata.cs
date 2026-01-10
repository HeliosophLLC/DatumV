using DatumIngest.Model;

namespace DatumIngest.Functions;

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
