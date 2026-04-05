using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Argument-kind matcher for <see cref="ParameterSpec"/> and
/// <see cref="VariadicSpec"/>. A matcher answers "does this
/// <see cref="DataKind"/> satisfy this slot?" without forcing the function
/// author to enumerate every accepted kind in code.
/// </summary>
public abstract class DataKindMatcher
{
    /// <summary>True when <paramref name="kind"/> satisfies this matcher.</summary>
    public abstract bool Matches(DataKind kind);

    /// <summary>
    /// Human-readable description of the matched set. Used in error
    /// messages (e.g. "expected NumericFamily, got String").
    /// </summary>
    public abstract string Describe();

    /// <summary>Matcher accepting exactly one kind.</summary>
    public static DataKindMatcher Exact(DataKind kind) => new ExactMatcher(kind);

    /// <summary>Matcher accepting one of the listed kinds.</summary>
    public static DataKindMatcher OneOf(params DataKind[] kinds)
    {
        if (kinds is null || kinds.Length == 0)
        {
            throw new ArgumentException("OneOf requires at least one kind.", nameof(kinds));
        }
        return new OneOfMatcher(kinds);
    }

    /// <summary>Matcher accepting any kind in the given <see cref="DataKindFamily"/>.</summary>
    public static DataKindMatcher Family(DataKindFamily family) =>
        new FamilyMatcher(family);

    /// <summary>Sentinel matcher accepting every kind.</summary>
    public static DataKindMatcher Any { get; } = new FamilyMatcher(DataKindFamily.AnyKind);

    /// <summary>
    /// Matcher accepting a <see cref="DataKind.Lambda"/> value with a
    /// specific function-context scope and declared return-kind. The
    /// matcher's <see cref="Matches"/> only checks kind equality at the
    /// plan-time type-resolution layer; structural signature validation
    /// (parameter count, parameter kinds, return-kind compatibility)
    /// happens through <see cref="LambdaSignatureValidator"/> against the
    /// lambda's AST.
    /// </summary>
    /// <param name="contextName">
    /// Name of the <see cref="DatumIngest.Execution.Contexts.IFunctionContext"/>
    /// the lambda body is scoped to. Used by the function resolver and the
    /// language server to determine which functions are callable inside
    /// the body. Pass <see langword="null"/> for an unscoped lambda (the
    /// body inherits the surrounding scope's resolution — useful for
    /// engine-internal callbacks that don't need DSL scoping).
    /// </param>
    /// <param name="returns">
    /// Return-kind matcher: what the lambda body's evaluation must produce.
    /// Typically <see cref="Exact(DataKind)"/> when the consumer needs a
    /// specific kind (e.g. <c>Image</c> for an animation frame).
    /// </param>
    public static LambdaMatcher Lambda(string? contextName, DataKindMatcher returns) =>
        new(contextName, returns ?? throw new ArgumentNullException(nameof(returns)));

    private sealed class ExactMatcher(DataKind kind) : DataKindMatcher
    {
        public override bool Matches(DataKind k) => k == kind;
        public override string Describe() => kind.ToString();
    }

    private sealed class OneOfMatcher(DataKind[] kinds) : DataKindMatcher
    {
        public override bool Matches(DataKind k)
        {
            for (int i = 0; i < kinds.Length; i++)
            {
                if (kinds[i] == k) return true;
            }
            return false;
        }
        public override string Describe() => "one of " + string.Join(", ", kinds);
    }

    private sealed class FamilyMatcher(DataKindFamily family) : DataKindMatcher
    {
        public override bool Matches(DataKind k) => family.Contains(k);
        public override string Describe() =>
            family == DataKindFamily.AnyKind ? "Any" : family.ToString();
    }
}
