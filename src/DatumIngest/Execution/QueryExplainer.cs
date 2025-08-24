using System.Text;
using DatumIngest.Execution.Operators;
using DatumIngest.Manifest;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Produces a static <see cref="ExplainPlanNode"/> tree from an operator tree,
/// describing the execution plan structure and emitting warnings about
/// potential performance issues.
/// </summary>
public static class QueryExplainer
{
    // Default selectivity when no statistics are available.
    // These are intentionally conservative heuristics, not data-driven.
    private const double DefaultFilterSelectivity = 0.33;

    /// <summary>
    /// Builds an explain plan tree from the root operator.
    /// </summary>
    /// <param name="root">The root of the operator tree.</param>
    /// <returns>An explain plan node tree describing the plan.</returns>
    public static ExplainPlanNode Explain(IQueryOperator root)
    {
        IReadOnlyDictionary<string, FeatureManifest>? stats = CollectColumnStatistics(root);
        return BuildNode(root, stats);
    }

    private static ExplainPlanNode BuildNode(IQueryOperator op, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        return op switch
        {
            InstrumentedOperator instrumented => BuildNode(instrumented.Inner, stats),
            ScanOperator scan => BuildScanNode(scan),
            IndexScanOperator indexScan => BuildIndexScanNode(indexScan),
            FilterOperator filter => BuildFilterNode(filter, stats),
            ProjectOperator project => BuildProjectNode(project, stats),
            JoinOperator join => BuildJoinNode(join, stats),
            OrderByOperator orderBy => BuildOrderByNode(orderBy, stats),
            LimitOperator limit => BuildLimitNode(limit, stats),
            AliasOperator alias => BuildAliasNode(alias, stats),
            SubqueryOperator subquery => BuildSubqueryNode(subquery, stats),
            LateMaterializationOperator lateMat => BuildLateMaterializationNode(lateMat, stats),
            GroupByOperator groupBy => BuildGroupByNode(groupBy, stats),
            CommonTableExpressionOperator cte => BuildCommonTableExpressionNode(cte, stats),
            RecursiveCommonTableExpressionOperator recursiveCte => BuildRecursiveCommonTableExpressionNode(recursiveCte, stats),
            SetOperationOperator setOp => BuildSetOperationNode(setOp, stats),
            _ => BuildGenericNode(op, stats),
        };
    }

    private static ExplainPlanNode BuildScanNode(ScanOperator scan)
    {
        OperatorPlanDescription description = scan.DescribeForExplain();

        string tableName = scan.Descriptor.Name;
        string provider = scan.Descriptor.Provider;
        string columns = scan.RequiredColumns is not null
            ? string.Join(", ", scan.RequiredColumns)
            : "*";

        string details = $"table: {tableName}, provider: {provider}, columns: [{columns}]";

        if (scan.FilterHint is not null)
        {
            details += $", statistics filter: {FormatExpression(scan.FilterHint)}";
        }

        ExplainPlanNode node = new()
        {
            OperatorName = description.AccessStrategy?.Method switch
            {
                AccessMethod.IndexScan => "Index Scan",
                _ => "Scan",
            },
            Details = details,
            EstimatedRows = scan.EstimatedRowCount,
            AccessStrategyMethod = description.AccessStrategy?.Method,
            Properties = description.Properties,
        };

        AppendAccessStrategyAnnotations(node, description.AccessStrategy);

        return node;
    }

    private static ExplainPlanNode BuildIndexScanNode(IndexScanOperator indexScan)
    {
        OperatorPlanDescription description = indexScan.DescribeForExplain();

        StringBuilder details = new();
        if (description.Properties is not null)
        {
            details.AppendJoin(", ", description.Properties.Select(
                keyValuePair => $"{keyValuePair.Key}: {keyValuePair.Value}"));
        }

        ExplainPlanNode node = new()
        {
            OperatorName = "Index Scan",
            Details = details.ToString(),
            EstimatedRows = description.EstimatedRows,
            AccessStrategyMethod = description.AccessStrategy?.Method,
            Properties = description.Properties,
        };

        AppendAccessStrategyAnnotations(node, description.AccessStrategy);

        foreach (string annotation in description.Annotations)
        {
            node.Annotations.Add(annotation);
        }

        return node;
    }

    /// <summary>
    /// Builds a generic explain node from an operator's <see cref="OperatorPlanDescription"/>.
    /// Used as the fallback for operators without dedicated BuildXxxNode methods.
    /// </summary>
    private static ExplainPlanNode BuildGenericNode(IQueryOperator op, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        OperatorPlanDescription description = op.DescribeForExplain();

        StringBuilder details = new();
        if (description.Properties is not null)
        {
            details.AppendJoin(", ", description.Properties.Select(
                keyValuePair => $"{keyValuePair.Key}: {keyValuePair.Value}"));
        }

        List<ExplainPlanNode> children = [];
        foreach ((IQueryOperator child, string? label) in description.Children)
        {
            ExplainPlanNode childNode = BuildNode(child, stats);
            childNode.ChildLabel = label;
            children.Add(childNode);
        }

        ExplainPlanNode node = new()
        {
            OperatorName = description.OperatorName,
            Details = details.ToString(),
            Children = children,
            EstimatedRows = description.EstimatedRows,
            AccessStrategyMethod = description.AccessStrategy?.Method,
            Properties = description.Properties,
        };

        AppendAccessStrategyAnnotations(node, description.AccessStrategy);

        foreach (string warning in description.Warnings)
        {
            node.Warnings.Add(warning);
        }

        foreach (string annotation in description.Annotations)
        {
            node.Annotations.Add(annotation);
        }

        return node;
    }

    /// <summary>
    /// Appends annotations describing data access strategy and pruning capabilities
    /// to an explain plan node.
    /// </summary>
    private static void AppendAccessStrategyAnnotations(
        ExplainPlanNode node, AccessStrategyDescription? accessStrategy)
    {
        if (accessStrategy is null)
        {
            return;
        }

        foreach (PruningCapability pruning in accessStrategy.PruningCapabilities)
        {
            string technique = pruning.Technique switch
            {
                PruningTechnique.StatisticsPruning => "statistics pruning (min/max bounds)",
                PruningTechnique.BloomFilterPruning => "bloom filter pruning",
                PruningTechnique.SortedIndexPruning => "sorted index pruning",
                PruningTechnique.ExactSeek => "exact index seek",
                PruningTechnique.BPlusTreeIndexPruning => "B+Tree index pruning",
                _ => pruning.Technique.ToString(),
            };

            string columns = pruning.Columns.Count > 0
                ? $" on [{string.Join(", ", pruning.Columns)}]"
                : "";

            string pending = pruning.PendingRuntime ? " (pending runtime)" : "";

            node.Annotations.Add($"{technique}{columns}{pending}");
        }
    }

    private static ExplainPlanNode BuildFilterNode(FilterOperator filter, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        ExplainPlanNode child = BuildNode(filter.Source, stats);

        ExplainPlanNode node = new()
        {
            OperatorName = "Filter",
            Details = $"predicate: {FormatExpression(filter.Predicate)}",
            Children = { child },
            EstimatedRows = EstimateFilterRows(child.EstimatedRows, filter.Predicate, stats),
        };

        // Warn about pattern matching predicates (no index, full scan).
        if (ContainsPatternMatch(filter.Predicate))
        {
            node.Warnings.Add("Pattern matching predicate requires full scan of input rows.");
        }

        return node;
    }

    private static ExplainPlanNode BuildProjectNode(ProjectOperator project, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        List<string> columnNames = [];
        foreach (SelectColumn column in project.Columns)
        {
            if (column.Alias is not null)
            {
                columnNames.Add($"{FormatExpression(column.Expression)} AS {column.Alias}");
            }
            else
            {
                columnNames.Add(FormatExpression(column.Expression));
            }
        }

        ExplainPlanNode child = BuildNode(project.Source, stats);

        return new ExplainPlanNode
        {
            OperatorName = "Project",
            Details = string.Join(", ", columnNames),
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildJoinNode(JoinOperator join, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        string joinType = join.Type switch
        {
            JoinType.Inner => "INNER",
            JoinType.Left => "LEFT",
            JoinType.Right => "RIGHT",
            JoinType.FullOuter => "FULL OUTER",
            JoinType.Cross => "CROSS",
            _ => join.Type.ToString(),
        };

        string condition = join.OnCondition is not null
            ? $", on: {FormatExpression(join.OnCondition)}"
            : "";

        // Determine the actual join strategy using JoinKeyExtractor.
        string strategy;
        JoinKeyExtractionResult? extraction = null;
        if (join.Type == JoinType.Cross)
        {
            strategy = "nested-loop";
        }
        else
        {
            extraction = JoinKeyExtractor.TryExtract(join.OnCondition);
            if (extraction is not null)
            {
                strategy = extraction.Residual is not null ? "hash+filter" : "hash";
            }
            else
            {
                strategy = "nested-loop";
            }
        }

        ExplainPlanNode leftChild = BuildNode(join.Left, stats);
        leftChild.ChildLabel = "probe";

        ExplainPlanNode rightChild = BuildNode(join.Right, stats);
        rightChild.ChildLabel = "build";

        long? estimatedRows = EstimateJoinRows(
            join.Type, leftChild.EstimatedRows, rightChild.EstimatedRows,
            extraction, stats, out bool usedFallback);

        ExplainPlanNode node = new()
        {
            OperatorName = $"{joinType} Join",
            Details = $"strategy: {strategy}{condition}",
            Children = { leftChild, rightChild },
            EstimatedRows = estimatedRows,
        };

        if (usedFallback)
        {
            node.Annotations.Add("no column statistics — row estimate uses containment heuristic");
        }

        // Warn about cross joins (can produce very large output).
        if (join.Type == JoinType.Cross)
        {
            node.Warnings.Add("CROSS JOIN produces a cartesian product; output size = left × right.");
        }

        // Warn about full outer joins (both sides fully materialized).
        if (join.Type == JoinType.FullOuter)
        {
            node.Warnings.Add("FULL OUTER JOIN materializes both sides in memory.");
        }

        // Warn about nested-loop join performance.
        if (strategy == "nested-loop" && join.Type != JoinType.Cross)
        {
            node.Warnings.Add(
                "Nested-loop join has O(n*m) complexity. Consider rewriting the ON condition as an equi-join.");
        }

        return node;
    }

    private static ExplainPlanNode BuildOrderByNode(OrderByOperator orderBy, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        List<string> items = [];
        foreach (OrderByItem item in orderBy.OrderByItems)
        {
            string direction = item.Direction == SortDirection.Ascending ? "ASC" : "DESC";
            items.Add($"{FormatExpression(item.Expression)} {direction}");
        }

        ExplainPlanNode child = BuildNode(orderBy.Source, stats);

        ExplainPlanNode node = new()
        {
            OperatorName = "Sort",
            Details = string.Join(", ", items),
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };

        if (orderBy.TopNRows is int topN)
        {
            node.Annotations.Add($"bounded top-N sort (N={topN})");
        }
        else
        {
            node.Warnings.Add("ORDER BY materializes all input rows for sorting.");
        }

        return node;
    }

    private static ExplainPlanNode BuildLimitNode(LimitOperator limit, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        string details = limit.Offset > 0
            ? $"limit: {limit.Limit}, offset: {limit.Offset}"
            : $"limit: {limit.Limit}";

        ExplainPlanNode child = BuildNode(limit.Source, stats);
        long effectiveLimit = limit.Limit + limit.Offset;

        return new ExplainPlanNode
        {
            OperatorName = "Limit",
            Details = details,
            Children = { child },
            EstimatedRows = child.EstimatedRows.HasValue
                ? Math.Min(effectiveLimit, child.EstimatedRows.Value)
                : effectiveLimit,
        };
    }

    private static ExplainPlanNode BuildAliasNode(AliasOperator alias, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        ExplainPlanNode child = BuildNode(alias.Source, stats);

        return new ExplainPlanNode
        {
            OperatorName = "Alias",
            Details = $"as: {alias.Alias}",
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildSubqueryNode(SubqueryOperator subquery, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        ExplainPlanNode child = BuildNode(subquery.InnerOperator, stats);

        return new ExplainPlanNode
        {
            OperatorName = "Subquery",
            Details = $"alias: {subquery.Alias}",
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildCommonTableExpressionNode(
        CommonTableExpressionOperator commonTableExpression,
        IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        ExplainPlanNode child = BuildNode(commonTableExpression.InnerOperator, stats);
        string mode = commonTableExpression.IsMaterialized ? "materialized" : "inlined";

        return new ExplainPlanNode
        {
            OperatorName = "CTE",
            Details = $"name: {commonTableExpression.Name}, mode: {mode}",
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildRecursiveCommonTableExpressionNode(
        RecursiveCommonTableExpressionOperator recursiveCommonTableExpression,
        IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        ExplainPlanNode anchorChild = BuildNode(recursiveCommonTableExpression.AnchorOperator, stats);

        return new ExplainPlanNode
        {
            OperatorName = "Recursive CTE",
            Details = $"name: {recursiveCommonTableExpression.Name}",
            Children = { anchorChild },
            EstimatedRows = anchorChild.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildLateMaterializationNode(LateMaterializationOperator lateMat, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        string columns = string.Join(", ", lateMat.DeferredColumns);
        string source = lateMat.Alias is not null
            ? $"{lateMat.Alias} ({lateMat.Descriptor.Provider})"
            : lateMat.Descriptor.Name;

        ExplainPlanNode child = BuildNode(lateMat.Child, stats);

        return new ExplainPlanNode
        {
            OperatorName = "Late Materialize",
            Details = $"source: {source}, key: {lateMat.KeyColumn}, fetch: [{columns}]",
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };
    }

    private static ExplainPlanNode BuildGroupByNode(GroupByOperator groupBy, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        string keys = groupBy.GroupByExpressions.Count > 0
            ? string.Join(", ", groupBy.GroupByExpressions.Select(FormatExpression))
            : "(global)";

        string aggregates = string.Join(", ", groupBy.AggregateColumns.Select(
            aggregateColumn => aggregateColumn.OutputName));

        ExplainPlanNode child = BuildNode(groupBy.Source, stats);

        ExplainPlanNode node = new()
        {
            OperatorName = groupBy.StreamingSorted ? "Streaming Group By" : "Group By",
            Details = $"keys: [{keys}], aggregates: [{aggregates}]",
            Children = { child },
            EstimatedRows = child.EstimatedRows,
        };

        if (!groupBy.StreamingSorted)
        {
            node.Warnings.Add("GroupBy materializes all groups in memory.");
        }
        else
        {
            node.Annotations.Add("sorted input \u2014 streaming one group at a time");
        }

        return node;
    }

    private static ExplainPlanNode BuildSetOperationNode(
        SetOperationOperator setOp, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        string operationName = setOp.OperationType switch
        {
            SetOperationType.Union => "UNION",
            SetOperationType.Intersect => "INTERSECT",
            SetOperationType.Except => "EXCEPT",
            _ => setOp.OperationType.ToString(),
        };

        if (setOp.All)
        {
            operationName += " ALL";
        }

        ExplainPlanNode leftChild = BuildNode(setOp.Left, stats);
        ExplainPlanNode rightChild = BuildNode(setOp.Right, stats);

        return new ExplainPlanNode
        {
            OperatorName = "SetOperation",
            Details = operationName,
            Children = { leftChild, rightChild },
        };
    }

    // ──────────────── Cardinality estimation ────────────────

    /// <summary>
    /// Estimates the number of rows surviving a filter predicate.
    /// When per-column statistics are available from a manifest, uses NDV-based
    /// selectivity for equality predicates and actual null ratios for IS NULL.
    /// Falls back to fixed default selectivities when no statistics are available.
    /// </summary>
    private static long? EstimateFilterRows(
        long? inputRows, Expression predicate, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        if (inputRows is null)
        {
            return null;
        }

        double selectivity = EstimateSelectivity(predicate, stats);
        long estimate = Math.Max(1, (long)(inputRows.Value * selectivity));
        return estimate;
    }

    private static double EstimateSelectivity(
        Expression predicate, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        return predicate switch
        {
            BinaryExpression { Operator: BinaryOperator.And } bin
                => EstimateSelectivity(bin.Left, stats) * EstimateSelectivity(bin.Right, stats),
            BinaryExpression { Operator: BinaryOperator.Or } bin
                => Math.Min(1.0, EstimateSelectivity(bin.Left, stats) + EstimateSelectivity(bin.Right, stats)),
            BinaryExpression { Operator: BinaryOperator.Equal } eq
                => EstimateEqualitySelectivity(eq, stats),
            BinaryExpression { Operator: BinaryOperator.NotEqual } neq
                => 1.0 - EstimateEqualitySelectivity(neq, stats),
            BinaryExpression { Operator: BinaryOperator.Like } => 0.25,
            BinaryExpression { Operator: BinaryOperator.ILike } => 0.25,
            BinaryExpression { Operator: BinaryOperator.Regexp } => 0.10,
            LikeExpression => 0.25,
            IsNullExpression isNull => EstimateIsNullSelectivity(isNull, stats),
            InExpression inExpr => EstimateInSelectivity(inExpr, stats),
            BetweenExpression => 0.25,
            UnaryExpression { Operator: UnaryOperator.Not } unary
                => 1.0 - EstimateSelectivity(unary.Operand, stats),
            _ => DefaultFilterSelectivity,
        };
    }

    private static double EstimateEqualitySelectivity(
        BinaryExpression expression, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        if (stats is not null)
        {
            FeatureManifest? feature =
                FindColumnStatistics(expression.Left, stats) ??
                FindColumnStatistics(expression.Right, stats);

            if (feature is not null && feature.EstimatedDistinctCount > 0)
            {
                return 1.0 / feature.EstimatedDistinctCount;
            }
        }

        return 0.10;
    }

    private static double EstimateIsNullSelectivity(
        IsNullExpression expression, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        if (stats is not null)
        {
            FeatureManifest? feature = FindColumnStatistics(expression.Expression, stats);

            if (feature is not null && feature.NullRatio.HasValue)
            {
                return expression.Negated ? 1.0 - feature.NullRatio.Value : feature.NullRatio.Value;
            }
        }

        return expression.Negated ? 0.90 : 0.10;
    }

    private static double EstimateInSelectivity(
        InExpression expression, IReadOnlyDictionary<string, FeatureManifest>? stats)
    {
        double perValue = 0.10;

        if (stats is not null)
        {
            FeatureManifest? feature = FindColumnStatistics(expression.Expression, stats);

            if (feature is not null && feature.EstimatedDistinctCount > 0)
            {
                perValue = 1.0 / feature.EstimatedDistinctCount;
            }
        }

        return Math.Min(1.0, expression.Values.Count * perValue);
    }

    /// <summary>
    /// Extracts a <see cref="FeatureManifest"/> for the column referenced by the expression,
    /// or <c>null</c> if the expression is not a column reference or no statistics exist.
    /// </summary>
    private static FeatureManifest? FindColumnStatistics(
        Expression expression, IReadOnlyDictionary<string, FeatureManifest> stats)
    {
        if (expression is not ColumnReference column)
        {
            return null;
        }

        // Try qualified lookup first (e.g. "t.age"), then bare column name.
        if (column.TableName is not null)
        {
            string qualified = $"{column.TableName}.{column.ColumnName}";
            if (stats.TryGetValue(qualified, out FeatureManifest? feature))
            {
                return feature;
            }
        }

        stats.TryGetValue(column.ColumnName, out FeatureManifest? bareFeature);
        return bareFeature;
    }

    // ──────────────── Column statistics collection ────────────────

    /// <summary>
    /// Walks the operator tree and collects per-column <see cref="FeatureManifest"/>
    /// entries from all scan operators. Column names are registered both bare and
    /// alias-qualified so that predicates like <c>t.age = 30</c> resolve correctly.
    /// </summary>
    private static IReadOnlyDictionary<string, FeatureManifest>? CollectColumnStatistics(IQueryOperator root)
    {
        Dictionary<string, FeatureManifest>? result = null;
        CollectColumnStatisticsCore(root, alias: null, ref result);
        return result;
    }

    private static void CollectColumnStatisticsCore(
        IQueryOperator op, string? alias, ref Dictionary<string, FeatureManifest>? result)
    {
        switch (op)
        {
            case InstrumentedOperator instrumented:
                CollectColumnStatisticsCore(instrumented.Inner, alias, ref result);
                break;

            case ScanOperator scan:
                if (scan.ColumnStatistics is null)
                {
                    break;
                }

                result ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, FeatureManifest> entry in scan.ColumnStatistics)
                {
                    result.TryAdd(entry.Key, entry.Value);
                    if (alias is not null)
                    {
                        result.TryAdd($"{alias}.{entry.Key}", entry.Value);
                    }
                }

                break;

            case AliasOperator aliasOperator:
                CollectColumnStatisticsCore(aliasOperator.Source, aliasOperator.Alias, ref result);
                break;

            case FilterOperator filter:
                CollectColumnStatisticsCore(filter.Source, alias, ref result);
                break;

            case ProjectOperator project:
                CollectColumnStatisticsCore(project.Source, alias, ref result);
                break;

            case JoinOperator join:
                CollectColumnStatisticsCore(join.Left, alias, ref result);
                CollectColumnStatisticsCore(join.Right, alias, ref result);
                break;

            case OrderByOperator orderBy:
                CollectColumnStatisticsCore(orderBy.Source, alias, ref result);
                break;

            case LimitOperator limit:
                CollectColumnStatisticsCore(limit.Source, alias, ref result);
                break;

            case SubqueryOperator subquery:
                CollectColumnStatisticsCore(subquery.InnerOperator, alias, ref result);
                break;

            case LateMaterializationOperator lateMaterialization:
                CollectColumnStatisticsCore(lateMaterialization.Child, alias, ref result);
                break;

            case CommonTableExpressionOperator commonTableExpression:
                CollectColumnStatisticsCore(commonTableExpression.InnerOperator, alias, ref result);
                break;

            case RecursiveCommonTableExpressionOperator recursiveCommonTableExpression:
                CollectColumnStatisticsCore(recursiveCommonTableExpression.AnchorOperator, alias, ref result);
                break;
        }
    }

    /// <summary>
    /// Estimates the number of rows produced by a join.
    /// Cross joins return left × right. Equi-joins use NDV-based estimation when
    /// column statistics are available, falling back to the containment principle.
    /// </summary>
    /// <param name="joinType">The type of join.</param>
    /// <param name="leftRows">Estimated rows from the left (probe) side.</param>
    /// <param name="rightRows">Estimated rows from the right (build) side.</param>
    /// <param name="extraction">Extracted equi-join key pairs, or null for non-equi joins.</param>
    /// <param name="stats">Per-column feature statistics, or null.</param>
    /// <param name="usedFallback">Set to true when the estimate uses the no-stats heuristic.</param>
    private static long? EstimateJoinRows(
        JoinType joinType,
        long? leftRows,
        long? rightRows,
        JoinKeyExtractionResult? extraction,
        IReadOnlyDictionary<string, FeatureManifest>? stats,
        out bool usedFallback)
    {
        usedFallback = false;

        if (leftRows is null || rightRows is null)
        {
            return null;
        }

        long left = leftRows.Value;
        long right = rightRows.Value;

        if (joinType == JoinType.Cross)
        {
            return left * right;
        }

        if (extraction is not null)
        {
            long maxNdv = 0;

            if (stats is not null && extraction.KeyPairs.Count > 0)
            {
                FeatureManifest? leftFeature = FindColumnStatistics(extraction.KeyPairs[0].Left, stats);
                FeatureManifest? rightFeature = FindColumnStatistics(extraction.KeyPairs[0].Right, stats);

                maxNdv = Math.Max(
                    leftFeature?.EstimatedDistinctCount ?? 0,
                    rightFeature?.EstimatedDistinctCount ?? 0);
            }

            long estimate;

            if (maxNdv > 0)
            {
                // NDV-based: left × right / max(NDV_left, NDV_right)
                // models uniform key distribution across both sides.
                estimate = Math.Max(1, (long)((double)left * right / maxNdv));
            }
            else
            {
                // No statistics available — apply the containment principle:
                // assume every key in the smaller side exists in the larger side,
                // producing a 1:many foreign-key relationship.
                estimate = Math.Max(left, right);
                usedFallback = true;
            }

            // For outer joins, the minimum is the preserved side.
            return joinType switch
            {
                JoinType.Left => Math.Max(left, estimate),
                JoinType.Right => Math.Max(right, estimate),
                JoinType.FullOuter => Math.Max(Math.Max(left, right), estimate),
                _ => estimate,
            };
        }

        // Nested-loop non-equi join — assume the same selectivity as a filter.
        return Math.Max(1, (long)(left * right * DefaultFilterSelectivity));
    }

    // ──────────────── Expression formatting ────────────────

    /// <summary>
    /// Formats an expression as a human-readable SQL-like string.
    /// </summary>
    /// <param name="expression">The expression to format.</param>
    /// <returns>A formatted string representation.</returns>
    public static string FormatExpression(Expression expression)
    {
        return expression switch
        {
            ColumnReference col => col.TableName is not null
                ? $"{col.TableName}.{col.ColumnName}"
                : col.ColumnName,
            LiteralExpression lit => FormatLiteral(lit.Value),
            BinaryExpression bin => $"{FormatExpression(bin.Left)} {FormatBinaryOp(bin.Operator)} {FormatExpression(bin.Right)}",
            LikeExpression like => $"{FormatExpression(like.Expression)} {(like.CaseInsensitive ? "ILIKE" : "LIKE")} {FormatExpression(like.Pattern)} ESCAPE {FormatExpression(like.EscapeCharacter)}",
            UnaryExpression unary => FormatUnary(unary),
            FunctionCallExpression func => $"{func.FunctionName}({(func.Distinct ? "DISTINCT " : "")}{string.Join(", ", func.Arguments.Select(FormatExpression))})",
            InExpression inExpr => $"{FormatExpression(inExpr.Expression)} {(inExpr.Negated ? "NOT IN" : "IN")} ({string.Join(", ", inExpr.Values.Select(FormatExpression))})",
            BetweenExpression between => $"{FormatExpression(between.Expression)} {(between.Negated ? "NOT BETWEEN" : "BETWEEN")} {FormatExpression(between.Low)} AND {FormatExpression(between.High)}",
            IsNullExpression isNull => $"{FormatExpression(isNull.Expression)} {(isNull.Negated ? "IS NOT NULL" : "IS NULL")}",
            CastExpression cast => $"CAST({FormatExpression(cast.Expression)} AS {cast.TargetType})",
            CaseExpression caseExpr => FormatCaseExpression(caseExpr),
            WindowFunctionCallExpression window => FormatWindowFunctionCall(window),
            _ => expression.ToString() ?? "?",
        };
    }

    private static string FormatLiteral(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s}'",
            bool b => b ? "TRUE" : "FALSE",
            _ => value.ToString() ?? "NULL",
        };
    }

    private static string FormatBinaryOp(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.LessThan => "<",
            BinaryOperator.GreaterThan => ">",
            BinaryOperator.LessThanOrEqual => "<=",
            BinaryOperator.GreaterThanOrEqual => ">=",
            BinaryOperator.And => "AND",
            BinaryOperator.Or => "OR",
            BinaryOperator.Like => "LIKE",
            BinaryOperator.ILike => "ILIKE",
            BinaryOperator.Regexp => "REGEXP",
            _ => op.ToString(),
        };
    }

    private static string FormatUnary(UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT {FormatExpression(unary.Operand)}",
            UnaryOperator.Negate => $"-{FormatExpression(unary.Operand)}",
            _ => $"{unary.Operator} {FormatExpression(unary.Operand)}",
        };
    }

    private static string FormatCaseExpression(CaseExpression caseExpression)
    {
        System.Text.StringBuilder builder = new();
        builder.Append("CASE");

        if (caseExpression.Operand is not null)
        {
            builder.Append(' ');
            builder.Append(FormatExpression(caseExpression.Operand));
        }

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            builder.Append(" WHEN ");
            builder.Append(FormatExpression(whenClause.Condition));
            builder.Append(" THEN ");
            builder.Append(FormatExpression(whenClause.Result));
        }

        if (caseExpression.ElseResult is not null)
        {
            builder.Append(" ELSE ");
            builder.Append(FormatExpression(caseExpression.ElseResult));
        }

        builder.Append(" END");
        return builder.ToString();
    }

    private static string FormatWindowFunctionCall(WindowFunctionCallExpression window)
    {
        System.Text.StringBuilder builder = new();
        builder.Append(window.FunctionName);
        builder.Append('(');
        if (window.Distinct)
        {
            builder.Append("DISTINCT ");
        }
        builder.Append(string.Join(", ", window.Arguments.Select(FormatExpression)));
        builder.Append(") OVER(");

        bool needsSpace = false;
        if (window.Window.PartitionBy is { Count: > 0 } partitionBy)
        {
            builder.Append("PARTITION BY ");
            builder.Append(string.Join(", ", partitionBy.Select(FormatExpression)));
            needsSpace = true;
        }

        if (window.Window.OrderBy is { Count: > 0 } orderBy)
        {
            if (needsSpace) builder.Append(' ');
            builder.Append("ORDER BY ");
            builder.Append(string.Join(", ", orderBy.Select(item =>
            {
                string direction = item.Direction == SortDirection.Descending ? " DESC" : "";
                return FormatExpression(item.Expression) + direction;
            })));
            needsSpace = true;
        }

        if (window.Window.Frame is { } frame)
        {
            if (needsSpace) builder.Append(' ');
            builder.Append("ROWS BETWEEN ");
            builder.Append(FormatFrameBound(frame.Start));
            builder.Append(" AND ");
            builder.Append(FormatFrameBound(frame.End));
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string FormatFrameBound(FrameBound bound)
    {
        return bound switch
        {
            UnboundedPrecedingBound => "UNBOUNDED PRECEDING",
            PrecedingBound preceding => $"{preceding.Offset} PRECEDING",
            CurrentRowBound => "CURRENT ROW",
            FollowingBound following => $"{following.Offset} FOLLOWING",
            UnboundedFollowingBound => "UNBOUNDED FOLLOWING",
            _ => "?",
        };
    }

    /// <summary>
    /// Returns whether the given expression contains a LIKE, ILIKE, or REGEXP operator.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <returns><c>true</c> if a pattern-matching operator is found.</returns>
    public static bool ContainsPatternMatch(Expression expression)
    {
        return expression switch
        {
            BinaryExpression bin => bin.Operator is BinaryOperator.Like
                    or BinaryOperator.ILike
                    or BinaryOperator.Regexp
                || ContainsPatternMatch(bin.Left)
                || ContainsPatternMatch(bin.Right),
            UnaryExpression unary => ContainsPatternMatch(unary.Operand),
            _ => false,
        };
    }
}
