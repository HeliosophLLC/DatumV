using Heliosoph.DatumV.Functions;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Optional hook for observing <c>models.X(...)</c> invocations during query
/// execution. The shell uses this to power <c>.trace on</c> — a per-dispatch
/// log of which model is running, what inputs it's receiving, and how long
/// each call takes. Production deployments can attach a different
/// implementation (metrics emission, structured logging, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Invoked by <see cref="Operators.ModelInvocationOperator"/> immediately
/// before and after each <c>IModel.InferBatchAsync</c> call.
/// Tracers should not throw; the operator does not catch tracer exceptions
/// and a faulty tracer can fail the whole query.
/// </para>
/// <para>
/// <strong>Why one tracer per execution context.</strong> A single shell
/// session may run many queries. The tracer is attached at context-build
/// time so it observes only the queries the user opted into (via
/// <c>.trace on</c>); turning trace off detaches the tracer from
/// subsequently built contexts.
/// </para>
/// </remarks>
public interface IModelInvocationTracer
{
    /// <summary>
    /// Called once per dispatch chunk, before the model's
    /// <c>InferBatchAsync</c> begins. <paramref name="inputs"/> and
    /// <paramref name="overrides"/> are the same row-major lists the model
    /// will receive — implementations should treat them as read-only.
    /// </summary>
    /// <param name="modelName">Catalog-visible name (e.g. <c>"sdxl_turbo"</c>).</param>
    /// <param name="rowCount">Number of rows in this dispatch chunk.</param>
    /// <param name="inputs">Per-row input columns, row-major.</param>
    /// <param name="overrides">Per-row optional positional args, row-major.</param>
    void OnDispatchStarted(
        string modelName,
        int rowCount,
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides);

    /// <summary>
    /// Called once per dispatch chunk, after <c>InferBatchAsync</c>
    /// completes successfully (including its post-processing scatter
    /// step). <paramref name="elapsed"/> covers just the model dispatch,
    /// not the surrounding evaluator / arena work.
    /// </summary>
    void OnDispatchCompleted(
        string modelName,
        int rowCount,
        TimeSpan elapsed);

    /// <summary>
    /// Called when the model's <c>InferBatchAsync</c> throws.
    /// <paramref name="elapsed"/> is the time from start to throw.
    /// Tracers should not rethrow; the operator already propagates the
    /// original exception.
    /// </summary>
    void OnDispatchFailed(
        string modelName,
        int rowCount,
        TimeSpan elapsed,
        Exception exception);
}
