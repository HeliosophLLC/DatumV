using System.Text.Json.Serialization;

namespace DatumIngest.DevWeb.Assistant;

/// <summary>
/// Wire shape for an assistant conversation row, returned by
/// <see cref="AssistantController.EnsureConversation"/>. Mirrors the
/// catalog's <c>conversations</c> table.
/// </summary>
public sealed record ConversationDto(
    long Id,
    string Workspace,
    string Title,
    DateTime StartedAt);

/// <summary>
/// Wire shape for one message row in a conversation. Nullable
/// fields mirror the catalog's column nullability — <see cref="UploadId"/>
/// and <see cref="ToolCallId"/> are only populated when the row was
/// produced by an image-attached turn or a tool-call round-trip.
/// </summary>
public sealed record MessageDto(
    long Id,
    int TurnIndex,
    string Role,
    string Content,
    long? UploadId,
    string? ToolCallId,
    DateTime CreatedAt);

/// <summary>
/// Discriminated union over the events the streaming-turn endpoint
/// emits as NDJSON lines. Subtype = <see cref="Type"/>; per-subtype
/// payloads ride on the polymorphic-record properties below.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessageInsertedEvent), "user_message_inserted")]
[JsonDerivedType(typeof(ChunkEvent), "chunk")]
[JsonDerivedType(typeof(AssistantMessageInsertedEvent), "assistant_message_inserted")]
[JsonDerivedType(typeof(CompleteTurnEvent), "complete")]
[JsonDerivedType(typeof(ErrorTurnEvent), "error")]
public abstract record TurnEvent
{
    [JsonIgnore]
    public string Type => GetType().GetCustomAttributes(false)
        .OfType<JsonDerivedTypeAttribute>().FirstOrDefault()?.TypeDiscriminator?.ToString()
        ?? GetType().Name;
}

/// <summary>The user message has just been INSERTed; the panel can render it as a bubble.</summary>
public sealed record UserMessageInsertedEvent(MessageDto Message) : TurnEvent;

/// <summary>One token chunk from the streaming model. Append to the live assistant bubble.</summary>
public sealed record ChunkEvent(string Text) : TurnEvent;

/// <summary>The assistant turn has been INSERTed; the streaming bubble settles into a final message.</summary>
public sealed record AssistantMessageInsertedEvent(MessageDto Message) : TurnEvent;

/// <summary>End-of-turn marker. Carries total elapsed milliseconds for telemetry.</summary>
public sealed record CompleteTurnEvent(double ElapsedMs) : TurnEvent;

/// <summary>Mid-turn error. Panel renders as a red bubble; turn is considered finished.</summary>
public sealed record ErrorTurnEvent(string Message, string? Detail) : TurnEvent;
