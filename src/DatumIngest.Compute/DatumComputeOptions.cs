namespace DatumIngest.Compute;

/// <summary>
/// Configuration options for the DatumIngest gRPC compute backend.
/// Bind from a configuration section (typically <c>"DatumCompute"</c>)
/// or populate programmatically before calling
/// <see cref="DatumComputeServiceExtensions.AddDatumCompute"/>.
/// </summary>
public sealed class DatumComputeOptions
{
    /// <summary>
    /// Gets or sets the API key that clients must supply in the
    /// <c>x-api-key</c> gRPC metadata header. Set to an empty string
    /// to disable API key authentication entirely.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the maximum gRPC receive message size in bytes.
    /// Defaults to 64 MB.
    /// </summary>
    public int MaxReceiveMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum gRPC send message size in bytes.
    /// Defaults to 64 MB.
    /// </summary>
    public int MaxSendMessageSize { get; set; } = 64 * 1024 * 1024;
}
