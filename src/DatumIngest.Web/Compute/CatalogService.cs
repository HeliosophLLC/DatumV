namespace DatumIngest.Web.Compute;

// The boundary every catalog operation crosses. Today's only implementation
// is in-process; tomorrow's gRPC adapter implements the same contract for
// remote compute nodes. Discipline: types flowing through this interface
// must be wire-serializable (no arena-backed DataValue, no IPlan, etc.).
//
// The full IStatementResult taxonomy (Schema / Rows / RowsChunk / Affected /
// Status / StatementError / NestedResults) is deferred to its own round
// alongside the engine-output migration. Today the interface exists as a
// seam so controllers and the DI graph are shaped correctly — the method
// signatures stake out the contract.
public interface ICatalogService
{
    IAsyncEnumerable<IStatementResult> ExecuteAsync(string sql, CancellationToken ct);
}

// Discriminated union root. Variants land in a subsequent round.
public abstract record IStatementResult(int StatementIndex);
