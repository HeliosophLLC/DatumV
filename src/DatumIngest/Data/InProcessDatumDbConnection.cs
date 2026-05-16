using DatumIngest.Catalog;

namespace DatumIngest.Data;

/// <summary>
/// ADO.NET-style connection over an in-process <see cref="TableCatalog"/>.
/// The connection is a thin handle: it carries a catalog reference and
/// hands out <see cref="InProcessDatumDbCommand"/> instances. There is no
/// session-level state today (no transactions, no connection-scoped
/// search-path) so <see cref="Dispose"/> is a no-op.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle is intentionally trivial — the heavy state lives on the
/// catalog (which the caller owns) or on the command/reader pair (which
/// own a per-execute <see cref="Execution.ExecutionContext"/>).
/// A future remote variant would distinguish itself with non-trivial
/// transport state at this layer; the in-process implementation does not.
/// </para>
/// </remarks>
public sealed class InProcessDatumDbConnection : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Initializes a new connection bound to <paramref name="catalog"/>.
    /// The catalog is held by reference; the caller owns its lifetime.
    /// </summary>
    public InProcessDatumDbConnection(TableCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
    }

    /// <summary>The catalog this connection executes against.</summary>
    public TableCatalog Catalog { get; }

    /// <summary>Creates an empty command. Set <see cref="InProcessDatumDbCommand.CommandText"/> before executing.</summary>
    public InProcessDatumDbCommand CreateCommand() => new(this);

    /// <summary>Creates a command pre-populated with the supplied SQL text.</summary>
    public InProcessDatumDbCommand CreateCommand(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        InProcessDatumDbCommand command = new(this);
        command.CommandText = commandText;
        return command;
    }

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
