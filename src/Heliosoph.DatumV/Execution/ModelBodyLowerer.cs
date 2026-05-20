using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution.Operators;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan post-pass hook that historically lowered a SQL-defined model's
/// procedural body into a column pipeline (chain of
/// <see cref="ProjectOperator"/> + a dedicated <c>InferOperator</c>) for
/// cross-row batched dispatch. That lowering was removed; every
/// SQL-defined model invocation now flows through
/// <see cref="ModelInvocationOperator"/> + <c>ProceduralModelAdapter</c>
/// alongside built-in models. Cross-row batching for batchable ONNX
/// shapes lives on <see cref="Heliosoph.DatumV.Functions.IScalarFunction.ExecuteBatchAsync"/>
/// (see <c>InferFunction.ExecuteBatchAsync</c>) rather than the plan layer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why we removed lowering.</strong> The chain-of-operators path
/// materialised the model body's intermediate values as columns flowing
/// through several arenas. Source columns referenced by multiple body
/// statements (the canonical case: an <c>img Image</c> parameter consumed
/// by <c>image_to_tensor</c>, <c>image_height</c>, AND <c>image_width</c>
/// in the same RETURN) were stabilised through every Project boundary —
/// for sidecar-backed Images that meant repeated bytes copies or even
/// decode round-trips per row. Measured wall-clock for a 1024-row MiDaS
/// query: ~20× slower than the procedural path that keeps <c>img</c> bound
/// once as a parameter ValueRef and reuses it across body statements.
/// </para>
/// <para>
/// <strong>Why the entry point still exists.</strong> This is the natural
/// plug-in point for per-model plan rewrites that DO benefit from
/// plan-shape changes (e.g. fusing a model whose output feeds straight
/// into a known scalar with no other consumers). None ship today; the pass
/// is a no-op walk that returns its argument unchanged.
/// </para>
/// </remarks>
public static class ModelBodyLowerer
{
    /// <summary>
    /// Walks the operator tree, applying any per-model rewrites
    /// registered above. Currently a no-op; preserved as the hook for
    /// future plan-shape-aware optimisations.
    /// </summary>
    /// <param name="op">Operator tree after hoisting.</param>
    /// <param name="declaredModels">
    /// Registry of SQL-defined models. <see langword="null"/> short-
    /// circuits to identity.
    /// </param>
    public static QueryOperator LowerSqlDefinedBodies(
        QueryOperator op,
        ModelRegistry? declaredModels)
    {
        if (declaredModels is null) return op;
        return WalkRecursive(op, declaredModels);
    }

    private static QueryOperator WalkRecursive(QueryOperator op, ModelRegistry declaredModels)
    {
        // Pre-order traversal so future per-node rewrites see a tree with
        // its children already in their final shape.
        return ModelInvocationHoister.RewriteChildren(
            op, child => WalkRecursive(child, declaredModels));
    }
}
