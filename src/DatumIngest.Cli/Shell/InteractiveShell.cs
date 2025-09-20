using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Server;
using RadLine;
using Spectre.Console;

namespace DatumIngest.Cli.Shell;

/// <summary>
/// Interactive REPL shell that creates a local admin session and
/// delegates all input to a <see cref="CommandDispatcher"/>.
/// </summary>
internal sealed class InteractiveShell
{
    private readonly TableCatalog _catalog;
    private readonly long? _memoryBudgetBytes;

    /// <summary>
    /// Initializes the shell with a pre-built catalog from CLI options.
    /// </summary>
    /// <param name="catalog">Table catalog constructed from --catalog/--source arguments.</param>
    /// <param name="memoryBudgetBytes">Optional memory budget for hash aggregates; <see langword="null"/> disables spill-to-disk.</param>
    public InteractiveShell(TableCatalog catalog, long? memoryBudgetBytes = null)
    {
        _catalog = catalog;
        _memoryBudgetBytes = memoryBudgetBytes;
    }

    /// <summary>
    /// Runs the interactive REPL loop until the user exits.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the shell lifetime.</param>
    /// <returns>Exit code.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        FunctionRegistry functionRegistry = FunctionRegistry.CreateDefault();
        SessionManager sessionManager = new(functionRegistry);
        QueryGovernor governor = new(null, null, null, MemoryBudgetBytes: _memoryBudgetBytes);
        Session session = sessionManager.CreateLocalSession(SessionRole.Admin, _catalog, governor);
        QueryContext queryContext = session.CreateQueryContext("Shell");
        CommandDispatcher dispatcher = new(sessionManager);
        TableFormatter formatter = new();

        bool timerEnabled = false;
        string? exportPath = null;

        PrintBanner();

        if (!LineEditor.IsSupported(AnsiConsole.Console))
        {
            AnsiConsole.MarkupLine("[yellow]Warning: Terminal does not fully support RadLine. Falling back to basic input.[/]");
        }

        LineEditor editor = new()
        {
            Prompt = new LineEditorPrompt("[green]datum>[/]"),
            MultiLine = false,
            Completion = new ShellCompletionHandler(_catalog, functionRegistry),
            Highlighter = new ShellHighlighter(),
        };

        StringBuilder inputBuffer = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // RadLine's Prompt is init-only, so recreate the editor each iteration
                // to switch between primary and continuation prompts.
                // However, RadLine 0.9.0 LineEditor.Prompt is init-only.
                // We use a single prompt and prefix the continuation visually via the buffer state.

                string? line = await editor.ReadLine(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    // Ctrl+C pressed.
                    if (inputBuffer.Length > 0)
                    {
                        // Cancel the current multi-line buffer.
                        inputBuffer.Clear();
                        AnsiConsole.WriteLine();
                        continue;
                    }

                    // If no buffer, treat as exit.
                    break;
                }

                string trimmedLine = line.Trim();

                // Empty line: if we have a pending buffer, continue prompting. Otherwise ignore.
                if (string.IsNullOrWhiteSpace(trimmedLine) && inputBuffer.Length == 0)
                {
                    continue;
                }

                // Dot-commands are dispatched immediately (no semicolon needed).
                if (inputBuffer.Length == 0 && trimmedLine.StartsWith('.'))
                {
                    // Handle shell-local commands.
                    if (trimmedLine.Equals(".quit", StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.Equals(".exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (trimmedLine.Equals(".help", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintHelp();
                        continue;
                    }

                    if (trimmedLine.Equals(".timer", StringComparison.OrdinalIgnoreCase))
                    {
                        timerEnabled = !timerEnabled;
                        AnsiConsole.MarkupLine(timerEnabled
                            ? "[green]Timer enabled.[/]"
                            : "[yellow]Timer disabled.[/]");
                        continue;
                    }

                    if (trimmedLine.StartsWith(".export", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] exportParts = trimmedLine.Split(' ', 2, StringSplitOptions.TrimEntries);
                        if (exportParts.Length < 2 || string.IsNullOrWhiteSpace(exportParts[1]))
                        {
                            if (exportPath is not null)
                            {
                                exportPath = null;
                                AnsiConsole.MarkupLine("[yellow]Export cleared.[/]");
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[red]Usage: .export <path>[/]");
                            }
                        }
                        else
                        {
                            exportPath = exportParts[1];
                            AnsiConsole.MarkupLine($"[green]Next query result will export to: {exportPath}[/]");
                        }

                        continue;
                    }

                    // Route to engine.
                    await ExecuteAndRenderAsync(dispatcher, session, queryContext, trimmedLine, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // SQL input: accumulate lines until semicolon-terminated.
                if (inputBuffer.Length > 0)
                {
                    inputBuffer.Append(' ');
                }

                inputBuffer.Append(trimmedLine);

                if (!trimmedLine.EndsWith(';'))
                {
                    continue;
                }

                // Complete SQL statement.
                string sql = inputBuffer.ToString();
                inputBuffer.Clear();

                // Intercept EXPLAIN [ANALYZE] prefix and route to .explain dot-command.
                string sqlTrimmed = sql.TrimEnd(';').TrimEnd();
                if (sqlTrimmed.StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = sqlTrimmed["EXPLAIN ".Length..].TrimStart();
                    sql = $".explain {remainder}";
                }

                await ExecuteAndRenderAsync(dispatcher, session, queryContext, sql, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Unexpected error: {Markup.Escape(ex.Message)}[/]");
            }
        }

        session.Dispose();
        AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
        return 0;
    }

    private static async Task ExecuteAndRenderAsync(
        CommandDispatcher dispatcher,
        Session session,
        QueryContext queryContext,
        string input,
        TableFormatter formatter,
        bool timerEnabled,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch? stopwatch = timerEnabled
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        CommandResult result = await dispatcher.DispatchAsync(session, queryContext, input, cancellationToken).ConfigureAwait(false);

        switch (result.Kind)
        {
            case CommandResultKind.StreamingRows:
                await formatter.FormatAsync(result.Rows!, result.Schema!, Console.Out).ConfigureAwait(false);
                RenderAssertionDiagnostics(result.AssertionDiagnostics);
                break;

            case CommandResultKind.SchemaResult:
                RenderSchema(result.Schema!);
                break;

            case CommandResultKind.ListResult:
                RenderList(result.Items!);
                break;

            case CommandResultKind.FunctionList:
                RenderFunctions(result.Functions!);
                break;

            case CommandResultKind.SessionList:
                RenderSessionList(result.Sessions!);
                break;

            case CommandResultKind.Success:
                if (result.ExplainPlan is not null)
                {
                    RenderExplainPlan(result.ExplainPlan);
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(result.Message)}[/]");
                }
                break;

            case CommandResultKind.Error:
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Message ?? "Unknown error")}[/]");
                break;
        }

        if (stopwatch is not null)
        {
            stopwatch.Stop();
            AnsiConsole.MarkupLine($"[grey]Time: {stopwatch.Elapsed.TotalMilliseconds:F1}ms[/]");
        }
    }

    private static void RenderSchema(Schema schema)
    {
        AnsiConsole.MarkupLine($"[bold]{"Column",-30} {"Type",-12} {"Nullable"}[/]");
        AnsiConsole.WriteLine(new string('-', 55));

        foreach (ColumnInfo column in schema.Columns)
        {
            string nullable = column.Nullable ? "YES" : "NO";
            AnsiConsole.WriteLine($"{column.Name,-30} {column.Kind,-12} {nullable}");
        }

        AnsiConsole.MarkupLine($"\n[grey]({schema.Columns.Count} column(s))[/]");
    }

    private static void RenderList(IReadOnlyList<string> items)
    {
        foreach (string item in items)
        {
            AnsiConsole.WriteLine(item);
        }

        AnsiConsole.MarkupLine($"\n[grey]({items.Count} item(s))[/]");
    }

    private static void RenderAssertionDiagnostics(AssertionDiagnostics? diagnostics)
    {
        if (diagnostics is null) return;
        if (diagnostics.WarnedRowCount == 0 && diagnostics.SkippedRowCount == 0) return;

        if (diagnostics.WarnedRowCount > 0)
            AnsiConsole.MarkupLine($"[yellow]WARN: {diagnostics.WarnedRowCount} row(s) failed assertion (ON FAIL WARN)[/]");

        if (diagnostics.SkippedRowCount > 0)
            AnsiConsole.MarkupLine($"[yellow]WARN: {diagnostics.SkippedRowCount} row(s) excluded by assertion (ON FAIL SKIP)[/]");

        IReadOnlyList<string> samples = diagnostics.SampleMessages;
        if (samples.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Sample messages:[/]");
            foreach (string message in samples)
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(message)}[/]");
        }
    }

    private static void RenderFunctions(IReadOnlyList<FunctionSignature> functions)
    {
        IOrderedEnumerable<IGrouping<FunctionCategory, FunctionSignature>> groups = functions
            .GroupBy(function => function.Category)
            .OrderBy(group => group.Key);

        foreach (IGrouping<FunctionCategory, FunctionSignature> group in groups)
        {
            AnsiConsole.MarkupLine($"\n[bold underline]{group.Key}[/]");
            AnsiConsole.WriteLine();

            foreach (FunctionSignature function in group)
            {
            StringBuilder signature = new();
            signature.Append(function.Name);
            signature.Append('(');

            for (int i = 0; i < function.Parameters.Count; i++)
            {
                if (i > 0)
                {
                    signature.Append(", ");
                }

                ParameterSignature parameter = function.Parameters[i];

                if (parameter.IsOptional)
                {
                    signature.Append('[');
                }

                signature.Append(parameter.Name);
                signature.Append(" : ");
                signature.Append(parameter.Kind);

                if (parameter.IsOptional)
                {
                    signature.Append(']');
                }
            }

            signature.Append(')');

            if (function.ReturnType is not null)
            {
                signature.Append(" -> ");
                signature.Append(function.ReturnType);
            }

            if (function.IsTableValued)
            {
                signature.Append("  [TVF]");
            }

            AnsiConsole.WriteLine(signature.ToString());
            }
        }

        AnsiConsole.MarkupLine($"\n[grey]({functions.Count} function(s))[/]");
    }

    private static void RenderSessionList(IReadOnlyList<SessionInfo> sessions)
    {
        AnsiConsole.MarkupLine($"[bold]{"SessionId",-38} {"Role",-8} {"Dataset",-20} {"Queries",-10} {"Last Activity"}[/]");
        AnsiConsole.WriteLine(new string('-', 100));

        foreach (SessionInfo session in sessions)
        {
            string dataset = session.DatasetId ?? "(local)";
            string lastActivity = session.LastActivityAt.ToString("HH:mm:ss");
            AnsiConsole.WriteLine($"{session.SessionId,-38} {session.Role,-8} {dataset,-20} {session.QueryCount,-10} {lastActivity}");
        }

        AnsiConsole.MarkupLine($"\n[grey]({sessions.Count} session(s))[/]");
    }

    private static void PrintBanner()
    {
        AnsiConsole.MarkupLine("[bold blue]DatumIngest Interactive Shell[/]");
        AnsiConsole.MarkupLine("[grey]Type SQL (end with ;) or .help for commands.[/]");
        AnsiConsole.WriteLine();
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Available commands:[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [green].tables[/]              List registered tables");
        AnsiConsole.MarkupLine("  [green].schema <table>[/]      Show columns and types for a table");
        AnsiConsole.MarkupLine("  [green].columns <table>[/]     Alias for .schema");
        AnsiConsole.MarkupLine("  [green].source <def>[/]        Add a data source (provider:name=path)");
        AnsiConsole.MarkupLine("  [green].providers[/]           List registered format providers");
        AnsiConsole.MarkupLine("  [green].functions[/]           List available functions");
        AnsiConsole.MarkupLine("  [green].explain <sql>[/]       Show query execution plan");
        AnsiConsole.MarkupLine("  [green].explain analyze <sql>[/] Run query and show plan with runtime metrics");
        AnsiConsole.MarkupLine("  [green].sessions[/]            List active sessions (admin)");
        AnsiConsole.MarkupLine("  [green].kill <session_id>[/]   Cancel a running query (admin)");
        AnsiConsole.MarkupLine("  [green].cancel[/]              Cancel the active query on this session");
        AnsiConsole.MarkupLine("  [green].timer[/]               Toggle query timing display");
        AnsiConsole.MarkupLine("  [green].export <path>[/]       Export next query to file");
        AnsiConsole.MarkupLine("  [green].help[/]                Show this help");
        AnsiConsole.MarkupLine("  [green].quit[/] / [green].exit[/]        Exit the shell");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]SQL queries must end with a semicolon (;).[/]");
        AnsiConsole.MarkupLine("[grey]Multi-line input is supported — keep typing until ;[/]");        AnsiConsole.MarkupLine("[grey]SQL-prefix syntax: EXPLAIN <sql>; or EXPLAIN ANALYZE <sql>;[/]");
    }

    /// <summary>
    /// Renders an <see cref="ExplainPlanNode"/> tree with Spectre.Console
    /// colored markup for operator names, details, metrics, and warnings.
    /// </summary>
    private static void RenderExplainPlan(ExplainPlanNode root)
    {
        AnsiConsole.WriteLine();
        RenderPlanNode(root, prefix: "", isLast: true, isRoot: true);
        AnsiConsole.WriteLine();
    }

    private static void RenderPlanNode(
        ExplainPlanNode node, string prefix, bool isLast, bool isRoot)
    {
        string connector = isRoot ? "" : (isLast ? "\u2514\u2500 " : "\u251c\u2500 ");
        string labelPrefix = node.ChildLabel is not null ? $"[grey][[{Markup.Escape(node.ChildLabel)}]][/] " : "";

        // Build the line: connector + label + operator name + details + cost
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

        // Runtime metrics (EXPLAIN ANALYZE)
        if (node.RowsProduced.HasValue || node.SelfTime.HasValue)
        {
            AnsiConsole.Markup("  [dim]|[/]");

            if (node.RowsConsumed.HasValue && node.RowsConsumed.Value != node.RowsProduced)
            {
                double selectivity = node.RowsConsumed.Value > 0
                    ? (double)node.RowsProduced!.Value / node.RowsConsumed.Value * 100.0
                    : 0;
                AnsiConsole.Markup(
                    $"  rows in: [white]{node.RowsConsumed.Value:N0}[/] \u2192 out: [white]{node.RowsProduced!.Value:N0}[/] ({selectivity:F1}%)");
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

        string childPrefix = isRoot ? "" : (prefix + (isLast ? "    " : "\u2502   "));

        // Runtime annotations
        foreach (string annotation in node.RuntimeAnnotations)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [grey]{Markup.Escape(annotation)}[/]");
        }

        // Static annotations
        foreach (string annotation in node.Annotations)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [dim]\u2192 {Markup.Escape(annotation)}[/]");
        }

        // Warnings
        foreach (string warning in node.Warnings)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(childPrefix)}    [yellow]\u26a0 {Markup.Escape(warning)}[/]");
        }

        // Children
        for (int i = 0; i < node.Children.Count; i++)
        {
            RenderPlanNode(node.Children[i], childPrefix, isLast: i == node.Children.Count - 1, isRoot: false);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalMilliseconds < 1.0)
        {
            return $"{time.TotalMicroseconds:F1} \u03bcs";
        }

        if (time.TotalSeconds < 1.0)
        {
            return $"{time.TotalMilliseconds:F1} ms";
        }

        return $"{time.TotalSeconds:F2} s";    }
}
