using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Plan-time helper that checks a <see cref="LambdaExpression"/> argument
/// against a <see cref="LambdaMatcher"/>'s declared signature
/// expectations. The <see cref="DataKindMatcher"/> layer only validates
/// kind compatibility (the value is of kind <see cref="Heliosoph.DatumV.Model.DataKind.Lambda"/>);
/// this validator handles the structural side — parameter count, the
/// declared parameter names suggest-or-pin question, and the context's
/// existence in the registry. Use <c>DataKindMatcher.Lambda(...)</c> on
/// the parameter side to build the matcher this validator inspects.
/// </summary>
/// <remarks>
/// <para>
/// Wired into function-call validation at plan time. When a function
/// declares a <see cref="LambdaMatcher"/> parameter and the call site
/// supplies a <see cref="LambdaExpression"/> argument, the planner calls
/// <see cref="Validate"/> to produce a strongly-worded diagnostic when
/// the lambda's shape doesn't match the consumer's expectations. The
/// return-kind side is intentionally <em>not</em> checked here —
/// validating the body's inferred return kind requires a recursive type
/// resolution that the planner does separately as part of its standard
/// expression-typing pass.
/// </para>
/// <para>
/// Parameter <em>names</em> in the user's lambda may differ from the
/// names declared by the context: <c>(u) -&gt; u + 1</c> is accepted in
/// an animation context whose canonical parameter is <c>t</c>. The
/// names declared by the context are the suggestions the language
/// server pre-fills on completion; the runtime binds by position.
/// </para>
/// </remarks>
public static class LambdaSignatureValidator
{
    /// <summary>
    /// Validates <paramref name="lambda"/> against <paramref name="matcher"/>.
    /// Throws <see cref="FunctionArgumentException"/> with a diagnostic
    /// message on mismatch.
    /// </summary>
    /// <param name="functionName">
    /// Name of the function whose parameter accepts the lambda. Surfaced in
    /// the error message so the user knows which call site is at fault.
    /// </param>
    /// <param name="parameterName">
    /// Name of the parameter slot accepting the lambda. Surfaced in the
    /// error message.
    /// </param>
    /// <param name="lambda">The supplied lambda expression.</param>
    /// <param name="matcher">The consumer's declared expectations.</param>
    /// <param name="contexts">
    /// Function-context registry used to look up the matcher's
    /// <see cref="LambdaMatcher.ContextName"/> and read its declared
    /// parameter list. <see langword="null"/> means "no registry available";
    /// the validator then skips the parameter-count check (matcher with a
    /// context name but no registry can't enforce the count without the
    /// canonical parameter list).
    /// </param>
    public static void Validate(
        string functionName,
        string parameterName,
        LambdaExpression lambda,
        LambdaMatcher matcher,
        FunctionContextRegistry? contexts)
    {
        if (matcher.ContextName is not null)
        {
            FunctionContextDescriptor? context = contexts?.TryGet(matcher.ContextName);
            if (context is null)
            {
                if (contexts is null)
                {
                    // Skip count check when no registry is available — the
                    // canonical parameter list lives in the context, so without
                    // a registry we have no source of truth.
                    return;
                }
                throw new FunctionArgumentException(
                    functionName,
                    $"parameter '{parameterName}' accepts a lambda in context "
                    + $"'{matcher.ContextName}', but no such context is registered. "
                    + "This is a bootstrap-order bug: ensure the context type is "
                    + "registered via FunctionContextRegistry.Register<T>() at startup.");
            }

            int expected = context.Parameters.Count;
            int actual = lambda.Parameters.Count;
            if (expected != actual)
            {
                string expectedNames = expected == 0
                    ? "no arguments"
                    : string.Join(", ", context.Parameters.Select(p => $"{p.Name}: {p.Kind}"));
                throw new FunctionArgumentException(
                    functionName,
                    $"parameter '{parameterName}' expects a lambda taking {expected} "
                    + $"argument(s) ({expectedNames}); got a lambda with {actual} parameter(s) "
                    + $"({string.Join(", ", lambda.Parameters)}).");
            }
            // Parameter names: the context's names are advisory (LS pre-fill).
            // The user may rename. No enforcement here.
        }
        // Unscoped lambda: no context-defined parameter list to validate
        // against. The function's own signature declares whatever it expects;
        // the consumer's ExecuteAsync will receive whatever shape it gets and
        // can complain itself if it cares.
    }
}
