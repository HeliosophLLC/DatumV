using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// A bound value for a parameter referenced by a SQL query as <c>$name</c>.
/// Two shapes:
/// <list type="bullet">
///   <item><description><see cref="InlineParameter"/> — a fully-formed
///   <see cref="DataValue"/>. Used for primitives (numbers, strings,
///   booleans, dates) where the value is cheap to construct up front and
///   needs no per-query store.</description></item>
///   <item><description><see cref="BinaryParameter"/> — raw bytes plus a
///   <see cref="DataKind"/>. Materialised into a <see cref="DataValue"/>
///   at expression-evaluation time, when the active query store is
///   available. Used for <c>Image</c> / <c>Audio</c> / <c>Video</c> /
///   <c>UInt8</c>-array parameters carried as multipart parts in HTTP
///   requests, so the parameter binder doesn't have to know about (or
///   own) a per-query <see cref="IValueStore"/>.</description></item>
/// </list>
/// </summary>
public abstract record ParameterValue;

/// <summary>
/// A scalar parameter value already encoded as a <see cref="DataValue"/>.
/// Primitive kinds (Int8/16/32/64, UInt8, Float32/64, String, Boolean,
/// Date, Time, Timestamp, Null) are constructed by the caller before
/// binding and pass through unchanged into a <see cref="Heliosoph.DatumV.Parsing.Ast.LiteralExpression"/>.
/// </summary>
/// <param name="Value">The pre-built scalar.</param>
public sealed record InlineParameter(DataValue Value) : ParameterValue;

/// <summary>
/// A binary parameter delivered as raw bytes. The materialisation into
/// a <see cref="DataValue"/> is deferred to expression evaluation time,
/// where the active query's <see cref="IValueStore"/> is available
/// through the <see cref="EvaluationFrame.Target"/>.
/// </summary>
/// <param name="Kind">
/// The element kind. Must be one of <see cref="DataKind.Image"/>,
/// <see cref="DataKind.Audio"/>, <see cref="DataKind.Video"/>, or
/// <see cref="DataKind.UInt8"/> (for an opaque byte array).
/// </param>
/// <param name="Bytes">The encoded payload, copied verbatim into the active store.</param>
public sealed record BinaryParameter(DataKind Kind, byte[] Bytes) : ParameterValue;

/// <summary>
/// A managed-string parameter value. Materialised against the active
/// query store at evaluation time, so the caller doesn't need an
/// <see cref="IValueStore"/> and isn't subject to the 16-byte inline
/// limit of <c>DataValue.FromString(string)</c>. The materialised
/// <see cref="DataValue"/> always has <see cref="DataKind.String"/>.
/// </summary>
/// <param name="Value">The string content; <see langword="null"/> is rejected — use a null <see cref="InlineParameter"/>.</param>
public sealed record StringParameter(string Value) : ParameterValue;
