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
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sync writes.</strong> The sink interface is sync — it's invoked
/// from inside the operator's await chain — so we use <see cref="Stream.Write(byte[],int,int)"/>
/// + <see cref="Stream.Flush"/> directly. The endpoint enables
/// <c>AllowSynchronousIO</c> for its request so ASP.NET Core lets these
/// blocking writes through. Async writes would require either making the
/// sink interface async (cascades into <c>ModelInvocationOperator</c> and
/// every model implementation) or marshalling chunks through a
/// <see cref="System.Threading.Channels.Channel{T}"/> with a separate writer
/// task — both heavier than this trade.
/// </para>
/// <para>
/// <strong>Non-string chunks are dropped.</strong> Today only string-emitting
/// models (LLMs) produce multi-chunk streams. Non-string streaming output
/// has no defined wire representation; revisit when a model with that
/// shape arrives.
/// </para>
/// </remarks>
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
    public void OnChunk(string modelName, ValueRef chunk)
    {
        if (chunk.IsNull || chunk.Kind != DataKind.String) return;
        string text = chunk.AsString();
        if (text.Length == 0) return;

        WriteLine(new ChunkEvent("chunk", _cellId, modelName, text));
    }

    /// <inheritdoc />
    public void OnCompleted(string modelName)
    {
        // The endpoint emits cell_completed after the plan finishes; per-
        // dispatch completion isn't on the wire today.
    }

    /// <inheritdoc />
    public void OnFailed(string modelName, Exception exception)
    {
        // The endpoint emits error after the exception propagates out of
        // ExecuteAsync; nothing to do here.
    }

    private void WriteLine(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), _jsonOptions);
        _output.Write(json, 0, json.Length);
        _output.WriteByte((byte)'\n');
        _output.Flush();
    }

    private sealed record ChunkEvent(string Type, string Cell, string Model, string Text);
}
