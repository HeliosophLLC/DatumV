using System.Diagnostics;
using System.Text;
using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;
using DatumIngest.Model;
using RadLine;
using Spectre.Console;

namespace DatumIngest.Shell;

/// <summary>
/// Interactive REPL over a <see cref="TableCatalog"/>. Accepts multi-line SQL
/// terminated by <c>;</c>, plus <c>EXPLAIN [ANALYZE] &lt;sql&gt;;</c> and the
/// dot-commands <c>.help</c>, <c>.quit</c>, <c>.exit</c>. Ctrl+C cancels the
/// running query without exiting the shell.
/// </summary>
internal sealed class InteractiveShell
{
    /// <summary>Number of rows shown per page before prompting to continue.</summary>
    private const int PageSize = 1000;

    private readonly TableCatalog _catalog;
    private readonly LanguageService _languageService;
    private CancellationTokenSource? _activeQueryCts;
    private bool _timerEnabled;

    public InteractiveShell(TableCatalog catalog)
    {
        _catalog = catalog;

        // Build a manifest snapshot from the live catalog and seed the language
        // server. The manifest covers every currently-registered table's schema
        // plus every function name in the catalog's registry — enough for the
        // completion provider to suggest tables, columns, functions, and
        // keywords. AddFile after this point won't be reflected until the
        // shell rebuilds the manifest (out of scope here).
        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        _languageService = new LanguageService();
        _languageService.Initialize(manifest);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        PrintBanner();

        if (!LineEditor.IsSupported(AnsiConsole.Console))
        {
            AnsiConsole.MarkupLine("[yellow]Warning: terminal does not fully support RadLine. Falling back to basic input.[/]");
        }

        LineEditor editor = new()
        {
            Prompt = new LineEditorPrompt("[green]datum>[/]"),
            MultiLine = false,
            Highlighter = new ShellHighlighter(),
            Completion = new SqlCompletion(_languageService),
        };

        // Tab is the default completion key (bound by RadLine), but VS Code's
        // integrated terminal swallows Tab for focus traversal. Bind Ctrl+Space
        // as a fallback so completions still trigger inside the IDE.
        editor.KeyBindings.Add(ConsoleKey.Spacebar, ConsoleModifiers.Control,
            () => new AutoCompleteCommand(AutoComplete.Next));

        // Single Ctrl+C handler: cancels the active query if one is running.
        // RadLine handles Ctrl+C at the prompt itself (clears buffer / returns null).
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            CancellationTokenSource? cts = _activeQueryCts;
            if (cts is not null)
            {
                cts.Cancel();
                e.Cancel = true;
            }
        };
        Console.CancelKeyPress += cancelHandler;

        StringBuilder inputBuffer = new();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await editor.ReadLine(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    if (inputBuffer.Length > 0)
                    {
                        inputBuffer.Clear();
                        AnsiConsole.WriteLine();
                        continue;
                    }
                    break;
                }

                string trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) && inputBuffer.Length == 0)
                {
                    continue;
                }

                if (inputBuffer.Length == 0 && trimmed.StartsWith('.'))
                {
                    if (trimmed.Equals(".quit", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals(".exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (trimmed.Equals(".help", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintHelp();
                        continue;
                    }

                    if (trimmed.Equals(".timer", StringComparison.OrdinalIgnoreCase))
                    {
                        _timerEnabled = !_timerEnabled;
                        AnsiConsole.MarkupLine(_timerEnabled
                            ? "[green]Timer on.[/]"
                            : "[yellow]Timer off.[/]");
                        continue;
                    }

                    if (trimmed.Equals(".tables", StringComparison.OrdinalIgnoreCase))
                    {
                        ListTables();
                        continue;
                    }

                    AnsiConsole.MarkupLine($"[red]Unknown command: {Markup.Escape(trimmed)}[/]");
                    continue;
                }

                if (inputBuffer.Length > 0)
                {
                    inputBuffer.Append(' ');
                }
                inputBuffer.Append(trimmed);

                if (!trimmed.EndsWith(';'))
                {
                    continue;
                }

                string sql = inputBuffer.ToString();
                inputBuffer.Clear();

                // SqlParser rejects trailing `;`; strip it before planning.
                string sqlCore = sql.TrimEnd().TrimEnd(';').TrimEnd();
                if (sqlCore.Length == 0)
                {
                    continue;
                }

                bool isExplain = sqlCore.StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase)
                              || sqlCore.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase);

                if (isExplain)
                {
                    string explainBody = sqlCore.Length > "EXPLAIN".Length
                        ? sqlCore["EXPLAIN".Length..].TrimStart()
                        : "";
                    bool analyze = explainBody.StartsWith("ANALYZE ", StringComparison.OrdinalIgnoreCase)
                                || explainBody.Equals("ANALYZE", StringComparison.OrdinalIgnoreCase);
                    if (analyze)
                    {
                        explainBody = explainBody.Length > "ANALYZE".Length
                            ? explainBody["ANALYZE".Length..].TrimStart()
                            : "";
                    }

                    if (explainBody.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]EXPLAIN requires a SQL statement.[/]");
                        continue;
                    }

                    await ExplainAsync(explainBody, analyze).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteAsync(sqlCore).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
        return 0;
    }

    private async Task ExecuteAsync(string sql)
    {
        IQueryPlan plan;
        try
        {
            plan = _catalog.Plan(sql);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        using CancellationTokenSource cts = new();
        _activeQueryCts = cts;
        Stopwatch? sw = _timerEnabled ? Stopwatch.StartNew() : null;
        try
        {
            await PaginateAsync(plan, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow](query cancelled)[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            _activeQueryCts = null;
            ReportElapsed(sw);
        }
    }

    /// <summary>
    /// Drives <see cref="IQueryPlan.ExecuteAsync"/> page by page, prompting the
    /// user between pages when more rows remain. Buffers up to <see cref="PageSize"/>
    /// rows of formatted cell strings per page, so the per-page <see cref="TableFormatter"/>
    /// has the data it needs to compute column widths.
    /// </summary>
    private async Task PaginateAsync(IQueryPlan plan, CancellationToken cancellationToken)
    {
        SidecarRegistry registry = _catalog.SidecarRegistry;

        IAsyncEnumerator<RowBatch> enumerator = plan.ExecuteAsync(cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                AnsiConsole.MarkupLine("[grey](0 rows)[/]");
                return;
            }

            Schema schema = DeriveSchema(enumerator.Current);
            int columnCount = schema.Columns.Count;
            long totalRows = 0;
            bool hasMore = true;
            int rowOffsetInBatch = 0;
            bool headerPrinted = false;

            while (hasMore)
            {
                List<string[]> page = new(PageSize);

                while (page.Count < PageSize && hasMore)
                {
                    RowBatch batch = enumerator.Current;
                    Arena arena = batch.Arena;
                    int i = rowOffsetInBatch;
                    while (i < batch.Count && page.Count < PageSize)
                    {
                        Row row = batch[i];
                        string[] cells = new string[columnCount];
                        for (int c = 0; c < columnCount; c++)
                        {
                            DataValue value = row[c];
                            cells[c] = value.IsNull
                                ? "NULL"
                                : TableFormatter.FormatValue(value, arena, registry, schema.Columns[c].Fields);
                        }
                        page.Add(cells);
                        i++;
                    }
                    rowOffsetInBatch = i;

                    if (rowOffsetInBatch == batch.Count)
                    {
                        hasMore = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        rowOffsetInBatch = 0;
                    }
                }

                totalRows += page.Count;
                TableFormatter.RenderPage(page, schema, printHeader: !headerPrinted, Console.Out);
                headerPrinted = true;

                if (!hasMore) break;

                if (!PromptContinue())
                {
                    AnsiConsole.MarkupLine($"[grey](stopped after {totalRows:N0} rows)[/]");
                    return;
                }
            }

            AnsiConsole.MarkupLine($"[grey]({totalRows:N0} rows)[/]");
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocks for a single keystroke between pages. Enter/Space continues to the
    /// next page; Esc/Q stops. Other keys are ignored.
    /// </summary>
    private static bool PromptContinue()
    {
        AnsiConsole.Markup("[grey]-- more — Enter to continue, q/Esc to stop --[/]");
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                    case ConsoleKey.Spacebar:
                        AnsiConsole.WriteLine();
                        return true;
                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        AnsiConsole.WriteLine();
                        return false;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Stdin redirected — no interactive prompt available; keep going.
            AnsiConsole.WriteLine();
            return true;
        }
    }

    private void ReportElapsed(Stopwatch? stopwatch)
    {
        if (stopwatch is null) return;
        stopwatch.Stop();
        AnsiConsole.MarkupLine($"[grey]elapsed: {stopwatch.Elapsed.TotalMilliseconds:F1} ms[/]");
    }

    private void ListTables()
    {
        List<(string Name, long Rows)> entries = new();
        foreach (ITableProvider provider in _catalog)
        {
            long rows;
            try { rows = provider.GetRowCount(); }
            catch { rows = -1; }
            entries.Add((provider.Name, rows));
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no tables registered)[/]");
            return;
        }

        int nameWidth = Math.Max(5, entries.Max(e => e.Name.Length));
        foreach ((string name, long rows) in entries)
        {
            string rowDisplay = rows >= 0 ? $"{rows:N0}" : "?";
            AnsiConsole.WriteLine($"  {name.PadRight(nameWidth)}   rows: {rowDisplay}");
        }
        AnsiConsole.MarkupLine($"[grey]({entries.Count} {(entries.Count == 1 ? "table" : "tables")})[/]");
    }

    private async Task ExplainAsync(string sql, bool analyze)
    {
        IQueryPlan plan;
        try
        {
            plan = _catalog.Plan(sql);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        Stopwatch? sw = _timerEnabled ? Stopwatch.StartNew() : null;
        ExplainPlanNode tree;
        if (analyze)
        {
            using CancellationTokenSource cts = new();
            _activeQueryCts = cts;
            try
            {
                tree = await plan.AnalyzeAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow](analyze cancelled)[/]");
                return;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return;
            }
            finally
            {
                _activeQueryCts = null;
            }
        }
        else
        {
            try
            {
                tree = plan.ExplainTree;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return;
            }
        }

        RenderExplainPlan(tree);
        ReportElapsed(sw);
    }

    private static Schema DeriveSchema(RowBatch batch)
    {
        IReadOnlyList<string> names = batch.ColumnLookup.ColumnNames;

        // Use the first row to read each column's DataKind. If the batch is empty
        // (e.g. all rows filtered out before yielding), fall back to Unknown so we
        // can still print headers.
        ColumnInfo[] columns = new ColumnInfo[names.Count];
        if (batch.Count > 0)
        {
            Row row = batch[0];
            for (int i = 0; i < names.Count; i++)
            {
                columns[i] = new ColumnInfo(names[i], row[i].Kind, true);
            }
        }
        else
        {
            for (int i = 0; i < names.Count; i++)
            {
                columns[i] = new ColumnInfo(names[i], DataKind.Unknown, true);
            }
        }
        return new Schema(columns);
    }

    private static void PrintBanner()
    {
        AnsiConsole.MarkupLine("[bold blue]DatumIngest Shell[/]");
        AnsiConsole.MarkupLine("[grey]Type SQL (end with ;) or .help for commands.[/]");
        AnsiConsole.WriteLine();
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  [green]<sql>;[/]                       Execute a SQL statement");
        AnsiConsole.MarkupLine("  [green]EXPLAIN <sql>;[/]               Show the static query plan");
        AnsiConsole.MarkupLine("  [green]EXPLAIN ANALYZE <sql>;[/]       Run the query and show the plan with runtime metrics");
        AnsiConsole.MarkupLine("  [green].tables[/]                      List registered tables and row counts");
        AnsiConsole.MarkupLine("  [green].timer[/]                       Toggle elapsed-time reporting after each query");
        AnsiConsole.MarkupLine("  [green].help[/]                        Show this help");
        AnsiConsole.MarkupLine("  [green].quit[/] / [green].exit[/]                Exit the shell");
        AnsiConsole.MarkupLine("  [grey]Tab[/] / [grey]Ctrl+Space[/]              Trigger SQL completion (Ctrl+Space if Tab is swallowed by the terminal)");
        AnsiConsole.MarkupLine("  [grey]Ctrl+C[/]                      Cancel the running query (does not exit)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Multi-line SQL is supported — keep typing until ;[/]");
        AnsiConsole.MarkupLine($"[grey]Long results paginate every {PageSize:N0} rows — Enter to continue, q/Esc to stop.[/]");
    }

    private static void RenderExplainPlan(ExplainPlanNode root)
    {
        AnsiConsole.WriteLine();
        RenderPlanNode(root, prefix: "", isLast: true, isRoot: true);
        AnsiConsole.WriteLine();
    }

    private static void RenderPlanNode(
        ExplainPlanNode node, string prefix, bool isLast, bool isRoot)
    {
        string connector = isRoot ? "" : (isLast ? "└─ " : "├─ ");
        string labelPrefix = node.ChildLabel is not null ? $"[grey][[{Markup.Escape(node.ChildLabel)}]][/] " : "";

        StringBuilder line = new();
        line.Append(Markup.Escape(prefix));
        line.Append(Markup.Escape(connector));

        AnsiConsole.Markup(line.ToString());
        AnsiConsole.Markup(labelPrefix);
        AnsiConsole.Markup($"[bold cyan]{Markup.Escape(node.OperatorName)}[/]");

        if (!string.IsNullOrEmpty(node.Details))
        {
            AnsiConsole.Markup($" [dim]({Markup.Escape(node.Details)})[/]");
        }

        if (node.EstimatedRows.HasValue)
        {
            AnsiConsole.Markup($"  [blue]~{node.EstimatedRows.Value:N0} rows[/]");
        }

        if (node.RowsProduced.HasValue || node.SelfTime.HasValue)
        {
            AnsiConsole.Markup("  [dim]|[/]");

            if (node.RowsConsumed.HasValue && node.RowsConsumed.Value != node.RowsProduced)
            {
                double selectivity = node.RowsConsumed.Value > 0
                    ? (double)node.RowsProduced!.Value / node.RowsConsumed.Value * 100.0
                    : 0;
                AnsiConsole.Markup(
                    $"  rows in: [white]{node.RowsConsumed.Value:N0}[/] → out: [white]{node.RowsProduced!.Value:N0}[/] ({selectivity:F1}%)");
            }
            else if (node.RowsProduced.HasValue)
            {
                AnsiConsole.Markup($"  rows: [white]{node.RowsProduced.Value:N0}[/]");
            }

            if (node.SelfTime.HasValue)
            {
                AnsiConsole.Markup($"  [dim]|[/]  self: [green]{FormatTime(node.SelfTime.Value)}[/]");
            }

            if (node.TotalTime.HasValue)
            {
                AnsiConsole.Markup($"  [dim]|[/]  total: [grey]{FormatTime(node.TotalTime.Value)}[/]");
            }
        }

        AnsiConsole.WriteLine();

        string childPrefix = isRoot ? "" : (prefix + (isLast ? "    " : "│   "));

        foreach (string annotation in node.RuntimeAnnotations)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [grey]{Markup.Escape(annotation)}[/]");
        }

        foreach (string annotation in node.Annotations)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [dim]→ {Markup.Escape(annotation)}[/]");
        }

        foreach (string warning in node.Warnings)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [yellow]⚠ {Markup.Escape(warning)}[/]");
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            RenderPlanNode(node.Children[i], childPrefix, isLast: i == node.Children.Count - 1, isRoot: false);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalMilliseconds < 1.0)
        {
            return $"{time.TotalMicroseconds:F1} μs";
        }

        if (time.TotalSeconds < 1.0)
        {
            return $"{time.TotalMilliseconds:F1} ms";
        }

        return $"{time.TotalSeconds:F2} s";
    }
}
