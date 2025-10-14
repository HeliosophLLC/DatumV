using System.IO.Compression;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Registry of named tables and their associated providers.
/// Resolves table names referenced in SQL FROM clauses to
/// <see cref="TableDescriptor"/> instances and creates the
/// appropriate <see cref="ITableProvider"/> for each.
/// </summary>
public sealed class TableCatalog : IDisposable
{
    /// <summary>
    /// Well-known option key stored on sub-table descriptors during multi-table expansion.
    /// The value is the sub-table qualifier used as the key in sidecar containers.
    /// </summary>
    public const string SubTableKeyOption = "datum:table_key";

    /// <summary>
    /// Providers that require seekable file access and cannot read from a forward-only
    /// decompression stream. Gzip-compressed sources for these providers are decompressed
    /// to a temporary file before registration.
    /// </summary>
    private static readonly HashSet<string> SeekableProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "parquet", "hdf5", "zip", "idx", "datum",
    };

    private readonly Dictionary<string, TableDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ITableProvider>> _providerFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QueryResultsManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Schema> _schemas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps table names to the sidecar file path that contains their index.
    /// Entries here have been discovered but not yet loaded — the index data is
    /// deserialized on first access rather than at sidecar discovery time,
    /// so that large index files do not consume heap memory until a query needs them.
    /// </summary>
    private readonly Dictionary<string, string> _pendingIndexSidecarPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks v5 memory-mapped index sets that must be disposed when the catalog is disposed.
    /// Each entry owns a <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> and its
    /// shared <see cref="System.IO.MemoryMappedFiles.MemoryMappedViewAccessor"/>.
    /// </summary>
    private readonly List<MappedSourceIndexSet> _mappedIndexSets = new();

    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// Tables that have been modified by DDL/DML since their last ANALYZE or sidecar rebuild.
    /// When a table name is absent from this set, ANALYZE is a no-op.
    /// </summary>
    private readonly HashSet<string> _analysisPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new <see cref="TableCatalog"/> with the datum provider factory
    /// pre-registered. All source formats are handled via ingestion, not direct querying.
    /// </summary>
    public TableCatalog()
    {
        _providerFactories["datum"] = () => new DatumFileTableProvider();
    }

    /// <summary>
    /// Optional parent catalog consulted when a table name is not found locally.
    /// Used by query context overlays to fall through to the session's base catalog
    /// for non-temp tables.
    /// </summary>
    public TableCatalog? Parent { get; init; }

    /// <summary>
    /// Registers a provider factory for a given provider identifier.
    /// </summary>
    /// <param name="providerName">Provider identifier (e.g. "csv", "json").</param>
    /// <param name="factory">Factory function that creates a new provider instance.</param>
    public void RegisterProvider(string providerName, Func<ITableProvider> factory)
    {
        _providerFactories[providerName] = factory;
    }

    /// <summary>
    /// Registers a table descriptor, making it available for resolution by name.
    /// </summary>
    /// <param name="descriptor">Table descriptor to register.</param>
    public void Register(TableDescriptor descriptor)
    {
        _descriptors[descriptor.Name] = descriptor;
    }

    /// <summary>
    /// Removes a previously registered table from the catalog, cleaning up any
    /// associated schemas, indexes, manifests, and pending sidecar references.
    /// </summary>
    /// <param name="tableName">The logical table name to remove.</param>
    /// <returns><see langword="true"/> if the table was found and removed; otherwise <see langword="false"/>.</returns>
    public bool Unregister(string tableName)
    {
        bool removed = _descriptors.Remove(tableName);

        if (removed)
        {
            _schemas.Remove(tableName);
            _indexes.Remove(tableName);
            _manifests.Remove(tableName);
            _pendingIndexSidecarPaths.Remove(tableName);
            _analysisPending.Remove(tableName);
        }

        return removed;
    }

    /// <summary>
    /// Marks a table as having been modified since its last analysis and removes
    /// any cached index, manifest, and schema so that stale metadata is never
    /// served to the query planner before a sidecar rebuild occurs.
    /// </summary>
    /// <param name="tableName">The logical table name.</param>
    public void InvalidateAnalysis(string tableName)
    {
        _analysisPending.Add(tableName);
        _indexes.Remove(tableName);
        _manifests.Remove(tableName);
        _schemas.Remove(tableName);
    }

    /// <summary>
    /// Marks a table as having been modified since its last analysis.
    /// A subsequent ANALYZE will rebuild sidecars; without this flag, ANALYZE is a no-op.
    /// </summary>
    /// <param name="tableName">The logical table name.</param>
    public void MarkAnalysisPending(string tableName)
    {
        _analysisPending.Add(tableName);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the table has been modified since its last analysis.
    /// </summary>
    /// <param name="tableName">The logical table name.</param>
    public bool IsAnalysisPending(string tableName)
    {
        return _analysisPending.Contains(tableName);
    }

    /// <summary>
    /// Clears the analysis-pending flag, indicating that sidecars are up to date.
    /// Called after a successful sidecar rebuild.
    /// </summary>
    /// <param name="tableName">The logical table name.</param>
    public void ClearAnalysisPending(string tableName)
    {
        _analysisPending.Remove(tableName);
    }

    /// <summary>
    /// Registers a table from a file path, using the full filename (including
    /// extension) as the table name and auto-detecting the provider.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined. Use the
    /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    /// </exception>
    public void Register(string filePath)
    {
        Register(FileFormatDetector.DeriveTableName(filePath), filePath);
    }

    /// <summary>
    /// Registers a table by name and file path, auto-detecting the provider
    /// from the file extension, filename pattern, or magic bytes.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined. Use the
    /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    /// </exception>
    public void Register(string name, string filePath)
    {
        Register(name, filePath, new Dictionary<string, string>());
    }

    /// <summary>
    /// Registers a table by name and file path with provider-specific options,
    /// auto-detecting the provider from the file extension, filename pattern,
    /// or magic bytes.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <param name="options">Provider-specific key-value options (e.g. delimiter, header).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined. Use the
    /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    /// </exception>
    public void Register(string name, string filePath, IReadOnlyDictionary<string, string> options)
    {
        DetectedFormat format = FileFormatDetector.DetectFormat(filePath)
            ?? throw new ArgumentException(
                $"Cannot detect file format for '{filePath}'. " +
                $"Supported formats: {FileFormatDetector.SupportedFormatList}. " +
                "Use Register(TableDescriptor) with an explicit provider.",
                nameof(filePath));

        if (format.Compression != CompressionKind.None && SeekableProviders.Contains(format.Provider))
        {
            string tempPath = DecompressGzip(filePath);
            _tempFiles.Add(tempPath);
            Register(new TableDescriptor(format.Provider, name, tempPath, options));
        }
        else
        {
            Register(new TableDescriptor(format.Provider, name, filePath, options, format.Compression));
        }
    }

    /// <summary>
    /// Registers a table by name and file path, auto-detecting the provider and
    /// expanding multi-table sources (e.g. root-object JSON files) in one call.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined.
    /// </exception>
    public Task RegisterAsync(string name, string filePath, CancellationToken cancellationToken)
    {
        return RegisterAsync(name, filePath, new Dictionary<string, string>(), cancellationToken);
    }

    /// <summary>
    /// Registers a table from a file path, using the full filename (including
    /// extension) as the table name, auto-detecting the provider, and expanding
    /// multi-table sources (e.g. root-object JSON files) in one call.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined.
    /// </exception>
    public Task RegisterAsync(string filePath, CancellationToken cancellationToken)
    {
        return RegisterAsync(FileFormatDetector.DeriveTableName(filePath), filePath, cancellationToken);
    }

    /// <summary>
    /// Registers a table by name and file path with provider-specific options,
    /// auto-detecting the provider and expanding multi-table sources
    /// (e.g. root-object JSON files) in one call.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <param name="options">Provider-specific key-value options (e.g. delimiter, header).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined.
    /// </exception>
    public async Task RegisterAsync(
        string name,
        string filePath,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        Register(name, filePath, options);
        await ExpandTableAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a table descriptor and expands multi-table sources
    /// (e.g. root-object JSON files) in one call.
    /// </summary>
    /// <param name="descriptor">Table descriptor to register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RegisterAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        Register(descriptor);
        await ExpandTableAsync(descriptor.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a table name to its descriptor.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <returns>The matching descriptor.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the table name is not registered.</exception>
    public TableDescriptor Resolve(string tableName)
    {
        if (_descriptors.TryGetValue(tableName, out TableDescriptor? descriptor))
        {
            return descriptor;
        }

        if (Parent is not null)
        {
            return Parent.Resolve(tableName);
        }

        throw new KeyNotFoundException($"Table '{tableName}' is not registered in the catalog.");
    }

    /// <summary>
    /// Creates a provider instance for the given descriptor.
    /// </summary>
    /// <param name="descriptor">Table descriptor whose provider should be created.</param>
    /// <returns>A new provider instance.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no factory is registered for the provider.</exception>
    public ITableProvider CreateProvider(TableDescriptor descriptor)
    {
        if (_providerFactories.TryGetValue(descriptor.Provider, out Func<ITableProvider>? factory))
        {
            return factory();
        }

        if (Parent is not null)
        {
            return Parent.CreateProvider(descriptor);
        }

        throw new KeyNotFoundException($"No provider factory registered for '{descriptor.Provider}'.");
    }

    /// <summary>
    /// Returns all registered table names, including those from the parent catalog.
    /// </summary>
    public IEnumerable<string> TableNames =>
        Parent is null
            ? _descriptors.Keys
            : _descriptors.Keys.Concat(
                Parent.TableNames.Where(name => !_descriptors.ContainsKey(name)));

    /// <summary>
    /// Returns the number of tables registered directly in this catalog,
    /// excluding any tables inherited from <see cref="Parent"/>.
    /// </summary>
    public int LocalTableCount => _descriptors.Count;

    /// <summary>
    /// Returns all registered provider names.
    /// </summary>
    public IEnumerable<string> ProviderNames => _providerFactories.Keys;

    /// <summary>
    /// Attempts to resolve a table name without throwing.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <param name="descriptor">The matching descriptor, or null if not found.</param>
    /// <returns>True if the table was found; otherwise false.</returns>
    public bool TryResolve(string tableName, out TableDescriptor? descriptor)
    {
        if (_descriptors.TryGetValue(tableName, out descriptor))
        {
            return true;
        }

        if (Parent is not null)
        {
            return Parent.TryResolve(tableName, out descriptor);
        }

        descriptor = null;
        return false;
    }

    /// <summary>
    /// Resolves a table name and returns its schema by creating the appropriate
    /// provider and calling <see cref="ITableProvider.GetSchemaAsync"/>.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schema of the named table.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when the table name or its provider is not registered.
    /// </exception>
    public async Task<Schema> GetSchemaAsync(string tableName, CancellationToken cancellationToken)
    {
        // Return cached schema from sidecar if available, avoiding provider I/O.
        // Uses TryGetSchema which chains to the parent catalog.
        if (TryGetSchema(tableName, out Schema? cachedSchema) && cachedSchema is not null)
        {
            return cachedSchema;
        }

        // Return cached schema from index if available, avoiding provider I/O.
        // Uses TryGetIndex which chains to the parent catalog.
        if (TryGetIndex(tableName, out SourceIndex? index) && index is not null)
        {
            return index.Schema.Schema;
        }

        TableDescriptor descriptor = Resolve(tableName);
        ITableProvider provider = CreateProvider(descriptor);
        return await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a pre-built source index for a table, enabling chunk-based
    /// partition pruning and cached schema resolution.
    /// </summary>
    /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    /// <param name="index">The source index to associate with the table.</param>
    public void RegisterIndex(string tableName, SourceIndex index)
    {
        _indexes[tableName] = index;
    }

    /// <summary>
    /// Registers a v5 memory-mapped index set loaded externally (e.g. via an explicit
    /// <c>--index-path</c> CLI option). The catalog takes ownership of the handle and
    /// will dispose it when the catalog itself is disposed.
    /// </summary>
    /// <param name="mappedIndexSet">The mapped index set to track for disposal.</param>
    internal void TrackMappedIndexSet(MappedSourceIndexSet mappedIndexSet)
    {
        _mappedIndexSets.Add(mappedIndexSet);
    }

    /// <summary>
    /// Attempts to retrieve a source index for the given table name.
    /// If the index was discovered via a sidecar file but not yet loaded, it is deserialized
    /// on demand here so that the heap cost is only paid when a query actually needs the index.
    /// </summary>
    /// <param name="tableName">Logical table name.</param>
    /// <param name="index">The source index, or <c>null</c> if none is registered.</param>
    /// <returns><c>true</c> if an index was found; otherwise <c>false</c>.</returns>
    public bool TryGetIndex(string tableName, out SourceIndex? index)
    {
        if (_indexes.TryGetValue(tableName, out index))
        {
            return true;
        }

        if (_pendingIndexSidecarPaths.ContainsKey(tableName))
        {
            LoadPendingIndexSidecar(tableName);
            return _indexes.TryGetValue(tableName, out index);
        }

        if (Parent is not null)
        {
            return Parent.TryGetIndex(tableName, out index);
        }

        index = null;
        return false;
    }

    /// <summary>
    /// Registers a pre-computed <see cref="QueryResultsManifest"/> for a table,
    /// enabling statistics-driven cardinality estimation in the query planner.
    /// </summary>
    /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    /// <param name="manifest">The manifest containing per-column statistics.</param>
    public void RegisterManifest(string tableName, QueryResultsManifest manifest)
    {
        _manifests[tableName] = manifest;
    }

    /// <summary>
    /// Attempts to retrieve a <see cref="QueryResultsManifest"/> for the given table name.
    /// </summary>
    /// <param name="tableName">Logical table name.</param>
    /// <param name="manifest">The manifest, or <c>null</c> if none is registered.</param>
    /// <returns><c>true</c> if a manifest was found; otherwise <c>false</c>.</returns>
    public bool TryGetManifest(string tableName, out QueryResultsManifest? manifest)
    {
        if (_manifests.TryGetValue(tableName, out manifest))
        {
            return true;
        }

        if (Parent is not null)
        {
            return Parent.TryGetManifest(tableName, out manifest);
        }

        manifest = null;
        return false;
    }

    /// <summary>
    /// Registers a pre-computed <see cref="Schema"/> for a table,
    /// enabling cached schema resolution without provider I/O.
    /// </summary>
    /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    /// <param name="schema">The schema to cache.</param>
    public void RegisterSchema(string tableName, Schema schema)
    {
        _schemas[tableName] = schema;
    }

    /// <summary>
    /// Attempts to retrieve a cached <see cref="Schema"/> for the given table name.
    /// </summary>
    /// <param name="tableName">Logical table name.</param>
    /// <param name="schema">The cached schema, or <c>null</c> if none is registered.</param>
    /// <returns><c>true</c> if a cached schema was found; otherwise <c>false</c>.</returns>
    public bool TryGetSchema(string tableName, out Schema? schema)
    {
        if (_schemas.TryGetValue(tableName, out schema))
        {
            return true;
        }

        if (Parent is not null)
        {
            return Parent.TryGetSchema(tableName, out schema);
        }

        schema = null;
        return false;
    }

    /// <summary>
    /// Auto-discovers <c>.datum-index</c>, <c>.datum-manifest</c>, <c>.datum-vocabulary</c>,
    /// and <c>.datum-schema</c> sidecar files for all registered tables. Each sidecar is
    /// loaded at most once per unique source file path, and tables that already have a
    /// registered artifact are skipped.
    /// </summary>
    /// <remarks>
    /// This is the single entry point for sidecar discovery, replacing the per-site
    /// implementations that previously existed in the CLI, gRPC server, and compute backend.
    /// Call this after all tables have been registered and expanded.
    /// </remarks>
    public void DiscoverSidecars()
    {
        HashSet<string> loadedIndexPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loadedManifestPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loadedVocabularyPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loadedSchemaPaths = new(StringComparer.OrdinalIgnoreCase);

        // Snapshot table names to avoid issues if the set is mutated.
        List<string> tableNames = new(_descriptors.Keys);

        foreach (string tableName in tableNames)
        {
            TableDescriptor descriptor = _descriptors[tableName];

            DiscoverSidecarIndex(descriptor, tableNames, loadedIndexPaths);
            DiscoverSidecarManifest(descriptor, tableNames, loadedManifestPaths);
            DiscoverSidecarVocabulary(descriptor, tableNames, loadedVocabularyPaths);
            DiscoverSidecarSchema(descriptor, tableNames, loadedSchemaPaths);
        }
    }

    private void DiscoverSidecarIndex(
        TableDescriptor descriptor,
        List<string> tableNames,
        HashSet<string> loadedPaths)
    {
        string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-index";

        if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
        {
            return;
        }

        // Register pending entries for all tables that share this source file without
        // reading the index data. The actual deserialization is deferred until the first
        // call to TryGetIndex, so that large index files (potentially gigabytes after
        // decompression) do not consume heap memory at shell startup time.
        foreach (string name in tableNames)
        {
            if (!_descriptors.TryGetValue(name, out TableDescriptor? d)
                || !string.Equals(d.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase)
                || _indexes.ContainsKey(name)
                || _pendingIndexSidecarPaths.ContainsKey(name))
            {
                continue;
            }

            _pendingIndexSidecarPaths[name] = sidecarPath;
        }
    }

    /// <summary>
    /// Deserializes the sidecar file associated with <paramref name="tableName"/> and registers
    /// the resulting <see cref="SourceIndex"/> for every pending table that references the same
    /// sidecar, so the file is read from disk at most once per sidecar path.
    /// </summary>
    private void LoadPendingIndexSidecar(string tableName)
    {
        if (!_pendingIndexSidecarPaths.TryGetValue(tableName, out string? sidecarPath))
        {
            return;
        }

        // Collect every table waiting on the same sidecar file so all are populated
        // in one pass rather than deserializing the (potentially very large) file repeatedly.
        List<string> pendingNames = _pendingIndexSidecarPaths
            .Where(pair => string.Equals(pair.Value, sidecarPath, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        foreach (string name in pendingNames)
        {
            _pendingIndexSidecarPaths.Remove(name);
        }

        if (!File.Exists(sidecarPath))
        {
            return;
        }

        MappedSourceIndexSet mapped = UnifiedIndexReader.Open(sidecarPath);
        _mappedIndexSets.Add(mapped);
        SourceIndexSet indexSet = mapped.IndexSet;

        foreach (string name in pendingNames)
        {
            if (_indexes.ContainsKey(name) || !_descriptors.TryGetValue(name, out TableDescriptor? d))
            {
                continue;
            }

            SourceIndex? entry = ResolveSidecarEntry(indexSet.Tables, name, d.FilePath);
            if (entry is not null)
            {
                _indexes[name] = entry;
            }
        }
    }

    private void DiscoverSidecarManifest(
        TableDescriptor descriptor,
        List<string> tableNames,
        HashSet<string> loadedPaths)
    {
        string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-manifest";

        if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
        {
            return;
        }

        string json = File.ReadAllText(sidecarPath);
        SourceManifest? sourceManifest = ManifestSerializer.Deserialize(json);

        if (sourceManifest is null)
        {
            return;
        }

        RegisterSidecarEntries(
            sourceManifest.Tables,
            descriptor.FilePath,
            tableNames,
            (name, manifest) => { if (!_manifests.ContainsKey(name)) RegisterManifest(name, manifest); });
    }

    private void DiscoverSidecarVocabulary(
        TableDescriptor descriptor,
        List<string> tableNames,
        HashSet<string> loadedPaths)
    {
        string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-vocabulary";

        if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
        {
            return;
        }

        string json = File.ReadAllText(sidecarPath);
        SourceVocabularySet? vocabularySet = ManifestSerializer.DeserializeVocabulary(json);

        if (vocabularySet is null)
        {
            return;
        }

        // Attach vocabularies to already-registered manifests rather than registering
        // a separate artifact. Each table's vocabulary set is applied to its manifest,
        // enabling exact Jaccard/containment scoring during schema matching analysis.
        foreach (string name in tableNames)
        {
            if (!_descriptors.TryGetValue(name, out TableDescriptor? d)
                || !string.Equals(d.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? resolvedName = ResolveVocabularyTableName(vocabularySet, name, d.FilePath);

            if (resolvedName is not null
                && vocabularySet.Tables.TryGetValue(resolvedName, out TableVocabularySet? tableVocabularySet)
                && _manifests.TryGetValue(name, out QueryResultsManifest? manifest))
            {
                tableVocabularySet.ApplyTo(manifest);
            }
        }
    }

    private static string? ResolveVocabularyTableName(
        SourceVocabularySet vocabularySet,
        string tableName,
        string sourceFilePath)
    {
        if (vocabularySet.Tables.ContainsKey(tableName))
        {
            return tableName;
        }

        string derivedTableName = FileFormatDetector.DeriveTableName(sourceFilePath);

        if (vocabularySet.Tables.ContainsKey(derivedTableName))
        {
            return derivedTableName;
        }

        return null;
    }

    private void DiscoverSidecarSchema(
        TableDescriptor descriptor,
        List<string> tableNames,
        HashSet<string> loadedPaths)
    {
        string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-schema";

        if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
        {
            return;
        }

        string json = File.ReadAllText(sidecarPath);
        SourceSchema? sourceSchema = SchemaSerializer.Deserialize(json);

        if (sourceSchema is null)
        {
            return;
        }

        RegisterSidecarEntries(
            sourceSchema.Tables,
            descriptor.FilePath,
            tableNames,
            (name, schema) => { if (!_schemas.ContainsKey(name)) _schemas[name] = schema; });
    }

    /// <summary>
    /// Matches sidecar entries to registered tables sharing the same source file path,
    /// then invokes <paramref name="register"/> for each match.
    /// </summary>
    private void RegisterSidecarEntries<T>(
        IReadOnlyDictionary<string, T> sidecarEntries,
        string filePath,
        List<string> tableNames,
        Action<string, T> register)
        where T : class
    {
        foreach (string name in tableNames)
        {
            if (!_descriptors.TryGetValue(name, out TableDescriptor? d)
                || !string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            T? value = ResolveSidecarEntry(sidecarEntries, name, d.FilePath);
            if (value is not null)
            {
                register(name, value);
            }
        }
    }

    private static T? ResolveSidecarEntry<T>(
        IReadOnlyDictionary<string, T> sidecarEntries,
        string tableName,
        string sourceFilePath)
        where T : class
    {
        T? value;

        // Primary key: registered catalog table name (for current sidecar format).
        if (sidecarEntries.TryGetValue(tableName, out value))
        {
            return value;
        }

        // Fallback: name derived from file conventions (e.g. orders_csv).
        string derivedTableName = FileFormatDetector.DeriveTableName(sourceFilePath);
        if (sidecarEntries.TryGetValue(derivedTableName, out value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Expands multi-table sources by discovering sub-tables for providers that implement
    /// <see cref="IMultiTableSource"/>. Each discovered sub-table replaces the original
    /// registration with a qualified name (<c>{baseName}.{subTableName}</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExpandMultiTableSourcesAsync(CancellationToken cancellationToken)
    {
        // Snapshot keys to allow mutation during iteration.
        List<string> tableNames = new(_descriptors.Keys);

        foreach (string tableName in tableNames)
        {
            await ExpandTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Expands a single table registration if its provider implements
    /// <see cref="IMultiTableSource"/>, replacing it with one entry per discovered sub-table.
    /// Does nothing when the provider is not a multi-table source or discovery returns no results.
    /// </summary>
    /// <param name="tableName">Logical table name to attempt expansion on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ExpandTableAsync(string tableName, CancellationToken cancellationToken)
    {
        if (!_descriptors.TryGetValue(tableName, out TableDescriptor? descriptor))
        {
            return;
        }

        ITableProvider provider = CreateProvider(descriptor);

        if (provider is not IMultiTableSource multiTableSource)
        {
            return;
        }

        IReadOnlyList<DiscoveredTable>? discovered = await multiTableSource
            .DiscoverTablesAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);

        if (discovered is null || discovered.Count == 0)
        {
            return;
        }

        // Remove the original single-table registration.
        _descriptors.Remove(tableName);

        // Register each discovered sub-table with a qualified name.
        foreach (DiscoveredTable subTable in discovered)
        {
            string qualifiedName = $"{tableName}_{subTable.Name}";

            // Merge sub-table options with the table-key marker.
            Dictionary<string, string> mergedOptions = new(subTable.Options)
            {
                [SubTableKeyOption] = subTable.Name
            };

            TableDescriptor subDescriptor = new(
                descriptor.Provider,
                qualifiedName,
                descriptor.FilePath,
                mergedOptions,
                descriptor.Compression);

            _descriptors[qualifiedName] = subDescriptor;
        }
    }

    /// <summary>
    /// Synchronously decompresses a gzip file to a temporary file.
    /// </summary>
    private static string DecompressGzip(string gzipFilePath)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"datum_gz_{Guid.NewGuid():N}");

        using FileStream source = new(
            gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);

        using GZipStream gzipStream = new(source, CompressionMode.Decompress);

        using FileStream target = new(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920);

        gzipStream.CopyTo(target);
        return tempPath;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (MappedSourceIndexSet mapped in _mappedIndexSets)
        {
            mapped.Dispose();
        }

        _mappedIndexSets.Clear();

        foreach (string tempFile in _tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        _tempFiles.Clear();
    }
}
