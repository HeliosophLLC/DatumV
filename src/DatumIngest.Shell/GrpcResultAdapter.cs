// Disabled until a programmatic DatumIngest API replaces the gRPC compute client.
// To re-enable, delete the `#if DATUM_SHELL` / `#endif` markers at the top and bottom.
#if DATUM_SHELL
using System.Runtime.CompilerServices;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Model;
using Grpc.Core;

namespace DatumIngest.Shell;

/// <summary>
/// Shell-local snapshot of assertion diagnostics received from a query stream.
/// Mirrors the relevant fields of <c>DatumIngest.Execution.AssertionDiagnostics</c>
/// without taking an internals-visible dependency on it.
/// </summary>
internal sealed class ShellAssertionDiagnostics
{
    public long WarnedRowCount { get; init; }
    public long SkippedRowCount { get; init; }
    public IReadOnlyList<string> SampleMessages { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Converts gRPC server-streaming query results into domain types consumable by the
/// shell's rendering and output-writing infrastructure.
/// </summary>
internal static class GrpcResultAdapter
{
    private const int BatchSize = 128;

    public static async Task<GrpcQueryResult> ReadQueryAsync(
        AsyncServerStreamingCall<QueryResult> call,
        CancellationToken cancellationToken = default)
    {
        Schema? schema = null;
        string[]? names = null;
        Dictionary<string, int>? nameIndex = null;
        ShellAssertionDiagnostics? diagnostics = null;
        StatementEffect? effect = null;

        IAsyncEnumerable<RowBatch> rows = ReadRowsCore(call, r =>
        {
            schema = r.Schema;
            names = r.Names;
            nameIndex = r.NameIndex;
            diagnostics = r.Diagnostics;
            effect = r.Effect;
        }, cancellationToken);

        await Task.CompletedTask;
        return new GrpcQueryResult(rows, () => schema, () => diagnostics, () => effect);
    }

    private static async IAsyncEnumerable<RowBatch> ReadRowsCore(
        AsyncServerStreamingCall<QueryResult> call,
        Action<StreamState> stateCallback,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StreamState state = new();
        RowBatch? batch = null;

        await foreach (QueryResult result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (result.ResultCase)
            {
                case QueryResult.ResultOneofCase.Row:
                    QueryResultRow protoRow = result.Row;

                    if (protoRow.Schema is { Columns.Count: > 0 } schemaMsg && state.Schema is null)
                    {
                        state.Schema = ProtoConverter.FromProto(schemaMsg);
                        state.Names = new string[state.Schema.Columns.Count];
                        state.NameIndex = new Dictionary<string, int>(
                            state.Schema.Columns.Count, StringComparer.OrdinalIgnoreCase);

                        for (int i = 0; i < state.Schema.Columns.Count; i++)
                        {
                            state.Names[i] = state.Schema.Columns[i].Name;
                            state.NameIndex[state.Names[i]] = i;
                        }

                        stateCallback(state);
                    }

                    if (state.Names is null || state.NameIndex is null)
                    {
                        continue;
                    }

                    Row row = ProtoConverter.RowFromProto(protoRow, state.Names, state.NameIndex);

                    batch ??= RowBatch.Rent(BatchSize);
                    batch.Add(row);

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }

                    break;

                case QueryResult.ResultOneofCase.Effect:
                    state.Effect = result.Effect;
                    stateCallback(state);
                    break;

                case QueryResult.ResultOneofCase.Diagnostics:
                    AssertionDiagnosticsMessage msg = result.Diagnostics;
                    state.Diagnostics = new ShellAssertionDiagnostics
                    {
                        WarnedRowCount = msg.WarnedRowCount,
                        SkippedRowCount = msg.SkippedRowCount,
                        SampleMessages = msg.SampleMessages.ToArray(),
                    };
                    stateCallback(state);
                    break;
            }
        }

        if (batch is { Count: > 0 })
        {
            yield return batch;
        }
        else
        {
            batch?.Return();
        }

        call.Dispose();
    }

    private sealed class StreamState
    {
        public Schema? Schema;
        public string[]? Names;
        public Dictionary<string, int>? NameIndex;
        public ShellAssertionDiagnostics? Diagnostics;
        public StatementEffect? Effect;
    }
}

/// <summary>
/// Holds the results of a gRPC query call: row stream, schema, diagnostics, and effect.
/// </summary>
internal sealed class GrpcQueryResult
{
    private readonly Func<Schema?> _schemaAccessor;
    private readonly Func<ShellAssertionDiagnostics?> _diagnosticsAccessor;
    private readonly Func<StatementEffect?> _effectAccessor;

    internal GrpcQueryResult(
        IAsyncEnumerable<RowBatch> rows,
        Func<Schema?> schemaAccessor,
        Func<ShellAssertionDiagnostics?> diagnosticsAccessor,
        Func<StatementEffect?> effectAccessor)
    {
        Rows = rows;
        _schemaAccessor = schemaAccessor;
        _diagnosticsAccessor = diagnosticsAccessor;
        _effectAccessor = effectAccessor;
    }

    public IAsyncEnumerable<RowBatch> Rows { get; }
    public Schema? Schema => _schemaAccessor();
    public ShellAssertionDiagnostics? Diagnostics => _diagnosticsAccessor();
    public StatementEffect? Effect => _effectAccessor();
}
#endif
