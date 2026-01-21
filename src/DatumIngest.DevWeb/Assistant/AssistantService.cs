using System.Diagnostics;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.DevWeb.Assistant;

/// <summary>
/// Service-layer interface for the AI-assistant flow. Wraps the
/// engine — typed verbs (start a conversation, post a turn, fetch
/// history) instead of the SQL-as-API path that <c>/api/query/stream</c>
/// exposes.
/// </summary>
public interface IAssistantService
{
    /// <summary>Idempotent <c>CREATE TABLE IF NOT EXISTS</c> for conversations / messages / uploads.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Returns the most recent conversation for <paramref name="workspace"/>, or creates one if none exists.</summary>
    Task<ConversationDto> EnsureConversationAsync(string workspace, CancellationToken ct);

    /// <summary>Loads the message history for <paramref name="conversationId"/>, ordered by turn_index ascending.</summary>
    Task<IReadOnlyList<MessageDto>> GetMessagesAsync(long conversationId, CancellationToken ct);

    /// <summary>
    /// Posts a user turn and streams the assistant's reply. The
    /// implementation:
    /// <list type="number">
    ///   <item><description>If <paramref name="upload"/> is set, INSERTs an <c>uploads</c> row.</description></item>
    ///   <item><description>INSERTs a <c>user</c> message (with the upload reference if any) and emits <see cref="UserMessageInsertedEvent"/>.</description></item>
    ///   <item><description>Dispatches the templated assistant SELECT against <c>messages</c> and streams <see cref="ChunkEvent"/>s.</description></item>
    ///   <item><description>INSERTs the assistant's response and emits <see cref="AssistantMessageInsertedEvent"/>.</description></item>
    ///   <item><description>Emits <see cref="CompleteTurnEvent"/>.</description></item>
    /// </list>
    /// On error mid-flow, emits <see cref="ErrorTurnEvent"/> followed by a final <see cref="CompleteTurnEvent"/>.
    /// </summary>
    Task PostTurnAsync(
        long conversationId,
        string text,
        UploadInput? upload,
        string modelName,
        Func<TurnEvent, ValueTask> onEvent,
        CancellationToken ct);
}

/// <summary>Bytes + metadata for an attached upload, supplied by the controller from a multipart part.</summary>
public sealed record UploadInput(byte[] Bytes, string Mime);

/// <inheritdoc />
public sealed class AssistantService : IAssistantService
{
    private readonly TableCatalog _catalog;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaSeeded;

    public AssistantService(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    // ───────────────────── Schema ─────────────────────

    private const string SeedSql = """
        CREATE TABLE IF NOT EXISTS conversations (
          id         Int64 PRIMARY KEY IDENTITY,
          workspace  String,
          title      String,
          started_at DateTime
        );
        CREATE TABLE IF NOT EXISTS uploads (
          id          Int64 PRIMARY KEY IDENTITY,
          workspace   String,
          bytes       Image,
          mime        String,
          size_bytes  Int32,
          uploaded_at DateTime
        );
        CREATE TABLE IF NOT EXISTS messages (
          id              Int64 PRIMARY KEY IDENTITY,
          conversation_id Int64,
          turn_index      Int32,
          role            String,
          content         String,
          upload_id       Int64,
          tool_call_id    String,
          created_at      DateTime
        );
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaSeeded) return;
        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaSeeded) return;
            await ExecuteWithEventsAsync(SeedSql, parameters: null, onEvent: null, ct)
                .ConfigureAwait(false);
            _schemaSeeded = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    // ───────────────────── Conversations ─────────────────────

    public async Task<ConversationDto> EnsureConversationAsync(
        string workspace, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        // Find the most recent conversation for this workspace; if
        // none exists, lazy-create. Workspace is a String parameter
        // so the value handles arbitrary text without escaping.
        IReadOnlyList<object?[]> rows = await SelectAsync(
            "SELECT id, workspace, title, started_at FROM conversations " +
            "WHERE workspace = $workspace ORDER BY id DESC LIMIT 1",
            new Dictionary<string, ParameterValue>
            {
                ["workspace"] = new StringParameter(workspace),
            },
            ct).ConfigureAwait(false);

        if (rows.Count > 0)
        {
            return ToConversation(rows[0]);
        }

        // Lazy-create. RETURNING surfaces the resolved (post-IDENTITY,
        // post-DEFAULT) row in the same statement.
        IReadOnlyList<object?[]> inserted = await SelectAsync(
            "INSERT INTO conversations (workspace, title, started_at) " +
            "VALUES ($workspace, 'Chat', now()) " +
            "RETURNING id, workspace, title, started_at",
            new Dictionary<string, ParameterValue>
            {
                ["workspace"] = new StringParameter(workspace),
            },
            ct).ConfigureAwait(false);

        if (inserted.Count == 0)
        {
            throw new InvalidOperationException("Failed to create conversation row.");
        }
        return ToConversation(inserted[0]);
    }

    // ───────────────────── Messages ─────────────────────

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        long conversationId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        IReadOnlyList<object?[]> rows = await SelectAsync(
            "SELECT id, turn_index, role, content, upload_id, tool_call_id, created_at " +
            "FROM messages WHERE conversation_id = $conv_id ORDER BY turn_index ASC",
            new Dictionary<string, ParameterValue>
            {
                ["conv_id"] = new InlineParameter(DataValue.FromInt64(conversationId)),
            },
            ct).ConfigureAwait(false);

        MessageDto[] result = new MessageDto[rows.Count];
        for (int i = 0; i < rows.Count; i++) result[i] = ToMessage(rows[i]);
        return result;
    }

    // ───────────────────── Turn dispatch ─────────────────────

    public async Task PostTurnAsync(
        long conversationId,
        string text,
        UploadInput? upload,
        string modelName,
        Func<TurnEvent, ValueTask> onEvent,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // ───────── 1. Optional upload ─────────
            long? uploadId = null;
            if (upload is not null)
            {
                IReadOnlyList<object?[]> uploadRows = await SelectAsync(
                    "INSERT INTO uploads (workspace, bytes, mime, size_bytes, uploaded_at) " +
                    "VALUES ('default', $bytes, $mime, $size, now()) " +
                    "RETURNING id",
                    new Dictionary<string, ParameterValue>
                    {
                        ["bytes"] = new BinaryParameter(DataKind.Image, upload.Bytes),
                        ["mime"] = new StringParameter(upload.Mime),
                        ["size"] = new InlineParameter(DataValue.FromInt32(upload.Bytes.Length)),
                    },
                    ct).ConfigureAwait(false);
                uploadId = (long?)uploadRows[0][0];
            }

            // ───────── 2. INSERT user message + read it back ─────────
            // INSERT VALUES doesn't accept subqueries today, so the
            // next turn_index is computed in its own SELECT and the
            // integer is inlined into the INSERT.
            string uploadFragment = uploadId is null
                ? "NULL"
                : uploadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string convFragment = conversationId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            int userTurnIndex = await NextTurnIndexAsync(conversationId, ct).ConfigureAwait(false);

            IReadOnlyList<object?[]> userRows = await SelectAsync(
                "INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at) " +
                "VALUES (" + convFragment + ", " + userTurnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " +
                "        'user', $content, " + uploadFragment + ", now()) " +
                "RETURNING id, turn_index, role, content, upload_id, tool_call_id, created_at",
                new Dictionary<string, ParameterValue>
                {
                    ["content"] = new StringParameter(text),
                },
                ct).ConfigureAwait(false);

            MessageDto userMsg = ToMessage(userRows[0]);
            await onEvent(new UserMessageInsertedEvent(userMsg)).ConfigureAwait(false);

            // ───────── 3. Stream assistant turn ─────────
            // Canonical chat-templates form: open || string_agg(...) WITHIN
            // GROUP (ORDER BY turn_index) || assistant_turn. Empty-string
            // separator because each templated msg already carries its
            // own family-specific delimiter (e.g. <|eot_id|>).
            string family = FamilyForModel(modelName);
            string assistantSql =
                "SELECT models." + modelName + "( " +
                "  templates." + family + "_open() " +
                "  || string_agg(templates." + family + "_msg(role, content), '') WITHIN GROUP (ORDER BY turn_index) " +
                "  || templates." + family + "_assistant_turn(), " +
                "  NULL, NULL, true) " +
                "FROM messages WHERE conversation_id = " + convFragment;

            string? assistantText = null;
            await ExecuteWithEventsAsync(assistantSql, parameters: null, onEvent: async ev =>
            {
                switch (ev)
                {
                    case CellChunkBatchEvent chunk:
                        await onEvent(new ChunkEvent(chunk.Text)).ConfigureAwait(false);
                        break;
                    case CellRowBatchEvent rowEv:
                    {
                        if (rowEv.Batch.Count > 0)
                        {
                            Row row = rowEv.Batch[0];
                            DataValue cell = row[0];
                            if (!cell.IsNull)
                            {
                                assistantText = cell.AsString(rowEv.Batch.Arena);
                            }
                        }
                        break;
                    }
                    case CellFailedBatchEvent failed:
                        throw failed.Error;
                }
            }, ct).ConfigureAwait(false);

            assistantText ??= string.Empty;

            // ───────── 4. INSERT assistant message + read it back ─────────
            int assistantTurnIndex = await NextTurnIndexAsync(conversationId, ct).ConfigureAwait(false);

            IReadOnlyList<object?[]> assistantRows = await SelectAsync(
                "INSERT INTO messages (conversation_id, turn_index, role, content, upload_id, created_at) " +
                "VALUES (" + convFragment + ", " + assistantTurnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " +
                "        'assistant', $content, NULL, now()) " +
                "RETURNING id, turn_index, role, content, upload_id, tool_call_id, created_at",
                new Dictionary<string, ParameterValue>
                {
                    ["content"] = new StringParameter(assistantText),
                },
                ct).ConfigureAwait(false);

            MessageDto assistantMsg = ToMessage(assistantRows[0]);
            await onEvent(new AssistantMessageInsertedEvent(assistantMsg)).ConfigureAwait(false);

            await onEvent(new CompleteTurnEvent(sw.Elapsed.TotalMilliseconds)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await onEvent(new ErrorTurnEvent("cancelled", null)).ConfigureAwait(false);
            await onEvent(new CompleteTurnEvent(sw.Elapsed.TotalMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await onEvent(new ErrorTurnEvent(ex.Message, ex.ToString())).ConfigureAwait(false);
            await onEvent(new CompleteTurnEvent(sw.Elapsed.TotalMilliseconds)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the next <c>turn_index</c> for a conversation by
    /// reading <c>COALESCE(MAX(turn_index), 0) + 1</c>. Done as its
    /// own round-trip because <c>INSERT VALUES</c> doesn't accept
    /// subqueries today; inlining the integer keeps the INSERT
    /// shape evaluator-friendly.
    /// </summary>
    private async Task<int> NextTurnIndexAsync(long conversationId, CancellationToken ct)
    {
        IReadOnlyList<object?[]> rows = await SelectAsync(
            "SELECT COALESCE(CAST(MAX(turn_index) AS Int32), 0) + 1 FROM messages WHERE conversation_id = $conv_id",
            new Dictionary<string, ParameterValue>
            {
                ["conv_id"] = new InlineParameter(DataValue.FromInt64(conversationId)),
            },
            ct).ConfigureAwait(false);
        if (rows.Count == 0 || rows[0][0] is null) return 1;
        return rows[0][0] switch
        {
            long l => (int)l,
            int i => i,
            _ => 1,
        };
    }

    // ───────────────────── Engine glue ─────────────────────

    /// <summary>
    /// Parses + binds + runs <paramref name="sql"/> through the batch
    /// executor, materialising every row into a <c>object?[]</c>
    /// (CLR primitive per cell) and returning the concatenated list.
    /// Suitable for small projections; large results would still
    /// allocate per row but the assistant flow only reads tiny
    /// row counts (one message at a time, history at most a few
    /// dozen rows).
    /// </summary>
    private async Task<IReadOnlyList<object?[]>> SelectAsync(
        string sql,
        IReadOnlyDictionary<string, ParameterValue>? parameters,
        CancellationToken ct)
    {
        List<object?[]> rows = new();
        await ExecuteWithEventsAsync(sql, parameters, ev =>
        {
            if (ev is CellRowBatchEvent rowEv)
            {
                Arena arena = rowEv.Batch.Arena;
                int colCount = rowEv.Batch.ColumnLookup.Count;
                for (int r = 0; r < rowEv.Batch.Count; r++)
                {
                    Row row = rowEv.Batch[r];
                    object?[] arr = new object?[colCount];
                    for (int c = 0; c < colCount; c++)
                    {
                        arr[c] = ToClr(row[c], arena);
                    }
                    rows.Add(arr);
                }
            }
            else if (ev is CellFailedBatchEvent failed)
            {
                throw failed.Error;
            }
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        return rows;
    }

    private async Task ExecuteWithEventsAsync(
        string sql,
        IReadOnlyDictionary<string, ParameterValue>? parameters,
        Func<BatchEvent, ValueTask>? onEvent,
        CancellationToken ct)
    {
        IReadOnlyList<(Statement Statement, string SourceText)> parsed =
            SqlParser.ParseBatchWithText(sql);

        IReadOnlyList<Statement> bound;
        if (parameters is { Count: > 0 })
        {
            Statement[] toBind = new Statement[parsed.Count];
            for (int i = 0; i < parsed.Count; i++) toBind[i] = parsed[i].Statement;
            bound = ParameterBinder.Bind(toBind, parameters);
        }
        else
        {
            Statement[] tmp = new Statement[parsed.Count];
            for (int i = 0; i < parsed.Count; i++) tmp[i] = parsed[i].Statement;
            bound = tmp;
        }

        (Statement, string?)[] pairs = new (Statement, string?)[parsed.Count];
        for (int i = 0; i < parsed.Count; i++) pairs[i] = (bound[i], parsed[i].SourceText);

        BatchExecutor executor = new(_catalog);
        Func<BatchEvent, ValueTask> handler = onEvent ?? (static _ => ValueTask.CompletedTask);
        await executor.RunWithEventsAsync(pairs, handler, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a <see cref="DataValue"/> into the CLR primitive type
    /// that matches its kind. Null values become <see langword="null"/>;
    /// strings allocate against the row's source arena. Used only for
    /// the assistant service's small projections; not a general-
    /// purpose row formatter.
    /// </summary>
    private static object? ToClr(DataValue cell, Arena arena)
    {
        if (cell.IsNull) return null;
        return cell.Kind switch
        {
            DataKind.Int8 => (long)cell.AsInt8(),
            DataKind.Int16 => (long)cell.AsInt16(),
            DataKind.Int32 => (long)cell.AsInt32(),
            DataKind.Int64 => cell.AsInt64(),
            DataKind.UInt8 => (long)cell.AsUInt8(),
            DataKind.Boolean => cell.AsBoolean(),
            DataKind.Float32 => (double)cell.AsFloat32(),
            DataKind.Float64 => cell.AsFloat64(),
            DataKind.String => cell.AsString(arena),
            DataKind.DateTime => cell.AsDateTime().UtcDateTime,
            _ => cell.AsString(arena),
        };
    }

    private static ConversationDto ToConversation(object?[] row) => new(
        Id: (long)row[0]!,
        Workspace: (string)row[1]!,
        Title: (string)row[2]!,
        StartedAt: (DateTime)row[3]!);

    private static MessageDto ToMessage(object?[] row) => new(
        Id: (long)row[0]!,
        TurnIndex: (int)(long)row[1]!,
        Role: (string)row[2]!,
        Content: (string)(row[3] ?? string.Empty),
        UploadId: row[4] is long u ? u : null,
        ToolCallId: row[5] as string,
        CreatedAt: (DateTime)row[6]!);

    private static string FamilyForModel(string modelName)
    {
        if (modelName.StartsWith("llama", StringComparison.OrdinalIgnoreCase)) return "llama31";
        if (modelName.StartsWith("phi3", StringComparison.OrdinalIgnoreCase)) return "phi3";
        if (modelName.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)) return "chatml";
        if (modelName.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return "mistral";
        if (modelName.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)) return "gemma";
        if (modelName.StartsWith("granite", StringComparison.OrdinalIgnoreCase)) return "granite";
        if (modelName.StartsWith("tinyllama", StringComparison.OrdinalIgnoreCase)) return "zephyr";
        return "llama31";
    }
}
