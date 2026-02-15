namespace DatumIngest.Functions;

/// <summary>
/// Declares the procedural-context a scalar function requires to be
/// callable. Read at plan time so call sites in the wrong context fail
/// before any rows are scanned, and surfaced through
/// <c>datum_catalog.functions.body_scope</c> so users can discover what's
/// callable where via introspection.
/// </summary>
/// <remarks>
/// <para>
/// The runtime guard inside the function (e.g.
/// <c>InferFunction.ExecuteAsync</c> checking <c>frame.CurrentModel</c>)
/// stays as defense-in-depth — anything that bypasses planning still
/// hits a hard floor. The <see cref="ModelBody"/> requirement only
/// triggers the plan-time check; runtime context is what actually makes
/// the call meaningful.
/// </para>
/// </remarks>
public enum BodyScopeRequirement
{
    /// <summary>Callable from any expression context. Default for every built-in scalar.</summary>
    None,

    /// <summary>
    /// Callable only from inside a <c>CREATE [OR REPLACE] MODEL ... AS BEGIN ... END</c>
    /// body, where <see cref="DatumIngest.Execution.EvaluationFrame.CurrentModel"/> is
    /// non-null. <c>infer()</c> is the v1 user; future body-scoped scalars
    /// (e.g. session-name resolution helpers) reuse the same flag.
    /// </summary>
    ModelBody,
}
