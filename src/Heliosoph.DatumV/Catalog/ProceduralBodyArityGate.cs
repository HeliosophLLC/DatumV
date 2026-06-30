using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Plan-time validator for procedural-routine bodies (procedural UDFs,
/// procedures, and CREATE MODEL bodies). Walks each statement and drives
/// <see cref="ExpressionTypeResolver.ResolveType"/> over every reachable
/// expression so a typo like <c>brighten(image)</c> missing the required
/// <c>intensity</c> argument throws at registration time instead of
/// surfacing per-row at the first <c>CALL</c> / first model invocation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope tracking.</strong> Parameters seed a synthetic
/// <see cref="Schema"/>; every <see cref="DeclareStatement"/> the walker
/// encounters appends a column for the local (resolving its kind either
/// from the explicit <c>TypeName</c> or by recursively type-resolving the
/// initializer). Inner blocks share the outer scope — variable shadowing
/// across nested <c>BEGIN…END</c> is rare enough that we accept the
/// over-approximation rather than carrying a real scope stack.
/// </para>
/// <para>
/// <strong>Why partial coverage is OK.</strong> Some sub-expressions can't
/// be statically typed (struct-field index access, locals whose initializer
/// involves an opaque type). <see cref="ExpressionTypeResolver.ResolveType"/>
/// returns <c>null</c> in those cases and the per-call
/// <see cref="IScalarFunction.ValidateArguments"/> is skipped — those
/// shapes still fail at runtime through the existing per-row evaluator.
/// What this gate catches is the typed direct-call case, which is the
/// majority of real arity bugs.
/// </para>
/// </remarks>
internal static class ProceduralBodyArityGate
{
    /// <summary>
    /// Validates the body's reachable function calls against the routine's
    /// parameter scope. <paramref name="routineLabel"/> is prefixed onto
    /// any thrown error (e.g. <c>"udf.foo"</c>, <c>"procedure bar"</c>,
    /// <c>"model models.baz"</c>) so a stack of wrapped errors clearly
    /// names the site at fault.
    /// </summary>
    public static void Enforce(
        IReadOnlyList<Statement> body,
        IReadOnlyList<UdfParameter> parameters,
        FunctionRegistry functions,
        string routineLabel)
    {
        List<ColumnInfo> scope = BuildParameterScope(parameters);
        WalkStatements(body, scope, functions, routineLabel);
    }

    /// <summary>
    /// Reserved name for a placeholder column added when a routine has
    /// neither parameters nor DECLAREd locals. <see cref="Schema"/>'s
    /// constructor requires ≥1 column; the placeholder satisfies that
    /// without polluting the visible scope (the leading <c>__</c> + the
    /// space character keep it out of any valid identifier).
    /// </summary>
    private const string EmptyScopeSentinel = "__ empty_scope_sentinel";

    private static List<ColumnInfo> BuildParameterScope(IReadOnlyList<UdfParameter> parameters)
    {
        List<ColumnInfo> scope = new(capacity: Math.Max(1, parameters.Count));
        foreach (UdfParameter p in parameters)
        {
            scope.Add(BuildColumnInfoFromTypeName(p.Name, p.TypeName));
        }
        if (scope.Count == 0)
        {
            scope.Add(new ColumnInfo(EmptyScopeSentinel, DataKind.Unknown, nullable: true));
        }
        return scope;
    }

    private static ColumnInfo BuildColumnInfoFromTypeName(string name, string? typeName)
    {
        // A List<T> accumulator's *static* type is its frozen form Array<T>:
        // every read of the variable crosses a promotion boundary that freezes
        // it, so signature checks (and any downstream type resolution) must see
        // it as an array of the element kind, not as the un-storable List<T>.
        if (typeName is not null
            && TypeAnnotationResolver.TryParseListBuilder(typeName, out DataKind elementKind))
        {
            return new ColumnInfo(name, elementKind, nullable: true) { IsArray = true };
        }
        // Unparseable / null types degrade to Unknown so references resolve
        // to *some* kind. ResolveType will treat the column as the fallback
        // kind, which usually means ValidateArguments won't fire (kind
        // mismatch on every signature variant) — that's an acceptable miss;
        // the runtime guard still catches it per-row.
        if (typeName is null || !TypeAnnotationResolver.TryParse(typeName, out DataKind kind, out bool isArray))
        {
            return new ColumnInfo(name, DataKind.Unknown, nullable: true);
        }
        return new ColumnInfo(name, kind, nullable: true) { IsArray = isArray };
    }

    private static void WalkStatements(
        IReadOnlyList<Statement> statements, List<ColumnInfo> scope, FunctionRegistry functions, string routineLabel)
    {
        foreach (Statement statement in statements)
        {
            WalkStatement(statement, scope, functions, routineLabel);
        }
    }

    private static void WalkStatement(
        Statement statement, List<ColumnInfo> scope, FunctionRegistry functions, string routineLabel)
    {
        switch (statement)
        {
            case BlockStatement block:
                WalkStatements(block.Statements, scope, functions, routineLabel);
                break;
            case IfStatement ifs:
                ValidateExpression(ifs.Predicate, scope, functions, routineLabel);
                WalkStatement(ifs.Then, scope, functions, routineLabel);
                if (ifs.Else is not null) WalkStatement(ifs.Else, scope, functions, routineLabel);
                break;
            case WhileStatement loop:
                ValidateExpression(loop.Predicate, scope, functions, routineLabel);
                WalkStatement(loop.Body, scope, functions, routineLabel);
                break;
            case ForCounterStatement forC:
                ValidateExpression(forC.Start, scope, functions, routineLabel);
                ValidateExpression(forC.End, scope, functions, routineLabel);
                if (forC.Step is not null) ValidateExpression(forC.Step, scope, functions, routineLabel);
                // Loop variable is auto-declared at the loop scope. Infer
                // from Start (typically Int32).
                AddInferredLocal(forC.VariableName, forC.Start, scope, functions);
                WalkStatement(forC.Body, scope, functions, routineLabel);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null) ValidateExpression(decl.Initializer, scope, functions, routineLabel);
                AddDeclaredLocal(decl, scope, functions);
                break;
            case SetStatement set:
                ValidateExpression(set.Value, scope, functions, routineLabel);
                break;
            case AppendStatement append:
                ValidateExpression(append.Value, scope, functions, routineLabel);
                break;
            case ReserveStatement reserve:
                ValidateExpression(reserve.Capacity, scope, functions, routineLabel);
                break;
            case ReturnStatement ret:
                ValidateExpression(ret.Value, scope, functions, routineLabel);
                break;
            // ForInStatement bodies see the loop variable as a struct; we
            // don't model struct shapes well enough to type it, and the
            // source query gets PlanTimeFunctionGate coverage when planned.
            // CallStatement / QueryStatement expressions similarly route
            // through the planner. Skip rather than double-walk.
            default:
                break;
        }
    }

    private static void AddDeclaredLocal(
        DeclareStatement decl, List<ColumnInfo> scope, FunctionRegistry functions)
    {
        // Explicit TypeName wins (authoritative). When absent, infer from
        // the initializer via ResolveType; an unresolved initializer falls
        // through to Object so later uses don't false-positive as unknown.
        if (decl.TypeName is not null)
        {
            scope.Add(BuildColumnInfoFromTypeName(decl.VariableName, decl.TypeName));
            return;
        }

        DataKind kind = DataKind.Unknown;
        bool isArray = false;
        if (decl.Initializer is not null)
        {
            try
            {
                Schema snapshot = new(scope.ToArray(), Array.Empty<int>());
                (DataKind Kind, bool IsArray, bool IsMultiDim)? resolved =
                    ExpressionTypeResolver.ResolveTypeShape(decl.Initializer, snapshot, functions);
                if (resolved is not null)
                {
                    kind = resolved.Value.Kind;
                    isArray = resolved.Value.IsArray;
                }
            }
            catch
            {
                // Resolution failed (e.g. malformed sub-call). The outer
                // ValidateExpression already wrapped and rethrew when we
                // walked the initializer; we won't get here on the happy
                // path. Defensive fallback to Unknown on the off-chance.
            }
        }
        scope.Add(new ColumnInfo(decl.VariableName, kind, nullable: true) { IsArray = isArray });
    }

    private static void AddInferredLocal(
        string variableName, Expression source, List<ColumnInfo> scope, FunctionRegistry functions)
    {
        DataKind kind = DataKind.Int32;
        try
        {
            Schema snapshot = new(scope.ToArray(), Array.Empty<int>());
            DataKind? resolved = ExpressionTypeResolver.ResolveType(source, snapshot, functions);
            if (resolved is not null) kind = resolved.Value;
        }
        catch
        {
            // See AddDeclaredLocal — defensive only.
        }
        scope.Add(new ColumnInfo(variableName, kind, nullable: false));
    }

    private static void ValidateExpression(
        Expression expression, List<ColumnInfo> scope, FunctionRegistry functions, string routineLabel)
    {
        Schema snapshot = new(scope.ToArray(), Array.Empty<int>());
        try
        {
            _ = ExpressionTypeResolver.ResolveType(expression, snapshot, functions);
        }
        catch (Exception inner)
            when (inner is InvalidOperationException
                  or NotSupportedException
                  or ArgumentException
                  or FunctionArgumentException)
        {
            throw new InvalidOperationException(
                $"{routineLabel}: {inner.Message}",
                inner);
        }
    }
}
