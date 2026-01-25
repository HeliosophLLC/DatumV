using System.Text.Json;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.DevWeb;

/// <summary>
/// <see cref="IModelStreamingSink"/> implementation for the
/// <c>/api/query/stream</c> NDJSON endpoint. Writes one
/// <c>{"type":"chunk", "cell":..., "model":..., "text":...}</c> line per
/// model chunk directly to the response stream, flushing on every line so
/// the browser sees tokens live rather than buffered with the next row.
/// Non-string chunks are dropped — only string-emitting models (LLMs)
/// produce multi-chunk streams today.
/// </summary>
internal sealed class NdjsonStreamingSink : IModelStreamingSink
{
    private readonly Stream _output;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _cellId;

    public NdjsonStreamingSink(Stream output, JsonSerializerOptions jsonOptions, string cellId)
    {
        _output = output;
        _jsonOptions = jsonOptions;
        _cellId = cellId;
    }

    /// <inheritdoc />
    public ValueTask OnChunkAsync(string modelName, ValueRef chunk)
    {
        if (chunk.IsNull || chunk.Kind != DataKind.String) return ValueTask.CompletedTask;
        string text = chunk.AsString();
        if (text.Length == 0) return ValueTask.CompletedTask;

        return WriteLineAsync(new ChunkEvent("chunk", _cellId, modelName, text));
    }

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(string modelName) => ValueTask.CompletedTask;
    // The endpoint emits cell_completed after the plan finishes; per-
    // dispatch completion isn't on the wire today.

    /// <inheritdoc />
    public ValueTask OnFailedAsync(string modelName, Exception exception) => ValueTask.CompletedTask;
    // The endpoint emits error after the exception propagates out of
    // ExecuteAsync; nothing to do here.

    private async ValueTask WriteLineAsync(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), _jsonOptions);
        await _output.WriteAsync(json).ConfigureAwait(false);
        _output.WriteByte((byte)'\n');
        await _output.FlushAsync().ConfigureAwait(false);
    }

    private sealed record ChunkEvent(string Type, string Cell, string Model, string Text);
}
