namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Thrown when a function that depends on an external service (e.g. the US Census
/// geocoder) cannot complete because the service is unreachable, times out, or
/// returns an unusable response. The message names the service and the failure so
/// the user can distinguish "my network / the service is down" from a problem with
/// their query.
/// </summary>
public sealed class RemoteServiceException : ExecutionException
{
    /// <summary>Creates a new <see cref="RemoteServiceException"/> with a user-facing message.</summary>
    public RemoteServiceException(string message)
        : base(message) { }

    /// <summary>
    /// Creates a new <see cref="RemoteServiceException"/> wrapping the underlying transport
    /// failure (HTTP error, timeout, DNS failure) that produced the user-facing message.
    /// </summary>
    public RemoteServiceException(string message, Exception? innerException)
        : base(message, innerException) { }

    /// <summary>
    /// Creates a new <see cref="RemoteServiceException"/> that additionally marks whether
    /// the failure is worth retrying (transient server-side / transport faults) or
    /// permanent (client errors, timeouts).
    /// </summary>
    public RemoteServiceException(string message, Exception? innerException, bool isRetryable)
        : base(message, innerException)
    {
        IsRetryable = isRetryable;
    }

    /// <summary>
    /// Whether a retry could plausibly succeed: true for 5xx statuses and transport
    /// failures, false for 4xx (the request is wrong) and timeouts (another
    /// full-length wait would only stall the caller further).
    /// </summary>
    public bool IsRetryable { get; }
}
