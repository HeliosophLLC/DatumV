using System.IO;
using System.Text;
using System.Text.Json;
using DatumIngest.Execution;
using DatumIngest.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DatumIngest.DevWeb;

/// <summary>
/// JSON shape carried in the <c>request</c> part of a multipart query
/// request, or in the body of a JSON-only request.
/// </summary>
internal sealed record QueryRequest(
    string Sql,
    int? MaxRows,
    bool? Trace,
    Dictionary<string, ParameterJson>? Parameters);

/// <summary>
/// JSON shape for a single parameter binding. Exactly one of
/// <see cref="Value"/> / <see cref="Ref"/> is meaningful per parameter:
/// inline scalars use <see cref="Value"/>; binary parameters
/// (<c>Image</c> / <c>Audio</c> / <c>Video</c> / <c>UInt8</c> array) use
/// <see cref="Ref"/> to name a sibling multipart part whose bytes carry
/// the payload.
/// </summary>
internal sealed record ParameterJson(
    string Kind,
    JsonElement? Value,
    string? Ref);

/// <summary>
/// Result of parsing the request envelope. Holds the parsed
/// <see cref="QueryRequest"/> body plus a <see cref="ParameterValue"/>
/// dictionary built from any inline values and resolved multipart
/// references.
/// </summary>
internal sealed record QueryRequestEnvelope(
    QueryRequest Body,
    IReadOnlyDictionary<string, ParameterValue> Parameters);

/// <summary>
/// Parses an inbound query request envelope. Accepts two transports:
/// <list type="bullet">
///   <item><description><b>JSON</b> — <c>Content-Type: application/json</c>.
///   The body is the <see cref="QueryRequest"/> directly. Inline parameter
///   values resolve from JSON; binary <c>ref</c> parameters are rejected
///   because there's nowhere to source the bytes from.</description></item>
///   <item><description><b>Multipart</b> —
///   <c>Content-Type: multipart/form-data; boundary=...</c>. The first
///   part is named <c>request</c> with <c>application/json</c> body;
///   subsequent parts carry binary payloads for any <c>ref</c>-mode
///   parameters.</description></item>
/// </list>
/// </summary>
internal static class QueryRequestBinding
{
    /// <summary>Reasonable upper bound on the JSON envelope; binary parts have their own per-part limit.</summary>
    private const int MaxJsonPartBytes = 4 * 1024 * 1024;

    /// <summary>Per-binary-part cap. 200 MB easily accommodates 1080p frame buffers and short audio clips.</summary>
    private const int MaxBinaryPartBytes = 200 * 1024 * 1024;

    public static async Task<QueryRequestEnvelope> ReadAsync(
        HttpRequest request,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        string? contentType = request.ContentType;
        if (contentType is not null && contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadMultipartAsync(request, jsonOptions, ct).ConfigureAwait(false);
        }

        // JSON body — same shape as before plus optional `parameters`.
        QueryRequest? body = await JsonSerializer
            .DeserializeAsync<QueryRequest>(request.Body, jsonOptions, ct)
            .ConfigureAwait(false);
        if (body is null)
        {
            throw new InvalidOperationException("request body is empty");
        }

        IReadOnlyDictionary<string, ParameterValue> parameters =
            BuildInlineParameters(body.Parameters, partBytesByName: null);
        return new QueryRequestEnvelope(body, parameters);
    }

    private static async Task<QueryRequestEnvelope> ReadMultipartAsync(
        HttpRequest request,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? media))
        {
            throw new InvalidOperationException("invalid multipart Content-Type header");
        }
        string? boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidOperationException("multipart Content-Type missing boundary");
        }

        MultipartReader reader = new(boundary, request.Body);
        QueryRequest? body = null;
        Dictionary<string, byte[]> partBytesByName =
            new(StringComparer.Ordinal);

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false)) is not null)
        {
            ContentDispositionHeaderValue? disposition =
                section.GetContentDispositionHeader();
            string? name = HeaderUtilities.RemoveQuotes(disposition?.Name ?? default).Value;
            if (string.IsNullOrEmpty(name))
            {
                // Unnamed part — skip silently rather than fail; the
                // envelope only consumes parts referenced by name.
                continue;
            }

            if (name == "request")
            {
                using MemoryStream ms = new();
                await section.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
                if (ms.Length > MaxJsonPartBytes)
                {
                    throw new InvalidOperationException(
                        $"`request` part is {ms.Length} bytes, larger than the {MaxJsonPartBytes}-byte limit.");
                }
                ms.Position = 0;
                body = await JsonSerializer
                    .DeserializeAsync<QueryRequest>(ms, jsonOptions, ct)
                    .ConfigureAwait(false);
                continue;
            }

            // Binary part — read into memory once. Streaming straight
            // into a DataValue would require knowing the parameter kind
            // here, but the kind comes from the JSON envelope which may
            // not have been parsed yet (parts arrive in order). Buffer
            // and resolve after the JSON is known.
            byte[] bytes = await ReadPartBytesAsync(section, MaxBinaryPartBytes, ct)
                .ConfigureAwait(false);
            partBytesByName[name] = bytes;
        }

        if (body is null)
        {
            throw new InvalidOperationException(
                "multipart body missing the required `request` JSON part.");
        }

        IReadOnlyDictionary<string, ParameterValue> parameters =
            BuildInlineParameters(body.Parameters, partBytesByName);
        return new QueryRequestEnvelope(body, parameters);
    }

    private static async Task<byte[]> ReadPartBytesAsync(
        MultipartSection section,
        int maxBytes,
        CancellationToken ct)
    {
        // CopyToAsync guards on cancellation; size is checked post-read
        // because Content-Length is not always carried per-part.
        using MemoryStream ms = new();
        await section.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"multipart part `{section.GetContentDispositionHeader()?.Name}` is {ms.Length} bytes, " +
                $"larger than the {maxBytes}-byte limit.");
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Resolves the JSON `parameters` map into <see cref="ParameterValue"/>
    /// instances, looking up multipart parts by name when a parameter uses
    /// the <c>ref</c> form. Unknown / missing parts cause an
    /// <see cref="InvalidOperationException"/> so the caller can return a
    /// 400 with the offending parameter name.
    /// </summary>
    private static IReadOnlyDictionary<string, ParameterValue> BuildInlineParameters(
        Dictionary<string, ParameterJson>? parameters,
        IReadOnlyDictionary<string, byte[]>? partBytesByName)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, ParameterValue> result =
            new(parameters.Count, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ParameterJson> kvp in parameters)
        {
            string name = kvp.Key;
            ParameterJson p = kvp.Value;
            if (string.IsNullOrEmpty(p.Kind))
            {
                throw new InvalidOperationException(
                    $"parameter '{name}' is missing the required `kind` field.");
            }
            if (!Enum.TryParse(p.Kind, ignoreCase: true, out DataKind kind))
            {
                throw new InvalidOperationException(
                    $"parameter '{name}' has unknown kind '{p.Kind}'.");
            }

            if (p.Ref is not null)
            {
                if (partBytesByName is null)
                {
                    throw new InvalidOperationException(
                        $"parameter '{name}' uses `ref` but the request was not multipart. " +
                        "Send `multipart/form-data` with a binary part to use `ref`.");
                }
                if (!partBytesByName.TryGetValue(p.Ref, out byte[]? bytes))
                {
                    throw new InvalidOperationException(
                        $"parameter '{name}' references multipart part '{p.Ref}' which was not present.");
                }
                if (!IsBinaryKind(kind))
                {
                    throw new InvalidOperationException(
                        $"parameter '{name}' uses `ref` but kind {kind} is not a binary kind. " +
                        "Use Image / Audio / Video / UInt8.");
                }
                result[name] = new BinaryParameter(kind, bytes);
                continue;
            }

            if (p.Value is null || p.Value.Value.ValueKind == JsonValueKind.Null)
            {
                result[name] = new InlineParameter(DataValue.UnknownNull());
                continue;
            }

            // Strings take a separate path: they're materialised against
            // the query's active store at evaluation time, not at the
            // endpoint, so there's no 16-byte inline-string limit.
            if (kind == DataKind.String)
            {
                result[name] = new StringParameter(p.Value.Value.GetString() ?? string.Empty);
                continue;
            }

            DataValue scalar = JsonScalarToDataValue(name, kind, p.Value.Value);
            result[name] = new InlineParameter(scalar);
        }

        return result;
    }

    private static bool IsBinaryKind(DataKind kind) =>
        kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.UInt8;

    /// <summary>
    /// Converts a single JSON scalar (number / bool) into a matching
    /// <see cref="DataValue"/>. Strings are not handled here — they go
    /// through <see cref="StringParameter"/> instead, which defers
    /// materialisation to evaluation time so no <see cref="IValueStore"/>
    /// is required at the endpoint.
    /// </summary>
    private static DataValue JsonScalarToDataValue(string name, DataKind kind, JsonElement value)
    {
        try
        {
            return kind switch
            {
                DataKind.Boolean => DataValue.FromBoolean(value.GetBoolean()),
                DataKind.Int8 => DataValue.FromInt8((sbyte)value.GetInt32()),
                DataKind.Int16 => DataValue.FromInt16((short)value.GetInt32()),
                DataKind.Int32 => DataValue.FromInt32(value.GetInt32()),
                DataKind.Int64 => DataValue.FromInt64(value.GetInt64()),
                DataKind.UInt8 => DataValue.FromUInt8((byte)value.GetInt32()),
                DataKind.Float32 => DataValue.FromFloat32(value.GetSingle()),
                DataKind.Float64 => DataValue.FromFloat64(value.GetDouble()),
                _ => throw new InvalidOperationException(
                    $"parameter '{name}' kind {kind} is not supported as an inline JSON `value`. " +
                    "Use `ref` for Image / Audio / Video / UInt8 binary kinds."),
            };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"parameter '{name}' could not be coerced to {kind}: {ex.Message}", ex);
        }
    }
}
