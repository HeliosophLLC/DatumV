// Disabled until a programmatic DatumIngest API replaces the gRPC compute client.
// To re-enable, delete the `#if DATUM_SHELL` / `#endif` markers at the top and bottom.
#if DATUM_SHELL
using System.Text;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Execution;
using DatumIngest.Model;
using Grpc.Core;
using RadLine;
using Spectre.Console;

using GrpcClient = global::DatumIngest.Compute.Grpc.DatumCompute.DatumComputeClient;

namespace DatumIngest.Shell;

/// <summary>
/// Interactive REPL shell that routes all commands through a gRPC
/// <see cref="GrpcClient"/> connected to a DatumIngest compute server.
/// </summary>
internal sealed class InteractiveShell
{
    private readonly GrpcClient _client;
    private readonly string _sessionId;
    private readonly string _contextId;

    public InteractiveShell(GrpcClient client, string sessionId, string contextId)
    {
        _client = client;
        _sessionId = sessionId;
        _contextId = contextId;
    }

    /// <summary>
    /// Runs the interactive REPL loop until the user exits.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
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
            Highlighter = new ShellHighlighter(),
        };

        StringBuilder inputBuffer = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? line = await editor.ReadLine(cancellationToken).ConfigureAwait(false);

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

                string trimmedLine = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmedLine) && inputBuffer.Length == 0)
                {
                    continue;
                }

                // Dot-commands are dispatched immediately (no semicolon needed).
                if (inputBuffer.Length == 0 && trimmedLine.StartsWith('.'))
                {
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

                    await ExecuteDotCommandAsync(trimmedLine, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
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

                string sql = inputBuffer.ToString();
                inputBuffer.Clear();

                // Intercept EXPLAIN [ANALYZE] prefix and route to .explain dot-command.
                string sqlTrimmed = sql.TrimEnd(';').TrimEnd();
                if (sqlTrimmed.StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = sqlTrimmed["EXPLAIN ".Length..].TrimStart();
                    sql = $".explain {remainder}";
                    await ExecuteDotCommandAsync(sql, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await ExecuteQueryAsync(sql, formatter, timerEnabled, exportPath, cancellationToken).ConfigureAwait(false);
                exportPath = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RpcException rpcEx)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(rpcEx.Status.Detail)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Unexpected error: {Markup.Escape(ex.Message)}[/]");
            }
        }

        AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
        return 0;
    }

    private async Task ExecuteQueryAsync(
        string sql,
        TableFormatter formatter,
        bool timerEnabled,
        string? exportPath,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch? stopwatch = timerEnabled
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        QueryRequest request = new()
        {
            SessionId = _sessionId,
            ContextId = _contextId,
            Sql = sql,
        };

        AsyncServerStreamingCall<QueryResult> call = _client.Query(request, cancellationToken: cancellationToken);
        GrpcQueryResult grpcResult = await GrpcResultAdapter.ReadQueryAsync(call, cancellationToken);

        if (grpcResult.Effect is { } effect)
        {
            await foreach (RowBatch batch in grpcResult.Rows.WithCancellation(cancellationToken))
            {
                batch.Return();
            }

            if (!string.IsNullOrEmpty(effect.Message))
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(effect.Message)}[/]");
            }
        }
        else if (exportPath is not null)
        {
            await ExportResultAsync(grpcResult, exportPath, cancellationToken);
        }
        else
        {
            // Consume the first batch to get the schema, then format all rows.
            List<RowBatch> prefetched = [];
            Schema? schema = null;

            await foreach (RowBatch batch in grpcResult.Rows.WithCancellation(cancellationToken))
            {
                prefetched.Add(batch);
                if (grpcResult.Schema is not null)
                {
                    schema = grpcResult.Schema;
                    break;
                }
            }

            if (schema is not null)
            {
                async IAsyncEnumerable<RowBatch> ChainBatches()
                {
                    foreach (RowBatch b in prefetched) yield return b;
                    await foreach (RowBatch b in grpcResult.Rows.WithCancellation(cancellationToken))
                        yield return b;
                }

                await formatter.FormatAsync(ChainBatches(), schema, Console.Out).ConfigureAwait(false);
            }
            else
            {
                foreach (RowBatch b in prefetched) b.Return();
            }

            RenderAssertionDiagnostics(grpcResult.Diagnostics);
        }

        if (stopwatch is not null)
        {
            stopwatch.Stop();
            AnsiConsole.MarkupLine($"[grey]Time: {stopwatch.Elapsed.TotalMilliseconds:F1}ms[/]");
        }
    }

    private static async Task ExportResultAsync(GrpcQueryResult grpcResult, string path, CancellationToken cancellationToken)
    {
        bool schemaInitialized = false;
        DatumIngest.Output.Writers.CsvOutputWriter writer = new(path);
        await using DatumIngest.Output.IOutputWriter outputWriter = writer;

        await foreach (RowBatch batch in grpcResult.Rows.WithCancellation(cancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                if (!schemaInitialized)
                {
                    Schema schema = new(Enumerable.Range(0, row.FieldCount)
                        .Select(j => new ColumnInfo(row.ColumnNames[j], row[j].Kind, true))
                        .ToArray());
                    await outputWriter.InitializeAsync(schema);
                    schemaInitialized = true;
                }

                await outputWriter.WriteRowAsync(row);
            }
            batch.Return();
        }

        DatumIngest.Output.OutputSummary summary = await outputWriter.FinalizeAsync();
        AnsiConsole.MarkupLine($"[green]Exported {summary.BytesWritten:N0} bytes to {path}[/]");
    }

    private async Task ExecuteDotCommandAsync(
        string command,
        TableFormatter formatter,
        bool timerEnabled,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch? stopwatch = timerEnabled
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        string[] parts = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case ".tables":
                ListResponse tablesResp = await _client.ListTablesAsync(new ListTablesRequest
                {
                    SessionId = _sessionId,
                    ContextId = _contextId,
                }, cancellationToken: cancellationToken);
                foreach (string item in tablesResp.Items)
                {
                    AnsiConsole.WriteLine(item);
                }
                AnsiConsole.MarkupLine($"\n[grey]({tablesResp.Items.Count} table(s))[/]");
                break;

            case ".schema":
            case ".columns":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    AnsiConsole.MarkupLine("[red]Usage: .schema <table_name>[/]");
                    break;
                }
                SchemaResponse schemaResp = await _client.GetSchemaAsync(new GetSchemaRequest
                {
                    SessionId = _sessionId,
                    TableName = arg,
                    ContextId = _contextId,
                }, cancellationToken: cancellationToken);
                ColumnInfo[] columns = new ColumnInfo[schemaResp.Columns.Count];
                for (int ci = 0; ci < columns.Length; ci++)
                {
                    columns[ci] = ProtoConverter.FromProto(schemaResp.Columns[ci]);
                }
                RenderSchema(new Schema(columns));
                break;

            case ".providers":
                ListResponse providersResp = await _client.ListProvidersAsync(new ListProvidersRequest
                {
                    SessionId = _sessionId,
                }, cancellationToken: cancellationToken);
                foreach (string item in providersResp.Items)
                {
                    AnsiConsole.WriteLine(item);
                }
                AnsiConsole.MarkupLine($"\n[grey]({providersResp.Items.Count} provider(s))[/]");
                break;

            case ".functions":
                ListFunctionsResponse funcsResp = await _client.ListFunctionsAsync(new ListFunctionsRequest
                {
                    SessionId = _sessionId,
                }, cancellationToken: cancellationToken);
                RenderFunctions(funcsResp);
                break;

            case ".explain":
                bool analyze = false;
                string explainSql = arg;
                if (arg.StartsWith("analyze ", StringComparison.OrdinalIgnoreCase))
                {
                    analyze = true;
                    explainSql = arg["analyze ".Length..].TrimStart();
                }
                ExplainResponse explainResp = await _client.ExplainAsync(new ExplainRequest
                {
                    SessionId = _sessionId,
                    Sql = explainSql,
                    Analyze = analyze,
                    ContextId = _contextId,
                }, cancellationToken: cancellationToken);
                if (explainResp.Root is not null)
                {
                    ExplainPlanNode plan = ProtoConverter.FromProto(explainResp.Root);
                    RenderExplainPlan(plan);
                }
                else
                {
                    AnsiConsole.WriteLine(explainResp.PlanText);
                }
                break;

            case ".source":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    AnsiConsole.MarkupLine("[red]Usage: .source <provider:name=path>[/]");
                    break;
                }
                AddSourceResponse sourceResp = await _client.AddSourceAsync(new AddSourceRequest
                {
                    SessionId = _sessionId,
                    SourceDefinition = arg,
                }, cancellationToken: cancellationToken);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(sourceResp.Message)}[/]");
                break;

            case ".sessions":
                ListSessionsResponse sessionsResp = await _client.ListSessionsAsync(new ListSessionsRequest
                {
                    SessionId = _sessionId,
                }, cancellationToken: cancellationToken);
                RenderSessionList(sessionsResp);
                break;

            case ".kill":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    AnsiConsole.MarkupLine("[red]Usage: .kill <session_id>[/]");
                    break;
                }
                KillQueryResponse killResp = await _client.KillQueryAsync(new KillQueryRequest
                {
                    SessionId = _sessionId,
                    TargetSessionId = arg,
                }, cancellationToken: cancellationToken);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(killResp.Message)}[/]");
                break;

            case ".cancel":
                CancelQueryResponse cancelResp = await _client.CancelQueryAsync(new CancelQueryRequest
                {
                    SessionId = _sessionId,
                    ContextId = _contextId,
                }, cancellationToken: cancellationToken);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(cancelResp.Message)}[/]");
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]");
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

    private static void RenderAssertionDiagnostics(ShellAssertionDiagnostics? diagnostics)
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

    private static void RenderFunctions(ListFunctionsResponse response)
    {
        IOrderedEnumerable<IGrouping<FunctionCategoryValue, FunctionInfoMessage>> groups = response.Functions
            .GroupBy(f => f.Category)
            .OrderBy(g => g.Key);

        foreach (IGrouping<FunctionCategoryValue, FunctionInfoMessage> group in groups)
        {
            AnsiConsole.MarkupLine($"\n[bold underline]{group.Key}[/]");
            AnsiConsole.WriteLine();

            foreach (FunctionInfoMessage function in group)
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

                    ParameterInfoMessage parameter = function.Parameters[i];

                    if (!parameter.Required)
                    {
                        signature.Append('[');
                    }

                    signature.Append(parameter.Name);
                    signature.Append(" : ");
                    signature.Append(parameter.Kind);

                    if (!parameter.Required)
                    {
                        signature.Append(']');
                    }
                }

                signature.Append(')');

                if (!string.IsNullOrEmpty(function.ReturnType))
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

        AnsiConsole.MarkupLine($"\n[grey]({response.Functions.Count} function(s))[/]");
    }

    private static void RenderSessionList(ListSessionsResponse response)
    {
        AnsiConsole.MarkupLine($"[bold]{"SessionId",-38} {"Role",-8} {"Dataset",-20} {"Queries",-10} {"Last Activity"}[/]");
        AnsiConsole.WriteLine(new string('-', 100));

        foreach (SessionInfoMessage session in response.Sessions)
        {
            string dataset = string.IsNullOrEmpty(session.DatasetId) ? "(local)" : session.DatasetId;
            string lastActivity = session.LastActivityAt;
            AnsiConsole.WriteLine($"{session.SessionId,-38} {session.Role,-8} {dataset,-20} {session.QueryCount,-10} {lastActivity}");
        }

        AnsiConsole.MarkupLine($"\n[grey]({response.Sessions.Count} session(s))[/]");
    }

    private static void PrintBanner()
    {
        AnsiConsole.MarkupLine("[bold blue]DatumIngest Shell[/]");
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
        AnsiConsole.MarkupLine("[grey]Multi-line input is supported — keep typing until ;[/]");
        AnsiConsole.MarkupLine("[grey]SQL-prefix syntax: EXPLAIN <sql>; or EXPLAIN ANALYZE <sql>;[/]");
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
#endif
