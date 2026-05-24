namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower.Model;

/// <summary>
/// Provides hover information (type details, function signatures, keyword documentation)
/// for tokens at a given cursor position in SQL text.
/// </summary>
public sealed class HoverProvider
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>
    /// Named-type vocabulary indexed by canonical name (case-insensitive)
    /// from <see cref="LanguageServerManifest.NamedTypes"/>. Lets the hover
    /// resolver expand a bare named-type reference like
    /// <c>LabeledDetection</c> into its constituent struct fields without
    /// requiring an engine-assembly reference. Empty when the manifest
    /// omits the section (offline-built manifests built against an older
    /// engine).
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<StructFieldShape>> _namedTypeFields;

    /// <summary>
    /// Creates a hover provider backed by the given manifest.
    /// </summary>
    public HoverProvider(LanguageServerManifest manifest)
    {
        _manifest = manifest;
        _namedTypeFields = new(StringComparer.OrdinalIgnoreCase);
        if (manifest.NamedTypes is { Count: > 0 } namedTypes)
        {
            foreach (NamedTypeEntry entry in namedTypes)
            {
                if (StructTypeAnnotation.TryParse(entry.Description, out IReadOnlyList<StructFieldShape> fields))
                {
                    _namedTypeFields[entry.Name] = fields;
                }
            }
        }
    }

    /// <summary>
    /// Resolves a kind annotation to a struct's field list. Handles both
    /// inline <c>Struct&lt;…&gt;</c> annotations (delegated to
    /// <see cref="StructTypeAnnotation.TryParse"/>) and bare named-type
    /// references that resolve through the manifest's
    /// <see cref="LanguageServerManifest.NamedTypes"/> vocabulary. Returns
    /// <see langword="null"/> for non-struct kinds and for unknown named
    /// types.
    /// </summary>
    private IReadOnlyList<StructFieldShape>? TryResolveStructFields(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        if (StructTypeAnnotation.TryParse(kind, out IReadOnlyList<StructFieldShape> inline))
        {
            return inline;
        }
        if (_namedTypeFields.TryGetValue(kind, out IReadOnlyList<StructFieldShape>? named))
        {
            return named;
        }
        return null;
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
        (Dictionary<string, FunctionSignature> tvfAliases, Dictionary<string, FunctionSource> tvfAliasSources) =
            BuildTvfAliasMaps(sql);
        // Plain-table FROM/JOIN aliases — lets hover on a bare `t` from
        // `FROM users t` resolve to the users table card, and lets
        // `t.column` resolve column hovers through the same alias.
        Dictionary<string, string> tableAliases = BuildTableAliasMap(sql);
        // CTE projection schemas — same global-scope simplification as the
        // TVF alias map. A bare CTE name or a FROM alias bound to a CTE
        // resolves to the CTE's derived output columns.
        CteSchemaResult cteSchemas = CteSchemaResolver.Resolve(sql, _manifest);
        // DECLAREd variables visible anywhere in the batch — covers both the
        // declaration site and downstream references (engine resolves these
        // by name through the variable scope before the row schema, so the
        // user expects hover on a bare `model_in_w` to surface its type even
        // when the reference site sits inside a CTE expression).
        Dictionary<string, string?> declaredVariables = BuildDeclaredVariableMap(sql);
        // Active lambda parameter scopes at the cursor — innermost last.
        // Used by ResolveIdentifierHover to recognise `t` / `x` / etc. as
        // lambda parameters before falling through to column lookup.
        IReadOnlyList<LambdaScope> lambdaScopes = LambdaScopeWalker.FindActiveScopes(tokens, cursorOffset);

        string? docKey = null;

        // Splice-aware hover dispatch. When the cursor lands on a template
        // string AND the locator says it's inside a `${…}` body, re-run
        // the hover pipeline against the splice body so identifiers,
        // columns, function calls, and literals inside the splice get
        // the same treatment as if they were typed in plain SQL. Outside
        // a splice (literal chunks, `${`, `}`) the outer TemplateString
        // arm of the switch keeps surfacing the generic blurb.
        if (hit.Kind == SqlToken.TemplateString
            && TemplateSpliceLocator.TryLocate(sql, cursorOffset, out var splice))
        {
            HoverResult? spliceHover = ResolveSpliceHover(
                sql, cursorOffset, splice, tvfAliases, tvfAliasSources, tableAliases, cteSchemas, declaredVariables);
            if (spliceHover is not null) return spliceHover;
            // Locator hit but inner dispatch produced nothing — fall
            // through to the generic template hover so the user still
            // gets some context for the click.
        }

        string? markdown = ResolveHoverMarkdown(hit, tokens, tvfAliases, tvfAliasSources, tableAliases, cteSchemas, lambdaScopes, declaredVariables, out docKey);

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
    /// Core token → hover-markdown dispatch, shared by the outer
    /// <see cref="GetHover"/> and the splice-aware inner dispatch in
    /// <see cref="ResolveSpliceHover"/>. Factored out so both call sites
    /// behave identically — anything that's added to the outer token
    /// hover automatically lights up inside a splice too.
    /// </summary>
    private string? ResolveHoverMarkdown(
        TokenHit hit,
        List<TokenHit> tokens,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        Dictionary<string, string> tableAliases,
        CteSchemaResult cteSchemas,
        IReadOnlyList<LambdaScope> lambdaScopes,
        Dictionary<string, string?> declaredVariables,
        out string? docKey)
    {
        docKey = null;
        return hit.Kind switch
        {
            SqlToken.Identifier => ResolveIdentifierHover(hit.Text, tokens, hit, tvfAliases, tvfAliasSources, tableAliases, cteSchemas, lambdaScopes, declaredVariables, out docKey),
            SqlToken.TypeKeyword => TypeDescriptions.TryGetValue(hit.Text, out string? typeDesc) ? typeDesc : null,
            SqlToken.Arrow => "**`->`** Lambda arrow — separates parameter(s) from the body expression.\n\n" +
                "Usage: `x -> expr` or `(a, b) -> expr` inside higher-order functions " +
                "such as `array_transform` and `array_filter`.",
            // Hit on a template-string token outside any splice body —
            // surface the generic blurb. Splice-internal identifiers /
            // calls are handled by ResolveSpliceHover before this switch.
            SqlToken.TemplateString => "**Template string** — backtick-delimited string with `${expression}` interpolation.\n\n" +
                "Lowers to a `concat(literal_chunks, splice_exprs…)` call at parse time. " +
                "Splices may contain any scalar expression, including model invocations and other UDFs.",
            SqlToken.NumberLiteral => GetNumberLiteralHover(hit.Text),
            SqlToken.StringLiteral => GetStringLiteralHover(hit.Text),
            _ when IsKeywordToken(hit.Kind) => GetKeywordHover(hit.Kind, hit.Text, out docKey),
            _ => null,
        };
    }

    /// <summary>
    /// Dispatches hover for a cursor that sits inside a <c>${…}</c>
    /// splice body. Re-tokenizes the splice text in isolation, runs
    /// <see cref="FindTokenAtOffset"/> against the translated cursor,
    /// and reuses the same resolver path the outer hover uses. The
    /// returned span is re-anchored back to outer-SQL line/column
    /// coordinates so Monaco paints the highlight on the right token,
    /// not somewhere inside the splice body's local frame.
    /// </summary>
    /// <remarks>
    /// Scope objects passed in (<paramref name="tvfAliases"/>,
    /// <paramref name="cteSchemas"/>, <paramref name="declaredVariables"/>)
    /// are computed off the outer SQL once — splices live inline in the
    /// outer SQL so the same maps are valid inside them. Lambda scopes
    /// are recomputed against the splice's own token list because lambda
    /// detection looks back to the nearest enclosing arrow, and that
    /// search must run in the splice's local token frame to surface a
    /// splice-internal lambda parameter correctly.
    /// </remarks>
    private HoverResult? ResolveSpliceHover(
        string sql,
        int cursorOffset,
        TemplateSpliceLocator.SpliceLocation splice,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        Dictionary<string, string> tableAliases,
        CteSchemaResult cteSchemas,
        Dictionary<string, string?> declaredVariables)
    {
        List<TokenHit> innerTokens = TokenizeWithSpans(splice.Body);
        int innerCursor = cursorOffset - splice.BodyStart;
        TokenHit? innerHit = FindTokenAtOffset(innerTokens, innerCursor);
        if (innerHit is null) return null;

        IReadOnlyList<LambdaScope> innerLambdaScopes =
            LambdaScopeWalker.FindActiveScopes(innerTokens, innerCursor);

        string? markdown = ResolveHoverMarkdown(
            innerHit, innerTokens, tvfAliases, tvfAliasSources, tableAliases, cteSchemas, innerLambdaScopes,
            declaredVariables, out string? docKey);

        if (markdown is null) return null;
        markdown = AppendDocLink(markdown, docKey);

        // Translate the inner token's 0-based (line, column) — which are
        // relative to the splice body — back to outer-SQL coordinates.
        // The splice body starts at a specific absolute offset; map that
        // offset to (line, column) in the outer SQL once, then offset the
        // inner token's position by it (column-only on the body's first
        // line; line-shifted only on subsequent lines).
        (int outerLine, int outerColumn) = OffsetToLineColumn(sql, splice.BodyStart);
        int finalLine = innerHit.Line == 0 ? outerLine : outerLine + innerHit.Line;
        int finalColumn = innerHit.Line == 0
            ? outerColumn + innerHit.Column
            : innerHit.Column;

        return new HoverResult
        {
            Contents = markdown,
            StartLine = finalLine,
            StartColumn = finalColumn,
            EndLine = finalLine,
            EndColumn = finalColumn + innerHit.Text.Length,
            DocumentationUri = docKey,
        };
    }

    /// <summary>
    /// Maps an absolute character offset in <paramref name="sql"/> to a
    /// 0-based <c>(line, column)</c> pair. Used by splice-aware hover to
    /// re-anchor inner-token spans back to outer-SQL coordinates so
    /// Monaco's highlight lands on the right characters.
    /// </summary>
    private static (int Line, int Column) OffsetToLineColumn(string sql, int absoluteOffset)
    {
        int clamped = absoluteOffset < 0 ? 0
            : absoluteOffset > sql.Length ? sql.Length
            : absoluteOffset;
        int line = 0;
        int lastLineStart = 0;
        for (int i = 0; i < clamped; i++)
        {
            if (sql[i] == '\n')
            {
                line++;
                lastLineStart = i + 1;
            }
        }
        return (line, clamped - lastLineStart);
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
        Dictionary<string, FunctionSource> tvfAliasSources,
        Dictionary<string, string> tableAliases,
        CteSchemaResult cteSchemas,
        IReadOnlyList<LambdaScope> lambdaScopes,
        Dictionary<string, string?> declaredVariables,
        out string? docKey)
    {
        docKey = null;

        // Lambda-parameter resolution. Innermost lambda wins on shadowing
        // (e.g. `(t) -> (t) -> t` resolves the inner `t`). Skip the function-
        // call check at this point on purpose — a lambda parameter `t`
        // followed by `(` would be a call invocation, not a parameter ref,
        // but lambda parameters in scope today are always scalar values, so
        // such a sequence is malformed user input rather than a real call.
        for (int i = lambdaScopes.Count - 1; i >= 0; i--)
        {
            LambdaScope scope = lambdaScopes[i];
            int paramIndex = -1;
            for (int p = 0; p < scope.Parameters.Count; p++)
            {
                if (string.Equals(scope.Parameters[p], name, StringComparison.Ordinal))
                {
                    paramIndex = p;
                    break;
                }
            }
            if (paramIndex < 0) continue;
            string? lambdaHover = FormatLambdaParameterHover(name, paramIndex, scope);
            if (lambdaHover is not null) return lambdaHover;
        }

        // Hover on the parameter DECLARATION (e.g. the `t` in `(t) -> body`
        // when the cursor is on the parameter name BEFORE the arrow).
        // FindActiveScopes only pushes a scope once it's processed `->`,
        // so the loop above misses this case. Synthesise the scope by
        // looking forward for the matching arrow.
        int idx = tokens.IndexOf(currentToken);
        if (idx >= 0)
        {
            LambdaScope? declScope =
                LambdaScopeWalker.TryFindLambdaScopeForParameterDeclaration(tokens, idx);
            if (declScope is not null)
            {
                int paramIndex = -1;
                for (int p = 0; p < declScope.Parameters.Count; p++)
                {
                    if (string.Equals(declScope.Parameters[p], name, StringComparison.Ordinal))
                    {
                        paramIndex = p;
                        break;
                    }
                }
                if (paramIndex >= 0)
                {
                    string? declHover = FormatLambdaParameterHover(name, paramIndex, declScope);
                    if (declHover is not null) return declHover;
                }
            }
        }

        // DECLAREd variable — surfaced before the function-call / column
        // checks so an identifier whose name matches a top-level
        // `DECLARE name TYPE = ...` always renders the declared kind. The
        // engine evaluates variables ahead of row columns when both share
        // a name, so hover mirrors runtime resolution. Skips the lookup
        // when the identifier is followed by `(` — that's a call site,
        // never a scalar variable reference.
        int callPeekIndex = tokens.IndexOf(currentToken);
        bool followedByCall = callPeekIndex >= 0
            && callPeekIndex + 1 < tokens.Count
            && tokens[callPeekIndex + 1].Kind == SqlToken.LeftParen;
        if (!followedByCall
            && declaredVariables.TryGetValue(name, out string? declaredKind))
        {
            string kindLabel = declaredKind ?? "?";
            return $"**{name}**: `{kindLabel}`\n\n*DECLAREd variable*";
        }

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

        // Three-segment chain `alias.column.field` (cursor on the deepest
        // segment) — try the struct-field reading first. Mirrors the
        // SemanticAnalyzer's disambiguation rule: when the first segment
        // is a known alias, the chain is alias.column.structField, not
        // schema.table.column. Resolve `alias.column` to a kind via the
        // TVF / CTE / table-alias surfaces, parse a `Struct<…>` annotation,
        // and look up the deepest segment as a field name.
        if (currentIndex >= 4
            && tokens[currentIndex - 1].Kind == SqlToken.Dot
            && (tokens[currentIndex - 2].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 2].Kind))
            && tokens[currentIndex - 3].Kind == SqlToken.Dot
            && (tokens[currentIndex - 4].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 4].Kind)))
        {
            string outerAlias = tokens[currentIndex - 4].Text;
            string structColumn = tokens[currentIndex - 2].Text;
            string? threePartHover = TryGetStructFieldChainHover(
                outerAlias, structColumn, name,
                tvfAliases, tvfAliasSources, tableAliases, cteSchemas);
            if (threePartHover is not null) return threePartHover;
        }

        // If preceded by a dot, it could be a qualified column (table.column)
        // or a schema-qualified table reference (schema.table). Resolve
        // against the live manifest first so any schema that holds a
        // matching table — public, system, information_schema,
        // system, or a user-created schema — produces hover; fall
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
            string? tvfQualifiedHover = GetTvfColumnHover(qualifier, name, tvfAliases, tvfAliasSources, cteSchemas);
            if (tvfQualifiedHover is not null) return tvfQualifiedHover;

            // CTE alias or bare CTE name (e.g. `frames.frame_index` or
            // `f1.frame_index` where `f1` is `FROM frames f1`).
            string? cteQualifiedHover = GetCteColumnHover(qualifier, name, cteSchemas);
            if (cteQualifiedHover is not null) return cteQualifiedHover;

            // Struct field access: `curr_depth.depth` where `curr_depth` is a
            // CTE-projected column carrying a `Struct<…>` annotation (typically
            // the output of a struct-returning model call). Parse the column's
            // kind back into fields and look up the requested field by name.
            string? structFieldHover = GetStructFieldHover(qualifier, name, cteSchemas);
            if (structFieldHover is not null) return structFieldHover;

            // Plain FROM/JOIN alias: when the qualifier is `t` from
            // `FROM users t`, resolve to `users` so column hovers light up.
            string columnTableName = qualifier;
            if (tableAliases.TryGetValue(qualifier, out string? aliasedTable))
            {
                columnTableName = aliasedTable;
            }
            return GetQualifiedColumnHover(columnTableName, name);
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

        // Plain FROM/JOIN alias: hover on the `t` in `FROM users t` (or on
        // any later bare reference to `t`) should surface the underlying
        // table's hover card.
        if (tableAliases.TryGetValue(name, out string? aliasedTableName))
        {
            string? aliasedTableHover = GetTableHover(aliasedTableName);
            if (aliasedTableHover is not null)
            {
                return aliasedTableHover;
            }
        }

        // Unqualified column referenced from a TVF source. Walk every TVF
        // alias in the SQL — first match wins. Same-name collisions across
        // different TVFs in one statement are rare and best resolved by
        // the user qualifying the reference.
        string? tvfUnqualifiedHover = GetTvfColumnHoverUnqualified(name, tvfAliases, tvfAliasSources, cteSchemas);
        if (tvfUnqualifiedHover is not null) return tvfUnqualifiedHover;

        // Unqualified CTE column — same first-match-wins policy. Walks
        // every CTE projection in the statement.
        string? cteUnqualifiedHover = GetCteColumnHoverUnqualified(name, cteSchemas);
        if (cteUnqualifiedHover is not null) return cteUnqualifiedHover;

        // LET-bound name. LETs aren't part of the CTE's output schema
        // unless explicitly aliased, but the user still references them
        // by name throughout the body — surface their resolved kind here.
        if (cteSchemas.LetBindingKinds.TryGetValue(name, out string? letKind))
        {
            return $"**{name}**: `{letKind}`\n\n*LET binding*";
        }

        return GetColumnHover(name);
    }

    /// <summary>
    /// Renders the hover for a lambda parameter. Walks the manifest to find
    /// the outer function call's signature, locates the parameter slot the
    /// lambda fills, reads its
    /// <see cref="ParameterSignature.LambdaContextName"/>, and looks the
    /// context up in <see cref="LanguageServerManifest.FunctionContexts"/>
    /// for the canonical kind + a parent-chain breadcrumb. Falls back to a
    /// minimal "Lambda parameter — `name`" when the manifest doesn't carry
    /// enough metadata to pin down a context.
    /// </summary>
    private string? FormatLambdaParameterHover(string name, int paramIndex, LambdaScope scope)
    {
        string? contextName = null;
        string? declaredKind = null;
        string? outerCallSummary = null;

        if (scope.OuterCallName is not null && scope.OuterArgIndex >= 0)
        {
            FunctionSignature? outer = ResolveFunctionEntry(null, scope.OuterCallName);
            if (outer is not null)
            {
                ParameterSignature? slot = TryGetParameterSlot(outer, scope.OuterArgIndex);
                if (slot is not null)
                {
                    contextName = slot.LambdaContextName;
                    declaredKind = slot.Kind;
                    string qualifiedFn = string.IsNullOrEmpty(outer.SchemaName)
                        || string.Equals(outer.SchemaName, "system", StringComparison.OrdinalIgnoreCase)
                        ? outer.Name
                        : $"{outer.SchemaName}.{outer.Name}";
                    outerCallSummary = $"`{qualifiedFn}(...)`, argument #{scope.OuterArgIndex + 1}";
                }
            }
        }

        // Canonical parameter info from the matched context. Prefer the
        // canonical name+kind, but stay anchored on the parameter's actual
        // declared name (user may have renamed e.g. `t -> ` to `u -> `).
        LambdaParameterEntry? canonical = null;
        FunctionContextEntry? contextEntry = null;
        if (contextName is not null)
        {
            contextEntry = FindContextEntry(contextName);
            if (contextEntry is not null
                && paramIndex >= 0
                && paramIndex < contextEntry.Parameters.Count)
            {
                canonical = contextEntry.Parameters[paramIndex];
            }
        }

        string headerKind = canonical?.Kind ?? "Lambda parameter";
        string header = $"**{name}**: `{headerKind}`";

        List<string> body = new();
        if (contextEntry is not null)
        {
            string contextLabel = $"`{contextEntry.Name}` lambda";
            if (contextEntry.ParentName is not null)
            {
                contextLabel += $" (extends `{contextEntry.ParentName}`)";
            }
            body.Add($"*Context:* {contextLabel}");
        }
        else if (declaredKind is not null)
        {
            // No context entry in the manifest — surface the declared shape
            // verbatim so the user still sees the slot's expected type.
            body.Add($"*Slot type:* `{declaredKind}`");
        }

        if (outerCallSummary is not null)
        {
            body.Add($"*Bound by:* {outerCallSummary}");
        }
        else
        {
            body.Add("*Bound by:* lambda expression in this scope");
        }

        if (contextEntry is not null)
        {
            string? description = LambdaContextDescriptions.TryGetValue(contextEntry.Name, out string? d)
                ? d : null;
            if (description is not null)
            {
                body.Add(description);
            }
        }

        return body.Count == 0 ? header : header + "\n\n" + string.Join("\n\n", body);
    }

    /// <summary>
    /// Resolves a parameter slot from a function's signature by index,
    /// preferring the primary <see cref="FunctionSignature.Parameters"/>
    /// list but falling back to any <see cref="FunctionSignature.AdditionalParameterShapes"/>
    /// variant that happens to have a parameter at the requested position.
    /// This second pass matters for multi-variant signatures like
    /// <c>draw_particles</c> where the lambda slot only appears in some
    /// variants — without it, the LS would silently drop hover info for
    /// the lambda variant the user actually typed.
    /// </summary>
    private static ParameterSignature? TryGetParameterSlot(FunctionSignature outer, int argIndex)
    {
        if (argIndex >= 0 && argIndex < outer.Parameters.Count)
        {
            ParameterSignature primarySlot = outer.Parameters[argIndex];
            if (primarySlot.LambdaContextName is not null) return primarySlot;
            // Look for a variant whose slot at this index IS a lambda — gives
            // us the context info even when the primary variant uses a static
            // type at the same position.
            if (outer.AdditionalParameterShapes is not null)
            {
                foreach (IReadOnlyList<ParameterSignature> variant in outer.AdditionalParameterShapes)
                {
                    if (argIndex < variant.Count && variant[argIndex].LambdaContextName is not null)
                    {
                        return variant[argIndex];
                    }
                }
            }
            return primarySlot;
        }
        if (outer.AdditionalParameterShapes is not null)
        {
            foreach (IReadOnlyList<ParameterSignature> variant in outer.AdditionalParameterShapes)
            {
                if (argIndex >= 0 && argIndex < variant.Count) return variant[argIndex];
            }
        }
        return null;
    }

    private FunctionContextEntry? FindContextEntry(string contextName)
    {
        if (_manifest.FunctionContexts is null) return null;
        foreach (FunctionContextEntry entry in _manifest.FunctionContexts)
        {
            if (string.Equals(entry.Name, contextName, StringComparison.Ordinal)) return entry;
        }
        return null;
    }

    /// <summary>
    /// Human-readable descriptions for the well-known lambda contexts.
    /// Centralised here rather than in each context's docstring because
    /// the runtime side already covers the developer audience; this is
    /// the editor-facing copy.
    /// </summary>
    // Note: deliberately avoid `[…)` half-open-interval notation in these
    // descriptions. Some Monaco markdown renderers in our embedded version
    // interpret a `[` (even inside backticks) as the start of a link and
    // silently drop the surrounding paragraph when the matching `](url)`
    // never arrives. Use word-form ranges instead.
    private static readonly Dictionary<string, string> LambdaContextDescriptions =
        new(StringComparer.Ordinal)
        {
            ["animation"] = "Animation lambda. Receives the current frame's normalised time `t` between 0 (start) and 1 (end of last frame) across the animation's duration.",
            ["particle"] = "Per-particle sprite lambda. Receives the particle's normalised age `x` between 0 (birth) and 1 (death).",
        };

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

    /// <summary>
    /// Resolves <c>qualifier.fieldName</c> as a struct field access when
    /// <paramref name="qualifier"/> is a CTE-projected column whose kind
    /// is a canonical <c>Struct&lt;…&gt;</c> annotation. Walks every CTE's
    /// columns looking for one named <paramref name="qualifier"/>; on
    /// match, parses the column's kind and surfaces the field by name.
    /// Returns <see langword="null"/> for non-struct columns and unknown
    /// fields.
    /// </summary>
    private string? GetStructFieldHover(string qualifier, string fieldName, CteSchemaResult cteSchemas)
    {
        foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> cte in cteSchemas.Schemas)
        {
            foreach (TableColumnEntry column in cte.Value)
            {
                if (!string.Equals(column.Name, qualifier, StringComparison.OrdinalIgnoreCase)) continue;
                IReadOnlyList<StructFieldShape>? fields = TryResolveStructFields(column.Kind);
                if (fields is null) continue;
                foreach (StructFieldShape field in fields)
                {
                    if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)) continue;
                    return $"**{qualifier}.{field.Name}**: `{field.Kind}`\n\nSource: CTE `{cte.Key}` field `{qualifier}`";
                }
            }
        }

        // LET-bound struct: `LET curr_depth = models.X(...)` then
        // `curr_depth.depth`. LETs without an OutputAlias don't appear in
        // the CTE schema above; consult the dedicated LET map.
        if (cteSchemas.LetBindingKinds.TryGetValue(qualifier, out string? letKind)
            && TryResolveStructFields(letKind) is { } letFields)
        {
            foreach (StructFieldShape field in letFields)
            {
                if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)) continue;
                return $"**{qualifier}.{field.Name}**: `{field.Kind}`\n\nSource: LET `{qualifier}`";
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a 3-segment <c>outerAlias.structColumn.fieldName</c> chain
    /// to a struct-field hover. Walks the TVF / CTE / table-alias surfaces
    /// to find <c>outerAlias.structColumn</c>'s kind annotation, parses it
    /// as a <c>Struct&lt;…&gt;</c>, and looks <c>fieldName</c> up among the
    /// fields. Returns <see langword="null"/> when any layer can't resolve.
    /// </summary>
    private string? TryGetStructFieldChainHover(
        string outerAlias,
        string structColumn,
        string fieldName,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        Dictionary<string, string> tableAliases,
        CteSchemaResult cteSchemas)
    {
        string? columnKind = TryResolveAliasColumnKind(
            outerAlias, structColumn, tvfAliases, tvfAliasSources, tableAliases, cteSchemas);
        if (columnKind is null) return null;

        IReadOnlyList<StructFieldShape>? fields = TryResolveStructFields(columnKind);
        if (fields is null) return null;

        foreach (StructFieldShape field in fields)
        {
            if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)) continue;
            return $"**{outerAlias}.{structColumn}.{field.Name}**: `{field.Kind}`\n\n"
                + $"Source: struct field of `{outerAlias}.{structColumn}`";
        }
        return null;
    }

    /// <summary>
    /// Looks up the kind annotation for <paramref name="alias"/>.<paramref name="columnName"/>
    /// across the surfaces a column reference can come from: TVF outputs
    /// (with unnest synthesis), CTE schemas, plain-table aliases. First
    /// hit wins. Returns <see langword="null"/> when no surface knows the
    /// alias-column pair.
    /// </summary>
    private string? TryResolveAliasColumnKind(
        string alias,
        string columnName,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        Dictionary<string, string> tableAliases,
        CteSchemaResult cteSchemas)
    {
        // TVF static output schema first, then per-call synthesis (unnest).
        if (tvfAliases.TryGetValue(alias, out FunctionSignature? signature))
        {
            if (signature.OutputColumns is not null)
            {
                foreach (TableColumnEntry column in signature.OutputColumns)
                {
                    if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                        return column.Kind;
                }
            }
            if (tvfAliasSources.TryGetValue(alias, out FunctionSource? source))
            {
                string? synthesized = TryGetTvfSynthesizedColumnKind(source, columnName, _manifest, cteSchemas);
                if (synthesized is not null) return synthesized;
            }
        }

        // CTE direct name, then FROM-alias → CTE.
        if (TryResolveCteSchema(alias, cteSchemas, out _, out IReadOnlyList<TableColumnEntry>? cteColumns))
        {
            foreach (TableColumnEntry column in cteColumns)
            {
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                    return column.Kind;
            }
        }

        // Plain-table alias (or unaliased name) → manifest table.
        string resolvedTable = tableAliases.TryGetValue(alias, out string? aliased) ? aliased : alias;
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            if (!string.Equals(table.Name, resolvedTable, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (TableColumnEntry column in table.Columns)
            {
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                    return column.Kind;
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
        return $"**{qualifier}.{column.Name}**: `{column.Kind}`{nullable}\n\nSource: CTE `{source}`";
    }

    /// <summary>
    /// Renders a column hover from a TVF alias map entry. When the static
    /// manifest signature has no matching column, falls back to per-call
    /// synthesis for TVFs whose output schema depends on the arguments
    /// (today: <c>unnest</c>). Returns <see langword="null"/> when neither
    /// path produces a column.
    /// </summary>
    private string? GetTvfColumnHover(
        string alias, string columnName,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        CteSchemaResult cteSchemas)
    {
        if (!tvfAliases.TryGetValue(alias, out FunctionSignature? signature)) return null;
        if (signature.OutputColumns is not null)
        {
            foreach (TableColumnEntry column in signature.OutputColumns)
            {
                if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return FormatTvfColumnHover(alias, signature, column);
                }
            }
        }

        if (tvfAliasSources.TryGetValue(alias, out FunctionSource? source))
        {
            string? synthesizedKind = TryGetTvfSynthesizedColumnKind(source, columnName, _manifest, cteSchemas);
            if (synthesizedKind is not null)
            {
                TableColumnEntry synthesized = new() { Name = columnName, Kind = synthesizedKind, Nullable = true };
                return FormatTvfColumnHover(alias, signature, synthesized);
            }
        }
        return null;
    }

    /// <summary>
    /// Unqualified column lookup across every TVF alias in the SQL. First
    /// match wins; deterministic by enumeration order over the dictionary.
    /// Same synthesis fallback as <see cref="GetTvfColumnHover"/>.
    /// </summary>
    private string? GetTvfColumnHoverUnqualified(
        string columnName,
        Dictionary<string, FunctionSignature> tvfAliases,
        Dictionary<string, FunctionSource> tvfAliasSources,
        CteSchemaResult cteSchemas)
    {
        foreach (KeyValuePair<string, FunctionSignature> entry in tvfAliases)
        {
            if (entry.Value.OutputColumns is not null)
            {
                foreach (TableColumnEntry column in entry.Value.OutputColumns)
                {
                    if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return FormatTvfColumnHover(entry.Key, entry.Value, column);
                    }
                }
            }
            if (tvfAliasSources.TryGetValue(entry.Key, out FunctionSource? source))
            {
                string? synthesizedKind = TryGetTvfSynthesizedColumnKind(source, columnName, _manifest, cteSchemas);
                if (synthesizedKind is not null)
                {
                    TableColumnEntry synthesized = new() { Name = columnName, Kind = synthesizedKind, Nullable = true };
                    return FormatTvfColumnHover(entry.Key, entry.Value, synthesized);
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
        return $"**{alias}.{column.Name}**: `{column.Kind}`{nullable}\n\nSource: `{qualifiedFn}(...)` *(table-valued)*";
    }

    /// <summary>
    /// Synthesises the output column kind for TVFs whose schema depends on
    /// the call site. Today the only consumer is <c>unnest(array)</c> whose
    /// single <c>value</c> column carries the array element's kind — for an
    /// <c>Array&lt;Struct&lt;…&gt;&gt;</c> argument that's the inner
    /// <c>Struct&lt;…&gt;</c> annotation, which downstream
    /// <see cref="StructTypeAnnotation.TryParse"/> can crack into field
    /// shapes. Returns <see langword="null"/> when the TVF isn't a known
    /// dynamic-output shape, when the argument's kind can't be resolved
    /// from the manifest / LET / CTE context, or when the resolved kind
    /// isn't an array wrapper we can strip.
    /// </summary>
    private static string? TryGetTvfSynthesizedColumnKind(
        FunctionSource source,
        string columnName,
        LanguageServerManifest manifest,
        CteSchemaResult cteSchemas)
    {
        if (!string.Equals(source.FunctionName, "unnest", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(columnName, "value", StringComparison.OrdinalIgnoreCase)) return null;
        if (source.Arguments.Count != 1) return null;

        string? argKind = TryResolveManifestExpressionKind(source.Arguments[0], manifest, cteSchemas);
        return TryStripArrayWrapper(argKind);
    }

    /// <summary>
    /// Narrow expression-kind resolver scoped to the shapes a TVF argument
    /// commonly takes: a bare LET reference (<c>unnest(classes)</c>), a
    /// schema/CTE-qualified column reference (<c>unnest(a.classes)</c>),
    /// or a function call whose return type is declared in the manifest
    /// (<c>unnest(models.X(file))</c>). Mirrors the manifest-only paths in
    /// <see cref="CteSchemaResolver"/> without taking a dependency on its
    /// private <c>InnerScope</c> — anything more exotic (arithmetic,
    /// CASE) stays unresolved here and the caller's hover falls back to
    /// the static manifest entry.
    /// </summary>
    private static string? TryResolveManifestExpressionKind(
        Expression expression,
        LanguageServerManifest manifest,
        CteSchemaResult cteSchemas)
    {
        switch (expression)
        {
            case ColumnReference colRef when colRef.TableName is null && colRef.SchemaName is null:
                // Bare name — try LET bindings first, then unqualified CTE columns.
                if (cteSchemas.LetBindingKinds.TryGetValue(colRef.ColumnName, out string? letKind))
                    return letKind;
                foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> cte in cteSchemas.Schemas)
                {
                    foreach (TableColumnEntry column in cte.Value)
                    {
                        if (string.Equals(column.Name, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                            return column.Kind;
                    }
                }
                return null;

            case ColumnReference colRef when colRef.TableName is not null:
                // alias.column — try CTE direct, then CTE alias, then nothing.
                // (Plain-table column lookup would need the manifest's Table
                // entries; not wired here today since the user-driven shapes
                // are LET / model-return / CTE — extend if a real case needs it.)
                if (cteSchemas.Schemas.TryGetValue(colRef.TableName, out IReadOnlyList<TableColumnEntry>? direct))
                {
                    foreach (TableColumnEntry column in direct)
                    {
                        if (string.Equals(column.Name, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                            return column.Kind;
                    }
                }
                if (cteSchemas.FromAliasToCteName.TryGetValue(colRef.TableName, out string? cteName)
                    && cteSchemas.Schemas.TryGetValue(cteName, out IReadOnlyList<TableColumnEntry>? aliased))
                {
                    foreach (TableColumnEntry column in aliased)
                    {
                        if (string.Equals(column.Name, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                            return column.Kind;
                    }
                }
                return null;

            case FunctionCallExpression fnCall:
                foreach (FunctionSignature sig in manifest.Functions)
                {
                    if (!string.Equals(sig.Name, fnCall.FunctionName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (fnCall.SchemaName is not null
                        && !string.Equals(sig.SchemaName, fnCall.SchemaName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(sig.ReturnType)) return sig.ReturnType;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Strips a single <c>Array&lt;…&gt;</c> wrapper off a manifest kind
    /// annotation, returning the inner element kind. Returns
    /// <see langword="null"/> for non-array annotations and for malformed
    /// strings — the caller falls back to the static TVF schema.
    /// </summary>
    private static string? TryStripArrayWrapper(string? kind)
    {
        if (kind is null) return null;
        const string prefix = "Array<";
        if (kind.Length <= prefix.Length + 1) return null;
        if (!kind.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (kind[^1] != '>') return null;
        return kind[prefix.Length..^1].Trim();
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
    /// <summary>
    /// Walks <paramref name="sql"/>'s parsed statement list and collects
    /// every <see cref="DeclareStatement"/>'s name → declared type pair.
    /// Used by hover so a reference to a DECLAREd variable — wherever it
    /// occurs, including inside a CTE expression nested below the
    /// declaration — surfaces the variable's type. Recurses into block /
    /// if / loop / try bodies so nested DECLAREs are still found; first
    /// declaration wins on name collision (mirrors the engine's
    /// outer-scope-shadowing rule).
    /// </summary>
    private static Dictionary<string, string?> BuildDeclaredVariableMap(string sql)
    {
        Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
        ParseResult parseResult;
        try
        {
            parseResult = SqlParser.TryParseRecovering(sql);
        }
        catch
        {
            return result;
        }
        if (parseResult.Statements is null) return result;
        foreach (Statement statement in parseResult.Statements)
        {
            CollectDeclaredVariables(statement, result);
        }
        return result;
    }

    private static void CollectDeclaredVariables(
        Statement statement,
        Dictionary<string, string?> sink)
    {
        switch (statement)
        {
            case DeclareStatement decl:
                AddIfNew(sink, decl.VariableName, decl.TypeName);
                break;
            case BlockStatement block:
                foreach (Statement child in block.Statements)
                {
                    CollectDeclaredVariables(child, sink);
                }
                break;
            case IfStatement ifStmt:
                CollectDeclaredVariables(ifStmt.Then, sink);
                if (ifStmt.Else is not null) CollectDeclaredVariables(ifStmt.Else, sink);
                break;
            case WhileStatement whileStmt:
                CollectDeclaredVariables(whileStmt.Body, sink);
                break;
            case ForCounterStatement forCtr:
                // `FOR i = start TO end` introduces i in the loop scope.
                // Kind is the engine's loop-counter contract — Int32 (matches
                // ForCounterPlan's counter binding).
                AddIfNew(sink, forCtr.VariableName, "Int32");
                CollectDeclaredVariables(forCtr.Body, sink);
                break;
            case ForInStatement forIn:
                // `FOR row IN (SELECT …)` binds a struct of the source row's
                // columns. Surfacing a concrete struct annotation would
                // require resolving the source query's projected schema —
                // out of scope for this slice. Recurse for any nested
                // DECLAREs so we don't lose those.
                CollectDeclaredVariables(forIn.Body, sink);
                break;
            case TryStatement tryStmt:
                CollectDeclaredVariables(tryStmt.TryBody, sink);
                // `CATCH err` binds the exception message — always String.
                AddIfNew(sink, tryStmt.ErrorVariableName, "String");
                CollectDeclaredVariables(tryStmt.CatchBody, sink);
                if (tryStmt.FinallyBody is not null)
                    CollectDeclaredVariables(tryStmt.FinallyBody, sink);
                break;
            case CreateProcedureStatement createProc:
                // Parameter names are visible throughout the procedure body.
                // No per-body scoping yet — a flat map is fine because the
                // engine itself only allows one procedure per name at a
                // time, so cross-procedure collisions are rare in practice.
                foreach (UdfParameter p in createProc.Parameters)
                {
                    AddIfNew(sink, p.Name, p.TypeName);
                }
                CollectDeclaredVariables(createProc.Body, sink);
                break;
            case CreateFunctionStatement createFn:
                foreach (UdfParameter p in createFn.Parameters)
                {
                    AddIfNew(sink, p.Name, p.TypeName);
                }
                if (createFn.StatementBody is { } stmtBody)
                {
                    foreach (Statement child in stmtBody)
                    {
                        CollectDeclaredVariables(child, sink);
                    }
                }
                break;
        }

        static void AddIfNew(Dictionary<string, string?> sink, string name, string? kind)
        {
            if (!sink.ContainsKey(name)) sink[name] = kind;
        }
    }

    private Dictionary<string, FunctionSignature> BuildTvfAliasMap(string sql)
    {
        return BuildTvfAliasMaps(sql).Signatures;
    }

    /// <summary>
    /// Companion to <see cref="BuildTvfAliasMap"/> that also returns the per-alias
    /// <see cref="FunctionSource"/> AST node. The source carries the call's
    /// argument expressions, which the hover layer needs for output-kind
    /// synthesis on TVFs whose schema depends on the call (today: <c>unnest</c>,
    /// whose <c>value</c> column kind follows the array argument's element type).
    /// One pass, two maps — both keyed by the same alias.
    /// </summary>
    private (Dictionary<string, FunctionSignature> Signatures, Dictionary<string, FunctionSource> Sources)
        BuildTvfAliasMaps(string sql)
    {
        Dictionary<string, FunctionSignature> signatures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FunctionSource> sources = new(StringComparer.OrdinalIgnoreCase);
        ParseResult parseResult;
        try
        {
            parseResult = SqlParser.TryParseRecovering(sql);
        }
        catch
        {
            return (signatures, sources);
        }

        // Walk EVERY query reachable in the batch — a semicolon-separated
        // file holds multiple QueryStatements, and EffectiveQuery only
        // returns the first one, so the second statement's TVF sources
        // would be invisible if we stopped there.
        foreach (QueryExpression query in EnumerateAllQueries(parseResult))
        {
            foreach (FunctionSource source in EnumerateFunctionSources(query))
            {
                FunctionSignature? signature = LookupTvfSignature(source);
                if (signature is null) continue;
                string aliasKey = source.Alias ?? source.FunctionName;
                // First-wins on alias clashes — preserves the natural document
                // order so the leftmost source surfaces under shared aliases.
                if (!signatures.ContainsKey(aliasKey))
                {
                    signatures[aliasKey] = signature;
                    sources[aliasKey] = source;
                }
            }
        }

        return (signatures, sources);
    }

    /// <summary>
    /// Builds an <c>alias → table-name</c> map by walking every
    /// <see cref="TableReference"/> reachable from the parsed query. The
    /// stored table name is schema-qualified when the user wrote it that
    /// way (e.g. <c>public.users u</c> ⇒ <c>u → public.users</c>) so the
    /// downstream manifest lookup matches the catalog's storage. Only
    /// references with an explicit <c>Alias</c> contribute — the bare
    /// table name itself doesn't need aliasing.
    /// </summary>
    private static Dictionary<string, string> BuildTableAliasMap(string sql)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        ParseResult parseResult;
        try
        {
            parseResult = SqlParser.TryParseRecovering(sql);
        }
        catch
        {
            return result;
        }

        // Walk EVERY query in the batch — see BuildTvfAliasMap for the
        // rationale (EffectiveQuery only surfaces the first statement).
        foreach (QueryExpression query in EnumerateAllQueries(parseResult))
        {
            foreach (TableReference table in EnumerateTableReferences(query))
            {
                if (table.Alias is null) continue;
                string tableName = table.SchemaName is not null
                    ? $"{table.SchemaName}.{table.Name}"
                    : table.Name;
                // First-wins on alias clash — matches BuildTvfAliasMap.
                if (!result.ContainsKey(table.Alias)) result[table.Alias] = tableName;
            }
        }

        return result;
    }

    /// <summary>
    /// Yields every <see cref="QueryExpression"/> reachable from
    /// <paramref name="parseResult"/> — both the bare-query case and every
    /// <see cref="QueryStatement"/> inside a semicolon-separated batch.
    /// Lets per-hover analyses (alias maps, schema resolution) see all
    /// statements in the file rather than just the first one
    /// <see cref="ParseResult.EffectiveQuery"/> returns.
    /// </summary>
    private static IEnumerable<QueryExpression> EnumerateAllQueries(ParseResult parseResult)
    {
        // Statements supersedes Query when both are present — for a
        // single-query batch ParseResult populates both, and yielding from
        // Statements alone covers the multi-statement case too.
        if (parseResult.Statements is not null)
        {
            foreach (Statement statement in parseResult.Statements)
            {
                if (statement is QueryStatement qs) yield return qs.Query;
            }
            yield break;
        }
        if (parseResult.Query is not null)
        {
            yield return parseResult.Query;
        }
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
    /// Yields every <see cref="TableReference"/> reachable from
    /// <paramref name="query"/>. Mirrors <see cref="EnumerateFunctionSources"/>
    /// but targets plain table sources rather than TVF calls — used to
    /// build the alias map so a bare alias hover resolves to its
    /// underlying table.
    /// </summary>
    private static IEnumerable<TableReference> EnumerateTableReferences(QueryExpression query)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                foreach (TableReference t in EnumerateTableReferencesInStatement(select.Statement))
                    yield return t;
                break;
            case CompoundQueryExpression compound:
                foreach (TableReference t in EnumerateTableReferences(compound.Left))
                    yield return t;
                foreach (TableReference t in EnumerateTableReferences(compound.Right))
                    yield return t;
                break;
        }
    }

    private static IEnumerable<TableReference> EnumerateTableReferencesInStatement(SelectStatement statement)
    {
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                foreach (TableReference t in EnumerateTableReferences(cte.Body))
                    yield return t;
                if (cte.RecursiveQuery is not null)
                {
                    foreach (TableReference t in EnumerateTableReferencesInStatement(cte.RecursiveQuery))
                        yield return t;
                }
            }
        }

        if (statement.From is not null)
        {
            foreach (TableReference t in EnumerateTableReferencesInSource(statement.From.Source))
                yield return t;
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                foreach (TableReference t in EnumerateTableReferencesInSource(join.Source))
                    yield return t;
            }
        }
    }

    private static IEnumerable<TableReference> EnumerateTableReferencesInSource(TableSource source)
    {
        switch (source)
        {
            case TableReference tr:
                yield return tr;
                break;
            case SubquerySource sub:
                foreach (TableReference inner in EnumerateTableReferencesInStatement(sub.Query))
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
                return $"{p.Name}: `{p.Kind}`{optional}";
            }));
        string returnInfo = entry.ReturnType is not null ? $" → `{entry.ReturnType}`" : "";
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
                return $"{p.Name}: `{p.Kind}`{optional}";
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

        string qualifiedName = string.IsNullOrEmpty(function.SchemaName)
            || string.Equals(function.SchemaName, "system", StringComparison.OrdinalIgnoreCase)
            ? function.Name
            : $"{function.SchemaName}.{function.Name}";
        string returnInfo = !string.IsNullOrEmpty(function.ReturnType) ? $" → `{function.ReturnType}`" : "";

        string signature = $"**{qualifiedName}**({FormatHoverParameters(function.Parameters)}){returnInfo}";

        // Append every additional overload shape on its own bold line so a
        // reader hovering image_draw_bounding_boxes sees both the
        // Array<Struct> and the single-Struct call shapes — not just the
        // primary the signature-help picker happened to surface.
        if (function.AdditionalParameterShapes is { Count: > 0 } extras)
        {
            System.Text.StringBuilder sb = new(signature);
            foreach (IReadOnlyList<ParameterSignature> variant in extras)
            {
                sb.Append("  \n");
                sb.Append("**").Append(qualifiedName).Append("**(");
                sb.Append(FormatHoverParameters(variant));
                sb.Append(')').Append(returnInfo);
            }
            signature = sb.ToString();
        }

        if (function.IsTableValued)
        {
            signature = $"*(table-valued)* {signature}";
        }

        string categoryLine = $"*Category: {function.Category}*";

        // Append a drift hint for `models.X` calls where the on-disk
        // active version trails the catalog's newest declared cut. The
        // model surface dual-registers under FunctionRegistry, so this
        // is the natural hook — readers hovering an identifier see the
        // update nudge without us routing through a parallel model-
        // specific hover path. Warn-only: never blocks, never alters
        // the signature.
        string? driftLine = TryGetModelDriftLine(explicitSchema, name);

        string body = function.Description is not null
            ? $"{signature}\n\n{categoryLine}\n\n{function.Description}"
            : $"{signature}\n\n{categoryLine}";
        return driftLine is not null ? $"{body}\n\n{driftLine}" : body;
    }

    // Returns a markdown line like
    // `*Active: 2026-04-15* · [Update to 2026-05-29 available](command:datum.openModelInTab?"depth-anything-v3-large")`
    // when the schema-qualified call is `models.<name>` and the matching
    // <see cref="ModelEntry"/> has an active on-disk version that trails
    // its catalog-declared latest. Returns null for any other schema, for
    // engine-only builtins (no catalog ownership, both version fields
    // null), and for installed-and-current entries.
    //
    // The link uses the <c>command:</c> URI scheme; Monaco renders it as
    // clickable only when the hover content is marked
    // <c>isTrusted: true</c> on the client. The host registers
    // <c>datum.openModelInTab</c> to focus the Models tab and select
    // the catalog entry passed as the single string argument.
    private string? TryGetModelDriftLine(string? explicitSchema, string name)
    {
        if (explicitSchema is null) return null;
        if (!string.Equals(explicitSchema, "models", StringComparison.OrdinalIgnoreCase)) return null;
        if (_manifest.Models is null) return null;
        foreach (ModelEntry entry in _manifest.Models)
        {
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.ActiveVersion is null || entry.LatestVersion is null) return null;
            if (string.Equals(entry.ActiveVersion, entry.LatestVersion, StringComparison.Ordinal)) return null;

            string activeLine = $"*Active: {entry.ActiveVersion}*";
            string updateLabel = $"Update to {entry.LatestVersion} available";
            if (entry.CatalogEntryId is null)
            {
                // No parent catalog entry — the link target is unknown,
                // so render plain text. Only happens for engine-only
                // builtins, which can't drift anyway, but defensive.
                return $"{activeLine} · *{updateLabel}*";
            }
            string encodedId = Uri.EscapeDataString($"\"{entry.CatalogEntryId}\"");
            return $"{activeLine} · [{updateLabel}](command:datum.openModelInTab?{encodedId})";
        }
        return null;
    }

    /// <summary>
    /// Renders a parameter list as a comma-separated markdown fragment —
    /// <c>name: `Kind`</c> for required, <c>name: `Kind`?</c> for optional.
    /// Factored out so the primary shape and every
    /// <see cref="FunctionSignature.AdditionalParameterShapes"/> variant
    /// share one renderer.
    /// </summary>
    private static string FormatHoverParameters(IReadOnlyList<ParameterSignature> parameters)
    {
        return string.Join(", ", parameters.Select(parameter =>
        {
            string optional = parameter.IsOptional ? "?" : "";
            return $"{parameter.Name}: `{parameter.Kind}`{optional}";
        }));
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

        // Pair the table hit (if any) with a dataset row so installed
        // datasets render with their entry / version / size context
        // rather than the bare column list. Discovered datasets have no
        // mounted provider, so the dataset row is the sole source.
        DatasetEntry? dataset = FindDataset(name);

        if (table is null && dataset is null)
        {
            return null;
        }

        if (dataset is not null)
        {
            return BuildDatasetHover(dataset, table);
        }

        string label = string.Equals(table!.Kind, "VIEW", StringComparison.OrdinalIgnoreCase)
            ? "View"
            : "Table";
        string header = $"**{label}: {table.Name}** ({table.Columns.Count} columns)\n\n";
        string columns = string.Join("\n", table.Columns.Select(column =>
        {
            string nullable = column.Nullable ? " *(nullable)*" : "";
            return $"- `{column.Name}`: `{column.Kind}`{nullable}";
        }));

        return header + columns;
    }

    // Resolves a hover target against the dataset manifest, accepting
    // either the fully-qualified `<schema>.<name>` form or a bare name
    // (in which case the dataset's declared schema must match by
    // search-path walk). Returns null when nothing in
    // <see cref="LanguageServerManifest.Datasets"/> matches.
    private DatasetEntry? FindDataset(string name)
    {
        if (_manifest.Datasets is null) return null;

        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            string schemaPart = name[..dot];
            string namePart = name[(dot + 1)..];
            foreach (DatasetEntry d in _manifest.Datasets)
            {
                if (string.Equals(d.Schema, schemaPart, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Name, namePart, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
            return null;
        }

        // Bare name — walk the search path. Same precedence as table
        // resolution, so the user's `SET search_path` survives the
        // dataset hover too.
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (DatasetEntry d in _manifest.Datasets)
            {
                if (string.Equals(d.Schema, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
        }
        return null;
    }

    private static string BuildDatasetHover(DatasetEntry dataset, TableSchemaEntry? table)
    {
        string statusBadge = dataset.Status == DatasetInstallStatus.Discovered
            ? " · *installable*"
            : "";
        string columnCount = table is not null ? $" ({table.Columns.Count} columns)" : "";
        string header = $"**Dataset: {dataset.Schema}.{dataset.Name}**{columnCount}{statusBadge}\n\n";
        string subtitle = $"*{dataset.EntryName} — {dataset.DisplayName} · v{dataset.Version}*\n\n";

        List<string> meta = [];
        if (dataset.Modalities.Count > 0)
        {
            meta.Add($"**Modalities:** {string.Join(", ", dataset.Modalities)}");
        }
        if (dataset.ApproxArchiveBytes > 0)
        {
            meta.Add($"**Download:** ~{FormatBytes(dataset.ApproxArchiveBytes)}");
        }
        if (dataset.ApproxIngestedBytes > 0)
        {
            meta.Add($"**Ingested:** ~{FormatBytes(dataset.ApproxIngestedBytes)}");
        }
        if (dataset.LicenseIds.Count > 0)
        {
            meta.Add($"**License:** {string.Join(", ", dataset.LicenseIds)}");
        }
        string metaBlock = meta.Count > 0 ? string.Join("\n\n", meta) + "\n\n" : "";

        string columns = "";
        if (table is not null && table.Columns.Count > 0)
        {
            columns = "---\n\n" + string.Join("\n", table.Columns.Select(column =>
            {
                string nullable = column.Nullable ? " *(nullable)*" : "";
                return $"- `{column.Name}`: `{column.Kind}`{nullable}";
            }));
        }

        return header + subtitle + metaBlock + columns;
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / (double)GB:F1} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F0} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F0} KB";
        return $"{bytes} B";
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
                return $"**{column.Name}**: `{column.Kind}`{nullable}\n\nSource: {table.Name}";
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
        return $"**{tableQualifier}.{column.Name}**: `{column.Kind}`{nullable}";
    }

    /// <summary>
    /// Hover for a numeric literal token. Reports the narrowest kind the
    /// engine's literal parser would resolve to so what hover shows
    /// matches what the value carries at execution time. Mirrors
    /// <c>SqlParser.ParseNumericLiteral</c>'s narrowing ladder.
    /// </summary>
    private static string GetNumberLiteralHover(string text)
    {
        string kind = ClassifyNumericLiteralKind(text);
        return $"**Numeric literal** `{text}`: `{kind}`";
    }

    private static string ClassifyNumericLiteralKind(string text)
    {
        bool fractional = text.IndexOf('.') >= 0
            || text.IndexOf('e') >= 0
            || text.IndexOf('E') >= 0;

        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.NumberStyles style = System.Globalization.NumberStyles.None;

        if (fractional)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, culture, out double d))
            {
                return "?";
            }
            // Whole-valued fractional literal (1.0, 1e3) narrows through
            // the integer ladder — same shape as the engine's parser.
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (!double.IsInfinity(d) && d == System.Math.Truncate(d)
                && d >= long.MinValue && d <= long.MaxValue)
            {
                return NarrowSignedIntKind((long)d);
            }
            float f = (float)d;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return (double)f == d ? "Float32" : "Float64";
        }

        if (sbyte.TryParse(text, style, culture, out _)) return "Int8";
        if (short.TryParse(text, style, culture, out _)) return "Int16";
        if (int.TryParse(text, style, culture, out _)) return "Int32";
        if (long.TryParse(text, style, culture, out _)) return "Int64";
        if (ulong.TryParse(text, style, culture, out _)) return "UInt64";
        if (Int128.TryParse(text, style, culture, out _)) return "Int128";
        if (UInt128.TryParse(text, style, culture, out _)) return "UInt128";
        return "?";
    }

    private static string NarrowSignedIntKind(long value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue) return "Int8";
        if (value >= short.MinValue && value <= short.MaxValue) return "Int16";
        if (value >= int.MinValue && value <= int.MaxValue) return "Int32";
        return "Int64";
    }

    /// <summary>
    /// Hover for a string-literal token. Renders kind and length;
    /// previews the value when short enough to fit comfortably in the
    /// popup. Token text retains its surrounding quotes — strip them so
    /// the preview shows the literal payload.
    /// </summary>
    private static string GetStringLiteralHover(string text)
    {
        string payload = text;
        if (payload.Length >= 2 && payload[0] == '\'' && payload[^1] == '\'')
        {
            payload = payload[1..^1];
        }
        const int maxPreview = 60;
        string preview = payload.Length <= maxPreview
            ? payload
            : payload[..maxPreview] + "…";
        return $"**String literal**: `String` ({payload.Length} chars)\n\n`'{preview}'`";
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
    /// action when the host registers a <c>datumv.openDoc</c> command and marks
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
        // The host registers a 'datumv.openDoc' command to handle navigation.
        string encodedKey = Uri.EscapeDataString($"\"{sectionKey}\"");
        markdown += $"\n\n[See more](command:datumv.openDoc?{encodedKey})";
        return markdown;
    }

    /// <summary>
    /// Tokenizes the full SQL, capturing position and text span for each token.
    /// Returns an empty list on failure.
    /// </summary>
    private static List<TokenHit> TokenizeWithSpans(string sql)
    {
        if (TryTokenize(sql, out List<TokenHit>? primary))
        {
            return primary;
        }

        // Template / splice repair fallback: when the user is mid-typing
        // inside an unterminated `${…}` splice or backtick template, the
        // raw tokenizer fails on the whole input and we'd lose hover for
        // everything in the file. The repair appends the minimal close
        // sequence so tokenization succeeds; the appended characters sit
        // past the user's cursor and never produce a hit. The unrepaired
        // text is what diagnostics see, so the "unterminated" warning
        // still surfaces in the editor independently.
        string? repairSuffix = TokenizeRepair.ComputeRepairSuffix(sql);
        if (repairSuffix is not null
            && TryTokenize(sql + repairSuffix, out List<TokenHit>? repaired))
        {
            return repaired;
        }

        return new List<TokenHit>();

        static bool TryTokenize(
            string input,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<TokenHit>? tokens)
        {
            try
            {
                TokenList<SqlToken> raw = SqlTokenizer.Instance.Tokenize(input);
                List<TokenHit> collected = new();
                foreach (Token<SqlToken> token in raw)
                {
                    // Superpower's Position is 1-based for Line/Column
                    // and 0-based for Absolute. We need 0-based
                    // Line/Column for the hover range Monaco paints, and
                    // 0-based Absolute to match the cursor offset.
                    int line = token.Position.Line - 1;
                    int column = token.Position.Column - 1;
                    int absolute = token.Position.Absolute;
                    collected.Add(new TokenHit(
                        token.Kind, token.ToStringValue(), line, column, absolute));
                }
                tokens = collected;
                return true;
            }
            catch
            {
                tokens = null;
                return false;
            }
        }
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
            ["system"] = "Heliosoph.DatumV-specific metadata schema exposing providers, functions, statistics, indexes, and interactions.",
        };

    private static readonly Dictionary<string, Dictionary<string, string>> VirtualTableDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["information_schema"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["tables"] = "Lists all tables (BASE TABLE, TEMPORARY TABLE) with their schema assignment.",
                ["columns"] = "Lists all columns of all tables with ordinal position, data type, and nullability.",
                ["schemata"] = "Lists the known schema namespaces (public, temp, information_schema, system).",
            },
            ["system"] = new(StringComparer.OrdinalIgnoreCase)
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
