using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Models.Calibration;

/// <summary>
/// Bridge between query operators and <see cref="CalibrationCoordinator"/>.
/// When an operator is about to dispatch a model that has no calibration
/// curve yet, the operator calls
/// <see cref="EnsureCalibratedAsync"/> to trigger a ramp pass before the
/// real dispatch begins. The helper handles the cross-cutting concerns
/// — fast-path skip when already calibrated or when the probe is
/// unavailable, sample-input synthesis from the current row, dispatch
/// delegate construction — so the operators stay focused on their own
/// chunk loops.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sample input strategy.</strong> The first row of the supplied
/// <see cref="RowBatch"/> is evaluated once to produce a representative
/// input row. The ramp then replicates that row N times to fill each
/// batch size in the ramp (1, 2, 4, 8, 16, 32). Identical inputs are
/// acceptable for VRAM measurement — activation footprint depends on
/// tensor shape, not content. Replicating real user data instead of
/// constructing synthetic inputs sidesteps the per-model-shape problem
/// of "what does a valid Image / Float32[] / Struct look like for THIS
/// model?"
/// </para>
/// <para>
/// <strong>Failure modes.</strong> A failed calibration ramp surfaces as
/// an exception to the calling operator, which aborts the user's query.
/// That's intentional for now: a failure here usually means the model
/// or its inputs are broken in a way the user needs to investigate.
/// Future work can soften this (log + fall through to uncalibrated
/// dispatch).
/// </para>
/// <para>
/// <strong>Idempotence.</strong> The first call per (model, process)
/// triggers the ramp; every subsequent call short-circuits on the
/// <see cref="ModelCalibration.State.Calibrated"/> registry check.
/// Concurrent first-callers dedup via the coordinator's per-model
/// <see cref="System.Lazy{T}"/> wrapper.
/// </para>
/// </remarks>
public static class CalibrationTrigger
{
    /// <summary>
    /// Ensures <paramref name="modelName"/> has a calibrated curve. If the
    /// registry already has one, returns immediately. Otherwise evaluates
    /// the supplied input + override expressions against
    /// <paramref name="sampleBatch"/>'s row 0 and triggers a coordinator
    /// ramp using the resulting <see cref="ValueRef"/> row as the sample.
    /// </summary>
    /// <param name="context">Live execution context; provides catalog, evaluator inputs, sidecar registry, etc.</param>
    /// <param name="modelName">Model whose calibration to ensure.</param>
    /// <param name="inputExpressions">Input expressions matching the model's required inputs.</param>
    /// <param name="optionalExpressions">Per-row hyperparameter expressions, or empty.</param>
    /// <param name="sampleBatch">Batch to draw the sample input row from. Must have at least 1 row; callers should skip the trigger entirely for empty batches.</param>
    /// <param name="evaluator">Configured <see cref="ExpressionEvaluator"/> for input evaluation.</param>
    /// <param name="cancellationToken">Caller's cancellation token. Only affects this caller's wait — the coordinator's internal ramp runs to completion regardless.</param>
    public static async Task EnsureCalibratedAsync(
        Heliosoph.DatumV.Execution.ExecutionContext context,
        string modelName,
        IReadOnlyList<Expression> inputExpressions,
        IReadOnlyList<Expression> optionalExpressions,
        RowBatch sampleBatch,
        ExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        ModelCatalog? models = context.Models;
        if (models is null) return;

        // Calibration only makes sense if a policy actually consumes the
        // curve. StaticBatchSizePolicy / BatchOnePolicy / custom test
        // policies don't, so triggering the ramp would just burn time
        // and inflate dispatch counts (breaking tests that count
        // expected `InferBatchAsync` invocations). CurvePolicy is the
        // engine default; this gate gracefully no-ops everywhere else.
        if (models.BatchSizePolicy is not CurvePolicy) return;

        // Fast-path: already calibrated. Avoid allocating anything on
        // the steady-state path.
        ModelCalibration? existing = models.CalibrationRegistry.Get(modelName);
        if (existing is { Status: ModelCalibration.State.Calibrated }) return;

        // No probe → calibration measurements would all be zero. CurvePolicy
        // also degrades to batch=1 in this case, so calibration data
        // wouldn't be used even if recorded. Skip entirely.
        if (!VramProbe.TryGetUsage(out _, out _)) return;

        if (sampleBatch.Count == 0) return;

        // Synthetic backends (no RelativePath, no FingerprintPath) have
        // no fingerprintable identity for cross-restart calibration —
        // the coordinator would throw if invoked. Detect and skip
        // gracefully so operators wrapping test FakeModels or EchoModel
        // don't fail their queries on the trigger.
        ModelCatalogEntry? entry = models.TryGetEntry(modelName);
        if (entry is null) return;
        if (string.IsNullOrEmpty(entry.FingerprintPath)
            && string.IsNullOrEmpty(entry.RelativePath)) return;

        // Evaluate the sample row's inputs ONCE; the ramp's dispatch
        // delegate replicates these ValueRefs for each batch size.
        Row sampleRow = sampleBatch[0];
        EvaluationFrame frame = new(sampleRow, sampleBatch.Arena, context, context.OuterRow);

        ValueRef[] sampleInputs = new ValueRef[inputExpressions.Count];
        for (int i = 0; i < inputExpressions.Count; i++)
        {
            sampleInputs[i] = await evaluator
                .EvaluateAsValueRefAsync(inputExpressions[i], frame, cancellationToken)
                .ConfigureAwait(false);
        }

        ValueRef[] sampleOverrides = optionalExpressions.Count == 0
            ? []
            : new ValueRef[optionalExpressions.Count];
        for (int i = 0; i < optionalExpressions.Count; i++)
        {
            sampleOverrides[i] = await evaluator
                .EvaluateAsValueRefAsync(optionalExpressions[i], frame, cancellationToken)
                .ConfigureAwait(false);
        }

        await models.CalibrationCoordinator.EnsureCalibratedAsync(
            modelName,
            cancellationToken: cancellationToken,
            dispatch: async batchSize =>
            {
                // Replicate the sample row N times. Same ValueRefs reused
                // across rows is safe — they're just handles into the
                // arena / sidecar.
                ValueRef[][] inputs = new ValueRef[batchSize][];
                ValueRef[][] overrides = new ValueRef[batchSize][];
                for (int r = 0; r < batchSize; r++)
                {
                    inputs[r] = sampleInputs;
                    overrides[r] = sampleOverrides;
                }

                using ModelLease lease = await models
                    .AcquireAsync(modelName, cancellationToken)
                    .ConfigureAwait(false);
                await lease.Model
                    .InferBatchAsync(inputs, overrides, context.Types, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
    }
}
