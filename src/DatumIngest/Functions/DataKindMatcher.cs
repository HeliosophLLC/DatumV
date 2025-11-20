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
