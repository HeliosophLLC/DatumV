using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the routine-DDL side of a <see cref="TableCatalog"/>: registering and
/// unregistering UDFs and procedures, validating their bodies against the
/// inliner, and writing the result through to the optional
/// <see cref="CatalogStore"/>. Extracted from <see cref="TableCatalog"/> so the
/// catalog stays focused on table/provider concerns and the routine-management
/// surface lives in one place.
/// </summary>
/// <remarks>
/// All four <c>Apply</c> entry points map 1:1 to a SQL statement:
/// <c>CREATE FUNCTION</c>, <c>DROP FUNCTION</c>, <c>CREATE PROCEDURE</c>,
/// <c>DROP PROCEDURE</c>. Each method mutates the registries and, when a
/// <see cref="CatalogStore"/> is configured, persists the resulting state
/// atomically. The dispatch logic itself stays in
/// <see cref="TableCatalog.PlanAsync(string)"/> / <see cref="TableCatalog.PlanAsync(Statement)"/>;
/// callers reach this class only through the catalog.
/// </remarks>
internal sealed partial class RoutineRegistrar
{
    private readonly TableCatalog _catalog;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly FunctionRegistry _functions;
    private readonly CatalogStore? _catalogStore;

    /// <summary>
    /// Wires the registrar to the catalog, registries, and (optional)
    /// persistent store it operates on. The instances are held by reference —
    /// every mutation goes through the same UDF / procedure / function
    /// registries the catalog exposes publicly, and every save targets the
    /// same file. The catalog reference exists so the registrar can build
    /// per-call <see cref="SchemaResolver"/> instances against the current
    /// session search_path.
    /// </summary>
    public RoutineRegistrar(
        TableCatalog catalog,
        UdfRegistry udfs,
        ProcedureRegistry procedures,
        FunctionRegistry functions,
        CatalogStore? catalogStore)
    {
        _catalog = catalog;
        _udfs = udfs;
        _procedures = procedures;
        _functions = functions;
        _catalogStore = catalogStore;
    }

    private SchemaResolver Resolver() => new(_catalog, _catalog.SearchPath);

    /// <summary>
    /// Reconciles every procedural UDF loaded from the catalog file. Two
    /// things happen per descriptor:
    /// <list type="bullet">
    ///   <item><description>Macro references inside the body are inlined
    ///   against the now-fully-loaded registry, mirroring what the
    ///   register-time pass does for fresh CREATE FUNCTION calls.</description></item>
    ///   <item><description>A <see cref="ProceduralUdfFunction"/> adapter is
    ///   wired into the scalar-function registry so call sites can dispatch.
    ///   </description></item>
    /// </list>
    /// Order matters: macro UDFs the procedural body depends on must be
    /// loaded first. The catalog file persists entries alphabetically, so
    /// users who name their procedurals after their dependencies (the
    /// existing macro chain rule) get correct ordering for free.
    /// </summary>
    public void SyncProceduralAdaptersFromRegistry()
    {
        // Snapshot before mutating so we don't iterate a collection that
        // we replace entries in.
        UdfDescriptor[] proceduralEntries = _udfs.Entries
            .Where(d => d.IsProcedural)
            .ToArray();

        foreach (UdfDescriptor descriptor in proceduralEntries)
        {
            IReadOnlyList<Statement> rewrittenBody = RewriteBodyWithInlinedMacros(
                descriptor.StatementBody!);
            UdfDescriptor finalDescriptor = descriptor with { StatementBody = rewrittenBody };
            _udfs.Register(finalDescriptor, replace: true);
            RegisterProceduralAdapter(finalDescriptor, replace: true);
        }
    }




    // ───────────────────── Shared validation ─────────────────────

    /// <summary>
    /// Enforces that any parameters with <see cref="UdfParameter.Default"/>
    /// values appear contiguously at the tail of the parameter list. Without
    /// this constraint a call site like <c>foo(1, 2)</c> against
    /// <c>foo(@a, @b = 0, @c)</c> would be ambiguous — does the second
    /// argument bind to <c>@b</c> or (with default <c>@b</c>) to <c>@c</c>?
    /// Disallowing the shape removes the ambiguity at registration time.
    /// </summary>
    private static void ValidateDefaultsContiguous(
        IReadOnlyList<UdfParameter> parameters, string contextLabel)
    {
        bool sawDefault = false;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null)
            {
                sawDefault = true;
            }
            else if (sawDefault)
            {
                throw new InvalidOperationException(
                    $"{contextLabel}: parameter '{p.Name}' has no default but follows a parameter " +
                    "with a default. Defaults must be contiguous at the end of the parameter list.");
            }
        }
    }
}
