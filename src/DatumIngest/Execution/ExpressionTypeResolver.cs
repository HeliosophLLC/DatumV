using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Infers the output <see cref="DataKind"/> of an AST <see cref="Expression"/>
/// by examining column references against a known source schema and resolving
/// function return types. Used for schema introspection (editor autocomplete)
/// without executing the query.
/// </summary>
public static class ExpressionTypeResolver
{
    /// <summary>
    /// Resolves the output data kind of an expression given a source schema
    /// and function registry. Returns <c>null</c> when the type cannot be
    /// determined statically (e.g. dynamic CAST targets, unknown columns).
    /// </summary>
    /// <param name="expression">The AST expression to analyze.</param>
    /// <param name="sourceSchema">The schema providing column type information.</param>
    /// <param name="functions">The function registry for resolving scalar function return types.</param>
    /// <returns>The inferred data kind, or <c>null</c> if it cannot be determined.</returns>
    public static DataKind? ResolveType(Expression expression, Schema sourceSchema, FunctionRegistry functions)
    {
        return expression switch
        {
            LiteralExpression literal => ResolveLiteral(literal),
            ColumnReference column => ResolveColumn(column, sourceSchema),
            BinaryExpression binary => ResolveBinary(binary, sourceSchema, functions),
            UnaryExpression unary => ResolveUnary(unary, sourceSchema, functions),
            FunctionCallExpression function => ResolveFunction(function, sourceSchema, functions),
            InExpression => DataKind.Float32,
            BetweenExpression => DataKind.Float32,
            IsNullExpression => DataKind.Float32,
            LikeExpression => DataKind.Float32,
            CastExpression cast => ResolveCast(cast),
            CaseExpression caseExpr => ResolveCaseExpression(caseExpr, sourceSchema, functions),
            WindowFunctionCallExpression window => ResolveWindowFunction(window, sourceSchema, functions),
            ParameterExpression => null,
            _ => null,
        };
    }

    private static DataKind? ResolveLiteral(LiteralExpression literal)
    {
        if (literal.Value is null)
        {
            return DataKind.Float32;
        }

        return literal.Value switch
        {
            int => DataKind.Int32,
            long => DataKind.Int64,
            float => DataKind.Float32,
            double => DataKind.Float64,
            string => DataKind.String,
            bool => DataKind.Boolean,
            _ => null,
        };
    }

    private static DataKind? ResolveColumn(ColumnReference column, Schema sourceSchema)
    {
        // Try qualified name first, then unqualified.
        if (column.TableName is not null)
        {
            string qualifiedName = $"{column.TableName}.{column.ColumnName}";
            ColumnInfo? qualified = sourceSchema.FindColumn(qualifiedName);
            if (qualified is not null)
            {
                return qualified.Kind;
            }
        }

        ColumnInfo? info = sourceSchema.FindColumn(column.ColumnName);
        return info?.Kind;
    }

    /// <summary>
    /// Resolves the array element kind for an expression when its resolved kind
    /// is <see cref="DataKind.Array"/>. Returns <c>null</c> when the element kind
    /// is not statically known (e.g. a computed expression or unknown column).
    /// Handles both direct column references (via <see cref="ColumnInfo.ArrayElementKind"/>)
    /// and aggregate function calls (via <see cref="IAggregateFunction.GetResultArrayElementKind"/>).
    /// </summary>
    internal static DataKind? ResolveArrayElementKindFromExpression(Expression expression, Schema sourceSchema, FunctionRegistry functions)
    {
        if (expression is ColumnReference column)
        {
            ColumnInfo? info = null;

            if (column.TableName is not null)
            {
                string qualifiedName = $"{column.TableName}.{column.ColumnName}";
                info = sourceSchema.FindColumn(qualifiedName);
            }

            info ??= sourceSchema.FindColumn(column.ColumnName);
            return info?.ArrayElementKind;
        }

        // Aggregate function call — determine element kind using
        // GetResultArrayElementKind if provided by the aggregate.
        if (expression is FunctionCallExpression func)
        {
            IAggregateFunction? aggregateFunction = functions.TryGetAggregate(func.FunctionName);
            if (aggregateFunction is not null)
            {
                DataKind?[] argKindBuffer = new DataKind?[func.Arguments.Count];
                bool allResolved = true;

                for (int index = 0; index < func.Arguments.Count; index++)
                {
                    DataKind? kind = ResolveType(func.Arguments[index], sourceSchema, functions);
                    if (kind is null) { allResolved = false; break; }
                    argKindBuffer[index] = kind.Value;
                }

                if (allResolved)
                {
                    DataKind[] argKinds = new DataKind[func.Arguments.Count];
                    for (int i = 0; i < argKindBuffer.Length; i++)
                    {
                        argKinds[i] = argKindBuffer[i]!.Value;
                    }

                    return aggregateFunction.GetResultArrayElementKind(argKinds);
                }
            }
        }

        return null;
    }

    private static DataKind? ResolveBinary(BinaryExpression binary, Schema sourceSchema, FunctionRegistry functions)
    {
        // Comparison and logical operators always produce Scalar (boolean result).
        if (IsComparisonOrLogical(binary.Operator))
        {
            return DataKind.Float32;
        }

        // Arithmetic operators: find common kind of both operands.
        DataKind? leftKind = ResolveType(binary.Left, sourceSchema, functions);
        DataKind? rightKind = ResolveType(binary.Right, sourceSchema, functions);

        if (leftKind is null || rightKind is null)
        {
            return leftKind ?? rightKind;
        }

        // Duration ± Duration preserves Duration (do not widen to Scalar).
        if (leftKind.Value == DataKind.Duration && rightKind.Value == DataKind.Duration
            && binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract)
        {
            return DataKind.Duration;
        }

        return TypeCoercion.FindCommonKind(leftKind.Value, rightKind.Value) ?? DataKind.Float32;
    }

    private static DataKind? ResolveUnary(UnaryExpression unary, Schema sourceSchema, FunctionRegistry functions)
    {
        return unary.Operator switch
        {
            // NOT always produces a boolean (Scalar).
            UnaryOperator.Not => DataKind.Float32,

            // Negate preserves the operand kind.
            UnaryOperator.Negate => ResolveType(unary.Operand, sourceSchema, functions) ?? DataKind.Float32,
            _ => null,
        };
    }

    private static DataKind? ResolveFunction(FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IScalarFunction? scalarFunction = functions.TryGetScalar(function.FunctionName);

        // If not a scalar function, check whether it is an aggregate. This path is
        // used by QuerySchemaResolver when resolving SELECT expressions that contain
        // aggregate calls directly (e.g. SELECT ARRAY_AGG(name) or scalar wrappers
        // around aggregates like array_get(ARRAY_AGG(name), 1)).
        if (scalarFunction is null)
        {
            return ResolveAggregate(function, sourceSchema, functions);
        }

        // Resolve argument kinds so we can call ValidateArguments.
        DataKind[] argumentKinds = new DataKind[function.Arguments.Count];
        bool allResolved = true;

        for (int index = 0; index < function.Arguments.Count; index++)
        {
            DataKind? kind = ResolveType(function.Arguments[index], sourceSchema, functions);
            if (kind is null)
            {
                allResolved = false;
                break;
            }

            argumentKinds[index] = kind.Value;
        }

        if (!allResolved)
        {
            return null;
        }

        // If the function is element-kind-aware, also resolve the array element
        // kinds and call the richer overload so it can produce a precise return type.
        if (scalarFunction is IElementKindAwareFunction elementKindAware)
        {
            DataKind?[] arrayElementKinds = new DataKind?[function.Arguments.Count];
            for (int index = 0; index < function.Arguments.Count; index++)
            {
                if (argumentKinds[index] == DataKind.Array)
                {
                    arrayElementKinds[index] = ResolveArrayElementKindFromExpression(
                        function.Arguments[index], sourceSchema, functions);
                }
            }

            try
            {
                return elementKindAware.ValidateArgumentsWithElementKinds(argumentKinds, arrayElementKinds);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        try
        {
            return scalarFunction.ValidateArguments(argumentKinds);
        }
        catch (ArgumentException)
        {
            // If validation fails, the type cannot be determined.
            return null;
        }
    }

    /// <summary>
    /// Resolves the return type of an aggregate function call. Called as a fallback
    /// from <see cref="ResolveFunction"/> when no scalar function matches the name.
    /// </summary>
    private static DataKind? ResolveAggregate(FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IAggregateFunction? aggregateFunction = functions.TryGetAggregate(function.FunctionName);
        if (aggregateFunction is null)
        {
            return null;
        }

        DataKind[] argumentKinds = new DataKind[function.Arguments.Count];

        for (int index = 0; index < function.Arguments.Count; index++)
        {
            DataKind? kind = ResolveType(function.Arguments[index], sourceSchema, functions);
            if (kind is null)
            {
                return null;
            }

            argumentKinds[index] = kind.Value;
        }

        try
        {
            return aggregateFunction.ValidateArguments(argumentKinds);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static DataKind? ResolveCast(CastExpression cast)
    {
        return ResolveCastTargetKind(cast.TargetType);
    }

    /// <summary>
    /// Resolves a CAST target type name to a DataKind. Accepts both enum names
    /// and common aliases ("bool", "time", "duration").
    /// </summary>
    internal static DataKind? ResolveCastTargetKind(string targetType)
    {
        // The target type is a string literal naming a DataKind member.
        if (Enum.TryParse<DataKind>(targetType, ignoreCase: true, out DataKind targetKind))
        {
            return targetKind;
        }

        // Accept common aliases that don't match enum names.
        if (string.Equals(targetType, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Boolean;
        }

        if (string.Equals(targetType, "time", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Time;
        }

        if (string.Equals(targetType, "duration", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Duration;
        }

        return null;
    }

    private static bool IsComparisonOrLogical(BinaryOperator op)
    {
        return op is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.GreaterThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.And
            or BinaryOperator.Or
            or BinaryOperator.Like
            or BinaryOperator.ILike
            or BinaryOperator.Regexp;
    }

    /// <summary>
    /// Resolves the output type of a CASE expression by finding the common type
    /// across all THEN branch results and the optional ELSE result.
    /// When the standard widening chain cannot unify two branch types and one of
    /// them is String, the non-String type wins (SQL Server-style precedence).
    /// String values are implicitly parsed to the target type at evaluation time.
    /// </summary>
    private static DataKind? ResolveCaseExpression(CaseExpression caseExpression, Schema sourceSchema, FunctionRegistry functions)
    {
        DataKind? commonKind = null;

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            DataKind? branchKind = ResolveType(whenClause.Result, sourceSchema, functions);
            if (branchKind is null)
            {
                return null;
            }

            commonKind = commonKind is null
                ? branchKind
                : UnifyCaseBranchKinds(commonKind.Value, branchKind.Value);

            if (commonKind is null)
            {
                return null;
            }
        }

        if (caseExpression.ElseResult is not null)
        {
            DataKind? elseKind = ResolveType(caseExpression.ElseResult, sourceSchema, functions);
            if (elseKind is not null && commonKind is not null)
            {
                commonKind = UnifyCaseBranchKinds(commonKind.Value, elseKind.Value);
            }
        }

        return commonKind;
    }

    /// <summary>
    /// Unifies two CASE branch kinds. Tries the standard widening chain first;
    /// when that fails and one kind is String, applies SQL Server-style precedence
    /// by preferring the non-String kind (String values are parsed at runtime).
    /// </summary>
    internal static DataKind? UnifyCaseBranchKinds(DataKind kindA, DataKind kindB)
    {
        DataKind? common = TypeCoercion.FindCommonKind(kindA, kindB);
        if (common is not null)
        {
            return common;
        }

        // String + coercible type: prefer the non-String kind.
        if (kindA == DataKind.String && TypeCoercion.CanCoerceStringTo(kindB))
        {
            return kindB;
        }

        if (kindB == DataKind.String && TypeCoercion.CanCoerceStringTo(kindA))
        {
            return kindA;
        }

        return null;
    }

    /// <summary>
    /// Resolves the output type of a window function call by looking up the
    /// window function (or aggregate-as-window) and calling its validation.
    /// </summary>
    private static DataKind? ResolveWindowFunction(
        WindowFunctionCallExpression window,
        Schema sourceSchema,
        FunctionRegistry functions)
    {
        IWindowFunction? windowFunction = functions.TryGetWindowOrAggregate(window.FunctionName);
        if (windowFunction is null)
        {
            return null;
        }

        DataKind[] argumentKinds = new DataKind[window.Arguments.Count];
        for (int index = 0; index < window.Arguments.Count; index++)
        {
            DataKind? kind = ResolveType(window.Arguments[index], sourceSchema, functions);
            if (kind is null)
            {
                return null;
            }
            argumentKinds[index] = kind.Value;
        }

        try
        {
            return windowFunction.ValidateArguments(argumentKinds);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
