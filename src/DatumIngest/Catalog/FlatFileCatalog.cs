using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// The default <see cref="ITableCatalog"/> backend: one <c>.datum</c>
/// file per persistent table, sidecars colocated, manifest persisted to
/// a single <c>.datum-catalog.json</c>. Owns the table registry, all
/// persistent-state tracking, and the file-touching half of CREATE /
/// DROP / CREATE INDEX / DROP INDEX.
/// </summary>
/// <remarks>
/// <para>
/// This is the file-based shape inherited from the original
/// <see cref="TableCatalog"/>. The future <c>DatumDbCatalog</c> (single
/// <c>.datumdb</c> + WAL) will be a different <see cref="ITableCatalog"/>
/// implementation; the facade (<see cref="TableCatalog"/>) doesn't change.
/// </para>
/// <para>
/// The catalog hosts more than persistent <c>.datum</c> tables — TEMP
/// tables (in-memory), system projections (UDF / Procedure / Model
/// virtual tables), and information_schema / datum_catalog views all
/// live in the same <see cref="_tables"/> registry today. Persistent
/// tracking dicts (file paths, indexes, PK names) only carry entries
/// for <c>.datum</c>-backed tables — the rest stay out by design, so
/// the manifest only persists what should actually round-trip on
/// reopen.
/// </para>
/// </remarks>
public sealed class FlatFileCatalog : ITableCatalog
{
    private readonly Pool _pool;
    private readonly SidecarRegistry _sidecarRegistry;
    private readonly string? _catalogDirectory;

    /// <summary>
    /// Invoked after every mutation that changes the persistent manifest
    /// (CREATE / DROP TABLE, CREATE / DROP INDEX, DROP CONSTRAINT name).
    /// Wired by the facade to <see cref="CatalogStore.Save"/> with the
    /// current UDF / Procedure registries.
    /// </summary>
    private readonly Action _persistManifest;

    private readonly ConcurrentDictionary<QualifiedName, ITableProvider> _tables = new();

    /// <summary>
    /// Persisted file path per <c>.datum</c>-backed table. Tables added
    /// via host-side <see cref="ITableCatalog.Add"/> (system / virtual
    /// providers, in-memory providers, TEMP TABLE) don't appear here.
    /// </summary>
    private readonly Dictionary<QualifiedName, string> _persistentTableEntries = new();

    /// <summary>User-defined secondary indexes per persistent table.</summary>
    private readonly Dictionary<QualifiedName, List<IndexDescriptor>> _persistentTableIndexes = new();

    /// <summary>Reverse lookup: index name → owning table.</summary>
    private readonly Dictionary<string, QualifiedName> _indexNameToTable =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-supplied PRIMARY KEY constraint names per table.</summary>
    private readonly Dictionary<QualifiedName, string> _persistentTablePkNames = new();

    /// <summary>
    /// Creates a backend with the given configuration.
    /// </summary>
    /// <param name="pool">Buffer pool used by new providers.</param>
    /// <param name="sidecarRegistry">
    /// Catalog-wide sidecar registry. Owned by the facade; this backend
    /// holds a reference so it can wire newly-added providers' sidecar
    /// store IDs.
    /// </param>
    /// <param name="catalogDirectory">
    /// Directory the persistent manifest lives in, used as the anchor for
    /// resolving relative table paths. <see langword="null"/> when no
    /// catalog file is attached — persistent CREATE TABLE is rejected
    /// upstream in that case.
    /// </param>
    /// <param name="persistManifest">
    /// Callback invoked after every state change to write the catalog
    /// manifest. Wraps <see cref="CatalogStore.Save"/> with the facade's
    /// UDF / Procedure registries.
    /// </param>
    public FlatFileCatalog(
        Pool pool,
        SidecarRegistry sidecarRegistry,
        string? catalogDirectory,
        Action persistManifest)
    {
        _pool = pool;
        _sidecarRegistry = sidecarRegistry;
        _catalogDirectory = catalogDirectory;
        _persistManifest = persistManifest;
    }

    /// <inheritdoc/>
    public bool SupportsDdl => true;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Schemas
    {
        get
        {
            HashSet<string> schemas = new(StringComparer.OrdinalIgnoreCase);
            foreach (QualifiedName name in _tables.Keys)
            {
                schemas.Add(name.Schema);
            }
            return schemas;
        }
    }

    /// <inheritdoc/>
    public int Count => _tables.Count;

    /// <inheritdoc/>
    public bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider)
        => _tables.TryGetValue(name, out provider);

    /// <inheritdoc/>
    public IEnumerable<ITableProvider> ListTables() => _tables.Values;

    /// <inheritdoc/>
    public ITableProvider Add(ITableProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_tables.TryAdd(provider.QualifiedName, provider))
        {
            throw new ArgumentException(
                $"A table with the name '{provider.QualifiedName}' is already registered.");
        }
        RegisterProviderSidecar(provider);
        return provider;
    }

    /// <inheritdoc/>
    public bool DropTable(QualifiedName name)
    {
        if (!_tables.TryGetValue(name, out ITableProvider? provider))
        {
            return false;
        }

        string? persistedPath = _persistentTableEntries.TryGetValue(name, out string? p) ? p : null;

        _tables.TryRemove(name, out _);
        try { provider.Dispose(); }
        catch { /* best-effort */ }

        if (persistedPath is not null)
        {
            string resolved = ResolveTablePath(persistedPath);
            TryDeleteFile(resolved);
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-blob"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-index"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-manifest"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-pkindex"));

            // User-defined secondary index sidecars (one per CREATE INDEX).
            string? dir = System.IO.Path.GetDirectoryName(resolved);
            string stem = System.IO.Path.GetFileNameWithoutExtension(resolved);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(stem))
            {
                foreach (string cindexFile in Directory.EnumerateFiles(dir, stem + ".datum-cindex-*"))
                {
                    TryDeleteFile(cindexFile);
                }
                foreach (string ftsFile in Directory.EnumerateFiles(dir, stem + ".datum-fts-*"))
                {
                    TryDeleteFile(ftsFile);
                }
            }

            _persistentTableEntries.Remove(name);
            if (_persistentTableIndexes.TryGetValue(name, out List<IndexDescriptor>? droppedIndexes))
            {
                foreach (IndexDescriptor index in droppedIndexes)
                {
                    _indexNameToTable.Remove(index.Name);
                }
                _persistentTableIndexes.Remove(name);
            }
            _persistentTablePkNames.Remove(name);
            _persistManifest();
        }

        return true;
    }

    /// <inheritdoc/>
    public ITableProvider CreatePersistentTable(
        QualifiedName name,
        Schema schema,
        string? primaryKeyConstraintName)
    {
        if (_catalogDirectory is null)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{name}' requires the catalog to be backed by a " +
                ".datum-catalog.json file. Either use CREATE TEMP TABLE for in-memory " +
                "scratch, or open the catalog with a catalogPath so persistent tables can be " +
                "recorded.");
        }

        if (schema.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{name}': a persistent table must declare at least one column.");
        }

        string targetPath = ResolveCreateTablePath(name);
        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{name}' would create a file at '{targetPath}' " +
                "but a file already exists there. Drop the existing file or pick a different name.");
        }

        // Create data/<schema>/ if it doesn't exist. CREATE TABLE is the only
        // path that materialises new files, so the subdirectory is created
        // lazily here rather than at catalog construction.
        string? targetDir = System.IO.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        ColumnDescriptorV2[] descriptors = new ColumnDescriptorV2[schema.Columns.Count];
        List<ColumnDefaultV4>? columnDefaults = null;
        List<ColumnComputedV4>? columnComputeds = null;
        IdentityWriterSpec? identityWriterSpec = null;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo c = schema.Columns[i];
            descriptors[i] = new ColumnDescriptorV2(
                Name: c.Name,
                Kind: c.Kind,
                Encoder: ColumnDescriptorV2.EncoderFor(c.Kind, c.IsArray),
                IsNullable: c.Nullable,
                IsArray: c.IsArray,
                FixedShape: c.FixedShape,
                MaxLength: c.MaxLength,
                IsBlankPadded: c.IsBlankPadded);

            if (c.DefaultExpression is not null)
            {
                columnDefaults ??= new List<ColumnDefaultV4>();
                columnDefaults.Add(new ColumnDefaultV4(
                    ColumnIndex: checked((ushort)i),
                    SqlFragment: Execution.QueryExplainer.FormatExpression(c.DefaultExpression)));
            }

            if (c.ComputedExpression is not null)
            {
                columnComputeds ??= new List<ColumnComputedV4>();
                columnComputeds.Add(new ColumnComputedV4(
                    ColumnIndex: checked((ushort)i),
                    SqlFragment: Execution.QueryExplainer.FormatExpression(c.ComputedExpression)));
            }

            if (c.Identity is { } identity)
            {
                identityWriterSpec = new IdentityWriterSpec(i, identity.Seed, identity.Step, identity.AcceptUserValues);
            }
        }

        ushort[]? pkColumnIndices = schema.PrimaryKeyColumnIndices.Count == 0
            ? null
            : schema.PrimaryKeyColumnIndices.Select(i => checked((ushort)i)).ToArray();

        DatumFileWriterV2.CreateEmpty(
            targetPath, descriptors, columnDefaults, identityWriterSpec, pkColumnIndices, columnComputeds);

        // PK sidecar.
        if (schema.PrimaryKeyColumnIndices.Count >= 1)
        {
            string pkIndexPath = DatumFileTableProviderV2.GetPrimaryKeyIndexPath(targetPath);
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes pkTree =
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(pkIndexPath);
            pkTree.Dispose();
        }

        // Construct + register the provider via the standard Add path so
        // sidecar registration runs in one place. Build the provider
        // through TableDescriptor so the file-open machinery does its
        // schema-validation pass.
        DatumFileTableProviderV2 provider = new(new TableDescriptor(name.ToString(), targetPath), _pool);
        if (!_tables.TryAdd(name, provider))
        {
            // Race-with-Add: dispose the just-created provider and surface
            // the same error message the public Add path would.
            (provider as IDisposable)?.Dispose();
            throw new ArgumentException(
                $"A table with the name '{name}' is already registered.");
        }
        RegisterProviderSidecar(provider);

        string persistedPath = ToPersistedPath(targetPath);
        _persistentTableEntries[name] = persistedPath;

        if (!string.IsNullOrEmpty(primaryKeyConstraintName)
            && schema.PrimaryKeyColumnIndices.Count > 0)
        {
            _persistentTablePkNames[name] = primaryKeyConstraintName!;
        }

        _persistManifest();
        return provider;
    }

    /// <inheritdoc/>
    public void RegisterIndex(QualifiedName tableName, IndexDescriptor descriptor)
    {
        if (!_persistentTableIndexes.TryGetValue(tableName, out List<IndexDescriptor>? list))
        {
            list = new List<IndexDescriptor>();
            _persistentTableIndexes[tableName] = list;
        }
        list.Add(descriptor);
        _indexNameToTable[descriptor.Name] = tableName;
        _persistManifest();
    }

    /// <inheritdoc/>
    public bool UnregisterIndex(string indexName, out QualifiedName ownerTable)
    {
        if (!_indexNameToTable.TryGetValue(indexName, out ownerTable))
        {
            return false;
        }

        _indexNameToTable.Remove(indexName);
        if (_persistentTableIndexes.TryGetValue(ownerTable, out List<IndexDescriptor>? list))
        {
            list.RemoveAll(idx => string.Equals(idx.Name, indexName, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
            {
                _persistentTableIndexes.Remove(ownerTable);
            }
        }
        _persistManifest();
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IndexDescriptor>? GetTableIndexes(QualifiedName tableName)
        => _persistentTableIndexes.TryGetValue(tableName, out List<IndexDescriptor>? list)
            ? list
            : null;

    /// <inheritdoc/>
    public bool TryGetIndexOwner(string indexName, out QualifiedName ownerTable)
        => _indexNameToTable.TryGetValue(indexName, out ownerTable);

    /// <inheritdoc/>
    public string? GetCustomPrimaryKeyConstraintName(QualifiedName tableName)
        => _persistentTablePkNames.TryGetValue(tableName, out string? custom) ? custom : null;

    /// <summary>
    /// Clears the user-supplied PRIMARY KEY constraint name for
    /// <paramref name="tableName"/>. Returns true when an entry existed
    /// (so the facade can persist).
    /// </summary>
    public bool RemoveCustomPrimaryKeyConstraintName(QualifiedName tableName)
    {
        if (!_persistentTablePkNames.Remove(tableName)) return false;
        _persistManifest();
        return true;
    }

    /// <summary>
    /// Rehydrates every persistent table from a loaded
    /// <see cref="FlatFileBackendState"/>. Used at construction by the
    /// facade. Does not invoke the persist callback (we're rehydrating,
    /// not mutating).
    /// </summary>
    internal void LoadBackendState(FlatFileBackendState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Tables is null) return;

        foreach (FlatFileTableEntry entry in state.Tables)
        {
            if (string.IsNullOrEmpty(entry.Schema)
                || string.IsNullOrEmpty(entry.Name)
                || string.IsNullOrEmpty(entry.FilePath))
            {
                continue;
            }

            QualifiedName name = new(entry.Schema, entry.Name);
            string resolved = ResolveTablePath(entry.FilePath);
            if (!File.Exists(resolved))
            {
                // Stale catalog entry — file has been moved or deleted.
                // Skip silently for now; a future REPAIR command can
                // prune dead entries.
                continue;
            }

            // Materialise the index list so the provider can open the
            // corresponding .datum-cindex-* sidecars at construction.
            List<IndexDescriptor>? indexes = null;
            if (entry.Indexes is { Count: > 0 } indexEntries)
            {
                indexes = new List<IndexDescriptor>(indexEntries.Count);
                foreach (FlatFileIndexEntry indexEntry in indexEntries)
                {
                    if (string.IsNullOrEmpty(indexEntry.Name)
                        || indexEntry.Columns is null
                        || indexEntry.Columns.Count == 0)
                    {
                        continue;
                    }
                    IndexKind kind = ParseIndexKindOrDefault(indexEntry.Kind);
                    indexes.Add(new IndexDescriptor(
                        indexEntry.Name,
                        indexEntry.Columns.ToArray(),
                        indexEntry.IsUnique,
                        kind,
                        indexEntry.AnalyzerName));
                }
            }

            DatumFileTableProviderV2 provider = new(
                new TableDescriptor(name.ToString(), resolved, Indexes: indexes),
                _pool);
            if (!_tables.TryAdd(name, provider))
            {
                (provider as IDisposable)?.Dispose();
                continue;
            }
            RegisterProviderSidecar(provider);

            _persistentTableEntries[name] = entry.FilePath;
            if (indexes is { Count: > 0 })
            {
                _persistentTableIndexes[name] = new List<IndexDescriptor>(indexes);
                foreach (IndexDescriptor index in indexes)
                {
                    _indexNameToTable[index.Name] = name;
                }
            }
            if (!string.IsNullOrEmpty(entry.PrimaryKeyConstraintName))
            {
                _persistentTablePkNames[name] = entry.PrimaryKeyConstraintName!;
            }
        }
    }

    /// <summary>
    /// Snapshots this backend's persistent state for
    /// <see cref="CatalogStore.Save"/>. Wired by the facade via
    /// <see cref="CatalogStore.SetFlatFileBackendStateProvider"/> at
    /// construction.
    /// </summary>
    internal FlatFileBackendState SnapshotBackendState()
    {
        List<FlatFileTableEntry> tables = new(_persistentTableEntries.Count);
        // Order by canonical name so save output is deterministic.
        IEnumerable<KeyValuePair<QualifiedName, string>> ordered = _persistentTableEntries
            .OrderBy(kv => kv.Key.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach ((QualifiedName name, string path) in ordered)
        {
            FlatFileTableEntry entry = new()
            {
                Schema = name.Schema,
                Name = name.Name,
                FilePath = path,
            };
            if (_persistentTableIndexes.TryGetValue(name, out List<IndexDescriptor>? indexes) && indexes.Count > 0)
            {
                List<FlatFileIndexEntry> indexEntries = new(indexes.Count);
                foreach (IndexDescriptor index in indexes)
                {
                    indexEntries.Add(new FlatFileIndexEntry
                    {
                        Name = index.Name,
                        Columns = new List<string>(index.Columns),
                        IsUnique = index.IsUnique,
                        Kind = FormatIndexKind(index.Kind),
                        AnalyzerName = index.AnalyzerName,
                    });
                }
                entry.Indexes = indexEntries;
            }
            if (_persistentTablePkNames.TryGetValue(name, out string? pkName))
            {
                entry.PrimaryKeyConstraintName = pkName;
            }
            tables.Add(entry);
        }
        return new FlatFileBackendState { Tables = tables };
    }

    private static IndexKind ParseIndexKindOrDefault(string? wireValue)
    {
        if (string.IsNullOrEmpty(wireValue))
        {
            return IndexKind.Composite;
        }
        return wireValue.ToLowerInvariant() switch
        {
            "composite" or "btree" => IndexKind.Composite,
            "fulltext" or "fts" => IndexKind.FullText,
            _ => IndexKind.Composite, // forward-compat: unknown kinds load as composite
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (ITableProvider provider in _tables.Values)
        {
            try { provider.Dispose(); }
            catch { /* best-effort */ }
        }
        _tables.Clear();
    }

    private void RegisterProviderSidecar(ITableProvider provider)
    {
        if (provider is not IDatumFileTableProvider datumProvider) return;
        datumProvider.SidecarRegistry = _sidecarRegistry;
        if (datumProvider.Sidecar is not { } source) return;
        datumProvider.SidecarStoreId = _sidecarRegistry.Register(source);
    }

    private string ResolveCreateTablePath(QualifiedName name)
    {
        // Persistent tables land under <catalog>/data/<schema>/<name>.datum.
        // Schema-as-directory makes the on-disk layout readable, lets users
        // gitignore /data/ wholesale for schema-only commits, and gives
        // same-named tables in different user-created schemas distinct files.
        string catalogDir = _catalogDirectory ?? Environment.CurrentDirectory;
        return System.IO.Path.Combine(catalogDir, "data", name.Schema, name.Name + ".datum");
    }

    private string ResolveTablePath(string storedPath)
    {
        if (System.IO.Path.IsPathRooted(storedPath)) return storedPath;
        string catalogDir = _catalogDirectory ?? Environment.CurrentDirectory;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(catalogDir, storedPath));
    }

    private string ToPersistedPath(string absolutePath)
    {
        if (_catalogDirectory is null) return absolutePath;
        string relative = System.IO.Path.GetRelativePath(_catalogDirectory, absolutePath);
        if (System.IO.Path.IsPathRooted(relative)) return absolutePath;
        if (relative.StartsWith("..")) return absolutePath;
        return relative;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort — file might be held open elsewhere or already gone.
        }
    }

    private static string FormatIndexKind(IndexKind kind) => kind switch
    {
        IndexKind.Composite => "composite",
        IndexKind.FullText => "fts",
        _ => "composite",
    };
}
