using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the runtime <see cref="DataKind"/> of a value as a
/// <see cref="DataKind.Type"/> tag. Enables type-oriented comparisons
/// like <c>typeof(x) == Int32</c> instead of string-based checks.
/// </summary>
public sealed class TypeofFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "typeof";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the runtime DataKind of a value as a Type tag. "
        + "Enables type-oriented comparisons like `typeof(x) == Int32`.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Type)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TypeofFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arg = args[0];

        // typeof(NULL) → typed null of kind Type. Downstream rendering shows "NULL"
        // and equality comparisons against other Type values follow null-propagation.
        if (arg.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Type));

        DataKind kind = arg.Kind;
        ushort typeId = arg.TypeId;

        // Forcing function: a struct that has flowed through query execution
        // without ever being stamped with a registered type is the symptom of
        // a missing TypeRegistry plumbing site. Fail fast so the gap is fixed
        // at the construction site, not silently rendered as f0..fN.
        if (kind == DataKind.Struct && typeId == 0)
        {
            throw new InvalidOperationException(
                "typeof() called on a struct value with no registered type. "
                + "The struct was constructed without a TypeId — every struct "
                + "construction site must intern its shape into the per-query "
                + "TypeRegistry and stamp the resulting TypeId on the DataValue.");
        }

        // Scalars and primitive-element typed arrays leave TypeId=0 (no rich
        // shape to describe). Array<Struct> carries its TypeId so
        // `typeof(detect(img))` renders as "Array<Struct{...}>".
        return new ValueTask<ValueRef>(ValueRef.FromType(kind, typeId));
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
