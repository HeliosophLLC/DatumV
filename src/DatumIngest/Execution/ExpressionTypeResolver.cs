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
    public static DataKind? ResolveType(Expression expression, Schema sourceSchema, FunctionRegistry functions) =>
        ResolveTypeShape(expression, sourceSchema, functions)?.Kind;

    /// <summary>
    /// Resolves both the <see cref="DataKind"/> and array-ness of an
    /// expression. Companion to <see cref="ResolveType"/> for callers that
    /// need to know whether the expression yields a typed array (the array
    /// flag is separate from <see cref="DataKind"/> because element kind and
    /// array-ness are tracked independently throughout the engine). Returns
    /// <c>null</c> when the type cannot be determined statically.
    /// </summary>
    /// <remarks>
    /// Branches that may yield <c>IsArray = true</c>:
    /// <list type="bullet">
    ///   <item>Column references — from <see cref="ColumnInfo.IsArray"/>.</item>
    ///   <item>Cast targets — from <c>TypeAnnotationResolver.TryParse</c>.</item>
    ///   <item>Function calls — from the matched signature variant's
    ///   <see cref="ReturnTypeRule.ProducesArray"/>.</item>
    ///   <item>Aggregate calls — from <see cref="IAggregateFunction.ReturnRule"/>.</item>
    /// </list>
    /// All other branches yield <c>IsArray = false</c>. Index access strips
    /// the array dimension (indexing a typed array yields the element kind).
    /// </remarks>
    public static (DataKind Kind, bool IsArray, bool IsMultiDim)? ResolveTypeShape(
        Expression expression, Schema sourceSchema, FunctionRegistry functions)
    {
        switch (expression)
        {
            case ColumnReference column:
            {
                ColumnInfo? info = ResolveColumnInfo(column, sourceSchema);
                if (info is null) return null;
                // Multi-dim either explicit (IsMultiDim flag — set by
                // QuerySchemaResolver on projected columns) or implicit via a
                // static-shape FixedShape with ndim >= 2 (LiteralCoercion
                // attaches the shape at INSERT time). 1-D fixed-shape and
                // variable-length arrays stay flat.
                bool isMultiDim = info.IsArray
                    && (info.IsMultiDim || info.FixedShape is { Length: >= 2 });
                return (info.Kind, info.IsArray, isMultiDim);
            }
            case CastExpression cast:
            {
                if (TypeAnnotationResolver.TryParse(cast.TargetType, out DataKind k, out bool arr))
                {
                    return (k, arr, IsMultiDim: false);
                }
                // Fall through to legacy resolver for unrecognised aliases.
                DataKind? legacy = ResolveCastTargetKind(cast.TargetType);
                return legacy is null ? null : (legacy.Value, false, false);
            }
            case FunctionCallExpression function:
            {
                return ResolveFunctionShape(function, sourceSchema, functions);
            }
        }

        // All other branches can't produce IsArray = true. Reuse the kind
        // resolution path and tag IsArray = false.
        DataKind? kind = ResolveTypeKindOnly(expression, sourceSchema, functions);
        return kind is null ? null : (kind.Value, false, false);
    }

    private static DataKind? ResolveTypeKindOnly(Expression expression, Schema sourceSchema, FunctionRegistry functions)
    {
        return expression switch
        {
            LiteralExpression literal => ResolveLiteral(literal),
            ColumnReference column => ResolveColumn(column, sourceSchema),
            BinaryExpression binary => ResolveBinary(binary, sourceSchema, functions),
            UnaryExpression unary => ResolveUnary(unary, sourceSchema, functions),
            FunctionCallExpression function => ResolveFunction(function, sourceSchema, functions),
            InExpression => DataKind.Boolean,
            BetweenExpression => DataKind.Boolean,
            IsNullExpression => DataKind.Boolean,
            LikeExpression => DataKind.Boolean,
            CastExpression cast => ResolveCast(cast),
            AtTimeZoneExpression => DataKind.DateTime,
            CurrentTimestampExpression ct => ct.Kind switch
            {
                CurrentTimestampKind.CurrentDate => DataKind.Date,
                CurrentTimestampKind.CurrentTime => DataKind.Time,
                CurrentTimestampKind.CurrentTimestamp => DataKind.DateTime,
                _ => null,
            },
            CaseExpression caseExpr => ResolveCaseExpression(caseExpr, sourceSchema, functions),
            WindowFunctionCallExpression window => ResolveWindowFunction(window, sourceSchema, functions),
            LambdaExpression => null, // Not a value — lambdas have no kind on their own.
            ParameterExpression => null,
            StructLiteralExpression structLiteral => ResolveStructLiteral(structLiteral, sourceSchema, functions),
            IndexAccessExpression indexAccess => ResolveIndexAccess(indexAccess, sourceSchema, functions),
            TypeLiteralExpression => DataKind.Type,
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
            sbyte => DataKind.Int8,
            short => DataKind.Int16,
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
    /// Returns the full <see cref="ColumnInfo"/> for a column-reference expression,
    /// including <see cref="ColumnInfo.Fields"/> for struct columns.
    /// Returns <c>null</c> for non-column-reference expressions or unknown columns.
    /// </summary>
    private static ColumnInfo? ResolveColumnInfo(ColumnReference column, Schema sourceSchema)
    {
        if (column.TableName is not null)
        {
            string qualifiedName = $"{column.TableName}.{column.ColumnName}";
            ColumnInfo? qualified = sourceSchema.FindColumn(qualifiedName);
            if (qualified is not null)
            {
                return qualified;
            }
        }

        return sourceSchema.FindColumn(column.ColumnName);
    }

    private static DataKind? ResolveStructLiteral(
        StructLiteralExpression structLiteral,
        Schema sourceSchema,
        FunctionRegistry functions)
    {
        // Cannot resolve type when there are no fields — an empty struct is unknown-typed.
        if (structLiteral.Fields.Count == 0)
        {
            return DataKind.Struct;
        }

        // All field kinds must be resolvable for the struct type to be fully known.
        // If any field kind is unresolvable, we still return Struct (just without Fields).
        return DataKind.Struct;
    }

    private static DataKind? ResolveIndexAccess(
        IndexAccessExpression indexAccess,
        Schema sourceSchema,
        FunctionRegistry functions)
    {
        DataKind? sourceKind = ResolveType(indexAccess.Source, sourceSchema, functions);

        // Typed-array source via an aggregate or scalar function whose result
        // is an array. For scalar functions, the matched signature variant's
        // ReturnTypeRule.ProducesArray is the source of truth. For aggregates,
        // the optional IAggregateFunction.ReturnRule reports the same thing
        // (null = scalar, ArrayOf(...) = array).
        if (indexAccess.Source is FunctionCallExpression arrayFnSource)
        {
            IAggregateFunction? aggregate = functions.TryGetAggregate(arrayFnSource.CallName);
            if (aggregate?.ReturnRule?.ProducesArray == true)
            {
                return sourceKind;
            }
            if (FunctionCallProducesArray(arrayFnSource, sourceSchema, functions))
            {
                return sourceKind;
            }
        }

        // Typed-array column source: ColumnInfo carries IsArray + Kind=elementKind.
        // Indexing yields a scalar of the element kind.
        if (indexAccess.Source is ColumnReference arrayColRef)
        {
            ColumnInfo? info = ResolveColumnInfo(arrayColRef, sourceSchema);
            if (info is { IsArray: true })
            {
                return info.Kind;
            }
        }

        if (sourceKind == DataKind.Struct
            && indexAccess.Indices.Count == 1
            && indexAccess.Indices[0] is LiteralExpression { Value: string fieldName })
        {
            // Struct field access by name: resolve via ColumnInfo.Fields.
            IReadOnlyList<ColumnInfo>? fields = null;

            if (indexAccess.Source is ColumnReference colRef)
            {
                fields = ResolveColumnInfo(colRef, sourceSchema)?.Fields;
            }
            else if (indexAccess.Source is StructLiteralExpression literal)
            {
                // Build a transient field list from the literal's AST names.
                // We only need the names here; kinds are resolved next.
                fields = BuildFieldColumnInfos(literal, sourceSchema, functions);
            }

            if (fields is not null)
            {
                foreach (ColumnInfo fieldInfo in fields)
                {
                    if (string.Equals(fieldInfo.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        return fieldInfo.Kind;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the output <see cref="ColumnInfo"/> for a SELECT-list expression,
    /// including <see cref="ColumnInfo.Fields"/> when the expression produces a struct.
    /// Callers building output schemas should use this instead of <see cref="ResolveType"/>
    /// to ensure that struct field metadata and array element kind are propagated.
    /// </summary>
    /// <param name="expression">The expression to analyze.</param>
    /// <param name="outputName">The output column name (after alias resolution).</param>
    /// <param name="nullable">Whether the output column is nullable.</param>
    /// <param name="sourceSchema">The schema providing column type information.</param>
    /// <param name="functions">The function registry for resolving scalar function return types.</param>
    /// <returns>A <see cref="ColumnInfo"/> describing the output column.</returns>
    public static ColumnInfo ResolveOutputColumnInfo(
        Expression expression,
        string outputName,
        bool nullable,
        Schema sourceSchema,
        FunctionRegistry functions)
    {
        if (expression is StructLiteralExpression structLiteral)
        {
            IReadOnlyList<ColumnInfo>? fields = BuildFieldColumnInfos(structLiteral, sourceSchema, functions);
            return fields is not null
                ? new ColumnInfo(outputName, nullable, fields)
                : new ColumnInfo(outputName, DataKind.Struct, nullable);
        }

        if (expression is ColumnReference colRef)
        {
            ColumnInfo? source = ResolveColumnInfo(colRef, sourceSchema);
            if (source?.Fields is not null)
            {
                return new ColumnInfo(outputName, nullable, source.Fields);
            }

            if (source is not null)
            {
                // Preserves IsArray when present (typed-array column → typed-array output).
                return new ColumnInfo(outputName, source.Kind, nullable) { IsArray = source.IsArray };
            }
        }

        DataKind kind = ResolveType(expression, sourceSchema, functions) ?? DataKind.String;
        return new ColumnInfo(outputName, kind, nullable);
    }

    /// <summary>
    /// Builds a transient <see cref="ColumnInfo"/> list from a struct literal's fields
    /// by resolving each field's value expression kind.
    /// </summary>
    private static IReadOnlyList<ColumnInfo>? BuildFieldColumnInfos(
        StructLiteralExpression literal,
        Schema sourceSchema,
        FunctionRegistry functions)
    {
        List<ColumnInfo> result = new(literal.Fields.Count);

        foreach (StructField field in literal.Fields)
        {
            DataKind? kind = ResolveType(field.Value, sourceSchema, functions);
            if (kind is null)
            {
                return null;
            }

            result.Add(new ColumnInfo(field.Name, kind.Value, nullable: false));
        }

        return result;
    }

    private static DataKind? ResolveBinary(BinaryExpression binary, Schema sourceSchema, FunctionRegistry functions)
    {
        // Comparison and logical operators always produce Boolean.
        if (IsComparisonOrLogical(binary.Operator))
        {
            return DataKind.Boolean;
        }

        // Arithmetic operators: find common kind of both operands.
        DataKind? leftKind = ResolveType(binary.Left, sourceSchema, functions);
        DataKind? rightKind = ResolveType(binary.Right, sourceSchema, functions);

        if (leftKind is null || rightKind is null)
        {
            return leftKind ?? rightKind;
        }

        // Duration ± Duration preserves Duration (do not widen to Scalar).
        // The runtime evaluator special-cases this same pattern at the
        // dispatch level; mirroring it here keeps schema reporting in
        // step with the non-promotion path.
        if (leftKind.Value == DataKind.Duration && rightKind.Value == DataKind.Duration
            && binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract)
        {
            return DataKind.Duration;
        }

        // Delegate to the same kind-promotion logic the runtime evaluator
        // uses, so plan-time schema reporting matches the value the row
        // ultimately carries. Returns null only for operand combinations
        // the runtime would also throw on — fine to surface upward as
        // "unknown" since the query would have failed at execution
        // anyway.
        return ExpressionEvaluator.TryPromoteArithmeticKind(
            leftKind.Value, rightKind.Value, binary.Operator);
    }

    private static DataKind? ResolveUnary(UnaryExpression unary, Schema sourceSchema, FunctionRegistry functions)
    {
        return unary.Operator switch
        {
            // NOT always produces Boolean.
            UnaryOperator.Not => DataKind.Boolean,

            // Negate preserves the operand kind.
            UnaryOperator.Negate => ResolveType(unary.Operand, sourceSchema, functions) ?? DataKind.Float32,
            _ => null,
        };
    }

    private static DataKind? ResolveFunction(FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IScalarFunction? scalarFunction = functions.TryGetScalar(function.CallName);

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

        // Surface ValidateArguments errors directly. A swallowed exception here
        // turns "concat() requires at least 2 arguments" into a runtime
        // IndexOutOfRangeException because the function is dispatched anyway
        // with whatever args were supplied.
        return scalarFunction.ValidateArguments(argumentKinds);
    }

    /// <summary>
    /// Resolves both kind and array-ness for a function call. For scalar
    /// functions, array-ness is read from the matched signature variant's
    /// <see cref="ReturnTypeRule.ProducesArray"/> (per-signature precision).
    /// Functions without a registered descriptor or without a matching
    /// variant default to scalar; runtime-constructed adapters that return
    /// arrays must register a synthetic descriptor (see procedural UDFs).
    /// For aggregates, array-ness is read from the optional
    /// <see cref="IAggregateFunction.ReturnRule"/> — aggregates do not expose
    /// per-signature variants, so a single rule covers the function.
    /// </summary>
    private static (DataKind Kind, bool IsArray, bool IsMultiDim)? ResolveFunctionShape(
        FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IScalarFunction? scalarFunction = functions.TryGetScalar(function.CallName);

        if (scalarFunction is null)
        {
            // Aggregate fallback — same as ResolveFunction's path.
            IAggregateFunction? aggregateFunction = functions.TryGetAggregate(function.CallName);
            if (aggregateFunction is null) return null;

            DataKind[] aggArgs = new DataKind[function.Arguments.Count];
            for (int index = 0; index < function.Arguments.Count; index++)
            {
                DataKind? kind = ResolveType(function.Arguments[index], sourceSchema, functions);
                if (kind is null) return null;
                aggArgs[index] = kind.Value;
            }

            DataKind aggKind = aggregateFunction.ValidateArguments(aggArgs);
            return (aggKind, aggregateFunction.ReturnRule?.ProducesArray ?? false, false);
        }

        DataKind[] argumentKinds = new DataKind[function.Arguments.Count];
        (DataKind, bool, bool)[] argumentShapes = new (DataKind, bool, bool)[function.Arguments.Count];
        for (int index = 0; index < function.Arguments.Count; index++)
        {
            (DataKind Kind, bool IsArray, bool IsMultiDim)? shape = ResolveTypeShape(
                function.Arguments[index], sourceSchema, functions);
            if (shape is null) return null;
            argumentKinds[index] = shape.Value.Kind;
            argumentShapes[index] = (shape.Value.Kind, shape.Value.IsArray, shape.Value.IsMultiDim);
        }

        DataKind resultKind = scalarFunction.ValidateArguments(argumentKinds);

        // Per-signature array-ness via array-aware matching against the
        // descriptor's signatures. Functions with no descriptor or no matching
        // variant default to scalar (IsArray = false) — runtime-constructed
        // adapters are expected to register a synthetic descriptor when their
        // return shape is array-typed (see RoutineRegistrar's procedural-UDF path).
        bool isArray = false;
        bool isMultiDim = false;
        FunctionDescriptor? descriptor = functions.TryGetScalarDescriptor(function.FunctionName);
        if (descriptor is not null)
        {
            FunctionSignatureVariant? matched = FunctionMetadata.MatchVariantWithShape(
                descriptor.Signatures, argumentShapes);
            if (matched is not null)
            {
                isArray = matched.ReturnType.ProducesArray;
                isMultiDim = matched.ReturnType.ProducesMultiDimArray;
            }
        }

        // Rank-dynamic functions (e.g. infer(), whose output ndim follows the
        // ONNX model's output shape) leave ProducesMultiDimArray = false on the
        // signature and emit multi-dim DataValues at runtime; the evaluator
        // consults runtime IsMultiDim directly for index-access dispatch.
        return (resultKind, isArray, isMultiDim);
    }

    /// <summary>
    /// True when a scalar function call's matched signature variant produces
    /// an array result. Returns <see langword="false"/> when no descriptor is
    /// registered or no variant matches — runtime-constructed adapters that
    /// return arrays must register a synthetic descriptor (see
    /// <c>RoutineRegistrar</c>'s procedural-UDF path).
    /// </summary>
    private static bool FunctionCallProducesArray(
        FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        FunctionDescriptor? descriptor = functions.TryGetScalarDescriptor(function.FunctionName);
        if (descriptor is null) return false;

        (DataKind, bool, bool)[] argumentShapes = new (DataKind, bool, bool)[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
        {
            (DataKind Kind, bool IsArray, bool IsMultiDim)? shape = ResolveTypeShape(
                function.Arguments[i], sourceSchema, functions);
            if (shape is null) return false;
            argumentShapes[i] = (shape.Value.Kind, shape.Value.IsArray, shape.Value.IsMultiDim);
        }

        FunctionSignatureVariant? matched = FunctionMetadata.MatchVariantWithShape(
            descriptor.Signatures, argumentShapes);
        return matched?.ReturnType.ProducesArray ?? false;
    }

    /// <summary>
    /// Resolves the return type of an aggregate function call. Called as a fallback
    /// from <see cref="ResolveFunction"/> when no scalar function matches the name.
    /// </summary>
    private static DataKind? ResolveAggregate(FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IAggregateFunction? aggregateFunction = functions.TryGetAggregate(function.CallName);
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

        return aggregateFunction.ValidateArguments(argumentKinds);
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
        IWindowFunction? windowFunction = functions.TryGetWindowOrAggregate(window.CallName);
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

        return windowFunction.ValidateArguments(argumentKinds);
    }
}
