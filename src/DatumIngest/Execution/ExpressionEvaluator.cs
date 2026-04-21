using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Audio;
using DatumIngest.Functions.Image;
using DatumIngest.Functions.Video;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Evaluates AST <see cref="Expression"/> nodes against a <see cref="Row"/>.
/// Per-call context (row, source arena for reading, target arena for writing, outer
/// row) is passed through an <see cref="EvaluationFrame"/>. A backward-compatible
/// <c>Row</c> overload reuses the store supplied at construction for both arenas.
/// </summary>
public sealed partial class ExpressionEvaluator : ILambdaInvoker
{
    private readonly FunctionRegistry _functions;

    /// <summary>
    /// Persistent store used for (1) per-evaluator caches (<see cref="_inValueSetCache"/>)
    /// whose <see cref="DataValue"/> entries must outlive any single batch, and
    /// (2) the <see cref="Row"/>-only overload of
    /// <see cref="EvaluateAsync(Expression, Row, CancellationToken)"/> which constructs
    /// a default frame using this store for both the source and target arenas. Callers
    /// that want true two-arena behaviour should invoke the <see cref="EvaluationFrame"/>-based
    /// overloads instead.
    /// </summary>
    private readonly IValueStore? _store;

    /// <summary>
    /// The persistent value store this evaluator was constructed with, or <see langword="null"/>
    /// if none was supplied. Exposed so callers that don't have a separate
    /// <see cref="EvaluationFrame"/>/<see cref="InvocationFrame"/> in scope can still build one
    /// keyed off the same store.
    /// </summary>
    public IValueStore? Store => _store;

    private readonly Row? _outerRow;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Procedural variable scope chain — the visibility side of the variable
    /// substrate. Walked innermost-first when resolving an unqualified
    /// <see cref="ColumnReference"/> at evaluation time (variable-first
    /// precedence). <see langword="null"/> when the evaluator runs outside
    /// a procedural batch (every existing query path); a name that doesn't
    /// match a column then falls through to the column-not-found error.
    /// </summary>
    // Not readonly: lazily initialised by InvokeLambdaAsync when the
    // evaluator was constructed without an explicit scope. Lambda parameter
    // bindings need somewhere to land, and the operator pipeline doesn't
    // pass a scope today (procedural UDFs / DECLARE do). The lazy backing
    // here means lambda invocation works in any evaluator-bearing context;
    // procedural callers that pass a scope keep using their own.
    private VariableScope? _variableScope;

    /// <summary>
    /// Borrowed reference to the procedure-lifetime arena holding bound
    /// variable payloads. Source store for the stabilise that copies
    /// variable values out into the active <see cref="EvaluationFrame.Target"/>
    /// arena on read. Paired with <see cref="_variableScope"/> — both are
    /// non-null inside a procedural batch, both null outside it.
    /// </summary>
    private readonly IValueStore? _variableStore;

    /// <summary>
    /// Maps LET binding names to their source expressions. Used by
    /// <see cref="EvaluateStructFieldAccess"/> when the schema doesn't carry struct
    /// field metadata for a hidden <c>__destructure_N</c> binding: if the binding's
    /// original expression is a <see cref="StructLiteralExpression"/>, field names
    /// can be recovered from the AST without schema.
    /// </summary>
    private readonly IReadOnlyDictionary<string, Expression>? _letBindingExpressions;

    /// <summary>
    /// Optional per-query type registry for stamping type-ids onto struct literals and
    /// using the registry as the primary resolution path in struct field access.
    /// Null when constructed via the field-based overload without one.
    /// </summary>
    private readonly TypeRegistry? _typeRegistry;

    /// <summary>
    /// Per-query video registry that backs <see cref="DataKind.VideoFrame"/>
    /// materialisation. Threaded into every <see cref="EvaluationFrame"/> built
    /// by the convenience overloads. Null when constructed via the field-based
    /// overload without one (ad-hoc test usage that never materialises frames).
    /// </summary>
    private readonly Model.VideoRegistry? _videoRegistry;

    /// <summary>
    /// Plan-wide accountant threaded into every <see cref="EvaluationFrame"/>
    /// built by the convenience overloads. Sourced from
    /// <see cref="ExecutionContext.Accountant"/> when the context ctor is
    /// used; falls back to a private throwaway when the field-based ctor is
    /// invoked without one (test code, evaluator instances built outside a
    /// query plan).
    /// </summary>
    private readonly MemoryAccountant _accountant;

    /// <summary>
    /// The <see cref="ExecutionContext"/> the evaluator was built against.
    /// Threaded into every <see cref="EvaluationFrame"/> built by the
    /// convenience overloads so consumer scalar functions can route ambient
    /// state (sidecar registry, type registry, translations, video registry)
    /// through a single handle.
    /// </summary>
    private readonly ExecutionContext _context;

    /// <summary>
    /// The <see cref="MemoryAccountant"/> this evaluator threads into the
    /// frames it constructs. Callers building their own frames (or wrapping
    /// the evaluator in a different scope) read this to keep notifications
    /// flowing into the same counter.
    /// </summary>
    public MemoryAccountant Accountant => _accountant;

    /// <summary>
    /// The owning <see cref="ExecutionContext"/>. Operator code building
    /// its own <see cref="EvaluationFrame"/>s (e.g. ORDER BY key materialisation)
    /// passes this through the frame ctor so the ambient registries / accountant
    /// flow consistently.
    /// </summary>
    public ExecutionContext Context => _context;

    /// <summary>
    /// Compiled regex cache for case-sensitive LIKE patterns. Avoids recompiling
    /// the same SQL LIKE pattern on every row comparison.
    /// </summary>
    private readonly Dictionary<string, Regex> _likeRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiled regex cache for case-insensitive ILIKE patterns.
    /// </summary>
    private readonly Dictionary<string, Regex> _iLikeRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiled regex cache for REGEXP patterns (user-supplied regular expressions).
    /// </summary>
    private readonly Dictionary<string, Regex> _regexpCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Cached resolved DataKind for each CASE expression, computed once on first evaluation.
    /// Used to coerce branch results to a consistent type for downstream consumers.
    /// </summary>
    private readonly Dictionary<CaseExpression, DataKind?> _caseResolvedKindCache = new();

    /// <summary>
    /// Cached hash set of non-null literal values for each <see cref="InExpression"/> whose
    /// <see cref="InExpression.Values"/> are all <see cref="LiteralExpression"/>.
    /// Built on first evaluation to convert O(n) linear scans into O(1) hash lookups.
    /// The <see langword="bool"/> tracks whether any value in the list was <see langword="null"/>
    /// (needed for SQL three-valued logic).
    /// </summary>
    private readonly Dictionary<InExpression, (HashSet<DataValue> NonNullValues, bool HasNull)> _inValueSetCache = new();

    /// <summary>
    /// Per-call-site cache of which <see cref="FunctionCallExpression"/> nodes have already
    /// passed <see cref="IScalarFunction.ValidateArguments"/>. The validation depends only on
    /// the static argument kinds (which are stable for the duration of a query), so it's safe
    /// to run exactly once per call site on first invocation. This catches errors like
    /// <c>blur(file)</c> (arity) and <c>blur(file, 'x')</c> (type) at the first row instead of
    /// letting the function body crash with an opaque <c>IndexOutOfRangeException</c> /
    /// <c>InvalidOperationException</c>.
    /// </summary>
    private readonly HashSet<FunctionCallExpression> _validatedScalarCalls = new();

    /// <summary>
    /// Per-call-site cache of resolved <see cref="ParameterCheck"/> bindings. Populated
    /// at the same time as <see cref="_validatedScalarCalls"/> — when we first match a
    /// call site to a signature variant we walk that variant's <see cref="ParameterSpec"/>
    /// list and pre-resolve only the slots that carry a <see cref="ParameterCheck"/>.
    /// An empty array means "matched, no checks to run"; the key being present at all
    /// signals "no need to re-resolve on subsequent invocations." Per-row dispatch
    /// just walks the cached array and invokes <see cref="ParameterCheck.Validate"/>.
    /// </summary>
    private readonly Dictionary<FunctionCallExpression, ParameterCheckBinding[]> _siteParameterChecks = new();

    /// <summary>
    /// Resolved binding for a single parameter slot that declared a <see cref="ParameterCheck"/>.
    /// </summary>
    private readonly record struct ParameterCheckBinding(int ArgIndex, string ParamName, ParameterCheck Check);

    /// <summary>
    /// Constructs an evaluator bound to <paramref name="context"/>. Every shared
    /// dependency (function registry, sidecar registry, type registry, accountant,
    /// video registry, optional variable scope / store) is read off the context.
    /// Operator-specific extras (<paramref name="sourceSchema"/>,
    /// <paramref name="letBindingExpressions"/>) stay as explicit parameters since
    /// they aren't on the context.
    /// </summary>
    /// <param name="context">Execution context the evaluator runs under. Required.</param>
    /// <param name="sourceSchema">Optional query output schema used to resolve
    /// struct field names at evaluation time.</param>
    /// <param name="letBindingExpressions">Optional map of LET binding names to
    /// their source expressions, used as fallback when struct field access
    /// cannot be resolved via <paramref name="sourceSchema"/>.</param>
    internal ExpressionEvaluator(
        ExecutionContext context,
        Schema? sourceSchema = null,
        IReadOnlyDictionary<string, Expression>? letBindingExpressions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _functions = context.FunctionRegistry;
        _store = context.Store;
        _outerRow = context.OuterRow;
        _sourceSchema = sourceSchema;
        _letBindingExpressions = letBindingExpressions;
        _variableScope = context.VariableScope;
        _variableStore = context.VariableStore;
        _typeRegistry = context.Types;
        _accountant = context.Accountant;
        _videoRegistry = context.VideoRegistry;
    }

    /// <summary>
    /// The per-query <see cref="Model.VideoRegistry"/> threaded through this
    /// evaluator. Exposed for operators that build their own
    /// <see cref="EvaluationFrame"/>s (e.g. ORDER BY key materialisation) and
    /// need to preserve the registry so downstream scalar functions can
    /// resolve <see cref="DataKind.VideoFrame"/> handles. Null when the
    /// evaluator was constructed without a context.
    /// </summary>
    public Model.VideoRegistry? VideoRegistry => _videoRegistry;

    /// <summary>Resolves a string DataValue against the frame's source arena.</summary>
    private static string Str(DataValue v, EvaluationFrame frame) => v.AsString(frame.Source);

    // ──────────────────── Public entry points ────────────────────

    /// <summary>
    /// Builds an <see cref="EvaluationFrame"/> for evaluating expressions against
    /// <paramref name="row"/> through this evaluator. The frame inherits the
    /// evaluator's context (ambient registries, accountant) and is wired as a
    /// lambda dispatcher back to this evaluator so consumer functions that call
    /// <see cref="ILambdaInvoker.InvokeLambdaAsync"/> route through here.
    /// </summary>
    /// <param name="row">The row to evaluate against.</param>
    /// <param name="store">Optional source arena for the frame. Defaults to the
    /// evaluator's context store, which is the right choice for the common
    /// "evaluate against my own store" path. Pass an explicit arena when the
    /// row's payloads live in a different arena (e.g. a scan batch's arena).</param>
    public EvaluationFrame CreateFrame(Row row, IValueStore? store = null)
    {
        IValueStore effectiveStore = store ?? _store ?? ThrowStoreRequired();
        return new EvaluationFrame(row, effectiveStore, _context, outerRow: _outerRow, lambdaInvoker: this);
    }

    /// <summary>
    /// Builds an <see cref="EvaluationFrame"/> with distinct source / target
    /// arenas, wired to this evaluator as the lambda dispatcher. Used by
    /// operators whose target arena (the output batch's) differs from the
    /// source row's arena.
    /// </summary>
    public EvaluationFrame CreateFrame(Row row, IValueStore source, IValueStore target)
        => new EvaluationFrame(row, source, target, _context, outerRow: _outerRow, lambdaInvoker: this);

    /// <summary>
    /// Evaluates an expression tree against the given row, using the store supplied at
    /// construction for both reads and writes. Convenience overload for callers that don't
    /// yet distinguish source and target arenas.
    /// </summary>
    public ValueTask<DataValue> EvaluateAsync(
        Expression expression, Row row, CancellationToken cancellationToken = default)
    {
        EvaluationFrame frame = CreateFrame(row);
        return EvaluateAsync(expression, frame, cancellationToken);
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean, using the store
    /// supplied at construction. Convenience overload.
    /// </summary>
    public ValueTask<bool> EvaluateAsBooleanAsync(
        Expression expression, Row row, CancellationToken cancellationToken = default)
    {
        EvaluationFrame frame = CreateFrame(row);
        return EvaluateAsBooleanAsync(expression, frame, cancellationToken);
    }

    private static IValueStore ThrowStoreRequired() =>
        throw new InvalidOperationException(
            "ExpressionEvaluator was constructed without a store; use the EvaluationFrame overload or supply a store.");

    /// <summary>
    /// Evaluates an expression tree against the given frame and returns the result.
    /// </summary>
    /// <param name="expression">The AST expression to evaluate.</param>
    /// <param name="frame">Row + arenas + outer row for this evaluation.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The computed result.</returns>
    public async ValueTask<DataValue> EvaluateAsync(
        Expression expression, EvaluationFrame frame, CancellationToken cancellationToken = default)
    {
        // Auto-attach this evaluator as the frame's LambdaInvoker if a caller
        // constructed the frame without one. Operators across the engine build
        // EvaluationFrames in many places (~27 sites); rather than threading
        // `lambdaInvoker:` through every constructor call, we centralise the
        // wiring here so the frame entering evaluation always has a usable
        // invoker. The auto-attach is a no-op when the caller already set one.
        if (frame.LambdaInvoker is null)
        {
            frame = frame.WithLambdaInvoker(this);
        }

        try
        {
            return expression switch
            {
                // Hoisted literals: produced by LiteralHoister before execution so the
                // DataValue is already materialized. Zero-cost read compared to the
                // switch-on-CLR-type + FromX() path taken by LiteralExpression below.
                LiteralValueExpression hoisted => hoisted.Value,
                LiteralExpression literal => EvaluateLiteral(literal, frame),
                ColumnReference column => EvaluateColumn(column, frame),
                BinaryExpression binary => await EvaluateBinaryAsync(binary, frame, cancellationToken).ConfigureAwait(false),
                UnaryExpression unary => await EvaluateUnaryAsync(unary, frame, cancellationToken).ConfigureAwait(false),
                FunctionCallExpression function => await EvaluateFunctionAsync(function, frame, cancellationToken).ConfigureAwait(false),
                InlineAccessorExpression inlineAccessor => await EvaluateInlineAccessorAsync(inlineAccessor, frame, cancellationToken).ConfigureAwait(false),
                InExpression inExpr => await EvaluateInAsync(inExpr, frame, cancellationToken).ConfigureAwait(false),
                BetweenExpression between => await EvaluateBetweenAsync(between, frame, cancellationToken).ConfigureAwait(false),
                IsNullExpression isNull => await EvaluateIsNullAsync(isNull, frame, cancellationToken).ConfigureAwait(false),
                CastExpression cast => await EvaluateCastAsync(cast, frame, cancellationToken).ConfigureAwait(false),
                AtTimeZoneExpression atz => await EvaluateAtTimeZoneAsync(atz, frame, cancellationToken).ConfigureAwait(false),
                CaseExpression caseExpr => await EvaluateCaseAsync(caseExpr, frame, cancellationToken).ConfigureAwait(false),
                LikeExpression like => await EvaluateLikeEscapeAsync(like, frame, cancellationToken).ConfigureAwait(false),
                WindowFunctionCallExpression window => throw new InvalidOperationException(
                    $"Window function '{window.FunctionName}' was not rewritten by the query planner. " +
                    "Window functions must be used with an OVER clause and are only allowed in SELECT and ORDER BY."),
                ScanExpression => throw new InvalidOperationException(
                    "SCAN expression was not rewritten by the query planner. " +
                    "SCAN expressions must appear in SELECT or LET bindings."),
                SubqueryExpression => throw new InvalidOperationException(
                    "Subquery expression was not rewritten by the query planner."),
                InSubqueryExpression => throw new InvalidOperationException(
                    "IN (SELECT ...) was not rewritten by the query planner into a semi-join."),
                ExistsExpression => throw new InvalidOperationException(
                    "[NOT] EXISTS (SELECT ...) was not rewritten by the query planner into a semi-join."),
                CurrentTimestampExpression ct => EvaluateTemporalConstant(ct),
                ParameterExpression parameter => throw new InvalidOperationException(
                    $"Unbound parameter '${parameter.Name}'. Parameters must be bound before evaluation."),
                LambdaExpression => throw new InvalidOperationException(
                    "Lambda expressions cannot be lowered to a DataValue (lambdas carry a "
                    + "managed-payload closure that only fits ValueRef). Call sites that "
                    + "consume lambdas must evaluate via EvaluateAsValueRefAsync; this "
                    + "indicates a code path that expected a non-lambda expression."),
                StructLiteralExpression structLiteral => await EvaluateStructLiteralAsync(structLiteral, frame, cancellationToken).ConfigureAwait(false),
                IndexAccessExpression indexAccess => await EvaluateIndexAccessAsync(indexAccess, frame, cancellationToken).ConfigureAwait(false),
                TypeLiteralExpression typeLiteral => EvaluateTypeLiteral(typeLiteral),
                _ => throw new InvalidOperationException(
                    $"Unsupported expression type: {expression.GetType().Name}.")
            };
        }
        catch (Exception ex) when (ex is not ExpressionEvaluationException)
        {
            SourceSpan? span = expression.TryGetSourceSpan();
            if (span is not null)
            {
                throw new ExpressionEvaluationException(
                    $"[Line {span.Line}, Col {span.Column}] {ex.Message}", span, ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean (truthy/falsy).
    /// Null is treated as false. Scalar 0 is false; non-zero is true.
    /// </summary>
    public async ValueTask<bool> EvaluateAsBooleanAsync(
        Expression expression, EvaluationFrame frame, CancellationToken cancellationToken = default)
    {
        ValueRef result = await EvaluateAsValueRefAsync(expression, frame, cancellationToken).ConfigureAwait(false);

        if (result.IsNull)
        {
            return false;
        }

        return result.Kind switch
        {
            DataKind.Boolean => result.AsBoolean(),
            DataKind.Float32 => result.AsFloat32() != 0f,
            DataKind.Float64 => result.AsFloat64() != 0.0,
            DataKind.UInt8 => result.AsUInt8() != 0,
            DataKind.Int8 => result.AsInt8() != 0,
            DataKind.Int16 => result.AsInt16() != 0,
            DataKind.UInt16 => result.AsUInt16() != 0,
            DataKind.Int32 => result.AsInt32() != 0,
            DataKind.UInt32 => result.AsUInt32() != 0,
            DataKind.Int64 => result.AsInt64() != 0,
            DataKind.UInt64 => result.AsUInt64() != 0,
            DataKind.String => !string.IsNullOrEmpty(result.AsString()),
            _ => true,
        };
    }






}
