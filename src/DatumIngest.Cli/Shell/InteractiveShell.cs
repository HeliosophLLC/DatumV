using System.Text;
using DatumIngest.Catalog;
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

    /// <summary>
    /// Initializes the shell with a pre-built catalog from CLI options.
    /// </summary>
    /// <param name="catalog">Table catalog constructed from --catalog/--source arguments.</param>
    public InteractiveShell(TableCatalog catalog)
    {
        _catalog = catalog;
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
        Session session = sessionManager.CreateLocalSession(SessionRole.Admin, _catalog);
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
                    await ExecuteAndRenderAsync(dispatcher, session, trimmedLine, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
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

                await ExecuteAndRenderAsync(dispatcher, session, sql, formatter, timerEnabled, cancellationToken).ConfigureAwait(false);
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
        string input,
        TableFormatter formatter,
        bool timerEnabled,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch? stopwatch = timerEnabled
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        CommandResult result = await dispatcher.DispatchAsync(session, input, cancellationToken).ConfigureAwait(false);

        switch (result.Kind)
        {
            case CommandResultKind.StreamingRows:
                await formatter.FormatAsync(result.Rows!, result.Schema!, Console.Out).ConfigureAwait(false);
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
                if (!string.IsNullOrEmpty(result.Message))
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

    private static void RenderFunctions(IReadOnlyList<FunctionSignature> functions)
    {
        foreach (FunctionSignature function in functions)
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
        AnsiConsole.MarkupLine("  [green].sessions[/]            List active sessions (admin)");
        AnsiConsole.MarkupLine("  [green].kill <session_id>[/]   Cancel a running query (admin)");
        AnsiConsole.MarkupLine("  [green].timer[/]               Toggle query timing display");
        AnsiConsole.MarkupLine("  [green].export <path>[/]       Export next query to file");
        AnsiConsole.MarkupLine("  [green].help[/]                Show this help");
        AnsiConsole.MarkupLine("  [green].quit[/] / [green].exit[/]        Exit the shell");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]SQL queries must end with a semicolon (;).[/]");
        AnsiConsole.MarkupLine("[grey]Multi-line input is supported — keep typing until ;[/]");
    }
}
