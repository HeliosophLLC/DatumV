using System.Diagnostics.CodeAnalysis;

using DatumIngest.Functions;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Parse-time pre-flight pass. Walks every <see cref="FunctionCallExpression"/>
/// reachable from a top-level query AST and collects:
/// <list type="bullet">
///   <item><description><strong>Catalog-known model references that aren't
///     ready to execute</strong> — <c>models.X</c> not installed,
///     <c>models.X@&lt;version&gt;</c> not on disk, or pinned to an unknown
///     version. Each becomes a <see cref="PreFlightModelRequirement"/> so the
///     host can offer an install / fix flow before any operator is built.
///     </description></item>
///   <item><description><strong>Likely typos</strong> — unknown bare function
///     names, or <c>models.X</c> where X matches no known identifier. Each
///     becomes a <see cref="PreFlightSuggestion"/>; the cheap-name pass
///     survives to the UI instead of being dropped by the eventual
///     <see cref="PlanTimeFunctionGate"/> "Unknown function" error.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Runs in <c>TableCatalog.PlanQuery</c> between <c>NamedArgPermuter</c> and
/// <c>UdfInliner</c>. Sits at the pre-inliner stage deliberately — UDF bodies
/// (whose <c>models.X</c> references may have been authored by someone other
/// than the current caller) stay opaque so a UDF using a not-yet-installed
/// model only blocks pre-flight for queries that name the model directly.
/// </para>
/// <para>
/// Scope: the top-level query statement, its CTE bodies (anchor +
/// recursive member), joined sub-queries, and INSERT … query sources.
/// UDF bodies (already-registered function descriptors) are skipped.
/// Bodies declared inline via CREATE OR REPLACE MODEL never reach the
/// planner — they're interpreted by <c>ProceduralModelFunction</c>.
/// </para>
/// <para>
/// The walker does not throw — call sites decide whether a non-empty
/// <see cref="PreFlightRequirements"/> result blocks execution
/// (the production wiring in <c>TableCatalog.PlanQuery</c> throws
/// <see cref="PreFlightRequiredException"/>). The host catches the typed
/// exception and projects the payload to the client (the query-stream
/// service emits a <c>preflight_required</c> NDJSON event).
/// </para>
/// </remarks>
internal static class PreFlightWalker
{
    private const string ModelSchema = "models";

    /// <summary>
    /// Walks <paramref name="query"/>, returning the collected requirements.
    /// Empty result == no blocker; the planner proceeds.
    /// </summary>
    /// <param name="query">The post-Permuter AST to walk.</param>
    /// <param name="models">
    /// The runtime model catalog (residency oracle). <see langword="null"/>
    /// is tolerated for standalone hosts without a model surface — every
    /// <c>models.X</c> reference is then a typo candidate against the
    /// declared vocabulary alone.
    /// </param>
    /// <param name="vocabulary">
    /// The catalog-declared vocabulary (<see cref="ICatalogVocabulary.ByIdentifier"/>
    /// + <see cref="ICatalogVocabulary.ByPinnedAs"/>). <see langword="null"/>
    /// is tolerated for hosts that don't ship a catalog (the pre-flight
    /// gate then can only emit typo hints against the function registry).
    /// </param>
    /// <param name="functions">
    /// The function registry — used for "is this bare name a real
    /// function?" checks and as a typo-suggestion candidate pool.
    /// </param>
    /// <param name="datasetSource">
    /// Optional dataset pre-flight source. Hosts that ship a dataset
    /// catalog pass an implementation so <c>FROM &lt;schema&gt;.&lt;table&gt;</c>
    /// table references against the manifest emit install requirements.
    /// <see langword="null"/> for hosts without a dataset surface.
    /// </param>
    public static PreFlightRequirements Walk(
        QueryExpression query,
        ModelCatalog? models,
        ICatalogVocabulary? vocabulary,
        FunctionRegistry functions,
        IPreFlightDatasetSource? datasetSource = null)
    {
        Builder builder = new(models, vocabulary, functions, datasetSource);
        builder.VisitQuery(query);
        return builder.Build();
    }

    /// <summary>
    /// Statement-level entrypoint. Dispatches to the right visit method
    /// for the top-level shape: pure queries delegate to
    /// <see cref="Walk(QueryExpression, ModelCatalog?, ICatalogVocabulary?, FunctionRegistry, IPreFlightDatasetSource?)"/>;
    /// <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> walk their own
    /// expression slots (WHERE, SET assignments, VALUES tuples,
    /// RETURNING projections). All other statement types (DDL, CALL,
    /// CREATE FUNCTION/MODEL bodies) return an empty result — they're
    /// either side-effect-only (DDL) or carry their own dispatch path
    /// that walks through this same gate later (CALL → PlanQuery on a
    /// synthetic SELECT).
    /// </summary>
    /// <remarks>
    /// Called from <c>TableCatalog.ExecuteStatementAsync</c> at the top
    /// of its dispatch switch so DML statements pre-flight without
    /// having to thread the walker through every executor.
    /// </remarks>
    public static PreFlightRequirements WalkStatement(
        Statement statement,
        ModelCatalog? models,
        ICatalogVocabulary? vocabulary,
        FunctionRegistry functions,
        IPreFlightDatasetSource? datasetSource = null)
    {
        Builder builder = new(models, vocabulary, functions, datasetSource);
        switch (statement)
        {
            case QueryStatement qs:
                builder.VisitQuery(qs.Query);
                break;
            case InsertStatement ins:
                builder.VisitInsert(ins);
                break;
            case UpdateStatement upd:
                builder.VisitUpdate(upd);
                break;
            case DeleteStatement del:
                builder.VisitDelete(del);
                break;
            // Everything else — DDL, CALL, CREATE [OR REPLACE] MODEL /
            // FUNCTION / PROCEDURE bodies, SET search_path, etc. — is
            // out of scope. UDF and model bodies stay opaque (memo
            // decision); CALL dispatches through PlanQuery which runs
            // pre-flight on the synthetic SELECT wrapper.
        }
        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly ModelCatalog? _models;
        private readonly ICatalogVocabulary? _vocabulary;
        private readonly FunctionRegistry _functions;
        private readonly IPreFlightDatasetSource? _datasetSource;

        private readonly List<PreFlightModelRequirement> _reqs = [];
        private readonly List<PreFlightDatasetRequirement> _datasetReqs = [];
        private readonly List<PreFlightSuggestion> _suggestions = [];
        // Dedupe by typed reference so the same call site appearing
        // twice in a query (`SELECT models.foo(a), models.foo(b)`)
        // emits one requirement, not two.
        private readonly HashSet<string> _seenRefs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenTypos = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenDatasetRefs = new(StringComparer.OrdinalIgnoreCase);

        public Builder(
            ModelCatalog? models,
            ICatalogVocabulary? vocabulary,
            FunctionRegistry functions,
            IPreFlightDatasetSource? datasetSource)
        {
            _models = models;
            _vocabulary = vocabulary;
            _functions = functions;
            _datasetSource = datasetSource;
        }

        public PreFlightRequirements Build() => new(_reqs, _datasetReqs, _suggestions);

        public void VisitQuery(QueryExpression query)
        {
            switch (query)
            {
                case SelectQueryExpression select:
                    VisitSelect(select.Statement);
                    break;
                case CompoundQueryExpression compound:
                    VisitQuery(compound.Left);
                    VisitQuery(compound.Right);
                    break;
                case InsertQueryExpression insertQuery:
                    VisitInsert(insertQuery.Insert);
                    break;
            }
        }

        private void VisitSelect(SelectStatement select)
        {
            if (select.CommonTableExpressions is not null)
            {
                foreach (CommonTableExpression cte in select.CommonTableExpressions)
                {
                    VisitQuery(cte.Body);
                    if (cte.RecursiveQuery is not null) { VisitSelect(cte.RecursiveQuery); }
                }
            }
            if (select.LetBindings is not null)
            {
                foreach (LetBinding binding in select.LetBindings)
                {
                    VisitExpression(binding.Expression);
                }
            }
            foreach (SelectColumn column in select.Columns)
            {
                VisitExpression(column.Expression);
            }
            if (select.From is not null) { VisitTableSource(select.From.Source); }
            if (select.Joins is not null)
            {
                foreach (JoinClause join in select.Joins)
                {
                    VisitTableSource(join.Source);
                    if (join.OnCondition is not null) { VisitExpression(join.OnCondition); }
                }
            }
            if (select.Where is not null) { VisitExpression(select.Where); }
            if (select.GroupBy is not null)
            {
                foreach (Expression g in select.GroupBy.Expressions) { VisitExpression(g); }
            }
            if (select.Having is not null) { VisitExpression(select.Having); }
            if (select.Qualify is not null) { VisitExpression(select.Qualify); }
            if (select.OrderBy is not null)
            {
                foreach (OrderByItem item in select.OrderBy.Items) { VisitExpression(item.Expression); }
            }
            if (select.Limit is not null) { VisitExpression(select.Limit); }
            if (select.Offset is not null) { VisitExpression(select.Offset); }
        }

        public void VisitInsert(InsertStatement insert)
        {
            switch (insert.Source)
            {
                case InsertQuerySource qs:
                    VisitQuery(qs.Query);
                    break;
                case InsertValuesSource vs:
                    foreach (IReadOnlyList<Expression> tuple in vs.Rows)
                    {
                        foreach (Expression e in tuple) { VisitExpression(e); }
                    }
                    break;
            }
            if (insert.Returning is not null)
            {
                foreach (SelectColumn col in insert.Returning) { VisitExpression(col.Expression); }
            }
        }

        // UPDATE has more expression surface than INSERT or DELETE: a FROM
        // clause plus JOINs (for `UPDATE … FROM other JOIN third ON …`),
        // a WHERE predicate, every SET assignment's right-hand side, and
        // optional RETURNING projections. The target table itself is a
        // bare identifier — no expression to walk there.
        public void VisitUpdate(UpdateStatement update)
        {
            if (update.From is not null) { VisitTableSource(update.From.Source); }
            if (update.Joins is not null)
            {
                foreach (JoinClause join in update.Joins)
                {
                    VisitTableSource(join.Source);
                    if (join.OnCondition is not null) { VisitExpression(join.OnCondition); }
                }
            }
            if (update.Where is not null) { VisitExpression(update.Where); }
            foreach (ColumnAssignment assign in update.Assignments)
            {
                VisitExpression(assign.Value);
            }
            if (update.Returning is not null)
            {
                foreach (SelectColumn col in update.Returning) { VisitExpression(col.Expression); }
            }
        }

        public void VisitDelete(DeleteStatement delete)
        {
            if (delete.Where is not null) { VisitExpression(delete.Where); }
            if (delete.Returning is not null)
            {
                foreach (SelectColumn col in delete.Returning) { VisitExpression(col.Expression); }
            }
        }

        private void VisitTableSource(TableSource source)
        {
            switch (source)
            {
                case SubquerySource sub:
                    VisitSelect(sub.Query);
                    break;
                case FunctionSource fn:
                    foreach (Expression arg in fn.Arguments) { VisitExpression(arg); }
                    break;
                case TableReference tr:
                    EvaluateTableReference(tr);
                    break;
            }
        }

        private void EvaluateTableReference(TableReference tr)
        {
            if (_datasetSource is null || tr.SchemaName is null) return;
            if (!_datasetSource.IsDatasetSchema(tr.SchemaName)) return;
            if (!_datasetSource.TryDescribe(tr.SchemaName, tr.Name, out PreFlightDatasetCandidate? d))
            {
                // The schema is dataset-mounted but the table name doesn't
                // resolve to a known variant. Could be a typo against the
                // manifest; treat as opaque for now — the planner will
                // surface the "table not found" error downstream. A future
                // pass could suggest the closest known variant id.
                return;
            }
            if (d.IsInstalled) return;
            string typed = $"{tr.SchemaName}.{tr.Name}";
            if (!_seenDatasetRefs.Add(typed)) return;
            _datasetReqs.Add(new PreFlightDatasetRequirement(
                TypedReference: typed,
                Identifier: tr.Name,
                VariantId: d.VariantId,
                EntryName: d.EntryName,
                DisplayName: d.DisplayName,
                Version: d.Version,
                ApproxArchiveBytes: d.ApproxArchiveBytes,
                LicenseIds: d.LicenseIds));
        }

        private void VisitExpression(Expression expression)
        {
            switch (expression)
            {
                case FunctionCallExpression fn:
                    EvaluateCall(fn);
                    foreach (Expression arg in fn.Arguments) { VisitExpression(arg); }
                    break;
                case BinaryExpression b:
                    VisitExpression(b.Left); VisitExpression(b.Right); break;
                case UnaryExpression u:
                    VisitExpression(u.Operand); break;
                case CastExpression c:
                    VisitExpression(c.Expression); break;
                case CaseExpression ce:
                    if (ce.Operand is not null) { VisitExpression(ce.Operand); }
                    foreach (WhenClause w in ce.WhenClauses)
                    {
                        VisitExpression(w.Condition);
                        VisitExpression(w.Result);
                    }
                    if (ce.ElseResult is not null) { VisitExpression(ce.ElseResult); }
                    break;
                case InExpression ie:
                    VisitExpression(ie.Expression);
                    foreach (Expression v in ie.Values) { VisitExpression(v); }
                    break;
                case BetweenExpression be:
                    VisitExpression(be.Expression);
                    VisitExpression(be.Low);
                    VisitExpression(be.High);
                    break;
                case IsNullExpression isn:
                    VisitExpression(isn.Expression); break;
                case LikeExpression lk:
                    VisitExpression(lk.Expression);
                    VisitExpression(lk.Pattern);
                    VisitExpression(lk.EscapeCharacter);
                    break;
                case AtTimeZoneExpression atz:
                    VisitExpression(atz.Expression);
                    VisitExpression(atz.TimeZone);
                    break;
                case StructLiteralExpression sl:
                    foreach (StructField f in sl.Fields) { VisitExpression(f.Value); }
                    break;
                case IndexAccessExpression ix:
                    VisitExpression(ix.Source);
                    foreach (Expression i in ix.Indices) { VisitExpression(i); }
                    break;
                case SubqueryExpression subq:
                    VisitSelect(subq.Query);
                    break;
                case InSubqueryExpression isq:
                    VisitExpression(isq.Expression);
                    VisitSelect(isq.Query);
                    break;
            }
        }

        private void EvaluateCall(FunctionCallExpression fn)
        {
            // models.X references — the only schema pre-flight cares about
            // for residency. Everything else (system.*, datum_catalog.*,
            // udf.*, public.*) is opaque to pre-flight.
            if (string.Equals(fn.SchemaName, ModelSchema, StringComparison.OrdinalIgnoreCase))
            {
                EvaluateModelReference(fn);
                return;
            }

            // Non-models references: a name miss against the function
            // registry plus the model surface is a likely typo. Skip
            // namespaced calls we don't own (let the planner / runtime
            // handle resolution errors for udf.x, system.x, etc.) —
            // suggestions there would risk noise without value.
            if (fn.SchemaName is not null) { return; }

            // Resolved → no work. Mirror the order of PlanTimeFunctionGate:
            // scalar > aggregate > window > tvf. A bare name that resolves
            // anywhere is not a typo.
            if (_functions.TryGetScalar(fn.CallName) is not null) { return; }
            if (_functions.TryGetAggregate(fn.CallName) is not null) { return; }
            if (_functions.TryGetWindow(fn.CallName) is not null) { return; }
            if (_functions.TryGetTableValued(fn.CallName) is not null) { return; }

            // Unknown bare name: suggest the closest known function name.
            // Empty suggestion pool means we silently let PlanTimeFunctionGate
            // surface the standard "Unknown function" error downstream —
            // pre-flight only intervenes when it has a concrete hint.
            if (TrySuggestForBareName(fn.FunctionName, out string? hint))
            {
                AddSuggestion(fn.FunctionName, hint);
            }
        }

        private void EvaluateModelReference(FunctionCallExpression fn)
        {
            string typed = fn.FunctionName;
            if (!_seenRefs.Add($"models.{typed}")) { return; }

            // Already-callable in the live catalog (active install or
            // pinned install registered the suffixed name) → nothing to do.
            // This is the dominant case for a query against a hot catalog.
            if (_models?.TryGetEntry(typed) is not null) { return; }

            // Pinned reference (`foo@20260529`)? Split — the suffix is the
            // catalog-version pin, the prefix is the bare identifier.
            int at = typed.IndexOf('@');
            if (at > 0)
            {
                string bareName = typed[..at];
                EmitPinned(typed, bareName);
                return;
            }

            EmitBare(typed);
        }

        private void EmitBare(string identifier)
        {
            CatalogVocabularyEntry? entry = _vocabulary is not null
                && _vocabulary.ByIdentifier.TryGetValue(identifier, out CatalogVocabularyEntry? found)
                ? found
                : null;
            if (entry is null)
            {
                // Not in catalog and not in live registry — typo or a
                // catalog-pruned id. Offer a closest-match suggestion
                // against catalog identifiers + live registry names.
                if (TrySuggestForModelName(identifier, out string? hint))
                {
                    AddSuggestion($"models.{identifier}", $"models.{hint}");
                }
                return;
            }

            CatalogVocabularyVersion recommended = entry.Versions[0];
            CatalogModel owner = entry.Owner;
            _reqs.Add(new PreFlightModelRequirement(
                TypedReference: $"models.{identifier}",
                Identifier: identifier,
                CatalogEntryId: entry.CatalogEntryId,
                Version: recommended.VersionString,
                VersionPinned: false,
                Reason: PreFlightReason.ModelNotInstalled,
                ApproxSizeMb: owner.ApproxSizeMb,
                SiblingIdentifiers: SiblingsOf(entry, recommended, exclude: identifier),
                EntryDeprecated: owner.Deprecated,
                SupersededBy: owner.SupersededBy,
                VersionDeprecated: recommended.Version.Deprecated,
                VersionDeprecationReason: recommended.Version.DeprecationReason,
                LicenseIds: owner.LicenseIds));
        }

        private void EmitPinned(string typed, string bareName)
        {
            // The fastest path: the materialised pinnedAs is a globally
            // unique key. Look the full `<bare>@<digits>` form up directly.
            if (_vocabulary is not null
                && _vocabulary.ByPinnedAs.TryGetValue(typed, out CatalogPinnedReference? pin))
            {
                CatalogVocabularyVersion vv = pin.Version;
                CatalogVocabularyEntry pinEntry = pin.Entry;
                _reqs.Add(new PreFlightModelRequirement(
                    TypedReference: $"models.{typed}",
                    Identifier: bareName,
                    CatalogEntryId: pinEntry.CatalogEntryId,
                    Version: vv.VersionString,
                    VersionPinned: true,
                    Reason: PreFlightReason.PinnedVersionNotInstalled,
                    ApproxSizeMb: pinEntry.Owner.ApproxSizeMb,
                    SiblingIdentifiers: SiblingsOf(pinEntry, vv, exclude: bareName),
                    EntryDeprecated: pinEntry.Owner.Deprecated,
                    SupersededBy: pinEntry.Owner.SupersededBy,
                    VersionDeprecated: vv.Version.Deprecated,
                    VersionDeprecationReason: vv.Version.DeprecationReason,
                    LicenseIds: pinEntry.Owner.LicenseIds));
                return;
            }

            // No catalog version maps to the typed pin. First try a
            // numeric-suffix typo against every known pinnedAs — catches
            // `foo@2026529` (missing digit) → suggest `foo@20260529`. A
            // close hit replaces the would-be PinnedVersionUnknown with
            // a Suggestion since the typo IS the issue; the user fixes
            // it and resubmits, then gets a real residency check.
            if (_vocabulary is not null
                && TrySuggestForPinnedReference(typed, out string? pinHint))
            {
                AddSuggestion($"models.{typed}", $"models.{pinHint}");
                return;
            }

            // If the bare name is in the catalog, surface PinnedVersionUnknown
            // so the UI can list the available versions; otherwise treat
            // the whole thing as a typo.
            if (_vocabulary is not null
                && _vocabulary.ByIdentifier.TryGetValue(bareName, out CatalogVocabularyEntry? bareEntry))
            {
                CatalogVocabularyVersion recommended = bareEntry.Versions[0];
                _reqs.Add(new PreFlightModelRequirement(
                    TypedReference: $"models.{typed}",
                    Identifier: bareName,
                    CatalogEntryId: bareEntry.CatalogEntryId,
                    Version: null,
                    VersionPinned: true,
                    Reason: PreFlightReason.PinnedVersionUnknown,
                    ApproxSizeMb: bareEntry.Owner.ApproxSizeMb,
                    SiblingIdentifiers: SiblingsOf(bareEntry, recommended, exclude: bareName),
                    EntryDeprecated: bareEntry.Owner.Deprecated,
                    SupersededBy: bareEntry.Owner.SupersededBy,
                    VersionDeprecated: false,
                    VersionDeprecationReason: null,
                    LicenseIds: bareEntry.Owner.LicenseIds));
                return;
            }

            if (TrySuggestForModelName(bareName, out string? hint))
            {
                AddSuggestion($"models.{typed}", $"models.{hint}");
            }
        }

        private static IReadOnlyList<string> SiblingsOf(
            CatalogVocabularyEntry entry,
            CatalogVocabularyVersion version,
            string exclude)
        {
            // Walk the underlying CatalogVersion.Models so the order matches
            // catalog.json (identifiers are authored together — "primary
            // first, ... aliases").
            IReadOnlyList<CatalogVersionModel>? declared = version.Version.Models;
            if (declared is null || declared.Count <= 1) { return []; }
            List<string> result = [];
            foreach (CatalogVersionModel vm in declared)
            {
                if (string.Equals(vm.Identifier, exclude, StringComparison.OrdinalIgnoreCase)) { continue; }
                result.Add(vm.Identifier);
            }
            return result;
        }

        private void AddSuggestion(string typed, string suggestion)
        {
            if (_seenTypos.Add(typed))
            {
                _suggestions.Add(new PreFlightSuggestion(typed, suggestion));
            }
        }

        private bool TrySuggestForBareName(string name, [NotNullWhen(true)] out string? suggestion)
        {
            // Pool: every scalar / aggregate / window / TVF name the
            // registry knows. Built lazily — typo correction is a rare
            // case so paying the enumeration cost is fine.
            string? best = null;
            int bestDistance = int.MaxValue;
            int budget = TypoBudget(name.Length);

            foreach (string candidate in EnumerateFunctionNames())
            {
                int d = LevenshteinDistance(name, candidate, budget);
                if (d <= budget && d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }
            }
            suggestion = best;
            return best is not null;
        }

        private IEnumerable<string> EnumerateFunctionNames()
        {
            foreach (string s in _functions.ScalarFunctionNames) { yield return s; }
            foreach (string s in _functions.AggregateFunctionNames) { yield return s; }
            foreach (string s in _functions.WindowFunctionNames) { yield return s; }
            foreach (string s in _functions.TableValuedFunctionNames) { yield return s; }
        }

        // Numeric-suffix typo against ByPinnedAs. Matches the typed
        // `<bare>@<digits>` form against every materialised pinnedAs the
        // catalog declares. Same Levenshtein budget as the other
        // suggestion paths.
        private bool TrySuggestForPinnedReference(string typed, [NotNullWhen(true)] out string? suggestion)
        {
            string? best = null;
            int bestDistance = int.MaxValue;
            int budget = TypoBudget(typed.Length);

            if (_vocabulary is not null)
            {
                foreach (string candidate in _vocabulary.ByPinnedAs.Keys)
                {
                    int d = LevenshteinDistance(typed, candidate, budget);
                    if (d <= budget && d < bestDistance)
                    {
                        bestDistance = d;
                        best = candidate;
                    }
                }
            }
            suggestion = best;
            return best is not null;
        }

        private bool TrySuggestForModelName(string name, [NotNullWhen(true)] out string? suggestion)
        {
            string? best = null;
            int bestDistance = int.MaxValue;
            int budget = TypoBudget(name.Length);

            if (_vocabulary is not null)
            {
                foreach (string candidate in _vocabulary.ByIdentifier.Keys)
                {
                    int d = LevenshteinDistance(name, candidate, budget);
                    if (d <= budget && d < bestDistance)
                    {
                        bestDistance = d;
                        best = candidate;
                    }
                }
            }
            suggestion = best;
            return best is not null;
        }

        // Heuristic: 1 edit for short names, 2 for longer. Keeps fallout
        // low — accidental matches against unrelated short names (e.g.
        // `abs` ↔ `acs`) are rare and the user can resubmit if the
        // suggestion isn't right.
        private static int TypoBudget(int length)
            => length < 5 ? 1 : 2;

        // Classic Levenshtein with an early-exit row maximum so we don't
        // pay the full O(n*m) when candidates are obviously out of range.
        // Implementation is the two-row tabulation form; allocating one
        // int[] per candidate keeps the suggestion pass O(N * |name|)
        // without exotic state.
        private static int LevenshteinDistance(string a, string b, int max)
        {
            if (a.Length == 0) { return b.Length; }
            if (b.Length == 0) { return a.Length; }
            // Cheap pre-check: length delta alone may exceed the budget.
            if (Math.Abs(a.Length - b.Length) > max) { return max + 1; }

            int aLen = a.Length;
            int bLen = b.Length;
            Span<int> prev = stackalloc int[bLen + 1];
            Span<int> curr = stackalloc int[bLen + 1];
            for (int j = 0; j <= bLen; j++) { prev[j] = j; }

            for (int i = 1; i <= aLen; i++)
            {
                curr[0] = i;
                int rowMin = curr[0];
                for (int j = 1; j <= bLen; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = curr[j - 1] + 1;
                    int sub = prev[j - 1] + cost;
                    int min = del < ins ? del : ins;
                    if (sub < min) { min = sub; }
                    curr[j] = min;
                    if (min < rowMin) { rowMin = min; }
                }
                if (rowMin > max) { return max + 1; }
                Span<int> tmp = prev; prev = curr; curr = tmp;
            }
            return prev[bLen];
        }
    }
}
