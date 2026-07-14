namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Thrown when a session-setting operation fails at execution time: an
/// unknown or unresolvable time zone name in <c>SET TIME ZONE</c>, or an
/// unrecognized configuration parameter passed to <c>current_setting()</c>.
/// The message names the offending value and is safe to surface verbatim.
/// </summary>
public sealed class SessionSettingException : ExecutionException
{
    /// <summary>Creates a new <see cref="SessionSettingException"/> with a user-facing message.</summary>
    public SessionSettingException(string message)
        : base(message) { }

    /// <summary>
    /// Creates a new <see cref="SessionSettingException"/> with a user-facing message and the
    /// underlying lookup failure (e.g. <see cref="TimeZoneNotFoundException"/>).
    /// </summary>
    public SessionSettingException(string message, Exception? innerException)
        : base(message, innerException) { }
}
