using DatumIngest.Web.Conversation;
using DatumIngest.Web.Messages;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationRegistry _registry;
    private readonly IMessageGraph _graph;

    public ConversationsController(IConversationRegistry registry, IMessageGraph graph)
    {
        _registry = registry;
        _graph = graph;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ConversationDto>> List(CancellationToken ct)
    {
        IReadOnlyList<ConversationSummary> summaries = await _registry.ListAsync(ct);
        return summaries.Select(ConversationDto.From).ToList();
    }

    // Returns the conversation the client should land on by default — the
    // most-recently-touched one, lazily creating one if none exist. The
    // client hits this on boot so it always has an active conversation id
    // to pass to SendMessage.
    [HttpGet("default")]
    public async Task<ConversationDto> Default(CancellationToken ct)
    {
        long id = await _registry.EnsureDefaultAsync(ct);
        ConversationSummary? summary = await _registry.GetAsync(id, ct);
        if (summary is null)
        {
            throw new InvalidOperationException(
                $"EnsureDefaultAsync returned id {id} but GetAsync couldn't find it.");
        }
        return ConversationDto.From(summary);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ConversationDto>> Get(long id, CancellationToken ct)
    {
        ConversationSummary? summary = await _registry.GetAsync(id, ct);
        return summary is null ? NotFound() : ConversationDto.From(summary);
    }

    [HttpGet("{id:long}/messages")]
    public async Task<IReadOnlyList<MessageDto>> Messages(long id, CancellationToken ct)
    {
        IReadOnlyList<MessageRecord> messages = await _graph.ReadHistoryAsync(id, ct);
        return messages.Select(MessageDto.From).ToList();
    }

    [HttpPost]
    public async Task<ConversationDto> Create([FromBody] CreateConversationDto dto, CancellationToken ct)
    {
        long id = await _registry.CreateAsync(dto.Title, dto.Model, ct);
        ConversationSummary? summary = await _registry.GetAsync(id, ct);
        if (summary is null)
        {
            throw new InvalidOperationException(
                $"CreateAsync returned id {id} but GetAsync couldn't find it.");
        }
        return ConversationDto.From(summary);
    }
}

public sealed record ConversationDto(
    long Id,
    string? Title,
    string? Model,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static ConversationDto From(ConversationSummary s) =>
        new(s.Id, s.Title, s.Model, s.CreatedAt, s.UpdatedAt);
}

public sealed record MessageDto(
    long Id,
    long ConversationId,
    string Kind,
    string Role,
    string Content,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    DateTime CreatedAt)
{
    public static MessageDto From(MessageRecord m) =>
        new(m.Id, m.ConversationId, m.Kind, m.Role, m.Content, m.Model,
            m.InputTokens, m.OutputTokens, m.CreatedAt);
}

public sealed record CreateConversationDto(string? Title, string? Model);
