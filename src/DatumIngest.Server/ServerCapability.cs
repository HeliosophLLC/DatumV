namespace DatumIngest.Server;

/// <summary>
/// Maps <see cref="SessionRole"/> values to the set of
/// <see cref="ServerOperation"/> values each role is authorized to perform.
/// </summary>
public static class ServerCapability
{
    private static readonly HashSet<ServerOperation> UserOperations = new()
    {
        ServerOperation.Query,
        ServerOperation.Schema,
        ServerOperation.Explain,
        ServerOperation.Stats,
    };

    /// <summary>
    /// Determines whether the given role is authorized to perform the specified operation.
    /// </summary>
    /// <param name="role">The session's role.</param>
    /// <param name="operation">The operation being attempted.</param>
    /// <returns><see langword="true"/> if the role is authorized; otherwise <see langword="false"/>.</returns>
    public static bool IsAuthorized(SessionRole role, ServerOperation operation)
    {
        if (role == SessionRole.Admin)
        {
            return true;
        }

        return UserOperations.Contains(operation);
    }
}
