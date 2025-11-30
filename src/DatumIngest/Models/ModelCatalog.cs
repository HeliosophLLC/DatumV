using System.Collections.Concurrent;

namespace DatumIngest.Models;

/// <summary>
/// Process-scoped registry of <see cref="ModelCatalogEntry"/> records and the
/// <see cref="IModel"/> instances they produce. Lives outside <c>ExecutionContext</c>
/// because models are server-wide resources: a loaded model is amortised across
/// queries, sessions, and tenants. Per-query state (memory budget, query meter,
/// spill arenas) belongs to the context; model residency does not.
/// </summary>
/// <remarks>
/// <para>
/// Lookup is namespaced by the SQL surface: <c>models.mobilenetv2</c> resolves to
/// the entry whose <see cref="ModelCatalogEntry.Name"/> equals <c>"mobilenetv2"</c>.
/// The leading <c>"models."</c> qualifier is stripped by the planner before lookup.
/// </para>
/// <para>
/// The actual load / cache / evict lifecycle lives in
/// <see cref="ModelResidencyManager"/>. The catalog hands out
/// <see cref="ModelLease"/>s via <see cref="AcquireAsync"/>; callers
/// (<c>ModelInvocationOperator</c>) hold the lease for the duration of their
/// model use and dispose it when done. Tests that just want to verify "is the
/// catalog wired correctly?" can use the synchronous
/// <see cref="ResolveLeaseSynchronously"/> helper.
/// </para>
/// </remarks>
public sealed class ModelCatalog : IDisposable
{
    /// <summary>
    /// Default model directory when none is explicitly configured. Resolved in
    /// this order:
    /// <list type="number">
    ///   <item><description>The <c>DATUM_MODELS</c> environment variable, if set.</description></item>
    ///   <item><description>A portable per-user fallback —
    ///     <c>%LOCALAPPDATA%/DatumIngest/models</c> on Windows,
    ///     <c>~/.local/share/DatumIngest/models</c> on Linux/macOS — via
    ///     <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    ///   </description></item>
    /// </list>
    /// Production deployments either set the env var or pass an explicit path
    /// to the constructor. Tests rely on the env var being set on developer
    /// machines and self-skip when the model file is absent.
    /// </summary>
    public static string DefaultModelDirectory =>
        Environment.GetEnvironmentVariable("DATUM_MODELS")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatumIngest",
            "models");

    private readonly ConcurrentDictionary<string, ModelCatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute path to the directory holding model files. Resolved at construction;
    /// each entry's <see cref="ModelCatalogEntry.RelativePath"/> is combined with this
    /// at load time.
    /// </summary>
    public string ModelDirectory { get; }

    /// <summary>
    /// The residency manager that owns the loaded <see cref="IModel"/>
    /// instances and enforces the VRAM budget. Created with the catalog;
    /// budget defaults to <see cref="ModelResidencyManager.UnlimitedBudget"/>
    /// — set <see cref="VramBudgetBytes"/> to bound it.
    /// </summary>
    public ModelResidencyManager ResidencyManager { get; }

    /// <summary>
    /// Convenience accessor for the residency manager's VRAM budget. Setting
    /// after construction is not supported in this build — initialise via the
    /// <see cref="ModelCatalog(string?, long, TimeSpan?)"/> ctor when you need
    /// a non-default budget.
    /// </summary>
    public long VramBudgetBytes => ResidencyManager.VramBudgetBytes;

    /// <summary>Creates a catalog rooted at <paramref name="modelDirectory"/>.</summary>
    /// <param name="modelDirectory">
    /// Absolute path to the models directory. <see langword="null"/> uses
    /// <see cref="DefaultModelDirectory"/>.
    /// </param>
    public ModelCatalog(string? modelDirectory = null)
        : this(modelDirectory, ModelResidencyManager.UnlimitedBudget, admissionTimeout: null)
    {
    }

    /// <summary>
    /// Creates a catalog with a specific VRAM budget and optional admission
    /// timeout. Use this overload when you want eviction to actually fire —
    /// the parameterless form leaves the budget unlimited (load-and-hold-
    /// forever, the same shape as before residency was introduced).
    /// </summary>
    public ModelCatalog(string? modelDirectory, long vramBudgetBytes, TimeSpan? admissionTimeout)
    {
        ModelDirectory = modelDirectory ?? DefaultModelDirectory;
        ResidencyManager = new ModelResidencyManager(vramBudgetBytes, admissionTimeout);
    }

    /// <summary>
    /// Registers <paramref name="entry"/>. Throws when an entry with the same name
    /// is already registered — replacement requires explicit
    /// <see cref="Unregister"/> first to avoid silent shadowing.
    /// </summary>
    public void Register(ModelCatalogEntry entry)
    {
        if (!_entries.TryAdd(entry.Name, entry))
        {
            throw new InvalidOperationException(
                $"Model '{entry.Name}' is already registered. Call Unregister first if replacement is intended.");
        }
    }

    /// <summary>
    /// Removes the entry. Any already-loaded <see cref="IModel"/> instance
    /// stays in the residency manager until it's evicted naturally — this
    /// just removes the registration so future acquires for the same name
    /// fail to resolve.
    /// </summary>
    public bool Unregister(string name)
    {
        return _entries.TryRemove(name, out _);
    }

    /// <summary>
    /// Returns the entry for <paramref name="name"/> if registered.
    /// </summary>
    public ModelCatalogEntry? TryGetEntry(string name)
        => _entries.TryGetValue(name, out ModelCatalogEntry? entry) ? entry : null;

    /// <summary>
    /// All registered entries, keyed by <see cref="ModelCatalogEntry.Name"/>.
    /// Used by the future <c>sys.models</c> virtual table to project catalog
    /// state into SQL.
    /// </summary>
    public IReadOnlyDictionary<string, ModelCatalogEntry> Entries => _entries;

    /// <summary>
    /// Acquires a <see cref="ModelLease"/> for the given model name, loading
    /// the model into VRAM if not already resident. The lease holds an
    /// active ref until disposed; callers must use <c>using</c> (or
    /// equivalent) at the call site so the manager can evict the model
    /// after the work completes.
    /// </summary>
    /// <param name="name">Catalog name (the unqualified model identifier — no <c>models.</c> prefix).</param>
    /// <param name="cancellationToken">Honoured during admission-timeout polling.</param>
    /// <returns>A lease wrapping the loaded model.</returns>
    /// <exception cref="InvalidOperationException">
    /// No entry registered for <paramref name="name"/>, or admission timed out.
    /// </exception>
    public Task<ModelLease> AcquireAsync(string name, CancellationToken cancellationToken)
    {
        ModelCatalogEntry entry = TryGetEntry(name)
            ?? throw new InvalidOperationException(
                $"No model registered as '{name}'. Register it via ModelCatalog.Register before referencing it from SQL.");

        return ResidencyManager.AcquireAsync(entry, ModelDirectory, cancellationToken);
    }

    /// <summary>
    /// Synchronous resolve for tests / setup paths that just need the model
    /// instance and don't care about the residency lifecycle. The returned
    /// lease MUST still be disposed; this is just sugar over
    /// <see cref="AcquireAsync"/>.<see cref="Task{T}.GetAwaiter"/>.<see cref="System.Runtime.CompilerServices.TaskAwaiter{T}.GetResult"/>.
    /// </summary>
    public ModelLease ResolveLeaseSynchronously(string name)
        => AcquireAsync(name, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Dispose() => ResidencyManager.Dispose();
}
