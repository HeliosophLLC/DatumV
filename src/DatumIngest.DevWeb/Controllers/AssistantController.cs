using System.Text.Json;
using DatumIngest.DevWeb.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.DevWeb.Controllers;

/// <summary>
/// Typed HTTP surface for the AI-assistant flow. Sits on top of
/// <see cref="IAssistantService"/>; every action is a thin wrapper
/// that translates request parameters into a service call and
/// formats the response.
/// </summary>
[ApiController]
[Route("api/assistant")]
public sealed class AssistantController : ControllerBase
{
    private readonly IAssistantService _service;
    private readonly JsonSerializerOptions _jsonOptions;

    public AssistantController(
        IAssistantService service,
        JsonSerializerOptions jsonOptions)
    {
        _service = service;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Returns the most recent conversation for the workspace, or
    /// creates one if none exists. Idempotent — repeated calls hand
    /// back the same row.
    /// </summary>
    [HttpPost("conversations")]
    public async Task<ActionResult<ConversationDto>> EnsureConversation(
        [FromQuery] string workspace = "default",
        CancellationToken ct = default)
    {
        ConversationDto conv = await _service.EnsureConversationAsync(workspace, ct)
            .ConfigureAwait(false);
        return conv;
    }

    /// <summary>Returns the message history for the named conversation, ordered by turn_index.</summary>
    [HttpGet("conversations/{conversationId:long}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> GetMessages(
        long conversationId,
        CancellationToken ct = default)
    {
        IReadOnlyList<MessageDto> messages =
            await _service.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        return Ok(messages);
    }

    /// <summary>
    /// Posts a user turn and streams the assistant's reply as
    /// NDJSON. Multipart body with two parts:
    /// <list type="bullet">
    ///   <item><description><c>text</c> — the user's message text (form field).</description></item>
    ///   <item><description><c>file</c> — optional attached image (file field).</description></item>
    /// </list>
    /// Response is <c>application/x-ndjson</c>; one
    /// <see cref="TurnEvent"/> per line. See <see cref="TurnEvent"/>
    /// for the event shapes.
    /// </summary>
    [HttpPost("conversations/{conversationId:long}/turn")]
    public async Task PostTurn(
        long conversationId,
        [FromForm] string? text,
        IFormFile? file,
        [FromQuery] string model = "llama31_8b",
        CancellationToken ct = default)
    {
        UploadInput? upload = null;
        if (file is not null && file.Length > 0)
        {
            using MemoryStream ms = new();
            await file.CopyToAsync(ms, ct).ConfigureAwait(false);
            upload = new UploadInput(ms.ToArray(), file.ContentType ?? "application/octet-stream");
        }

        Response.ContentType = "application/x-ndjson";
        Stream output = Response.Body;

        async ValueTask WriteEvent(TurnEvent ev)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(ev, ev.GetType(), _jsonOptions);
            await output.WriteAsync(json, ct).ConfigureAwait(false);
            output.WriteByte((byte)'\n');
            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        await _service.PostTurnAsync(
            conversationId,
            text ?? string.Empty,
            upload,
            model,
            WriteEvent,
            ct).ConfigureAwait(false);
    }
}
