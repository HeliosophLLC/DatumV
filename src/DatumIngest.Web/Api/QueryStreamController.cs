using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Web.Dtos.Execution;
using DatumIngest.Web.Execution;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

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

    [HttpPost("stream")]
    public async Task Stream([FromBody] QueryStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                new { error = "sql is required" });
            return;
        }

        int maxRows = request.MaxRows is > 0 ? request.MaxRows.Value : 1000;
        bool trace = request.Trace == true;

        HttpContext.Response.ContentType = "application/x-ndjson";

        await service.ExecuteAsync(
            request.Sql,
            maxRows,
            trace,
            HttpContext.Response.Body,
            StreamingJson,
            HttpContext.RequestAborted);
    }
}
