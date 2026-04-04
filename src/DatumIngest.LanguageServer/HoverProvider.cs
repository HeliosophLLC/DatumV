namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;
using Superpower.Model;

/// <summary>
/// Provides hover information (type details, function signatures, keyword documentation)
/// for tokens at a given cursor position in SQL text.
/// </summary>
public sealed class HoverProvider
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>
    /// Creates a hover provider backed by the given manifest.
    /// </summary>
    public HoverProvider(LanguageServerManifest manifest)
    {
        _manifest = manifest;
    }

    /// <summary>
    /// Returns hover information for the token at the given cursor offset, or null
    /// if there is nothing meaningful to display.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A hover result with Markdown content, or null.</returns>
    public HoverResult? GetHover(string sql, int cursorOffset)
    {
        if (string.IsNullOrEmpty(sql) || cursorOffset < 0 || cursorOffset >= sql.Length)
        {
            return null;
        }

        // Tokenize the full SQL to find which token the cursor is on.
        List<TokenHit> tokens = TokenizeWithSpans(sql);
        TokenHit? hit = FindTokenAtOffset(tokens, cursorOffset);

        if (hit is null)
        {
            return null;
        }

        // Build an alias map of TVF sources visible in the SQL. Per-hover
        // parsing is cheap (error-recovering parser, no I/O) and avoids
        // staleness — typing a new FROM clause is reflected immediately.
        // No per-cursor scoping yet: an alias defined anywhere in the
        // statement is visible everywhere. Scope-aware lookup is a follow-up.
        Dictionary<string, FunctionSignature> tvfAliases = BuildTvfAliasMap(sql);
        // CTE projection schemas — same global-scope simplification as the
        // TVF alias map. A bare CTE name or a FROM alias bound to a CTE
        // resolves to the CTE's derived output columns.
        CteSchemaResult cteSchemas = CteSchemaResolver.Resolve(sql, _manifest);

        string? docKey = null;

        string? markdown = hit.Kind switch
        {
            SqlToken.Identifier => ResolveIdentifierHover(hit.Text, tokens, hit, tvfAliases, cteSchemas, out docKey),
            SqlToken.TypeKeyword => TypeDescriptions.TryGetValue(hit.Text, out string? typeDesc) ? typeDesc : null,
            SqlToken.Arrow => "**`->`** Lambda arrow — separates parameter(s) from the body expression.\n\n" +
                "Usage: `x -> expr` or `(a, b) -> expr` inside higher-order functions " +
                "such as `array_transform` and `array_filter`.",
            // Splice-aware hover (resolving identifiers inside ${…} as columns/
            // functions) needs precise multi-line offset accounting that the
            // current approximate hover positioning doesn't support cleanly.
            // For now, surface a single descriptive hover on the whole template;
            // an identifier outside the template still gets per-token hover.
            SqlToken.TemplateString => "**Template string** — backtick-delimited string with `${expression}` interpolation.\n\n" +
                "Lowers to a `concat(literal_chunks, splice_exprs…)` call at parse time. " +
                "Splices may contain any scalar expression, including model invocations and other UDFs.",
            _ when IsKeywordToken(hit.Kind) => GetKeywordHover(hit.Kind, hit.Text, out docKey),
            _ => null,
        };

        if (markdown is null)
        {
            return null;
        }

        // Append documentation excerpt and "See more" link if a matching section exists.
        markdown = AppendDocLink(markdown, docKey);

        return new HoverResult
        {
            Contents = markdown,
            StartLine = hit.Line,
            StartColumn = hit.Column,
            EndLine = hit.Line,
            EndColumn = hit.Column + hit.Text.Length,
            DocumentationUri = docKey,
        };
    }

    /// <summary>
    /// Resolves hover for an identifier by checking if it's a function, table, or column name.
    /// <paramref name="tvfAliases"/> maps each TVF source alias visible in the
    /// SQL to its manifest signature so column hovers originating from a
    /// table-valued function source (e.g. <c>FROM video_unnest_frames(...) vid</c>)
    /// can resolve to the TVF's output columns — the persistent-table column
    /// lookup doesn't know about these. <paramref name="cteSchemas"/> serves
    /// the same purpose for <c>WITH</c>-clause CTEs.
    /// </summary>
    private string? ResolveIdentifierHover(
        string name,
        List<TokenHit> tokens,
        TokenHit currentToken,
        Dictionary<string, FunctionSignature> tvfAliases,
        CteSchemaResult cteSchemas,
        out string? docKey)
    {
        docKey = null;

        // If followed by '(' it's a function call.
        int currentIndex = tokens.IndexOf(currentToken);
        if (currentIndex >= 0 && currentIndex + 1 < tokens.Count &&
            tokens[currentIndex + 1].Kind == SqlToken.LeftParen)
        {
            // Detect a schema qualifier (`schema.fn(`) so we can route the
            // hover to UDFs / procedures registered in that schema before
            // falling back to built-ins.
            string? callQualifier = null;
            if (currentIndex >= 2
                && tokens[currentIndex - 1].Kind == SqlToken.Dot
                && (tokens[currentIndex - 2].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 2].Kind)))
            {
                callQualifier = tokens[currentIndex - 2].Text;
            }

            // UDFs first — qualified exact match, then search_path walk.
            string? udfHover = GetUdfHover(callQualifier, name);
            if (udfHover is not null) return udfHover;

            // Procedures — surface a "use CALL" hint so users see the
            // semantic difference even from hover.
            string? procedureHover = GetProcedureHover(callQualifier, name);
            if (procedureHover is not null) return procedureHover;

            // Built-ins live in many schemas (system, inference, tokenizer,
            // templates, …) — and TVFs do too. When the user qualified the
            // call, search that specific schema; otherwise walk search_path.
            // GetFunctionHover handles both scalar and table-valued entries
            // off the manifest's Functions list.
            string? functionHover = GetFunctionHover(callQualifier, name);
            if (functionHover is not null)
            {
                docKey = DocumentationIndex.Instance.FindFunctionSection(name);
                return functionHover;
            }
            return null;
        }

        // If preceded by a dot, it could be a qualified column (table.column)
        // or a schema-qualified table reference (schema.table). Resolve
        // against the live manifest first so any schema that holds a
        // matching table — public, system, information_schema,
        // datum_catalog, or a user-created schema — produces hover; fall
        // back to the column-on-aliased-table path when no table matches.
        if (currentIndex >= 2 &&
            tokens[currentIndex - 1].Kind == SqlToken.Dot &&
            (tokens[currentIndex - 2].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 2].Kind)))
        {
            string qualifier = tokens[currentIndex - 2].Text;

            string qualifiedTable = $"{qualifier}.{name}";
            string? qualifiedTableHover = GetTableHover(qualifiedTable);
            if (qualifiedTableHover is not null)
            {
                // Enrich with a curated one-line description when we have
                // one for the schema.table combination. Falls back to
                // just the schema-level note when the table is unfamiliar.
                if (VirtualTableDescriptions.TryGetValue(qualifier, out Dictionary<string, string>? tableDescriptions)
                    && tableDescriptions.TryGetValue(name, out string? description))
                {
                    return qualifiedTableHover + "\n\n" + description;
                }
                return qualifiedTableHover;
            }

            // `alias.column` where `alias` is a TVF source — look up the
            // column in the TVF's output schema before falling back to the
            // persistent-table column path.
            string? tvfQualifiedHover = GetTvfColumnHover(qualifier, name, tvfAliases);
            if (tvfQualifiedHover is not null) return tvfQualifiedHover;

            // CTE alias or bare CTE name (e.g. `frames.frame_index` or
            // `f1.frame_index` where `f1` is `FROM frames f1`).
            string? cteQualifiedHover = GetCteColumnHover(qualifier, name, cteSchemas);
            if (cteQualifiedHover is not null) return cteQualifiedHover;

            return GetQualifiedColumnHover(qualifier, name);
        }

        // Check if the identifier is a known virtual schema name itself (e.g. hovering over "information_schema").
        if (VirtualSchemaDescriptions.ContainsKey(name))
        {
            return $"**Schema: {name}**\n\n{VirtualSchemaDescriptions[name]}";
        }

        // Check if the identifier is a data type name.
        if (TypeDescriptions.TryGetValue(name, out string? typeDescription))
        {
            return typeDescription;
        }

        // Try as table name first, then as unqualified column.
        string? tableHover = GetTableHover(name);
        if (tableHover is not null)
        {
            return tableHover;
        }

        // Unqualified column referenced from a TVF source. Walk every TVF
        // alias in the SQL — first match wins. Same-name collisions across
        // different TVFs in one statement are rare and best resolved by
        // the user qualifying the reference.
        string? tvfUnqualifiedHover = GetTvfColumnHoverUnqualified(name, tvfAliases);
        if (tvfUnqualifiedHover is not null) return tvfUnqualifiedHover;

        // Unqualified CTE column — same first-match-wins policy. Walks
        // every CTE projection in the statement.
        string? cteUnqualifiedHover = GetCteColumnHoverUnqualified(name, cteSchemas);
        if (cteUnqualifiedHover is not null) return cteUnqualifiedHover;

        return GetColumnHover(name);
    }

    /// <summary>
    /// Renders a CTE-column hover for <c>qualifier.columnName</c>. Resolves
    /// the qualifier as a direct CTE name first, then as a FROM alias bound
    /// to a CTE; returns <see langword="null"/> when neither matches the
    /// requested column.
    /// </summary>
    private static string? GetCteColumnHover(string qualifier, string columnName, CteSchemaResult cteSchemas)
    {
        if (!TryResolveCteSchema(qualifier, cteSchemas, out string? cteName, out IReadOnlyList<TableColumnEntry>? cols))
        {
            return null;
        }
        foreach (TableColumnEntry column in cols)
        {
            if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return FormatCteColumnHover(qualifier, cteName, column);
            }
        }
        return null;
    }

    /// <summary>
    /// Walks every resolved CTE projection looking for an output column
    /// matching <paramref name="columnName"/>. First match wins — mirrors
    /// the unqualified TVF lookup; collisions are rare in practice and
    /// best disambiguated by qualifying the reference.
    /// </summary>
    private static string? GetCteColumnHoverUnqualified(string columnName, CteSchemaResult cteSchemas)
    {
        foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> entry in cteSchemas.Schemas)
        {
            foreach (TableColumnEntry column in entry.Value)
            {
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return FormatCteColumnHover(entry.Key, entry.Key, column);
                }
            }
        }
        return null;
    }

    private static bool TryResolveCteSchema(
        string qualifier,
        CteSchemaResult cteSchemas,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? cteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<TableColumnEntry>? columns)
    {
        if (cteSchemas.Schemas.TryGetValue(qualifier, out IReadOnlyList<TableColumnEntry>? direct))
        {
            cteName = qualifier;
            columns = direct;
            return true;
        }
        if (cteSchemas.FromAliasToCteName.TryGetValue(qualifier, out string? mapped)
            && cteSchemas.Schemas.TryGetValue(mapped, out IReadOnlyList<TableColumnEntry>? aliasCols))
        {
            cteName = mapped;
            columns = aliasCols;
            return true;
        }
        cteName = null;
        columns = null;
        return false;
    }

    private static string FormatCteColumnHover(string qualifier, string cteName, TableColumnEntry column)
    {
        string nullable = column.Nullable ? " *(nullable)*" : "";
        string source = string.Equals(qualifier, cteName, StringComparison.OrdinalIgnoreCase)
            ? cteName
            : $"{cteName} (via `{qualifier}`)";
        return $"**{qualifier}.{column.Name}**: {column.Kind}{nullable}\n\nSource: CTE `{source}`";
    }

    /// <summary>
    /// Renders a column hover from a TVF alias map entry. Returns
    /// <see langword="null"/> when the alias isn't a TVF source or the
    /// column name doesn't match any of its declared output columns.
    /// </summary>
    private static string? GetTvfColumnHover(
        string alias, string columnName, Dictionary<string, FunctionSignature> tvfAliases)
    {
        if (!tvfAliases.TryGetValue(alias, out FunctionSignature? signature)) return null;
        if (signature.OutputColumns is null) return null;
        foreach (TableColumnEntry column in signature.OutputColumns)
        {
            if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return FormatTvfColumnHover(alias, signature, column);
            }
        }
        return null;
    }

    /// <summary>
    /// Unqualified column lookup across every TVF alias in the SQL. First
    /// match wins; deterministic by enumeration order over the dictionary.
    /// </summary>
    private static string? GetTvfColumnHoverUnqualified(
        string columnName, Dictionary<string, FunctionSignature> tvfAliases)
    {
        foreach (KeyValuePair<string, FunctionSignature> entry in tvfAliases)
        {
            if (entry.Value.OutputColumns is null) continue;
            foreach (TableColumnEntry column in entry.Value.OutputColumns)
            {
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return FormatTvfColumnHover(entry.Key, entry.Value, column);
                }
            }
        }
        return null;
    }

    private static string FormatTvfColumnHover(string alias, FunctionSignature signature, TableColumnEntry column)
    {
        string nullable = column.Nullable ? " *(nullable)*" : "";
        string qualifiedFn = string.IsNullOrEmpty(signature.SchemaName)
            || string.Equals(signature.SchemaName, "system", StringComparison.OrdinalIgnoreCase)
            ? signature.Name
            : $"{signature.SchemaName}.{signature.Name}";
        return $"**{alias}.{column.Name}**: {column.Kind}{nullable}\n\nSource: `{qualifiedFn}(...)` *(table-valued)*";
    }

    /// <summary>
    /// Parses the SQL with the error-recovering parser and walks the
    /// resulting tree to collect every <see cref="FunctionSource"/> mapped
    /// to its manifest <see cref="FunctionSignature"/>. Key is the source's
    /// alias when present, otherwise the function call name — matching the
    /// resolution rule the engine uses for FROM-clause aliases.
    /// </summary>
    /// <remarks>
    /// Per-hover parse cost is negligible against keystroke cadence; no
    /// caching layer yet. If hover latency ever becomes an issue, hash the
    /// SQL and memoise on the last seen hash.
    /// </remarks>
    private Dictionary<string, FunctionSignature> BuildTvfAliasMap(string sql)
    {
        Dictionary<string, FunctionSignature> result = new(StringComparer.OrdinalIgnoreCase);
        ParseResult parseResult;
        try
        {
            parseResult = SqlParser.TryParseRecovering(sql);
        }
        catch
        {
            return result;
        }

        if (parseResult.Query is null) return result;

        foreach (FunctionSource source in EnumerateFunctionSources(parseResult.Query))
        {
            FunctionSignature? signature = LookupTvfSignature(source);
            if (signature is null) continue;
            string aliasKey = source.Alias ?? source.FunctionName;
            // First-wins on alias clashes — preserves the natural document
            // order so the leftmost source surfaces under shared aliases.
            if (!result.ContainsKey(aliasKey)) result[aliasKey] = signature;
        }

        return result;
    }

    private FunctionSignature? LookupTvfSignature(FunctionSource source)
    {
        foreach (FunctionSignature signature in _manifest.Functions)
        {
            if (!signature.IsTableValued) continue;
            if (!string.Equals(signature.Name, source.FunctionName, StringComparison.OrdinalIgnoreCase)) continue;
            if (source.SchemaName is not null
                && !string.Equals(signature.SchemaName, source.SchemaName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return signature;
        }
        return null;
    }

    /// <summary>
    /// Yields every <see cref="FunctionSource"/> reachable from
    /// <paramref name="query"/> — top-level FROM/JOIN, CTE bodies (anchor
    /// and recursive sides), subqueries embedded as FROM sources, and
    /// nested subquery expressions in the SELECT list. Subquery-as-table
    /// sources have their inner statement walked too, so a TVF inside an
    /// inline subquery still surfaces.
    /// </summary>
    private static IEnumerable<FunctionSource> EnumerateFunctionSources(QueryExpression query)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                foreach (FunctionSource fs in EnumerateInStatement(select.Statement))
                    yield return fs;
                break;
            case CompoundQueryExpression compound:
                foreach (FunctionSource fs in EnumerateFunctionSources(compound.Left))
                    yield return fs;
                foreach (FunctionSource fs in EnumerateFunctionSources(compound.Right))
                    yield return fs;
                break;
        }
    }

    private static IEnumerable<FunctionSource> EnumerateInStatement(SelectStatement statement)
    {
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                foreach (FunctionSource fs in EnumerateFunctionSources(cte.Body))
                    yield return fs;
                if (cte.RecursiveQuery is not null)
                {
                    foreach (FunctionSource fs in EnumerateInStatement(cte.RecursiveQuery))
                        yield return fs;
                }
            }
        }

        if (statement.From is not null)
        {
            foreach (FunctionSource fs in EnumerateInTableSource(statement.From.Source))
                yield return fs;
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                foreach (FunctionSource fs in EnumerateInTableSource(join.Source))
                    yield return fs;
            }
        }
    }

    private static IEnumerable<FunctionSource> EnumerateInTableSource(TableSource source)
    {
        switch (source)
        {
            case FunctionSource fs:
                yield return fs;
                break;
            case SubquerySource sub:
                foreach (FunctionSource inner in EnumerateInStatement(sub.Query))
                    yield return inner;
                break;
        }
    }

    /// <summary>
    /// Resolves UDF hover via explicit schema (exact match) or, when
    /// <paramref name="explicitSchema"/> is <see langword="null"/>, walks
    /// the manifest's <see cref="LanguageServerManifest.SearchPath"/>.
    /// </summary>
    private string? GetUdfHover(string? explicitSchema, string name)
    {
        if (_manifest.Udfs is null) return null;
        UdfEntry? entry = ResolveUdfEntry(explicitSchema, name);
        if (entry is null) return null;

        string parameters = entry.Parameters is null
            ? ""
            : string.Join(", ", entry.Parameters.Select(p =>
            {
                string optional = p.IsOptional ? "?" : "";
                return $"{p.Name}: {p.Kind}{optional}";
            }));
        string returnInfo = entry.ReturnType is not null ? $" → {entry.ReturnType}" : "";
        string signature = $"**{entry.SchemaName}.{entry.Name}**({parameters}){returnInfo}";

        List<string> tags = new(2);
        if (entry.BodyKind is not null) tags.Add(entry.BodyKind);
        if (entry.IsPure) tags.Add("pure");
        string detail = tags.Count > 0 ? $"\n\n*{string.Join(" · ", tags)}*" : "";

        return signature + detail;
    }

    /// <summary>
    /// Resolves procedure hover, returning a "procedure · use CALL" hint
    /// so the user sees the semantic difference at edit time.
    /// </summary>
    private string? GetProcedureHover(string? explicitSchema, string name)
    {
        if (_manifest.Procedures is null) return null;
        ProcedureEntry? entry = ResolveProcedureEntry(explicitSchema, name);
        if (entry is null) return null;

        string parameters = entry.Parameters is null
            ? ""
            : string.Join(", ", entry.Parameters.Select(p =>
            {
                string optional = p.IsOptional ? "?" : "";
                return $"{p.Name}: {p.Kind}{optional}";
            }));
        return $"**{entry.SchemaName}.{entry.Name}**({parameters})\n\n" +
            $"*procedure · invoke via* `CALL {entry.SchemaName}.{entry.Name}(...)`";
    }

    private UdfEntry? ResolveUdfEntry(string? explicitSchema, string name)
    {
        if (_manifest.Udfs is null) return null;
        if (explicitSchema is not null)
        {
            foreach (UdfEntry e in _manifest.Udfs)
            {
                if (string.Equals(e.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
            return null;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (UdfEntry e in _manifest.Udfs)
            {
                if (string.Equals(e.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }
        return null;
    }

    private ProcedureEntry? ResolveProcedureEntry(string? explicitSchema, string name)
    {
        if (_manifest.Procedures is null) return null;
        if (explicitSchema is not null)
        {
            foreach (ProcedureEntry e in _manifest.Procedures)
            {
                if (string.Equals(e.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
            return null;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (ProcedureEntry e in _manifest.Procedures)
            {
                if (string.Equals(e.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves hover for a function call by name, optionally restricted to
    /// a specific schema. When <paramref name="explicitSchema"/> is
    /// <see langword="null"/>, walks the manifest's <see cref="LanguageServerManifest.SearchPath"/>
    /// the same way <see cref="GetUdfHover"/> and <see cref="GetProcedureHover"/>
    /// do; when non-null, restricts the lookup to that schema so a qualified
    /// call like <c>inference.devices()</c> doesn't accidentally match an
    /// unrelated <c>devices</c> elsewhere.
    /// </summary>
    private string? GetFunctionHover(string? explicitSchema, string name)
    {
        FunctionSignature? function = ResolveFunctionEntry(explicitSchema, name);
        if (function is null)
        {
            return null;
        }

        string parameters = string.Join(", ", function.Parameters.Select(parameter =>
        {
            string optional = parameter.IsOptional ? "?" : "";
            return $"{parameter.Name}: {parameter.Kind}{optional}";
        }));

        string returnInfo = !string.IsNullOrEmpty(function.ReturnType) ? $" → {function.ReturnType}" : "";
        string qualifiedName = string.IsNullOrEmpty(function.SchemaName)
            || string.Equals(function.SchemaName, "system", StringComparison.OrdinalIgnoreCase)
            ? function.Name
            : $"{function.SchemaName}.{function.Name}";
        string signature = $"**{qualifiedName}**({parameters}){returnInfo}";

        if (function.IsTableValued)
        {
            signature = $"*(table-valued)* {signature}";
        }

        string categoryLine = $"*Category: {function.Category}*";

        return function.Description is not null
            ? $"{signature}\n\n{categoryLine}\n\n{function.Description}"
            : $"{signature}\n\n{categoryLine}";
    }

    private FunctionSignature? ResolveFunctionEntry(string? explicitSchema, string name)
    {
        if (explicitSchema is not null)
        {
            foreach (FunctionSignature f in _manifest.Functions)
            {
                if (string.Equals(f.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            return null;
        }
        // Unqualified: prefer a match on a search-path schema so the
        // user sees the same function the engine will resolve.
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (FunctionSignature f in _manifest.Functions)
            {
                if (string.Equals(f.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }
        // Fall back to any schema — preserves hover for legacy callers
        // that don't set SchemaName (e.g. offline JSON manifests where
        // every entry defaults to "system").
        foreach (FunctionSignature f in _manifest.Functions)
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    private string? GetTableHover(string name)
    {
        // Manifest stores tables as their fully-qualified name
        // (`public.users`, `system.functions`, etc.). Accept either form:
        // the user's hover might be over `public.users` or just `users`.
        // For the unqualified form we walk the search_path so a `users`
        // hover prefers the first matching schema rather than picking
        // arbitrarily across schemas.
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        if (table is null && !name.Contains('.'))
        {
            foreach (string schema in _manifest.SearchPath)
            {
                string qualified = $"{schema}.{name}";
                table = _manifest.Tables.FirstOrDefault(
                    entry => string.Equals(entry.Name, qualified, StringComparison.OrdinalIgnoreCase));
                if (table is not null) break;
            }
        }

        if (table is null)
        {
            return null;
        }

        string header = $"**Table: {table.Name}** ({table.Columns.Count} columns)\n\n";
        string columns = string.Join("\n", table.Columns.Select(column =>
        {
            string nullable = column.Nullable ? " *(nullable)*" : "";
            return $"- `{column.Name}`: {column.Kind}{nullable}";
        }));

        return header + columns;
    }

    private string? GetColumnHover(string name)
    {
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            TableColumnEntry? column = table.Columns.FirstOrDefault(
                entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

            if (column is not null)
            {
                string nullable = column.Nullable ? " *(nullable)*" : "";
                return $"**{column.Name}**: {column.Kind}{nullable}\n\nSource: {table.Name}";
            }
        }

        return null;
    }

    private string? GetQualifiedColumnHover(string tableQualifier, string columnName)
    {
        // Same regression as GetTableHover: manifest holds tables as
        // "schema.name" but the qualifier the user typed may be just the
        // unqualified table portion. Try exact first, then walk
        // search_path with `{schema}.{tableQualifier}`.
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, tableQualifier, StringComparison.OrdinalIgnoreCase));

        if (table is null && !tableQualifier.Contains('.'))
        {
            foreach (string schema in _manifest.SearchPath)
            {
                string qualified = $"{schema}.{tableQualifier}";
                table = _manifest.Tables.FirstOrDefault(
                    entry => string.Equals(entry.Name, qualified, StringComparison.OrdinalIgnoreCase));
                if (table is not null) break;
            }
        }

        if (table is null)
        {
            return null;
        }

        TableColumnEntry? column = table.Columns.FirstOrDefault(
            entry => string.Equals(entry.Name, columnName, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            return null;
        }

        string nullable = column.Nullable ? " *(nullable)*" : "";
        return $"**{tableQualifier}.{column.Name}**: {column.Kind}{nullable}";
    }

    private static string? GetKeywordHover(SqlToken kind, string text, out string? docKey)
    {
        docKey = null;
        string? baseHover = GetKeywordDescription(kind);
        if (baseHover is null)
        {
            return null;
        }

        // Try to find a matching documentation section for this keyword.
        docKey = DocumentationIndex.Instance.FindKeywordSection(text.ToUpperInvariant());
        return baseHover;
    }

    private static string? GetKeywordDescription(SqlToken kind)
    {
        return kind switch
        {
            SqlToken.Select => "**SELECT** — Specifies columns and expressions to include in the query output.",
            SqlToken.From => "**FROM** — Specifies the data source table(s) for the query.",
            SqlToken.Where => "**WHERE** — Filters rows based on a boolean condition.",
            SqlToken.Join => "**JOIN** — Combines rows from two tables based on a related column.",
            SqlToken.Left => "**LEFT** — Left outer join: all rows from the left table, matching from right.",
            SqlToken.Right => "**RIGHT** — Right outer join: all rows from the right table, matching from left.",
            SqlToken.Full => "**FULL** — Full outer join: all rows from both tables.",
            SqlToken.Cross => "**CROSS** — Cross join: cartesian product of both tables.",
            SqlToken.Inner => "**INNER** — Inner join: only rows that match in both tables.",
            SqlToken.Lateral => "**LATERAL** — Lateral join: re-executes the right-hand source per outer row, allowing it to reference left-side columns. O(N × M) nested-loop execution.",
            SqlToken.Apply => "**APPLY** — T-SQL style lateral join. CROSS APPLY = CROSS JOIN LATERAL, OUTER APPLY = LEFT JOIN LATERAL.",
            SqlToken.On => "**ON** — Specifies the join condition.",
            SqlToken.Into => "**INTO** — Writes query output to a file. Format inferred from extension (.csv, .parquet, .h5).",
            SqlToken.As => "**AS** — Creates an alias for a table or column.",
            SqlToken.Order => "**ORDER** — Used with BY to sort results.",
            SqlToken.By => "**BY** — Used with ORDER or SHARD to specify the sort/partition key.",
            SqlToken.Limit => "**LIMIT** — Restricts the number of output rows.",
            SqlToken.Offset => "**OFFSET** — Skips the specified number of rows before returning results.",
            SqlToken.And => "**AND** — Logical conjunction: both conditions must be true.",
            SqlToken.Or => "**OR** — Logical disjunction: at least one condition must be true.",
            SqlToken.Not => "**NOT** — Logical negation: inverts the boolean value.",
            SqlToken.In => "**IN** — Tests set membership: `column IN (value1, value2, ...)`.",
            SqlToken.Between => "**BETWEEN** — Tests range inclusion: `column BETWEEN low AND high`.",
            SqlToken.Like => "**LIKE** — Pattern matching with `%` (any chars) and `_` (single char) wildcards.",
            SqlToken.ILike => "**ILIKE** — Case-insensitive pattern matching with `%` and `_` wildcards.",
            SqlToken.Regexp => "**REGEXP** — Matches against a .NET regular expression (unanchored, case-sensitive). Use `^`/`$` for full-string match, `(?i)` for case-insensitive.",
            SqlToken.Escape => "**ESCAPE** — Specifies a custom escape character for LIKE/ILIKE patterns: `col LIKE '100\\%' ESCAPE '\\\\'` treats `%` as a literal.",
            SqlToken.Is => "**IS** — Null testing: `column IS NULL` or `column IS NOT NULL`.",
            SqlToken.Null => "**NULL** — The null (missing value) literal.",
            SqlToken.Cast => "**CAST** — Explicit type conversion: `CAST(value AS type)`.",
            SqlToken.Shard => "**SHARD** — Partitions output into multiple files by row count or byte size.",
            SqlToken.Asc => "**ASC** — Ascending sort order (default).",
            SqlToken.Desc => "**DESC** — Descending sort order.",
            SqlToken.True => "**TRUE** — Boolean true literal.",
            SqlToken.False => "**FALSE** — Boolean false literal.",
            SqlToken.Outer => "**OUTER** — Outer join modifier. `LEFT [OUTER] JOIN`, `RIGHT [OUTER] JOIN`, `FULL [OUTER] JOIN` — the keyword is optional in all three forms.",
            SqlToken.Case => "**CASE** — Conditional expression. Searched form: `CASE WHEN condition THEN result … [ELSE default] END`. Simple form: `CASE value WHEN match THEN result … END`.",
            SqlToken.When => "**WHEN** — A conditional branch within a CASE expression: `WHEN condition THEN result`.",
            SqlToken.Then => "**THEN** — The result expression for a matching WHEN branch.",
            SqlToken.Else => "**ELSE** — The default result when no WHEN branch matches. Without ELSE an unmatched CASE returns NULL.",
            SqlToken.End => "**END** — Closes a CASE expression.",
            SqlToken.Over => "**OVER** — Defines a window specification for a window function: `function() OVER(PARTITION BY … ORDER BY … ROWS BETWEEN …)`.",
            SqlToken.Partition => "**PARTITION** — Used with BY to divide rows into partitions for window function evaluation.",
            SqlToken.Within => "**WITHIN GROUP** — Ordered-set aggregate syntax. The ORDER BY expression inside WITHIN GROUP supplies the values to aggregate: `PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY salary)`, `MODE() WITHIN GROUP (ORDER BY category)`.",
            SqlToken.Rows => "**ROWS** — Specifies a row-based window frame: `ROWS BETWEEN start AND end`.",
            SqlToken.Range => "**RANGE** — Value-based window frame: `RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW` includes all rows whose ORDER BY value is ≤ the current row's value.",
            SqlToken.Unbounded => "**UNBOUNDED** — Indicates the frame extends to the beginning (PRECEDING) or end (FOLLOWING) of the partition.",
            SqlToken.Preceding => "**PRECEDING** — Indicates rows before the current row in a window frame.",
            SqlToken.Following => "**FOLLOWING** — Indicates rows after the current row in a window frame.",
            SqlToken.Current => "**CURRENT** — Used with ROW to indicate the current row in a window frame: `CURRENT ROW`.",
            SqlToken.Exists => "**EXISTS** — Tests whether a subquery returns any rows: `WHERE EXISTS (SELECT 1 FROM …)`. Short-circuits after finding the first matching row.",
            SqlToken.Distinct => "**DISTINCT** — Eliminates duplicate rows from the result set. Also used inside aggregates: `COUNT(DISTINCT col)`.",
            SqlToken.Ignore => "**IGNORE NULLS** — Instructs value window functions (FIRST_VALUE, LAST_VALUE, NTH_VALUE) to skip NULL values when searching for the target row.",
            SqlToken.Respect => "**RESPECT NULLS** — Default NULL handling for value window functions; NULL values are included rather than skipped.",
            SqlToken.Nulls => "**NULLS** — Used with IGNORE or RESPECT to control null handling in value window functions: `FIRST_VALUE(col) IGNORE NULLS OVER (…)`.",
            SqlToken.With => "**WITH** — Introduces a Common Table Expression (CTE): `WITH name AS (SELECT …) SELECT …`. CTEs can be composed, recursive (`WITH RECURSIVE`), and optionally materialization-hinted.",
            SqlToken.Recursive => "**RECURSIVE** — Enables recursive CTEs: `WITH RECURSIVE name AS (anchor UNION ALL recursive_member)`. The recursive member references the CTE by name to build hierarchical or iterative results.",
            SqlToken.Materialized => "**MATERIALIZED** / **NOT MATERIALIZED** — Hints the planner to buffer the CTE result once (`MATERIALIZED`) or inline it at each reference site (`NOT MATERIALIZED`). By default single-reference CTEs are inlined and multi-reference ones are materialized.",
            SqlToken.Union => "**UNION** — Combines results from two queries, removing duplicates. Use `UNION ALL` to preserve duplicates.",
            SqlToken.All => "**ALL** — Modifier for set operations that preserves duplicate rows: `UNION ALL`, `INTERSECT ALL`, `EXCEPT ALL`.",
            SqlToken.Intersect => "**INTERSECT** — Returns only rows present in both query results. Use `INTERSECT ALL` to keep duplicates.",
            SqlToken.Except => "**EXCEPT** — Returns rows from the first query that are not in the second. Use `EXCEPT ALL` to preserve duplicates.",
            SqlToken.Let => "**LET** — Declares a named, memoized intermediate expression in SELECT. Evaluated once per row. Not included in output unless aliased with AS. Syntax: `LET name = expression [AS alias]`.",
            SqlToken.Assert => "**ASSERT** — Validates a predicate against every projected row. Failing rows are handled according to the failure mode: `ABORT` (default) throws an error, `SKIP` silently discards the row, `WARN` records a diagnostic and continues. Syntax: `ASSERT predicate [MESSAGE expr] [ON FAIL SKIP|WARN|ABORT]`.",
            SqlToken.Message => "**MESSAGE** — Provides a custom failure message for an ASSERT clause. The expression is evaluated only when the assertion fails. Syntax: `ASSERT predicate MESSAGE 'text or expression'`.",
            SqlToken.Define => "**DEFINE** — Declares a block of LET bindings and ASSERT clauses that apply to the entire SELECT statement. Syntax: `DEFINE { LET name = expr; ASSERT predicate [MESSAGE expr]; … }`.",
            SqlToken.Pivot => "**PIVOT** — Rotates distinct values of a column into separate output columns, applying an aggregate to each cell: `FROM t PIVOT (SUM(amount) FOR category IN ('A', 'B', 'C'))`.",
            SqlToken.Unpivot => "**UNPIVOT** — Rotates columns into rows, producing a name/value pair per source column per input row: `FROM t UNPIVOT (value FOR col_name IN (a, b, c))`.",
            SqlToken.For => "**FOR** — In PIVOT, specifies the pivot axis column: `PIVOT (SUM(x) FOR category IN ('A', 'B'))`. In UNPIVOT, specifies the name-output column: `UNPIVOT (v FOR col IN (a, b))`.",
            SqlToken.Include => "**INCLUDE NULLS** — UNPIVOT modifier that retains rows where the source column is NULL. By default UNPIVOT excludes NULL-valued source columns.",
            SqlToken.Tablesample => "**TABLESAMPLE** — Samples rows from a table source.\n\n" +
                "**Methods:**\n" +
                "- `BERNOULLI(pct)` — row-level probabilistic sampling\n" +
                "- `SYSTEM(pct)` — chunk-level sampling\n" +
                "- `STRATIFIED(pct) ON col` — per-class proportional sampling (preserves distribution)\n" +
                "- `BALANCED(count) ON col` — per-class fixed-count reservoir sampling (equalizes distribution)\n\n" +
                "Add `REPEATABLE(seed)` for deterministic results.",
            SqlToken.Repeatable => "**REPEATABLE** — Seeds the random sampler for deterministic TABLESAMPLE results: `TABLESAMPLE BERNOULLI(10) REPEATABLE(42)`. The same seed on the same data always returns the same sample.",
            SqlToken.Create => "**CREATE** — Defines a new catalog object. `CREATE [TEMP] TABLE name (col type, …) [PRIMARY KEY (col, …)] [AS SELECT …]` for tables, `CREATE [OR REPLACE] [PURE] FUNCTION name(@p type, …) RETURNS type AS …` for scalar UDFs, `CREATE [OR REPLACE] PROCEDURE name(…) AS BEGIN … END` for procedures. Add `IF NOT EXISTS` to suppress errors.",
            SqlToken.Drop => "**DROP** — Removes a catalog object. `DROP [TEMP] TABLE [IF EXISTS] name` for tables, `DROP FUNCTION [IF EXISTS] name` for scalar UDFs, `DROP PROCEDURE [IF EXISTS] name` for procedures. Persistent tables also delete their `.datum`, `.datum-blob`, `.datum-index`, and `.datum-pkindex` sidecars.",
            SqlToken.Insert => "**INSERT INTO** — Inserts rows into a table: `INSERT INTO name [(col1, col2, …)] VALUES (v1, v2, …)` or `INSERT INTO name SELECT …`. Works on TEMP and persistent tables; columns with DEFAULT/IDENTITY auto-fill when omitted.",
            SqlToken.Update => "**UPDATE** — Updates rows in a table: `UPDATE name SET col = expr [, col = expr …] [FROM source [AS alias]] [WHERE condition]`. The optional FROM clause joins another table to supply update values (last-match-wins on multi-match). PRIMARY KEY columns cannot be updated — DELETE and re-INSERT instead.",
            SqlToken.Delete => "**DELETE FROM** — Removes rows from a table: `DELETE FROM name [WHERE condition]`. Soft-deletes via chapter-level tombstones on persistent tables; storage reclaims at compaction.",
            SqlToken.Analyze => "**ANALYZE** — Rebuilds the `.datum-index` acceleration sidecar so query-planner chunk pruning reflects current data: `ANALYZE name`. Functionally an alias for `REINDEX` in this build; the `.datum-manifest` column-statistics refresh will land with per-kind FeatureManifest expansion.",
            SqlToken.Reindex => "**REINDEX** — Rebuilds the `.datum-index` acceleration sidecar for a persistent table from current data: `REINDEX [TABLE] name`. Mutations (INSERT/UPDATE/DELETE/ALTER) drop the cached index and indexed queries fall back to scan; running REINDEX restores acceleration.",
            SqlToken.Alter => "**ALTER TABLE** — Modifies a table's schema: `ALTER TABLE name ADD [COLUMN] col type [NOT NULL] [DEFAULT expr]` to add a column, `ALTER TABLE name DROP [COLUMN] col` to soft-drop one. Works on both TEMP and persistent tables; ADD requires the new column to be nullable when the table already has rows.",
            SqlToken.Table => "**TABLE** — Specifies a table in DDL statements: `CREATE [TEMP] TABLE name (…)`, `DROP TABLE name`, `ALTER TABLE name ADD …`.",
            SqlToken.Temp => "**TEMP** — Marks a table as session-scoped (temporary). Equivalent to `TEMPORARY`. The table is automatically dropped when the session ends.",
            SqlToken.Temporary => "**TEMPORARY** — Marks a table as session-scoped (temporary). Equivalent to `TEMP`. The table is automatically dropped when the session ends.",
            SqlToken.Values => "**VALUES** — Supplies literal row data for INSERT: `INSERT INTO name VALUES (v1, v2), (v3, v4)`.",
            SqlToken.Set => "**SET** — Introduces column assignments in UPDATE: `UPDATE name SET col1 = expr1, col2 = expr2`.",
            SqlToken.Add => "**ADD** — Adds a new column in ALTER TABLE: `ALTER TABLE name ADD [COLUMN] col type [NOT NULL] [DEFAULT expr]`.",
            SqlToken.Column => "**COLUMN** — Optional keyword in ALTER TABLE ADD: `ALTER TABLE name ADD COLUMN col type`.",
            SqlToken.Default => "**DEFAULT** — Specifies a default expression for a column: `col type DEFAULT expr`. Accepts any tableless expression — literals, function calls (`now()`, `uuidv4()`), arithmetic, array literals (`[1, 2, 3]`), etc. Evaluated per row at INSERT time when the column is omitted, so `DEFAULT now()` gives every row a fresh timestamp. Column references, subqueries, and window functions are rejected (no source row in scope).",
            SqlToken.Primary => "**PRIMARY** — Used with KEY to define the primary key constraint: `PRIMARY KEY (col1, col2)`.",
            SqlToken.Key => "**KEY** — Used with PRIMARY to define the primary key constraint: `PRIMARY KEY (col1, col2)`.",
            SqlToken.If => "**IF** — Conditional guard for DDL: `CREATE TABLE IF NOT EXISTS name …` or `DROP TABLE IF EXISTS name`.",
            SqlToken.Identity => "**IDENTITY** — Auto-generated integer column. T-SQL-flavored alias for `GENERATED ALWAYS AS IDENTITY`; the catalog reserves the next value on each INSERT and explicit values are rejected. Bare `IDENTITY` defaults to `(1, 1)`; `IDENTITY(seed, step)` lets you customise. Prefer the PG-canonical `GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY` form.",
            SqlToken.Generated => "**GENERATED** — Introduces a generated-column clause:\n\n- `GENERATED ALWAYS AS (expr)` — STORED computed column. Value derived per row from `expr`; explicit values rejected on INSERT/UPDATE.\n- `GENERATED ALWAYS AS IDENTITY [(seed, step)]` — auto-generated integer; explicit values rejected.\n- `GENERATED BY DEFAULT AS IDENTITY [(seed, step)]` — auto-generated integer that accepts user-supplied values when provided; the counter is only consulted on omission.",
            SqlToken.Always => "**ALWAYS** — Used inside the `GENERATED ALWAYS AS …` column clause to specify either a computed expression (`GENERATED ALWAYS AS (expr)`) or an IDENTITY column that rejects explicit values (`GENERATED ALWAYS AS IDENTITY`). Contrast with `GENERATED BY DEFAULT AS IDENTITY`, which accepts user-supplied values.",
            _ => null,
        };
    }

    /// <summary>
    /// Appends a documentation excerpt and "See more" link to existing hover markdown.
    /// The link uses the <c>command:</c> URI scheme so Monaco renders it as a clickable
    /// action when the host registers a <c>datumingest.openDoc</c> command and marks
    /// the hover markdown as trusted (<c>isTrusted: true</c>).
    /// </summary>
    private static string AppendDocLink(string markdown, string? sectionKey)
    {
        if (sectionKey is null)
        {
            return markdown;
        }

        DocumentationSection? section = DocumentationIndex.Instance.TryGetSection(sectionKey);
        if (section is null)
        {
            return markdown;
        }

        if (!string.IsNullOrEmpty(section.Excerpt))
        {
            markdown += $"\n\n---\n\n{section.Excerpt}";
        }

        // Use command: URI scheme — Monaco supports this natively in trusted markdown.
        // The host registers a 'datumingest.openDoc' command to handle navigation.
        string encodedKey = Uri.EscapeDataString($"\"{sectionKey}\"");
        markdown += $"\n\n[See more](command:datumingest.openDoc?{encodedKey})";
        return markdown;
    }

    /// <summary>
    /// Tokenizes the full SQL, capturing position and text span for each token.
    /// Returns an empty list on failure.
    /// </summary>
    private static List<TokenHit> TokenizeWithSpans(string sql)
    {
        List<TokenHit> result = new();
        try
        {
            TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
            foreach (Token<SqlToken> token in tokens)
            {
                string text = token.ToStringValue();
                // Superpower's Position is 1-based for Line/Column and
                // already 0-based for Absolute. We need:
                //   - Line/Column (0-based) for the hover range Monaco
                //     paints around the token,
                //   - Absolute (0-based) to match the cursor offset Monaco
                //     supplies — line+column alone would only land on
                //     line-1 tokens, leaving everything below silently
                //     unhoverable.
                int line = token.Position.Line - 1;
                int column = token.Position.Column - 1;
                int absolute = token.Position.Absolute;
                result.Add(new TokenHit(token.Kind, text, line, column, absolute));
            }
        }
        catch
        {
            // Partial tokenization is acceptable.
        }

        return result;
    }

    /// <summary>
    /// Finds the token whose absolute-offset span contains the cursor.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="TokenHit.AbsoluteOffset"/> rather than column, which
    /// only coincides with the document offset on line 1 — a previous
    /// column-as-offset heuristic silently returned <see langword="null"/>
    /// for any token below the first newline, breaking hover on function
    /// calls, table names, and columns in any multi-line SQL.
    /// </remarks>
    private static TokenHit? FindTokenAtOffset(List<TokenHit> tokens, int cursorOffset)
    {
        foreach (TokenHit token in tokens)
        {
            int tokenStart = token.AbsoluteOffset;
            int tokenEnd = tokenStart + token.Text.Length;

            if (cursorOffset >= tokenStart && cursorOffset < tokenEnd)
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsKeywordToken(SqlToken kind)
    {
        return kind < SqlToken.Identifier;
    }

    // ─────────────────── Data type hover support ───────────────────

    internal static readonly Dictionary<string, string> TypeDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Unknown"] = "**Unknown** — Untyped or uninitialized value. Used internally for untyped SQL NULLs.",
            ["Boolean"] = "**Boolean** — True or false. Aliases: `bool`.",
            ["Int8"] = "**Int8** — Signed 8-bit integer (−128 to 127).",
            ["Int16"] = "**Int16** — Signed 16-bit integer (−32,768 to 32,767).",
            ["Int32"] = "**Int32** — Signed 32-bit integer (−2,147,483,648 to 2,147,483,647).",
            ["Int64"] = "**Int64** — Signed 64-bit integer.",
            ["UInt8"] = "**UInt8** — Unsigned 8-bit integer (0 to 255).",
            ["UInt16"] = "**UInt16** — Unsigned 16-bit integer (0 to 65,535).",
            ["UInt32"] = "**UInt32** — Unsigned 32-bit integer (0 to 4,294,967,295).",
            ["UInt64"] = "**UInt64** — Unsigned 64-bit integer.",
            ["Float32"] = "**Float32** — 32-bit IEEE 754 floating-point number.",
            ["Float64"] = "**Float64** — 64-bit IEEE 754 floating-point number (double precision).",
            ["String"] = "**String** — Variable-length UTF-8 text.",
            ["Date"] = "**Date** — Calendar date without time component (year, month, day).",
            ["Timestamp"] = "**Timestamp** — PG `timestamp without time zone`: naive wall-clock ticks, no tz info.",
            ["TimestampTz"] = "**TimestampTz** — PG `timestamp with time zone`: UTC ticks; input offset is normalised to UTC at construction and discarded.",
            ["Time"] = "**Time** — Time of day without date component.",
            ["Duration"] = "**Duration** — Elapsed time span with microsecond precision.",
            ["Uuid"] = "**Uuid** — 128-bit universally unique identifier (RFC 4122).",
            ["Vector"] = "**Vector** — Fixed-length array of Float32 values. Supports distance and similarity operations.",
            ["Matrix"] = "**Matrix** — Two-dimensional array of Float32 values.",
            ["Tensor"] = "**Tensor** — Multi-dimensional array of Float32 values.",
            ["Array"] = "**Array** — Variable-length typed array. Element type is inferred from context.",
            ["Struct"] = "**Struct** — Named tuple of typed fields. Field types are inferred from context.",
            ["Image"] = "**Image** — Binary image data with format metadata.",
            ["UInt8Array"] = "**UInt8Array** — Variable-length byte array (binary data).",
            ["Type"] = "**Type** — A type tag describing another DataKind. Produced by `typeof()` for type comparisons.",
        };

    // ─────────────────── Virtual schema hover support ───────────────────

    private static readonly Dictionary<string, string> VirtualSchemaDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["information_schema"] = "PostgreSQL-compatible metadata schema exposing tables, columns, and schemata.",
            ["datum_catalog"] = "DatumIngest-specific metadata schema exposing providers, functions, statistics, indexes, and interactions.",
        };

    private static readonly Dictionary<string, Dictionary<string, string>> VirtualTableDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["information_schema"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["tables"] = "Lists all tables (BASE TABLE, TEMPORARY TABLE) with their schema assignment.",
                ["columns"] = "Lists all columns of all tables with ordinal position, data type, and nullability.",
                ["schemata"] = "Lists the known schema namespaces (public, temp, information_schema, datum_catalog).",
            },
            ["datum_catalog"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["providers"] = "Lists all registered data providers.",
                ["functions"] = "Lists all available functions with type, category, return type, description, parameter count, and query-unit cost.",
                ["function_parameters"] = "Lists all documented function parameters with ordinal position, name, data type, and optionality.",
                ["statistics"] = "Lists column-level statistics from feature manifests including distribution shape, quantiles, and type-specific metrics.",
                ["indexes"] = "Lists per-column index metadata (sorted, B+Tree, bitmap, bloom, mapped) with entry counts.",
                ["interactions"] = "Lists pairwise column interaction statistics (Pearson, Spearman, Cramér's V, mutual information, etc.).",
            },
        };
}

/// <summary>
/// A token with its position information for hover hit-testing.
/// <see cref="Line"/> / <see cref="Column"/> are 0-based and drive the
/// hover range Monaco paints; <see cref="AbsoluteOffset"/> is 0-based and
/// drives cursor-offset hit-testing in <see cref="HoverProvider"/>.
/// </summary>
internal sealed record TokenHit(SqlToken Kind, string Text, int Line, int Column, int AbsoluteOffset);
