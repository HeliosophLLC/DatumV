using System.Text.Json;
using System.Text.Json.Serialization;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Web.Dtos.Execution;
using Heliosoph.DatumV.Web.Execution;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// Streaming SQL execution endpoint. POST a <see cref="QueryStreamRequest"/>
/// and receive an NDJSON event stream: one JSON object per line, terminated
/// by '\n'. See <see cref="QueryStreamService"/> for the event vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation flows through <see cref="HttpContext.RequestAborted"/> — when
/// the client closes the connection (typically via <c>AbortController</c>),
/// the batch executor sees the cancellation and emits a final
/// <c>error{message:"cancelled"}</c> + <c>complete</c> event pair before
/// returning.
/// </para>
/// <para>
/// Content type: <c>application/x-ndjson</c>. ASP.NET's response-buffer
/// behaviour is bypassed by calling <c>Flush</c> after every event
/// (handled inside the service) so the client sees rows arrive as the
/// engine produces them rather than at the end of the batch.
/// </para>
/// <para>
/// Two request transports are accepted: <c>application/json</c> for inline-
/// only parameters, and <c>multipart/form-data</c> when any parameter binds
/// to a binary kind (<c>Image</c> / <c>Audio</c> / <c>Video</c> / <c>UInt8</c>
/// array). The multipart shape carries a <c>request</c> JSON part with the
/// <see cref="QueryStreamRequest"/> envelope plus one binary part per
/// referenced parameter; see <see cref="QueryRequestBinding"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/query")]
public sealed class QueryStreamController(QueryStreamService service) : ControllerBase
{
    private static readonly JsonSerializerOptions StreamingJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Per-request body cap for the stream endpoint. Matches the multipart binary-part cap.</summary>
    public const long MaxRequestBodyBytes = QueryRequestBinding.MaxBinaryPartBytes;

    [HttpPost("stream")]
    [Microsoft.AspNetCore.Mvc.RequestSizeLimit(MaxRequestBodyBytes)]
    [Microsoft.AspNetCore.Mvc.RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBodyBytes)]
    public async Task Stream()
    {
        QueryStreamEnvelope envelope;
        try
        {
            envelope = await QueryRequestBinding.ReadAsync(
                HttpContext.Request,
                StreamingJson,
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, StreamingJson).ConfigureAwait(false);
            return;
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = $"Bad request: {ex.Message}" }, StreamingJson).ConfigureAwait(false);
            return;
        }

        QueryStreamRequest request = envelope.Body;
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "sql is required" }, StreamingJson).ConfigureAwait(false);
            return;
        }

        int maxRows = request.MaxRows is > 0 ? request.MaxRows.Value : 1000;
        TraceOptions trace = request.Trace is { } t
            ? new TraceOptions(t.Operators, t.Scalars)
            : TraceOptions.Off;

        HttpContext.Response.ContentType = "application/x-ndjson";

        await service.ExecuteAsync(
            request.Sql,
            maxRows,
            trace,
            envelope.Parameters,
            HttpContext.Response.Body,
            StreamingJson,
            HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
