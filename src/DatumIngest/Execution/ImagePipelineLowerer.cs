using DatumIngest.Functions;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time pass that recognises calls to the <c>img(source, lambda)</c> function
/// and rewrites them into <see cref="FusedImagePipelineExpression"/> nodes that the
/// runtime evaluator decodes/encodes exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Recognised lambda body shapes (where <c>f</c> is the lambda parameter):
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>f</c> — identity. Lowers to <c>source</c> itself; no pipeline node emitted.
///   </description></item>
///   <item><description>
///     <c>transform_a(transform_b(... f, args ...), args)</c> — zero or more transforms.
///     Each function must implement <see cref="IImagePipelineFunction"/>.
///   </description></item>
///   <item><description>
///     <c>sink(transform_a(... f, args ...), args)</c> — optionally terminated by an
///     <see cref="IImagePipelineSink"/>. Sinks may appear only at the outermost position.
///   </description></item>
/// </list>
/// <para>
/// Anything else — a non-pipeline function, a literal, a reference to a column other than
/// the lambda parameter — fails fast with a clear message at plan time, never reaching the
/// runtime evaluator. Auxiliary (non-image) arguments are left as ordinary
/// <see cref="Expression"/> trees and evaluated per-row against the outer frame.
/// </para>
/// </remarks>
public static class ImagePipelineLowerer
{
    /// <summary>The SQL function name this pass triggers on.</summary>
    private const string ImageFunctionName = "img";

    /// <summary>
    /// Recursively rewrites <paramref name="expression"/>, lowering every
    /// <c>img(source, lambda)</c> call inside it.
    /// </summary>
    /// <param name="expression">The expression to rewrite.</param>
    /// <param name="functions">Function registry — used to resolve names in the lambda body.</param>
    public static Expression Lower(Expression expression, FunctionRegistry functions) =>
        expression switch
        {
            // Recurse into composite shapes; rewrite the children, then handle the
            // current node. This mirrors LiteralHoister's structure so the two passes
            // compose cleanly.
            FunctionCallExpression fn when string.Equals(fn.FunctionName, ImageFunctionName, StringComparison.OrdinalIgnoreCase)
                => LowerImageCall(fn, functions),

            FunctionCallExpression fn => LowerOrAutoFuseFunctionCall(fn, functions),

            BinaryExpression b => b with
            {
                Left = Lower(b.Left, functions),
                Right = Lower(b.Right, functions),
            },

            UnaryExpression u => u with { Operand = Lower(u.Operand, functions) },

            CastExpression c => c with { Expression = Lower(c.Expression, functions) },

            InExpression i => i with
            {
                Expression = Lower(i.Expression, functions),
                Values = LowerList(i.Values, functions),
            },

            BetweenExpression bt => bt with
            {
                Expression = Lower(bt.Expression, functions),
                Low = Lower(bt.Low, functions),
                High = Lower(bt.High, functions),
            },

            IsNullExpression n => n with { Expression = Lower(n.Expression, functions) },

            CaseExpression ce => ce with
            {
                Operand = ce.Operand is null ? null : Lower(ce.Operand, functions),
                WhenClauses = ce.WhenClauses
                    .Select(w => new WhenClause(Lower(w.Condition, functions), Lower(w.Result, functions)))
                    .ToList(),
                ElseResult = ce.ElseResult is null ? null : Lower(ce.ElseResult, functions),
            },

            LikeExpression like => like with
            {
                Expression = Lower(like.Expression, functions),
                Pattern = Lower(like.Pattern, functions),
                EscapeCharacter = Lower(like.EscapeCharacter, functions),
            },

            AtTimeZoneExpression atz => atz with
            {
                Expression = Lower(atz.Expression, functions),
                TimeZone = Lower(atz.TimeZone, functions),
            },

            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields
                    .Select(f => new StructField(f.Name, Lower(f.Value, functions)))
                    .ToList(),
            },

            IndexAccessExpression ia => ia with
            {
                Source = Lower(ia.Source, functions),
                Index = Lower(ia.Index, functions),
            },

            // Lambda bodies inside non-image higher-order functions (array_map etc.) may
            // also contain img() calls — recurse so a chain like
            //   array_map(images, img => img(img, f => f.blur(3)))
            // gets fully lowered.
            LambdaExpression lam => lam with { Body = Lower(lam.Body, functions) },

            _ => expression,
        };

    private static IReadOnlyList<Expression> LowerList(
        IReadOnlyList<Expression> list, FunctionRegistry functions)
    {
        if (list.Count == 0) return list;

        Expression[] result = new Expression[list.Count];
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            Expression original = list[i];
            Expression lowered = Lower(original, functions);
            result[i] = lowered;
            if (!ReferenceEquals(original, lowered)) changed = true;
        }
        return changed ? result : list;
    }

    /// <summary>
    /// Handles a non-<c>img()</c> function call. After recursively lowering arguments, if
    /// the function is itself a pipeline-eligible image function we synthesise (or extend)
    /// a <see cref="FusedImagePipelineExpression"/>. This is the auto-fusion path: bare
    /// calls like <c>blur(file, 5)</c> become single-stage pipelines, and chained calls
    /// like <c>brightness_mean(blur(file, 5))</c> fuse into <c>source + [blur] + sink</c>
    /// with a single decode/encode at the boundaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Auto-fusion eliminates the need for the standalone (non-pipeline) execution path on
    /// image functions. Once every reachable call site is lowered, image functions never
    /// dispatch through <see cref="IScalarFunction.Execute(ReadOnlySpan{DataValue})"/>
    /// at runtime — the pipeline evaluator handles them all uniformly.
    /// </para>
    /// </remarks>
    private static Expression LowerOrAutoFuseFunctionCall(
        FunctionCallExpression call, FunctionRegistry functions)
    {
        IReadOnlyList<Expression> loweredArgs = LowerList(call.Arguments, functions);

        IScalarFunction? func = functions.TryGetScalar(call.FunctionName);

        if (func is IImagePipelineSink sink && loweredArgs.Count >= 1)
        {
            return AutoFuseSink(sink, loweredArgs);
        }

        if (func is IImagePipelineFunction transform && loweredArgs.Count >= 1)
        {
            return AutoFuseTransform(transform, loweredArgs);
        }

        // Not a pipeline function — keep the regular call shape with lowered children.
        return ReferenceEquals(loweredArgs, call.Arguments)
            ? call
            : call with { Arguments = loweredArgs };
    }

    /// <summary>
    /// Builds (or extends) a <see cref="FusedImagePipelineExpression"/> for a transform
    /// whose first argument is the image input. If that argument is already a fused
    /// pipeline, append this transform to its <see cref="FusedImagePipelineExpression.Transforms"/>;
    /// otherwise start a fresh pipeline rooted at the argument.
    /// </summary>
    private static Expression AutoFuseTransform(
        IImagePipelineFunction transform, IReadOnlyList<Expression> loweredArgs)
    {
        Expression imageArg = loweredArgs[0];
        IReadOnlyList<Expression> auxArgs = SkipFirst(loweredArgs);
        ValidateTransformAuxiliary(transform, auxArgs);

        PipelineStage newStage = new(transform, auxArgs);

        if (imageArg is FusedImagePipelineExpression existing)
        {
            if (existing.TerminalSink is not null)
            {
                throw new ArgumentException(
                    $"Image pipeline transform '{transform.Name}' cannot be applied to the " +
                    $"result of a sink ('{existing.TerminalSink.Function.Name}'). Sinks must " +
                    $"be terminal in a pipeline chain.");
            }

            List<PipelineStage> stages = new(existing.Transforms.Count + 1);
            stages.AddRange(existing.Transforms);
            stages.Add(newStage);
            return existing with { Transforms = stages };
        }

        return new FusedImagePipelineExpression(
            Source: imageArg,
            Transforms: [newStage],
            TerminalSink: null,
            OutputFormatOverride: null,
            ResultKind: DataKind.Image);
    }

    /// <summary>
    /// Builds (or extends) a <see cref="FusedImagePipelineExpression"/> terminating in
    /// the supplied sink. If the image argument is already a fused pipeline, attach this
    /// sink as its terminal. Sinks may not be applied on top of other sinks.
    /// </summary>
    private static Expression AutoFuseSink(
        IImagePipelineSink sink, IReadOnlyList<Expression> loweredArgs)
    {
        Expression imageArg = loweredArgs[0];
        IReadOnlyList<Expression> auxArgs = SkipFirst(loweredArgs);
        ValidateSinkAuxiliary(sink, auxArgs);

        PipelineSink newSink = new(sink, auxArgs);

        if (imageArg is FusedImagePipelineExpression existing)
        {
            if (existing.TerminalSink is not null)
            {
                throw new ArgumentException(
                    $"Image pipeline sink '{sink.Name}' cannot be applied to the result of " +
                    $"another sink ('{existing.TerminalSink.Function.Name}').");
            }

            return existing with { TerminalSink = newSink, ResultKind = sink.ResultKind };
        }

        return new FusedImagePipelineExpression(
            Source: imageArg,
            Transforms: [],
            TerminalSink: newSink,
            OutputFormatOverride: null,
            ResultKind: sink.ResultKind);
    }

    /// <summary>
    /// Lowers an <c>img(source, lambda)</c> call. Validates the call shape, walks the
    /// lambda body to collect transforms and an optional terminal sink, and emits a
    /// <see cref="FusedImagePipelineExpression"/> — except for the identity case
    /// <c>f =&gt; f</c>, which short-circuits to the source.
    /// </summary>
    private static Expression LowerImageCall(FunctionCallExpression call, FunctionRegistry functions)
    {
        if (call.Arguments.Count != 2)
        {
            throw new ArgumentException(
                $"img() expects exactly 2 arguments (source, lambda); got {call.Arguments.Count}.");
        }

        // The lambda may itself contain nested img() calls; lower the source first too
        // so any inner img() pipelines are already FusedImagePipelineExpressions when
        // we walk the body.
        Expression source = Lower(call.Arguments[0], functions);

        if (call.Arguments[1] is not LambdaExpression lambda)
        {
            throw new ArgumentException(
                "img() second argument must be a lambda (e.g. f => f.blur(5)).");
        }

        if (lambda.Parameters.Count != 1)
        {
            throw new ArgumentException(
                $"img() lambda must take exactly one parameter; got {lambda.Parameters.Count}.");
        }

        string parameterName = lambda.Parameters[0];

        // Walk the body bottom-up. The outermost call (if any) may be a sink; everything
        // below must be IImagePipelineFunction transforms terminating at a reference to
        // the lambda parameter.
        PipelineSink? sink = null;
        List<PipelineStage> transforms = new();
        Expression cursor = lambda.Body;

        // Step 1: optional terminal sink at the outermost position.
        if (cursor is FunctionCallExpression outer)
        {
            IScalarFunction? func = functions.TryGetScalar(outer.FunctionName);
            if (func is IImagePipelineSink pipelineSink)
            {
                if (outer.Arguments.Count == 0)
                {
                    throw new ArgumentException(
                        $"img() pipeline sink '{outer.FunctionName}' must take the image as its first argument.");
                }

                IReadOnlyList<Expression> auxiliary = LowerList(SkipFirst(outer.Arguments), functions);
                ValidateSinkAuxiliary(pipelineSink, auxiliary);
                sink = new PipelineSink(pipelineSink, auxiliary);
                cursor = outer.Arguments[0];
            }
        }

        // Step 2: zero or more transform stages, each wrapping its predecessor as arg[0].
        while (cursor is FunctionCallExpression stageCall)
        {
            IScalarFunction? func = functions.TryGetScalar(stageCall.FunctionName);
            if (func is not IImagePipelineFunction transform)
            {
                throw new ArgumentException(
                    $"img() pipeline body may only contain pipeline-compatible image " +
                    $"functions; '{stageCall.FunctionName}' is not registered as an " +
                    $"IImagePipelineFunction.");
            }

            if (stageCall.Arguments.Count == 0)
            {
                throw new ArgumentException(
                    $"img() pipeline transform '{stageCall.FunctionName}' must take the image as its first argument.");
            }

            IReadOnlyList<Expression> auxiliary = LowerList(SkipFirst(stageCall.Arguments), functions);
            ValidateTransformAuxiliary(transform, auxiliary);

            // Recorded outer-to-inner; we'll reverse before constructing the final node so
            // the runtime can apply them in source-to-result order.
            transforms.Add(new PipelineStage(transform, auxiliary));
            cursor = stageCall.Arguments[0];
        }

        // Step 3: the chain must terminate at a reference to the lambda parameter.
        if (cursor is not ColumnReference paramRef
            || paramRef.TableName is not null
            || !string.Equals(paramRef.ColumnName, parameterName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"img() pipeline body must terminate at the lambda parameter '{parameterName}'; " +
                $"got an expression of kind {cursor.GetType().Name}. Pipeline transforms must " +
                $"thread '{parameterName}' through their first argument.");
        }

        // Identity short-circuit: f => f.
        if (transforms.Count == 0 && sink is null)
        {
            return source;
        }

        transforms.Reverse();

        DataKind resultKind = sink is not null ? sink.Function.ResultKind : DataKind.Image;
        return new FusedImagePipelineExpression(
            Source: source,
            Transforms: transforms,
            TerminalSink: sink,
            OutputFormatOverride: null,
            ResultKind: resultKind);
    }

    /// <summary>
    /// Returns a view of <paramref name="arguments"/> with index 0 dropped — that's the
    /// implicit image arg, threaded through the pipeline; the remainder are auxiliary args.
    /// </summary>
    private static IReadOnlyList<Expression> SkipFirst(IReadOnlyList<Expression> arguments)
    {
        if (arguments.Count <= 1) return [];
        Expression[] tail = new Expression[arguments.Count - 1];
        for (int i = 1; i < arguments.Count; i++) tail[i - 1] = arguments[i];
        return tail;
    }

    /// <summary>
    /// Resolves auxiliary argument kinds best-effort and asks the transform to validate them.
    /// Any unresolvable kind is reported as <see cref="DataKind.Unknown"/> so the function
    /// can decide whether to accept (e.g. for tolerant numeric-coercing args) or reject.
    /// </summary>
    private static void ValidateTransformAuxiliary(
        IImagePipelineFunction transform, IReadOnlyList<Expression> auxiliary)
    {
        DataKind[] kinds = ResolveAuxiliaryKinds(auxiliary);
        try
        {
            transform.ValidateAuxiliaryArguments(kinds);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"img() transform '{transform.Name}' rejected its arguments: {ex.Message}", ex);
        }
    }

    /// <inheritdoc cref="ValidateTransformAuxiliary" />
    private static void ValidateSinkAuxiliary(
        IImagePipelineSink sink, IReadOnlyList<Expression> auxiliary)
    {
        DataKind[] kinds = ResolveAuxiliaryKinds(auxiliary);
        try
        {
            sink.ValidateAuxiliaryArguments(kinds);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"img() sink '{sink.Name}' rejected its arguments: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Best-effort kind resolution: we don't have a source schema here so column refs and
    /// non-literal sub-expressions resolve to <see cref="DataKind.Unknown"/>. Pipeline
    /// functions that need precise typing should be permissive (e.g. accept any numeric
    /// kind) and rely on the runtime widening helpers.
    /// </summary>
    private static DataKind[] ResolveAuxiliaryKinds(IReadOnlyList<Expression> auxiliary)
    {
        DataKind[] kinds = new DataKind[auxiliary.Count];
        for (int i = 0; i < auxiliary.Count; i++)
        {
            kinds[i] = auxiliary[i] switch
            {
                LiteralValueExpression lv => lv.Value.Kind,
                LiteralExpression le => le.Value switch
                {
                    sbyte => DataKind.Int8,
                    short => DataKind.Int16,
                    int => DataKind.Int32,
                    long => DataKind.Int64,
                    float => DataKind.Float32,
                    double => DataKind.Float64,
                    string => DataKind.String,
                    bool => DataKind.Boolean,
                    _ => DataKind.Unknown,
                },
                _ => DataKind.Unknown,
            };
        }
        return kinds;
    }
}
