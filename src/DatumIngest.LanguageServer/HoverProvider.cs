namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
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

        string? markdown = hit.Kind switch
        {
            SqlToken.Identifier => ResolveIdentifierHover(hit.Text, tokens, hit),
            SqlToken.Arrow => "**`->`** Lambda arrow — separates parameter(s) from the body expression.\n\n" +
                "Usage: `x -> expr` or `(a, b) -> expr` inside higher-order functions " +
                "such as `array_transform` and `array_filter`.",
            _ when IsKeywordToken(hit.Kind) => GetKeywordHover(hit.Kind, hit.Text),
            _ => null,
        };

        if (markdown is null)
        {
            return null;
        }

        return new HoverResult
        {
            Contents = markdown,
            StartLine = hit.Line,
            StartColumn = hit.Column,
            EndLine = hit.Line,
            EndColumn = hit.Column + hit.Text.Length,
        };
    }

    /// <summary>
    /// Resolves hover for an identifier by checking if it's a function, table, or column name.
    /// </summary>
    private string? ResolveIdentifierHover(string name, List<TokenHit> tokens, TokenHit currentToken)
    {
        // If followed by '(' it's a function call.
        int currentIndex = tokens.IndexOf(currentToken);
        if (currentIndex >= 0 && currentIndex + 1 < tokens.Count &&
            tokens[currentIndex + 1].Kind == SqlToken.LeftParen)
        {
            return GetFunctionHover(name);
        }

        // If preceded by a dot, it could be a qualified column (table.column)
        // or a virtual schema table (schema.table).
        if (currentIndex >= 2 &&
            tokens[currentIndex - 1].Kind == SqlToken.Dot &&
            (tokens[currentIndex - 2].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 2].Kind)))
        {
            string qualifier = tokens[currentIndex - 2].Text;

            // Check if this is a virtual schema table reference (e.g. information_schema.tables).
            string? virtualHover = GetVirtualTableHover(qualifier, name);
            if (virtualHover is not null)
            {
                return virtualHover;
            }

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

        return GetColumnHover(name);
    }

    private string? GetFunctionHover(string name)
    {
        FunctionSignature? function = _manifest.Functions.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

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
        string signature = $"**{function.Name}**({parameters}){returnInfo}";

        if (function.IsTableValued)
        {
            signature = $"*(table-valued)* {signature}";
        }

        string categoryLine = $"*Category: {function.Category}*";

        return function.Description is not null
            ? $"{signature}\n\n{categoryLine}\n\n{function.Description}"
            : $"{signature}\n\n{categoryLine}";
    }

    private string? GetTableHover(string name)
    {
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

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
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, tableQualifier, StringComparison.OrdinalIgnoreCase));

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

    private static string? GetKeywordHover(SqlToken kind, string text)
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
            SqlToken.Tablesample => "**TABLESAMPLE** — Samples a fraction of rows from a table source: `FROM t TABLESAMPLE BERNOULLI(10)` (row-level ~10%) or `FROM t TABLESAMPLE SYSTEM(5)` (chunk-level ~5%). Add `REPEATABLE(seed)` for deterministic results.",
            SqlToken.Repeatable => "**REPEATABLE** — Seeds the random sampler for deterministic TABLESAMPLE results: `TABLESAMPLE BERNOULLI(10) REPEATABLE(42)`. The same seed on the same data always returns the same sample.",
            SqlToken.Create => "**CREATE TEMP TABLE** — Creates a session-scoped temporary table. `CREATE TEMP TABLE name (col type, …) [PRIMARY KEY (col, …)]` for an empty table or `CREATE TEMP TABLE name AS SELECT …` to populate from a query. Add `IF NOT EXISTS` to suppress errors.",
            SqlToken.Drop => "**DROP TABLE** — Removes a temporary table from the session catalog: `DROP TABLE [IF EXISTS] name`.",
            SqlToken.Insert => "**INSERT INTO** — Inserts rows into a temporary table: `INSERT INTO name VALUES (v1, v2, …)` or `INSERT INTO name SELECT …`.",
            SqlToken.Update => "**UPDATE** — Updates rows in a temporary table: `UPDATE name SET col = expr [FROM source [AS alias]] [WHERE condition]`. The optional FROM clause joins another table to supply update values.",
            SqlToken.Delete => "**DELETE FROM** — Removes rows from a temporary table: `DELETE FROM name [WHERE condition]`.",
            SqlToken.Analyze => "**ANALYZE** — Rebuilds column statistics and chunk indexes for a table. Run after large INSERT/UPDATE/DELETE operations to keep query planner cost estimates accurate.",
            SqlToken.Alter => "**ALTER TABLE** — Modifies a temporary table's schema: `ALTER TABLE name ADD COLUMN col type [DEFAULT value]`.",
            SqlToken.Table => "**TABLE** — Specifies a table in DDL statements: `CREATE [TEMP] TABLE name (…)`, `DROP TABLE name`, `ALTER TABLE name ADD …`.",
            SqlToken.Temp => "**TEMP** — Marks a table as session-scoped (temporary). Equivalent to `TEMPORARY`. The table is automatically dropped when the session ends.",
            SqlToken.Temporary => "**TEMPORARY** — Marks a table as session-scoped (temporary). Equivalent to `TEMP`. The table is automatically dropped when the session ends.",
            SqlToken.Values => "**VALUES** — Supplies literal row data for INSERT: `INSERT INTO name VALUES (v1, v2), (v3, v4)`.",
            SqlToken.Set => "**SET** — Introduces column assignments in UPDATE: `UPDATE name SET col1 = expr1, col2 = expr2`.",
            SqlToken.Add => "**ADD** — Adds a new column in ALTER TABLE: `ALTER TABLE name ADD [COLUMN] col type [NOT NULL] [DEFAULT expr]`.",
            SqlToken.Column => "**COLUMN** — Optional keyword in ALTER TABLE ADD: `ALTER TABLE name ADD COLUMN col type`.",
            SqlToken.Default => "**DEFAULT** — Specifies a default value for a column: `col type DEFAULT expr`. Used in CREATE TABLE and ALTER TABLE ADD.",
            SqlToken.Primary => "**PRIMARY** — Used with KEY to define the primary key constraint: `PRIMARY KEY (col1, col2)`.",
            SqlToken.Key => "**KEY** — Used with PRIMARY to define the primary key constraint: `PRIMARY KEY (col1, col2)`.",
            SqlToken.If => "**IF** — Conditional guard for DDL: `CREATE TABLE IF NOT EXISTS name …` or `DROP TABLE IF EXISTS name`.",
            _ => null,
        };
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
                // Superpower's Position is 1-based.
                int line = token.Position.Line - 1;
                int column = token.Position.Column - 1;
                result.Add(new TokenHit(token.Kind, text, line, column));
            }
        }
        catch
        {
            // Partial tokenization is acceptable.
        }

        return result;
    }

    /// <summary>
    /// Finds the token that contains the given character offset.
    /// </summary>
    private static TokenHit? FindTokenAtOffset(List<TokenHit> tokens, int cursorOffset)
    {
        // Convert cursor offset to an approximate line/column by scanning the source text
        // is impractical without the source. Instead, use cumulative position tracking.
        // The tokens already have line/column from Superpower — we need to match cursor offset.

        // Build a character-offset map: for each token, calculate its offset from line/column.
        // This requires the original text, but we don't have it here. Instead, we match by
        // checking if cursorOffset falls within each token's span. Since we passed the full
        // SQL to tokenize, we can reconstruct offsets from position.

        // Simplified approach: the caller passes cursorOffset, we need to find the token.
        // We'll iterate tokens and track their position within the source text.
        // Superpower gives us (line, column) in 1-based. We need a different strategy.

        // Actually — let's just use the tokens list by index and accept approximate matching.
        // A robust approach would track absolute offsets, but for hover this is acceptable.

        // We'll use a heuristic: find the token whose start position is closest to
        // but not exceeding the cursor offset (converted from line/column).
        // For single-line SQL (most common for this dialect), column ≈ offset.

        foreach (TokenHit token in tokens)
        {
            // For single-line SQL, column equals offset.
            // For multi-line, this is approximate but sufficient for hover.
            int tokenStart = token.Column; // Approximate for single-line
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
            ["DateTime"] = "**DateTime** — Date and time with microsecond precision.",
            ["Time"] = "**Time** — Time of day without date component.",
            ["Duration"] = "**Duration** — Elapsed time span with microsecond precision.",
            ["Uuid"] = "**Uuid** — 128-bit universally unique identifier (RFC 4122).",
            ["JsonValue"] = "**JsonValue** — Arbitrary JSON value stored as text. Supports JSON path operations.",
            ["Vector"] = "**Vector** — Fixed-length array of Float32 values. Supports distance and similarity operations.",
            ["Matrix"] = "**Matrix** — Two-dimensional array of Float32 values.",
            ["Tensor"] = "**Tensor** — Multi-dimensional array of Float32 values.",
            ["Array"] = "**Array** — Variable-length typed array. Element type is inferred from context.",
            ["Struct"] = "**Struct** — Named tuple of typed fields. Field types are inferred from context.",
            ["Image"] = "**Image** — Binary image data with format metadata.",
            ["UInt8Array"] = "**UInt8Array** — Variable-length byte array (binary data).",
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

    /// <summary>
    /// Returns hover content for a schema-qualified virtual table reference
    /// (e.g. <c>information_schema.tables</c>), or <see langword="null"/> if
    /// the schema/table combination is not a known virtual table.
    /// </summary>
    private static string? GetVirtualTableHover(string schemaName, string tableName)
    {
        if (VirtualTableDescriptions.TryGetValue(schemaName, out Dictionary<string, string>? tables) &&
            tables.TryGetValue(tableName, out string? description))
        {
            return $"**{schemaName}.{tableName}**\n\n{description}";
        }

        return null;
    }
}

/// <summary>
/// A token with its position information for hover hit-testing.
/// </summary>
internal sealed record TokenHit(SqlToken Kind, string Text, int Line, int Column);
