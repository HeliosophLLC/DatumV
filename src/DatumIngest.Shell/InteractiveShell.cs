using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Execution;
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
    private readonly TableCatalog _catalog;
    private readonly TableFormatter _formatter;
    private CancellationTokenSource? _activeQueryCts;

    public InteractiveShell(TableCatalog catalog)
    {
        _catalog = catalog;
        _formatter = new TableFormatter(catalog.SidecarRegistry);
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
        };

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
        try
        {
            // Prefetch the first batch to derive a Schema for TableFormatter.
            IAsyncEnumerator<RowBatch> enumerator = plan.ExecuteAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    AnsiConsole.MarkupLine("[grey](0 rows)[/]");
                    return;
                }

                RowBatch first = enumerator.Current;
                Schema schema = DeriveSchema(first);

                // Chain the prefetched batch back into the stream so TableFormatter
                // sees every row, not just batches 2..N.
                await _formatter.FormatAsync(ChainBatches(first, enumerator), schema, Console.Out)
                    .ConfigureAwait(false);
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
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
        }
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
    }

    private static async IAsyncEnumerable<RowBatch> ChainBatches(
        RowBatch first, IAsyncEnumerator<RowBatch> rest)
    {
        yield return first;
        while (await rest.MoveNextAsync().ConfigureAwait(false))
        {
            yield return rest.Current;
        }
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
        AnsiConsole.MarkupLine("  [green].help[/]                        Show this help");
        AnsiConsole.MarkupLine("  [green].quit[/] / [green].exit[/]                Exit the shell");
        AnsiConsole.MarkupLine("  [grey]Ctrl+C[/]                      Cancel the running query (does not exit)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Multi-line SQL is supported — keep typing until ;[/]");
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
