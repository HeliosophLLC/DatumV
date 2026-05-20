using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Managed payload backing a <see cref="DataKind.Lambda"/>
/// <see cref="ValueRef"/>: the lambda's AST body plus a snapshot of the row
/// context it was constructed in (closure captures).
/// </summary>
/// <remarks>
/// <para>
/// Lambdas in this engine are <em>row-scoped</em>. A <see cref="LambdaValue"/>
/// is constructed when the evaluator encounters a
/// <see cref="LambdaExpression"/> in a context that flows it as a value
/// (e.g. as an argument to a function whose signature declares a
/// <see cref="DataKind.Lambda"/> parameter). The current frame's
/// <see cref="Row"/> is snapshotted at that point so the lambda body's
/// free variables resolve consistently when the lambda is later invoked,
/// possibly multiple times.
/// </para>
/// <para>
/// <strong>Persistence is refused.</strong> <see cref="ValueRef.ToDataValue"/>
/// throws when the materialised payload is a <see cref="LambdaValue"/>, so
/// a SELECT-output column, INSERT row, or any other arena-write path
/// reports a meaningful error rather than silently producing a broken value.
/// Lambdas exist only as intra-query intermediate values.
/// </para>
/// <para>
/// Construction is cheap: a record allocation plus a reference to the
/// already-built <see cref="Row"/> struct (which is itself just two managed
/// references). No deep copy of the captured environment is performed.
/// </para>
/// </remarks>
/// <param name="Body">The lambda's AST body — the expression evaluated each invocation.</param>
/// <param name="Parameters">Parameter names declared by the lambda (in order). Mirrors <c>Body.Parameters</c>; cached here for fast lookup during invocation.</param>
/// <param name="Captures">The row snapshot at construction time. The lambda body resolves free variables against this row.</param>
public sealed record LambdaValue(
    LambdaExpression Body,
    IReadOnlyList<string> Parameters,
    Row Captures)
{
    /// <summary>
    /// Constructs a <see cref="LambdaValue"/> from a parsed
    /// <see cref="LambdaExpression"/> and the current frame's
    /// <see cref="Row"/>. The parameter list is taken from the AST directly;
    /// the captures snapshot is the supplied row.
    /// </summary>
    public static LambdaValue Capture(LambdaExpression body, Row captures) =>
        new(body, body.Parameters, captures);
}
