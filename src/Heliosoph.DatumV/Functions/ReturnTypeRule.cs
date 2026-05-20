using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

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

    /// <summary>
    /// Whether the result is a typed array whose element kind is given by
    /// <see cref="Resolve"/>. Defaults to <see langword="false"/>; set to
    /// <see langword="true"/> by <see cref="ArrayOf"/>.
    /// </summary>
    public virtual bool ProducesArray => false;

    /// <summary>
    /// Whether the result is a multi-dimensional typed array (carries an
    /// explicit shape; <see cref="DataValue.IsMultiDim"/> on the produced
    /// value). Defaults to <see langword="false"/>; set to
    /// <see langword="true"/> only by <see cref="MultiDimArrayOf"/>.
    /// Implies <see cref="ProducesArray"/>.
    ///
    /// Functions whose output rank is runtime-dependent (e.g. <c>infer()</c>,
    /// where the ndim follows the model's ONNX output shape) should leave
    /// this <see langword="false"/> and build multi-dim
    /// <see cref="DataValue"/>s at the runtime construction site — the
    /// evaluator's index-access path consults runtime <c>IsMultiDim</c>
    /// directly, so static declaration is only needed when downstream
    /// signature dispatch needs to see multi-dim at type-resolution time.
    /// </summary>
    public virtual bool ProducesMultiDimArray => false;

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

    /// <summary>
    /// Wraps <paramref name="elementRule"/> to declare that the result is a
    /// typed array. <see cref="Resolve"/> delegates to the inner rule to
    /// return the element kind; <see cref="Describe"/> renders as
    /// <c>Array&lt;elementKind&gt;</c>.
    /// </summary>
    public static ReturnTypeRule ArrayOf(ReturnTypeRule elementRule) =>
        new ArrayOfRule(elementRule);

    /// <summary>
    /// Wraps <paramref name="elementRule"/> to declare that the result is a
    /// multi-dimensional typed array (carries an explicit shape — <see cref="DataValue.IsMultiDim"/>
    /// is set on the produced value). Like <see cref="ArrayOf"/> but additionally
    /// sets <see cref="ProducesMultiDimArray"/> so the type resolver can
    /// propagate <c>IsMultiDim = true</c> for chained signature dispatch.
    /// Only declare this when the function's output ndim is fixed at the
    /// signature level (e.g. a function that always returns a 2-D matrix);
    /// for rank-dynamic outputs, leave the static rule as <see cref="ArrayOf"/>
    /// and emit a multi-dim <see cref="DataValue"/> at runtime.
    /// </summary>
    public static ReturnTypeRule MultiDimArrayOf(ReturnTypeRule elementRule) =>
        new MultiDimArrayOfRule(elementRule);

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

    private sealed class ArrayOfRule(ReturnTypeRule elementRule) : ReturnTypeRule
    {
        public override DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds) =>
            elementRule.Resolve(argumentKinds);
        public override string Describe() => $"Array<{elementRule.Describe()}>";
        public override DataKind? StaticHint => elementRule.StaticHint;
        public override bool ProducesArray => true;
    }

    private sealed class MultiDimArrayOfRule(ReturnTypeRule elementRule) : ReturnTypeRule
    {
        public override DataKind Resolve(ReadOnlySpan<DataKind> argumentKinds) =>
            elementRule.Resolve(argumentKinds);
        public override string Describe() => $"MultiDimArray<{elementRule.Describe()}>";
        public override DataKind? StaticHint => elementRule.StaticHint;
        public override bool ProducesArray => true;
        public override bool ProducesMultiDimArray => true;
    }
}
