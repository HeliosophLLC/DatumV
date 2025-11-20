using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Computes a function's result <see cref="DataKind"/> from its argument
/// kinds. Lets the static signature carry the rule (e.g. "same as
/// argument 0") rather than forcing every implementation to special-case
/// it in <c>ValidateArguments</c>.
/// </summary>
public abstract class ReturnTypeRule
{
    /// <summary>Resolves the result kind from concrete argument kinds.</summary>
    public abstract DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>Human-readable description for diagnostics / catalog output.</summary>
    public abstract string Describe();

    /// <summary>
    /// The result kind when it can be reported without seeing arguments
    /// (e.g. <see cref="Constant"/>); <c>null</c> when the rule depends on
    /// argument kinds (e.g. <see cref="SameAs"/>).
    /// </summary>
    public abstract DataKind? StaticHint { get; }

    /// <summary>Always returns <paramref name="kind"/>.</summary>
    public static ReturnTypeRule Constant(DataKind kind) => new ConstantRule(kind);

    /// <summary>Returns the kind of argument <paramref name="parameterIndex"/>.</summary>
    public static ReturnTypeRule SameAs(int parameterIndex) =>
        new SameAsRule(parameterIndex);

    /// <summary>
    /// Custom resolver. Use sparingly — prefer the named rules so
    /// catalog tooling can describe the rule without invoking it.
    /// </summary>
    public static ReturnTypeRule Custom(Func<ReadOnlySpan<DataKind>, DataKind> resolver, string description) =>
        new CustomRule(resolver, description);

    private sealed class ConstantRule(DataKind kind) : ReturnTypeRule
    {
        public override DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds) => kind;
        public override string Describe() => kind.ToString();
        public override DataKind? StaticHint => kind;
    }

    private sealed class SameAsRule(int parameterIndex) : ReturnTypeRule
    {
        public override DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds)
        {
            if (parameterIndex < 0 || parameterIndex >= argumentKinds.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(argumentKinds),
                    $"SameAs({parameterIndex}) but only {argumentKinds.Length} argument(s) supplied.");
            }
            return argumentKinds[parameterIndex];
        }
        public override string Describe() => $"same as argument {parameterIndex}";
        public override DataKind? StaticHint => null;
    }

    private sealed class CustomRule(
        Func<ReadOnlySpan<DataKind>, DataKind> resolver,
        string description)
        : ReturnTypeRule
    {
        public override DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds) => resolver(argumentKinds);
        public override string Describe() => description;
        public override DataKind? StaticHint => null;
    }
}
