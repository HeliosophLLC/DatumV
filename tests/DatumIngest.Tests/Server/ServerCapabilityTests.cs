using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for the <see cref="ServerCapability"/> authorization model.
/// </summary>
public sealed class ServerCapabilityTests
{
    /// <summary>
    /// Admin role is authorized for every operation.
    /// </summary>
    [Theory]
    [InlineData(ServerOperation.Query)]
    [InlineData(ServerOperation.Schema)]
    [InlineData(ServerOperation.Explain)]
    [InlineData(ServerOperation.Stats)]
    [InlineData(ServerOperation.AddSource)]
    [InlineData(ServerOperation.RemoveSource)]
    [InlineData(ServerOperation.ReloadCatalog)]
    [InlineData(ServerOperation.ManageIndexes)]
    [InlineData(ServerOperation.ListSessions)]
    [InlineData(ServerOperation.KillQuery)]
    [InlineData(ServerOperation.Shutdown)]
    public void IsAuthorized_AdminRole_ReturnsTrue(ServerOperation operation)
    {
        Assert.True(ServerCapability.IsAuthorized(SessionRole.Admin, operation));
    }

    /// <summary>
    /// User role is authorized for read-only operations.
    /// </summary>
    [Theory]
    [InlineData(ServerOperation.Query)]
    [InlineData(ServerOperation.Schema)]
    [InlineData(ServerOperation.Explain)]
    [InlineData(ServerOperation.Stats)]
    public void IsAuthorized_UserRoleReadOperations_ReturnsTrue(ServerOperation operation)
    {
        Assert.True(ServerCapability.IsAuthorized(SessionRole.User, operation));
    }

    /// <summary>
    /// User role is denied administrative operations.
    /// </summary>
    [Theory]
    [InlineData(ServerOperation.AddSource)]
    [InlineData(ServerOperation.RemoveSource)]
    [InlineData(ServerOperation.ReloadCatalog)]
    [InlineData(ServerOperation.ManageIndexes)]
    [InlineData(ServerOperation.ListSessions)]
    [InlineData(ServerOperation.KillQuery)]
    [InlineData(ServerOperation.Shutdown)]
    public void IsAuthorized_UserRoleAdminOperations_ReturnsFalse(ServerOperation operation)
    {
        Assert.False(ServerCapability.IsAuthorized(SessionRole.User, operation));
    }
}
