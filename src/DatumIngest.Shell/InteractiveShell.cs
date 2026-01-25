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
    private bool _imagesEnabled;
    private bool _dumpEnabled;
    private string _dumpPath = DefaultDumpPath;
    private bool _traceEnabled;

    /// <summary>
    /// Default directory where <c>.dump on</c> writes Image-typed cells.
    /// Resolved in this order:
    /// <list type="number">
    ///   <item><description>The <c>DATUM_IMAGES</c> environment variable, if set.</description></item>
    ///   <item><description>A portable per-user fallback —
    ///     <c>%LOCALAPPDATA%/DatumIngest/images</c> on Windows,
    ///     <c>~/.local/share/DatumIngest/images</c> on Linux/macOS.
    ///   </description></item>
    /// </list>
    /// Mirrors <c>ModelCatalog.DefaultModelDirectory</c>'s
    /// <c>DATUM_MODELS</c> resolution so users only need to set
    /// <c>DATUM_IMAGES</c> once.
    /// </summary>
    private static string DefaultDumpPath =>
        Environment.GetEnvironmentVariable("DATUM_IMAGES")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatumIngest",
            "images");

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

                    if (trimmed.StartsWith(".images", StringComparison.OrdinalIgnoreCase))
                    {
                        string arg = trimmed[".images".Length..].Trim().ToLowerInvariant();
                        if (arg == "on" || arg == "" && !_imagesEnabled) _imagesEnabled = true;
                        else if (arg == "off" || arg == "" && _imagesEnabled) _imagesEnabled = false;
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]Usage: .images on|off (or just .images to toggle)[/]");
                            continue;
                        }
                        AnsiConsole.MarkupLine(_imagesEnabled
                            ? "[green]Images on. Results render one record at a time with Sixel-encoded image cells.[/]"
                            : "[yellow]Images off. Image cells render as a hex preview.[/]");
                        continue;
                    }

                    if (trimmed.StartsWith(".dump", StringComparison.OrdinalIgnoreCase))
                    {
                        string arg = trimmed[".dump".Length..].Trim();
                        if (arg.Length == 0)
                        {
                            // Show current state.
                            AnsiConsole.MarkupLine(_dumpEnabled
                                ? $"[green]Dump on:[/] [white]{Markup.Escape(_dumpPath)}[/]"
                                : "[grey]Dump off.[/]");
                        }
                        else if (arg.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                        {
                            string pathArg = arg["on".Length..].Trim();
                            _dumpEnabled = true;
                            if (pathArg.Length > 0) _dumpPath = pathArg;
                            AnsiConsole.MarkupLine(
                                $"[green]Dump on. Image/Audio/Video/Json cells will be saved to:[/] [white]{Markup.Escape(_dumpPath)}[/]");
                        }
                        else if (arg.StartsWith("off", StringComparison.OrdinalIgnoreCase))
                        {
                            _dumpEnabled = false;
                            AnsiConsole.MarkupLine("[yellow]Dump off.[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                "[red]Usage: .dump on [path] | .dump off | .dump (show state)[/]");
                        }
                        continue;
                    }

                    if (trimmed.StartsWith(".trace", StringComparison.OrdinalIgnoreCase))
                    {
                        string arg = trimmed[".trace".Length..].Trim().ToLowerInvariant();
                        if (arg == "on" || (arg == "" && !_traceEnabled)) _traceEnabled = true;
                        else if (arg == "off" || (arg == "" && _traceEnabled)) _traceEnabled = false;
                        else
                        {
                            AnsiConsole.MarkupLine(
                                "[red]Usage: .trace on | .trace off (or just .trace to toggle)[/]");
                            continue;
                        }
                        // Attach / detach the tracer on the catalog. QueryPlan
                        // reads this when constructing each query's
                        // ExecutionContext, so the toggle takes effect on the
                        // next query.
                        _catalog.ModelTracer = _traceEnabled ? new ShellModelTracer() : null;
                        AnsiConsole.MarkupLine(_traceEnabled
                            ? "[green]Trace on. Per-model dispatch lines will print before each query result.[/]"
                            : "[yellow]Trace off.[/]");
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
                bool isExec = sqlCore.StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase)
                           || sqlCore.Equals("EXEC", StringComparison.OrdinalIgnoreCase);

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
                else if (isExec)
                {
                    await ExecuteExecAsync(sqlCore).ConfigureAwait(false);
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

    /// <summary>
    /// Executes an <c>EXEC &lt;expression&gt;</c> statement, forwarding model
    /// chunks live to the terminal as they arrive and falling back to
    /// table-formatted row rendering if the EXEC target wasn't a streaming
    /// model (e.g. <c>EXEC upper('hi')</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// EXEC lowers to a synthetic single-row <c>SELECT</c> in the planner; the
    /// streaming-aware <see cref="IQueryPlan.ExecuteAsync(CancellationToken, IModelStreamingSink?)"/>
    /// overload attaches a <see cref="TerminalStreamingSink"/> to the per-query
    /// context. The model invocation operator branches on the sink's presence:
    /// when set, it switches to <c>InferStreamingAsync</c> and pushes chunks
    /// through the sink as the model produces them.
    /// </para>
    /// <para>
    /// <strong>Two outcomes:</strong>
    /// <list type="bullet">
    ///   <item><description>
    ///     A streaming model fired chunks → the sink already wrote them to
    ///     the terminal and printed a "(streamed)" footer. The synthetic
    ///     SELECT's row is redundant; we skip rendering it.
    ///   </description></item>
    ///   <item><description>
    ///     No streaming model in the call (or a non-streaming function like
    ///     <c>upper</c>) → no chunks fired. The query still produced a row;
    ///     buffer its formatted cells and render via
    ///     <see cref="TableFormatter.RenderPage"/> after draining.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private async Task ExecuteExecAsync(string sql)
    {
        IQueryPlan plan;
        try
        {
            plan = await _catalog.PlanAsync(sql).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        using CancellationTokenSource cts = new();
        _activeQueryCts = cts;
        Stopwatch? sw = _timerEnabled ? Stopwatch.StartNew() : null;

        TerminalStreamingSink sink = new();
        SidecarRegistry registry = _catalog.SidecarRegistry;

        // Buffer formatted cells so we can fall back to table rendering when
        // the EXEC body wasn't a streaming model. Cell strings are copied
        // out before each MoveNext, so we don't depend on RowBatch lifetime.
        Schema? schema = null;
        List<string[]> bufferedRows = [];

        try
        {
            await foreach (RowBatch batch in plan.ExecuteAsync(cts.Token, sink).ConfigureAwait(false))
            {
                if (sink.ChunksReceived > 0)
                {
                    // Streaming already painted the output; the synthetic
                    // SELECT row is an artefact. Drain and discard.
                    continue;
                }

                schema ??= DeriveSchema(batch);
                Arena arena = batch.Arena;
                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    string[] cells = new string[schema.Columns.Count];
                    for (int c = 0; c < schema.Columns.Count; c++)
                    {
                        DataValue value = row[c];
                        cells[c] = value.IsNull
                            ? "NULL"
                            : TableFormatter.FormatValue(value, arena, registry, schema.Columns[c].Fields, batch.Types, batch.TypeIdTranslations);
                    }
                    bufferedRows.Add(cells);
                }
            }

            if (sink.ChunksReceived == 0 && schema is not null && bufferedRows.Count > 0)
            {
                TableFormatter.RenderPage(bufferedRows, schema, printHeader: true, Console.Out);
            }
            else if (sink.ChunksReceived == 0 && bufferedRows.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no result)[/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow](query cancelled)[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.ToString())}[/]");
        }
        finally
        {
            _activeQueryCts = null;
            ReportElapsed(sw);
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        IQueryPlan plan;
        try
        {
            plan = await _catalog.PlanAsync(sql).ConfigureAwait(false);
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
            if (_imagesEnabled)
            {
                await PaginateExpandedAsync(plan, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await PaginateAsync(plan, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow](query cancelled)[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.ToString())}[/]");
        }
        finally
        {
            _activeQueryCts = null;
            ReportElapsed(sw);
        }
    }

    /// <summary>
    /// Drives <see cref="IQueryPlan.ExecuteAsync(CancellationToken)"/> page by page, prompting the
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
                        long absoluteRowIndex = totalRows + page.Count;
                        DumpImageCellsIfEnabled(row, schema, arena, registry, absoluteRowIndex);
                        string[] cells = new string[columnCount];
                        for (int c = 0; c < columnCount; c++)
                        {
                            DataValue value = row[c];
                            cells[c] = value.IsNull
                                ? "NULL"
                                : TableFormatter.FormatValue(value, arena, registry, schema.Columns[c].Fields, batch.Types, batch.TypeIdTranslations);
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
    /// Renders results one record at a time in vertical (key: value) form, with
    /// image-typed cells emitted as inline Sixel escape sequences. Pagination
    /// is per-row — Enter advances to the next record, Esc/Q stops. Used when
    /// <c>.images on</c> is set.
    /// </summary>
    private async Task PaginateExpandedAsync(IQueryPlan plan, CancellationToken cancellationToken)
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
            int nameWidth = schema.Columns.Max(c => c.Name.Length);
            long recordIndex = 0;
            bool hasMore = true;
            int batchOffset = 0;

            while (hasMore)
            {
                RowBatch batch = enumerator.Current;
                Arena arena = batch.Arena;
                Row row = batch[batchOffset];
                recordIndex++;

                DumpImageCellsIfEnabled(row, schema, arena, registry, recordIndex - 1);
                RenderExpandedRow(row, schema, arena, registry, batch.Types, batch.TypeIdTranslations, recordIndex, nameWidth);

                batchOffset++;
                if (batchOffset >= batch.Count)
                {
                    hasMore = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    batchOffset = 0;
                }

                if (!hasMore) break;

                if (!PromptContinue())
                {
                    AnsiConsole.MarkupLine($"[grey](stopped after {recordIndex:N0} records)[/]");
                    return;
                }
            }

            AnsiConsole.MarkupLine($"[grey]({recordIndex:N0} {(recordIndex == 1 ? "record" : "records")})[/]");
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void RenderExpandedRow(
        Row row, Schema schema, Arena arena, SidecarRegistry registry, TypeRegistry? types,
        TypeIdTranslationTable? translations, long recordIndex, int nameWidth)
    {
        AnsiConsole.MarkupLine($"[grey]── Record {recordIndex} {new string('─', Math.Max(8, 60 - nameWidth))}[/]");

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo column = schema.Columns[i];
            DataValue value = row[i];

            bool isImageKind = column.Kind == DataKind.Image || column.IsByteArrayColumn;
            string label = column.Name.PadRight(nameWidth);

            if (isImageKind && !value.IsNull)
            {
                // Print the label, then dump the Sixel block on the line(s) below.
                // We bypass AnsiConsole because Spectre.Console treats `[` as markup
                // syntax — the Sixel sequence has to reach stdout untouched.
                Console.Out.WriteLine($"{label} | <image>");
                try
                {
                    byte[] bytes = ResolveImageBytes(value, arena, registry);
                    string sixel = SixelEncoder.EncodeImage(bytes);
                    Console.Out.Write(sixel);
                    Console.Out.WriteLine();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red](image render failed: {Markup.Escape(ex.Message)})[/]");
                }
            }
            else
            {
                string text = value.IsNull
                    ? "NULL"
                    : TableFormatter.FormatValue(value, arena, registry, column.Fields, types, translations);
                Console.Out.WriteLine($"{label} | {text}");
            }
        }
        Console.Out.WriteLine();
    }

    /// <summary>
    /// Returns encoded image bytes for an Image or UInt8Array value resolved through
    /// arena/sidecar storage. Image payloads are always encoded bytes now (the legacy
    /// ImageHandle-in-object-slot path was removed when image functions moved to fused
    /// pipelines), so a single byte-span fetch covers every case.
    /// </summary>
    private static byte[] ResolveImageBytes(DataValue value, Arena arena, SidecarRegistry registry)
    {
        return value.AsByteSpan(arena, registry).ToArray();
    }

    /// <summary>
    /// When <c>.dump on</c> is active, writes every <c>DataKind.Image</c> cell
    /// in the row to <see cref="_dumpPath"/>. Filename pattern:
    /// <c>{guid}-r{rowIndex}-{columnName}.{ext}</c>, where <c>ext</c> is
    /// detected from the magic bytes (PNG / JPEG / GIF / WebP / fallback
    /// to <c>bin</c>). Prints the saved path inline so users can correlate
    /// rendered output with files on disk.
    /// </summary>
    /// <remarks>
    /// Runs independently of <c>.images on</c> rendering — dumping happens
    /// whether the cell is being Sixel-rendered, hex-previewed, or shown in
    /// expanded mode. One side effect: querying a table of source images
    /// (e.g. <c>SELECT image FROM coco</c>) with dump on will write every
    /// row's image to disk. Useful for "snapshot the dataset to a folder"
    /// workflows; surprising for casual queries against image columns.
    /// </remarks>
    private void DumpImageCellsIfEnabled(
        Row row, Schema schema, Arena arena, SidecarRegistry registry, long rowIndex)
    {
        if (!_dumpEnabled) return;

        // Lazy-create the directory on the first row that needs it. Avoids
        // creating the folder when dump is on but the query yields no images.
        bool dirChecked = false;

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo column = schema.Columns[i];
            DataValue value = row[i];
            if (value.IsNull) continue;
            if (column.Kind != DataKind.Image
                && column.Kind != DataKind.Audio
                && column.Kind != DataKind.Video
                && column.Kind != DataKind.Json
                && !column.IsByteArrayColumn) continue;

            if (!dirChecked)
            {
                Directory.CreateDirectory(_dumpPath);
                dirChecked = true;
            }

            try
            {
                ReadOnlySpan<byte> rawBytes = value.AsByteSpan(arena, registry);
                // For Json columns, write JSON text instead of raw CBOR — much friendlier
                // for users opening the dumped file, and the round-trip through DecodeToJsonText
                // preserves canonical structure.
                byte[] fileBytes;
                string ext;
                if (column.Kind == DataKind.Json)
                {
                    string json = DatumIngest.Functions.Json.CborJsonCodec.DecodeToJsonText(rawBytes);
                    fileBytes = System.Text.Encoding.UTF8.GetBytes(json);
                    ext = "json";
                }
                else
                {
                    fileBytes = rawBytes.ToArray();
                    ext = DetectBlobExtension(fileBytes, column.Kind);
                }
                string safeColName = SanitizeForFilename(column.Name);
                string filename = $"{Guid.NewGuid():N}-r{rowIndex}-{safeColName}.{ext}";
                string fullPath = Path.Combine(_dumpPath, filename);
                File.WriteAllBytes(fullPath, fileBytes);
                AnsiConsole.MarkupLine(
                    $"[dim]  saved: {Markup.Escape(fullPath)} ({fileBytes.Length:N0} bytes)[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]  dump failed for column '{Markup.Escape(column.Name)}': {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    /// <summary>
    /// Maps known blob-format magic bytes to a filename extension, scoped by the
    /// column's <see cref="DataKind"/>. Falls back to <c>bin</c> for unrecognised
    /// payloads so the user can still inspect the bytes (and we don't promise an
    /// extension we can't honour).
    /// </summary>
    private static string DetectBlobExtension(ReadOnlySpan<byte> bytes, DataKind kind)
    {
        return kind switch
        {
            DataKind.Audio => DetectAudioExtension(bytes),
            DataKind.Video => DetectVideoExtension(bytes),
            // Image and the legacy byte-array column path both produce image-
            // shaped payloads; fall through to image detection.
            _ => DetectImageExtension(bytes),
        };
    }

    private static string DetectImageExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "jpg";
        }
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46
            && bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return "gif";
        }
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46
            && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42
            && bytes[11] == 0x50)
        {
            return "webp";
        }
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return "bmp";
        }
        return "bin";
    }

    /// <summary>
    /// Maps known audio-format magic bytes to a filename extension.
    /// </summary>
    private static string DetectAudioExtension(ReadOnlySpan<byte> bytes)
    {
        // RIFF....WAVE: WAV/PCM (RIFF at 0..4, WAVE at 8..12)
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x41 && bytes[10] == 0x56 && bytes[11] == 0x45)
        {
            return "wav";
        }
        // fLaC
        if (bytes.Length >= 4 && bytes[0] == 0x66 && bytes[1] == 0x4C
            && bytes[2] == 0x61 && bytes[3] == 0x43)
        {
            return "flac";
        }
        // OggS (Ogg/Vorbis/Opus)
        if (bytes.Length >= 4 && bytes[0] == 0x4F && bytes[1] == 0x67
            && bytes[2] == 0x67 && bytes[3] == 0x53)
        {
            return "ogg";
        }
        // ID3 tag header (MP3 with tag): "ID3"
        if (bytes.Length >= 3 && bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
        {
            return "mp3";
        }
        // MP3 frame sync: 0xFF E0/F0 (top 11 bits = 0xFFE+; permissive across MPEG layer/version)
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
        {
            return "mp3";
        }
        // ftyp box at offset 4 → M4A (audio MP4 container)
        if (bytes.Length >= 12
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // Could be M4A or video MP4; brand at bytes 8..12 disambiguates. M4A
            // brands include "M4A " and "M4B "; default to .m4a for audio columns.
            return "m4a";
        }
        return "bin";
    }

    /// <summary>
    /// Maps known video-format magic bytes to a filename extension.
    /// </summary>
    private static string DetectVideoExtension(ReadOnlySpan<byte> bytes)
    {
        // ftyp box at offset 4 → MP4-family. Brand at bytes 8..12 (e.g. "isom", "mp42",
        // "qt  ") refines the choice; .mp4 is the safe default for video columns.
        if (bytes.Length >= 12
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            return "mp4";
        }
        // EBML header (WebM / MKV): 0x1A 0x45 0xDF 0xA3
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45
            && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            return "webm";
        }
        // RIFF....AVI : AVI (RIFF at 0..4, AVI at 8..12)
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x41 && bytes[9] == 0x56 && bytes[10] == 0x49 && bytes[11] == 0x20)
        {
            return "avi";
        }
        return "bin";
    }

    /// <summary>
    /// Strips characters illegal in filenames (Windows is the strictest;
    /// applying its rules makes paths portable). Falls back to a constant
    /// when sanitisation strips everything.
    /// </summary>
    private static string SanitizeForFilename(string columnName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(columnName.Length);
        foreach (char c in columnName)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        string cleaned = sb.ToString().Trim('_', '.', ' ');
        return cleaned.Length > 0 ? cleaned : "col";
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
            plan = await _catalog.PlanAsync(sql).ConfigureAwait(false);
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

        // Use the first row to read each column's DataKind + IsArray flag. The
        // IsArray bit matters for renderers — without it, an Array<Struct>
        // column (e.g. YOLO output) looks indistinguishable from a scalar
        // Struct and the formatter picks the wrong arm. If the batch is empty
        // (all rows filtered upstream) fall back to Unknown so we can still
        // print headers.
        ColumnInfo[] columns = new ColumnInfo[names.Count];
        if (batch.Count > 0)
        {
            Row row = batch[0];
            for (int i = 0; i < names.Count; i++)
            {
                DataValue cell = row[i];
                columns[i] = new ColumnInfo(names[i], cell.Kind, true) { IsArray = cell.IsArray };
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

    private void PrintBanner()
    {
        AnsiConsole.MarkupLine("[bold blue]DatumIngest Shell[/]");
        AnsiConsole.MarkupLine("[grey]Type SQL (end with ;) or .help for commands.[/]");

        // Surface the resolved models directory + a quick available/registered
        // count so users immediately see whether the shell is pointing at the
        // right place. Without this, a stale env var or wrong --models flag
        // produces the silently-missing failure mode where every model says
        // "missing" in system_models and queries fail with a not-found error.
        Models.ModelCatalog? models = _catalog.Models;
        if (models is not null)
        {
            (int registered, int available) = SummarizeModels(models);
            string countSummary = $"{registered} registered, {available} available";
            string countColour = available == 0 && registered > 0 ? "yellow" : "grey";
            AnsiConsole.MarkupLine(
                $"[grey]Models:[/] [white]{Markup.Escape(models.ModelDirectory)}[/] " +
                $"[{countColour}]({countSummary})[/]");

            if (available == 0 && registered > 0)
            {
                AnsiConsole.MarkupLine(
                    "[grey]  Set DATUM_MODELS or pass --models <path> to point at a directory with model files.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]  Run `SELECT * FROM system_models` to see registered names, sources, and licenses.[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Counts registered model entries and how many have their backing file
    /// present on disk (or are synthetic — no <c>RelativePath</c>). Cheap
    /// stat-per-entry; runs once at shell startup.
    /// </summary>
    private static (int Registered, int Available) SummarizeModels(Models.ModelCatalog catalog)
    {
        int registered = catalog.Entries.Count;
        int available = 0;
        foreach (Models.ModelCatalogEntry entry in catalog.Entries.Values)
        {
            if (entry.RelativePath is null)
            {
                // Synthetic backend (e.g. EchoModel) — no file required, count as available.
                available++;
                continue;
            }
            string resolved = Path.Combine(catalog.ModelDirectory, entry.RelativePath);
            if (File.Exists(resolved))
            {
                available++;
            }
        }
        return (registered, available);
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  [green]<sql>;[/]                       Execute a SQL statement");
        AnsiConsole.MarkupLine("  [green]EXPLAIN <sql>;[/]               Show the static query plan");
        AnsiConsole.MarkupLine("  [green]EXPLAIN ANALYZE <sql>;[/]       Run the query and show the plan with runtime metrics");
        AnsiConsole.MarkupLine("  [green].tables[/]                      List registered tables and row counts");
        AnsiConsole.MarkupLine("  [green].timer[/]                       Toggle elapsed-time reporting after each query");
        AnsiConsole.MarkupLine("  [green].images on[/] / [green].images off[/]      Render image cells as inline Sixel (one record at a time)");
        AnsiConsole.MarkupLine("  [green].dump on [[path]][/] / [green].dump off[/]   Save image cells to disk as files. Path defaults to");
        AnsiConsole.MarkupLine("                                  [grey]$DATUM_IMAGES, falling back to %LOCALAPPDATA%\\DatumIngest\\images.[/]");
        AnsiConsole.MarkupLine("  [green].trace on[/] / [green].trace off[/]            Print per-dispatch model invocation log (model name, row count, timing)");
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
