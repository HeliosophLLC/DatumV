using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan-time AST rewrite that resolves PG-style named function-call
/// arguments (<c>fn(a := 1, b => 2)</c>) into the canonical positional
/// shape. Downstream passes — <see cref="UdfInliner"/>,
/// <see cref="ExpressionTypeResolver"/>, the evaluator — see only
/// positional arguments and never observe
/// <see cref="FunctionCallExpression.ArgumentNames"/> non-null.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pipeline placement.</strong> Runs in
/// <c>TableCatalog.PlanQuery</c> immediately before
/// <see cref="UdfInliner.Inline(QueryExpression, UdfRegistry, IReadOnlyList{string}, ProcedureRegistry?)"/>
/// so the inliner's positional substitution sees an aligned argument list.
/// </para>
/// <para>
/// <strong>Resolution rules.</strong>
/// <list type="bullet">
///   <item>Positional arguments must precede every named argument.
///   Positional-after-named raises a parse-time-style error with the
///   call site span.</item>
///   <item>Each named argument is matched (case-insensitive) against
///   the function's parameter names from the registered descriptor's
///   signatures. For overloaded scalar functions the variant chosen is
///   the first one whose parameter-name set is a superset of the
///   supplied named-arg set AND whose parameter count covers the
///   positional + named occupancy.</item>
///   <item>Skipped trailing slots are simply omitted (matching the
///   existing trailing-trim behaviour <see cref="FunctionMetadata.TryMatch"/>
///   permits). Skipped middle slots are filled with the parameter's
///   default value when available (procedural UDFs carry
///   <see cref="UdfParameter.Default"/> AST fragments) or with a NULL
///   literal otherwise — and only when the parameter is
///   <see cref="ParameterSpec.IsOptional"/>; required-slot skips raise
///   an error.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Table-valued functions.</strong> TVF call sites in
/// <c>FROM</c> / <c>JOIN</c> (the <see cref="FunctionSource"/> AST node)
/// are permuted through the same path. The TVF registry's
/// <see cref="TableValuedFunctionDescriptor.Signatures"/> drives variant
/// selection; skipped middle slots are NULL-filled when the parameter
/// is <see cref="ParameterSpec.IsOptional"/> (TVFs don't carry default
/// AST fragments — the descriptor-level optional flag is sufficient).
/// </para>
/// <para>
/// <strong>What it doesn't handle.</strong> Aggregate and window
/// functions remain out of scope — named arguments reaching one of
/// those resolve through the standard scalar lookup failure and
/// surface as the usual "unknown function" diagnostic. Calls to
/// functions with no registered scalar / TVF descriptor pass through
/// unchanged (the evaluator's standard error path reports the unknown
/// name with full source context).
/// </para>
/// </remarks>
public static class NamedArgPermuter
{
    /// <summary>
    /// Returns a new <see cref="QueryExpression"/> with every named-arg
    /// call site rewritten into its positional form. The input tree is
    /// not mutated.
    /// </summary>
    public static QueryExpression Permute(
        QueryExpression query, FunctionRegistry functions, UdfRegistry udfs, IReadOnlyList<string> searchPath)
    {
        Permuter permuter = new(functions, udfs, searchPath);
        return permuter.RewriteQuery(query);
    }

    /// <summary>
    /// Convenience overload for tests / single-expression rewrites.
    /// </summary>
    public static Expression Permute(
        Expression expression, FunctionRegistry functions, UdfRegistry udfs, IReadOnlyList<string> searchPath)
    {
        Permuter permuter = new(functions, udfs, searchPath);
        return permuter.Rewrite(expression);
    }

    private sealed class Permuter
    {
        private readonly FunctionRegistry _functions;
        private readonly UdfRegistry _udfs;
        private readonly IReadOnlyList<string> _searchPath;

        public Permuter(FunctionRegistry functions, UdfRegistry udfs, IReadOnlyList<string> searchPath)
        {
            _functions = functions;
            _udfs = udfs;
            _searchPath = searchPath;
        }

        public QueryExpression RewriteQuery(QueryExpression query) => query switch
        {
            SelectQueryExpression select => new SelectQueryExpression(RewriteSelect(select.Statement)),
            CompoundQueryExpression compound => new CompoundQueryExpression(
                RewriteQuery(compound.Left),
                compound.OperationType,
                compound.All,
                RewriteQuery(compound.Right),
                compound.OrderBy is { } ob ? RewriteOrderBy(ob) : null,
                compound.Limit,
                compound.Offset),
            _ => query,
        };

        private SelectStatement RewriteSelect(SelectStatement stmt) => stmt with
        {
            Columns = stmt.Columns.Select(RewriteSelectColumn).ToList(),
            From = stmt.From is { } from ? new FromClause(RewriteTableSource(from.Source)) : null,
            Joins = stmt.Joins?.Select(RewriteJoin).ToList(),
            Where = stmt.Where is { } w ? Rewrite(w) : null,
            GroupBy = stmt.GroupBy is { } gb
                ? new GroupByClause(gb.Expressions.Select(Rewrite).ToList(), gb.IsAll)
                : null,
            Having = stmt.Having is { } h ? Rewrite(h) : null,
            Qualify = stmt.Qualify is { } q ? Rewrite(q) : null,
            Assertions = stmt.Assertions?.Select(a => a with
            {
                Predicate = Rewrite(a.Predicate),
                Message = a.Message is { } m ? Rewrite(m) : null,
            }).ToList(),
            OrderBy = stmt.OrderBy is { } ob ? RewriteOrderBy(ob) : null,
            LetBindings = stmt.LetBindings?.Select(b => b with { Expression = Rewrite(b.Expression) }).ToList(),
            CommonTableExpressions = stmt.CommonTableExpressions?.Select(cte => cte with
            {
                Body = RewriteQuery(cte.Body),
                RecursiveQuery = cte.RecursiveQuery is { } rq ? RewriteSelect(rq) : null,
            }).ToList(),
        };

        private SelectColumn RewriteSelectColumn(SelectColumn column) => column switch
        {
            SelectAllColumns or SelectTableColumns => column,
            _ => column with { Expression = Rewrite(column.Expression) },
        };

        private TableSource RewriteTableSource(TableSource source) => source switch
        {
            SubquerySource sub => new SubquerySource(RewriteSelect(sub.Query), sub.Alias),
            FunctionSource fn => RewriteFunctionSource(fn),
            _ => source,
        };

        private TableSource RewriteFunctionSource(FunctionSource fn)
        {
            FunctionSource rewritten = new(
                fn.FunctionName,
                fn.Arguments.Select(Rewrite).ToList(),
                fn.Alias,
                fn.Span,
                fn.SchemaName,
                fn.ArgumentNames);
            return rewritten.HasNamedArguments
                ? PermuteFunctionSource(rewritten)
                : rewritten;
        }

        private JoinClause RewriteJoin(JoinClause join) => new(
            join.Type,
            RewriteTableSource(join.Source),
            join.OnCondition is { } cond ? Rewrite(cond) : null,
            join.IsLateral);

        private OrderByClause RewriteOrderBy(OrderByClause orderBy) => new(
            orderBy.Items.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList());

        public Expression Rewrite(Expression expression)
        {
            Expression rewritten = RewriteChildren(expression);
            if (rewritten is FunctionCallExpression { HasNamedArguments: true } call)
            {
                return PermuteCall(call);
            }
            return rewritten;
        }

        private Expression RewriteChildren(Expression expression) => expression switch
        {
            FunctionCallExpression f => f with
            {
                Arguments = f.Arguments.Select(Rewrite).ToList(),
                OrderBy = f.OrderBy?.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList(),
                WithinGroupOrderBy = f.WithinGroupOrderBy?.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList(),
            },
            WindowFunctionCallExpression w => w with
            {
                Arguments = w.Arguments.Select(Rewrite).ToList(),
                Window = RewriteWindow(w.Window),
            },
            BinaryExpression b => new BinaryExpression(Rewrite(b.Left), b.Operator, Rewrite(b.Right)),
            UnaryExpression u => new UnaryExpression(u.Operator, Rewrite(u.Operand)),
            CastExpression c => c with { Expression = Rewrite(c.Expression) },
            CaseExpression ce => new CaseExpression(
                ce.Operand is { } o ? Rewrite(o) : null,
                ce.WhenClauses.Select(w => new WhenClause(Rewrite(w.Condition), Rewrite(w.Result))).ToList(),
                ce.ElseResult is { } er ? Rewrite(er) : null,
                ce.Span),
            InExpression ie => new InExpression(Rewrite(ie.Expression), ie.Values.Select(Rewrite).ToList(), ie.Negated),
            BetweenExpression be => new BetweenExpression(Rewrite(be.Expression), Rewrite(be.Low), Rewrite(be.High), be.Negated),
            IsNullExpression isn => new IsNullExpression(Rewrite(isn.Expression), isn.Negated),
            LikeExpression lk => new LikeExpression(
                Rewrite(lk.Expression), Rewrite(lk.Pattern), Rewrite(lk.EscapeCharacter), lk.CaseInsensitive),
            AtTimeZoneExpression atz => atz with
            {
                Expression = Rewrite(atz.Expression),
                TimeZone = Rewrite(atz.TimeZone),
            },
            SubqueryExpression sq => new SubqueryExpression(RewriteSelect(sq.Query)),
            InSubqueryExpression isq => new InSubqueryExpression(Rewrite(isq.Expression), RewriteSelect(isq.Query), isq.Negated),
            ExistsExpression ex => new ExistsExpression(RewriteSelect(ex.Query), ex.Negated),
            LambdaExpression lam => lam with { Body = Rewrite(lam.Body) },
            IndexAccessExpression ix => ix with { Source = Rewrite(ix.Source), Indices = ix.Indices.Select(Rewrite).ToArray() },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields.Select(f => new StructField(f.Name, Rewrite(f.Value))).ToList(),
            },
            InlineAccessorExpression iax => iax with { Argument = Rewrite(iax.Argument) },
            _ => expression,
        };

        private static WindowSpecification RewriteWindow(WindowSpecification window) => window;

        private Expression PermuteCall(FunctionCallExpression call)
        {
            // Resolve through the scalar registry. Procedural UDFs register
            // a synthetic FunctionDescriptor too (RoutineRegistrar), so the
            // scalar lookup covers built-ins + macro UDFs + procedural UDFs
            // uniformly. The udfs lookup is consulted only to recover
            // UdfParameter.Default AST fragments for skipped slots.
            FunctionDescriptor? descriptor = _functions.TryGetScalarDescriptor(call.CallName);
            if (descriptor is null)
            {
                // Unknown function name. Don't reject here — the
                // downstream PlanTimeFunctionGate produces the standard
                // "Unknown function" diagnostic with source context;
                // we'd just be duplicating the message.
                return call;
            }

            // Optional UDF descriptor lookup for default-expression injection.
            // Macro UDFs are inlined by UdfInliner from positional args, so
            // their defaults flow through that path; procedural UDFs reach
            // the runtime via ProceduralUdfFunction and need defaults
            // injected here.
            UdfDescriptor? udf = null;
            _udfs.TryResolve(call.SchemaName, call.FunctionName, _searchPath, out udf);

            string?[] names = call.ArgumentNames!.ToArray();
            int argCount = names.Length;

            // 1. Locate the boundary between positional and named arguments.
            int positionalCount = 0;
            while (positionalCount < argCount && names[positionalCount] is null) positionalCount++;

            // 2. Enforce "no positional after named".
            for (int i = positionalCount; i < argCount; i++)
            {
                if (names[i] is null)
                {
                    throw new InvalidOperationException(
                        $"Function '{call.CallName}': positional argument at position {i + 1} appears after a named argument. " +
                        "All positional arguments must precede every named argument.");
                }
            }

            // 3. Choose the signature variant that admits all supplied names.
            IReadOnlyList<IReadOnlyList<ParameterSpec>> signatures = descriptor.Signatures
                .Select(v => v.Parameters)
                .ToList();
            IReadOnlyList<ParameterSpec> parameters = ChooseVariant(signatures, names, positionalCount, call.CallName);
            int paramCount = parameters.Count;

            if (positionalCount > paramCount)
            {
                throw new InvalidOperationException(
                    $"Function '{call.CallName}': too many positional arguments " +
                    $"({positionalCount}) for signature with {paramCount} parameter(s).");
            }

            // 4. Place each argument into its declared slot.
            Expression?[] slots = new Expression?[paramCount];
            for (int i = 0; i < positionalCount; i++)
            {
                slots[i] = call.Arguments[i];
            }
            for (int i = positionalCount; i < argCount; i++)
            {
                string name = names[i]!;
                int paramIndex = FindParameterIndex(parameters, name);
                if (paramIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Function '{call.CallName}': no parameter named '{name}'.");
                }
                if (paramIndex < positionalCount)
                {
                    throw new InvalidOperationException(
                        $"Function '{call.CallName}': parameter '{name}' is already supplied positionally.");
                }
                if (slots[paramIndex] is not null)
                {
                    throw new InvalidOperationException(
                        $"Function '{call.CallName}': parameter '{name}' supplied more than once.");
                }
                slots[paramIndex] = call.Arguments[i];
            }

            // 5. Fill skipped slots: trim trailing nulls; middle nulls take
            // the procedural UDF's Default expression when available, or a
            // NULL literal for IsOptional scalar slots. Required slots
            // skipped by the caller are rejected here with a precise name.
            int lastFilled = -1;
            for (int i = paramCount - 1; i >= 0; i--)
            {
                if (slots[i] is not null) { lastFilled = i; break; }
            }
            int totalSlots = lastFilled + 1;

            Expression[] permutedArgs = new Expression[totalSlots];
            for (int i = 0; i < totalSlots; i++)
            {
                if (slots[i] is not null)
                {
                    permutedArgs[i] = slots[i]!;
                    continue;
                }

                // Skipped middle slot. Procedural UDF defaults win, then
                // scalar IsOptional NULL-fill, then error.
                if (udf is not null && i < udf.Parameters.Count && udf.Parameters[i].Default is { } defaultExpr)
                {
                    permutedArgs[i] = defaultExpr;
                    continue;
                }

                if (parameters[i].IsOptional)
                {
                    permutedArgs[i] = new LiteralExpression(null);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Function '{call.CallName}': missing required argument for parameter '{parameters[i].Name}'.");
            }

            return call with
            {
                Arguments = permutedArgs,
                ArgumentNames = null,
            };
        }

        /// <summary>
        /// Picks the first signature variant compatible with the call's
        /// argument shape: all named-arg names appear in the variant's
        /// parameter list AND the variant has room for the positional
        /// prefix. Returns the matching parameter list; for overloaded
        /// functions the descriptor's signature order is the disambiguator.
        /// Throws with a precise reason when nothing matches. Generic over
        /// the variant type because scalar (<see cref="FunctionDescriptor"/>)
        /// and TVF (<see cref="TableValuedFunctionDescriptor"/>) descriptors
        /// share parameter shapes but not return-side metadata.
        /// </summary>
        private static IReadOnlyList<ParameterSpec> ChooseVariant(
            IReadOnlyList<IReadOnlyList<ParameterSpec>> signatures,
            string?[] names,
            int positionalCount,
            string callName)
        {
            IReadOnlyList<ParameterSpec>? best = null;
            string? lastUnknown = null;
            foreach (IReadOnlyList<ParameterSpec> parameters in signatures)
            {
                if (parameters.Count < positionalCount)
                {
                    // Positional prefix doesn't even fit; skip.
                    continue;
                }

                bool allNamesPresent = true;
                for (int i = positionalCount; i < names.Length; i++)
                {
                    string name = names[i]!;
                    if (FindParameterIndex(parameters, name) < 0)
                    {
                        allNamesPresent = false;
                        lastUnknown = name;
                        break;
                    }
                }
                if (!allNamesPresent) continue;

                best = parameters;
                break;
            }

            if (best is null)
            {
                throw new InvalidOperationException(
                    lastUnknown is not null
                        ? $"Function '{callName}': no signature variant accepts a parameter named '{lastUnknown}'."
                        : $"Function '{callName}': no signature variant matches the supplied named arguments.");
            }
            return best;
        }

        /// <summary>
        /// TVF-flavoured sibling of <see cref="PermuteCall"/>. Same
        /// positional/named partitioning + variant selection + slot
        /// placement; differs only in registry lookup
        /// (<see cref="FunctionRegistry.TryGetTableValuedDescriptor"/>)
        /// and the absence of a parallel UDF-default path (TVFs don't
        /// carry default AST fragments — skipped optional slots are
        /// NULL-filled).
        /// </summary>
        private TableSource PermuteFunctionSource(FunctionSource fn)
        {
            TableValuedFunctionDescriptor? descriptor =
                _functions.TryGetTableValuedDescriptor(new QualifiedName(fn.SchemaName ?? string.Empty, fn.FunctionName))
                ?? (fn.SchemaName is null
                    ? _functions.TryGetTableValuedDescriptor(new QualifiedName("system", fn.FunctionName))
                    : null);
            if (descriptor is null)
            {
                // Unknown TVF — let SourcePlanner produce the standard
                // diagnostic with full source context. Carry the
                // ArgumentNames forward so a later stage can still
                // distinguish "we never resolved this" from "permuter
                // already consumed it".
                return fn;
            }

            string?[] names = fn.ArgumentNames!.ToArray();
            int argCount = names.Length;

            // 1. Locate the boundary between positional and named arguments.
            int positionalCount = 0;
            while (positionalCount < argCount && names[positionalCount] is null) positionalCount++;

            // 2. Enforce "no positional after named".
            for (int i = positionalCount; i < argCount; i++)
            {
                if (names[i] is null)
                {
                    throw new InvalidOperationException(
                        $"Function '{fn.CallName}': positional argument at position {i + 1} appears after a named argument. " +
                        "All positional arguments must precede every named argument.");
                }
            }

            // 3. Choose the signature variant that admits all supplied names.
            IReadOnlyList<IReadOnlyList<ParameterSpec>> signatures = descriptor.Signatures
                .Select(v => v.Parameters)
                .ToList();
            IReadOnlyList<ParameterSpec> parameters = ChooseVariant(signatures, names, positionalCount, fn.CallName);
            int paramCount = parameters.Count;

            if (positionalCount > paramCount)
            {
                throw new InvalidOperationException(
                    $"Function '{fn.CallName}': too many positional arguments " +
                    $"({positionalCount}) for signature with {paramCount} parameter(s).");
            }

            // 4. Place each argument into its declared slot.
            Expression?[] slots = new Expression?[paramCount];
            for (int i = 0; i < positionalCount; i++)
            {
                slots[i] = fn.Arguments[i];
            }
            for (int i = positionalCount; i < argCount; i++)
            {
                string name = names[i]!;
                int paramIndex = FindParameterIndex(parameters, name);
                
                if (paramIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Function '{fn.CallName}': no parameter named '{name}'.");
                }
                else if (paramIndex < positionalCount)
                {
                    throw new InvalidOperationException(
                        $"Function '{fn.CallName}': parameter '{name}' is already supplied positionally.");
                }
                else if (slots[paramIndex] is not null)
                {
                    throw new InvalidOperationException(
                        $"Function '{fn.CallName}': parameter '{name}' supplied more than once.");
                }

                slots[paramIndex] = fn.Arguments[i];
            }

            // 5. Trim trailing nulls; middle skips NULL-fill when the slot
            // is optional, error otherwise.
            int lastFilled = -1;
            for (int i = paramCount - 1; i >= 0; i--)
            {
                if (slots[i] is not null) { lastFilled = i; break; }
            }
            int totalSlots = lastFilled + 1;

            Expression[] permutedArgs = new Expression[totalSlots];
            for (int i = 0; i < totalSlots; i++)
            {
                if (slots[i] is not null)
                {
                    permutedArgs[i] = slots[i]!;
                    continue;
                }

                if (parameters[i].IsOptional)
                {
                    permutedArgs[i] = new LiteralExpression(null);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Function '{fn.CallName}': missing required argument for parameter '{parameters[i].Name}'.");
            }

            return fn with
            {
                Arguments = permutedArgs,
                ArgumentNames = null,
            };
        }

        private static int FindParameterIndex(IReadOnlyList<ParameterSpec> parameters, string name)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                if (string.Equals(parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
