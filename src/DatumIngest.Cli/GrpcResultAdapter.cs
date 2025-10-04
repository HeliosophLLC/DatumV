using System.Runtime.CompilerServices;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Execution;
using DatumIngest.Model;
using Grpc.Core;

namespace DatumIngest.Cli;

/// <summary>
/// Converts gRPC server-streaming query results into domain types
/// consumable by the CLI rendering and output-writing infrastructure.
/// </summary>
internal static class GrpcResultAdapter
{
    private const int BatchSize = 128;

    /// <summary>
    /// Reads a server-streaming <c>Query</c> call and yields domain
    /// <see cref="RowBatch"/> objects along with the resolved schema.
    /// </summary>
    /// <param name="call">The active gRPC streaming call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result containing the schema (available after the first row),
    /// the row stream, and any assertion diagnostics.
    /// </returns>
    public static async Task<GrpcQueryResult> ReadQueryAsync(
        AsyncServerStreamingCall<QueryResult> call,
        CancellationToken cancellationToken = default)
    {
        Schema? schema = null;
        string[]? names = null;
        Dictionary<string, int>? nameIndex = null;
        AssertionDiagnostics? diagnostics = null;
        StatementEffect? effect = null;

        var rows = ReadRowsCore(call, r =>
        {
            schema = r.Schema;
            names = r.Names;
            nameIndex = r.NameIndex;
            diagnostics = r.Diagnostics;
            effect = r.Effect;
        }, cancellationToken);

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

                    // First row carries the schema.
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
                    AssertionDiagnostics diag = new();
                    for (long i = 0; i < msg.WarnedRowCount; i++)
                    {
                        diag.RecordWarn(null);
                    }
                    for (long i = 0; i < msg.SkippedRowCount; i++)
                    {
                        diag.RecordSkip(null);
                    }
                    foreach (string sample in msg.SampleMessages)
                    {
                        diag.RecordWarn(sample);
                    }
                    state.Diagnostics = diag;
                    stateCallback(state);
                    break;
            }
        }

        // Yield any partial batch after the stream completes.
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
        public AssertionDiagnostics? Diagnostics;
        public StatementEffect? Effect;
    }
}

/// <summary>
/// Holds the results of a gRPC query call: the row stream, schema, and
/// optional diagnostics/effects.
/// </summary>
internal sealed class GrpcQueryResult
{
    private readonly Func<Schema?> _schemaAccessor;
    private readonly Func<AssertionDiagnostics?> _diagnosticsAccessor;
    private readonly Func<StatementEffect?> _effectAccessor;

    internal GrpcQueryResult(
        IAsyncEnumerable<RowBatch> rows,
        Func<Schema?> schemaAccessor,
        Func<AssertionDiagnostics?> diagnosticsAccessor,
        Func<StatementEffect?> effectAccessor)
    {
        Rows = rows;
        _schemaAccessor = schemaAccessor;
        _diagnosticsAccessor = diagnosticsAccessor;
        _effectAccessor = effectAccessor;
    }

    /// <summary>
    /// The row stream. Consuming this stream populates <see cref="Schema"/>
    /// and <see cref="Diagnostics"/> as data arrives.
    /// </summary>
    public IAsyncEnumerable<RowBatch> Rows { get; }

    /// <summary>
    /// The result schema. Available after the first row has been yielded.
    /// </summary>
    public Schema? Schema => _schemaAccessor();

    /// <summary>
    /// Assertion diagnostics. Available after the stream completes.
    /// </summary>
    public AssertionDiagnostics? Diagnostics => _diagnosticsAccessor();

    /// <summary>
    /// Statement effect for DDL/DML statements. Available after the stream completes.
    /// </summary>
    public StatementEffect? Effect => _effectAccessor();
}
