using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// <c>pca_project(model STRUCT, vec FLOAT32[]) → FLOAT32[]</c>. Projects a vector
/// into the k-dimensional space of a <c>pca_fit_agg</c> model: subtracts the
/// model's <c>mean</c>, then dots the centered vector with each row of
/// <c>components</c>. Row-stream counterpart to the aggregate fit — fit once per
/// group (or on a sample), project every row.
/// </summary>
/// <remarks>
/// Model fields are resolved by name (<c>mean</c>, <c>components</c>) through the
/// per-query <see cref="TypeRegistry"/>, not by position, so any struct carrying
/// those two fields works — including models loaded from a table rather than fit
/// in the same query. Accumulates in <c>double</c> and narrows to Float32,
/// matching the other vector functions.
/// </remarks>
public sealed class PcaProjectFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pca_project";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Projects a Float32 vector through a pca_fit_agg model (center on mean, dot with components): "
        + "pca_project(model STRUCT, vec FLOAT32[]) → FLOAT32[k].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("model", DataKindMatcher.Exact(DataKind.Struct)),
                new ParameterSpec("vec", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcaProjectFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }

        ValueRef model = args[0];
        TypeDescriptor? descriptor = frame.Types?.GetDescriptor(model.TypeId);
        if (descriptor?.Fields is null)
        {
            throw new FunctionArgumentException(Name,
                "model struct has no registered field descriptors; expected a pca_fit_agg model "
                + "with 'mean' and 'components' fields.");
        }

        int meanIdx = descriptor.FindFieldIndex("mean");
        int componentsIdx = descriptor.FindFieldIndex("components");
        if (meanIdx < 0 || componentsIdx < 0)
        {
            throw new FunctionArgumentException(Name,
                "model struct must carry 'mean' and 'components' fields; got "
                + $"[{string.Join(", ", descriptor.Fields.Select(f => f.Name))}].");
        }

        ReadOnlySpan<ValueRef> fields = model.GetStructFields();
        float[] mean = ActivationOps.ReadFloat32Array(fields[meanIdx]);
        float[] components = ActivationOps.ReadFloat32Array(fields[componentsIdx]);

        int d = mean.Length;
        if (d < 1 || components.Length < d || components.Length % d != 0)
        {
            throw new FunctionArgumentException(Name,
                $"model is inconsistent: mean has {d} dimensions but components carries "
                + $"{components.Length} elements (expected a k×{d} matrix).");
        }
        int k = components.Length / d;

        float[] vec = ActivationOps.ReadFloat32Array(args[1]);
        if (vec.Length != d)
        {
            throw new FunctionArgumentException(Name,
                $"vector has {vec.Length} dimensions but the model was fit on {d}.");
        }

        float[] projected = new float[k];
        for (int c = 0; c < k; c++)
        {
            int row = c * d;
            double acc = 0;
            for (int i = 0; i < d; i++)
            {
                acc += ((double)vec[i] - mean[i]) * components[row + i];
            }
            projected[c] = (float)acc;
        }

        return new(ValueRef.FromPrimitiveArray(projected, DataKind.Float32));
    }
}
