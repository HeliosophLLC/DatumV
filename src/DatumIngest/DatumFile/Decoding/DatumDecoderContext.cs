using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Reader-side context passed to every column decoder. Carries the value store used
/// for arena-backed string and binary payloads.
/// </summary>
public sealed class DatumDecoderContext
{
    /// <summary>
    /// Value store for string and binary payloads. Decoders use this store for
    /// Arena-backed string storage. Defaults to <c>null</c> — callers must set this
    /// when decoding String/JsonValue columns (see <see cref="StringColumnDecoder"/>,
    /// which throws on a null store). Defaulting to <c>null</c> avoids a per-context
    /// <see cref="Arena"/> allocation that previously leaked MemoryMappedFile handles
    /// through GC finalization on every row group.
    /// </summary>
    public IValueStore? Store { get; init; }

    /// <summary>
    /// A shared singleton suitable for in-memory decode scenarios with no per-call state.
    /// </summary>
    public static DatumDecoderContext Empty { get; } = new();
}
