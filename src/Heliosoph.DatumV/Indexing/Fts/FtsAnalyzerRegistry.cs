using System.Collections.Frozen;

namespace Heliosoph.DatumV.Indexing.Fts;

/// <summary>
/// Resolves an <see cref="IFullTextAnalyzer"/> by name. Backs the manifest's
/// per-index analyzer field — when an FTS index is opened, the stored
/// analyzer name is looked up here and the resulting instance is used for
/// both query-time tokenization and incremental insert tokenization.
/// </summary>
/// <remarks>
/// <para>The registry is currently <c>internal</c> and populated from a
/// sealed list at construction time. Promotion to a public registration
/// surface (host-supplied <see cref="IFullTextAnalyzer"/> implementations,
/// or a SQL-level <c>CREATE TEXT ANALYZER</c>) does not change the on-disk
/// format — existing indexes keep working as long as the registered names
/// stay stable.</para>
///
/// <para>Use <see cref="Default"/> for the standard process-wide instance;
/// tests construct their own when they need to register or remove
/// analyzers in isolation.</para>
/// </remarks>
internal sealed class FtsAnalyzerRegistry
{
    /// <summary>
    /// Singleton populated with every analyzer that ships in this build.
    /// </summary>
    internal static FtsAnalyzerRegistry Default { get; } = new();

    private readonly FrozenDictionary<string, IFullTextAnalyzer> _byName;

    internal FtsAnalyzerRegistry()
        : this(BuiltinAnalyzers())
    {
    }

    internal FtsAnalyzerRegistry(IEnumerable<IFullTextAnalyzer> analyzers)
    {
        ArgumentNullException.ThrowIfNull(analyzers);

        Dictionary<string, IFullTextAnalyzer> map = new(StringComparer.OrdinalIgnoreCase);

        foreach (IFullTextAnalyzer analyzer in analyzers)
        {
            if (!map.TryAdd(analyzer.Name, analyzer))
            {
                throw new ArgumentException(
                    $"Duplicate FTS analyzer name '{analyzer.Name}'.", nameof(analyzers));
            }
        }

        _byName = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Names of all registered analyzers, for diagnostics and completion.</summary>
    internal IReadOnlyCollection<string> RegisteredNames => _byName.Keys;

    /// <summary>
    /// Returns the analyzer registered under <paramref name="name"/>.
    /// Throws <see cref="FtsAnalyzerNotFoundException"/> when no such
    /// analyzer is registered — callers opening an index should surface
    /// this as a clear "REINDEX or use a build that includes 'X'" error.
    /// </summary>
    internal IFullTextAnalyzer Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_byName.TryGetValue(name, out IFullTextAnalyzer? analyzer))
        {
            return analyzer;
        }

        throw new FtsAnalyzerNotFoundException(name, _byName.Keys);
    }

    internal bool TryGet(string name, out IFullTextAnalyzer? analyzer)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out analyzer);
    }

    private static IEnumerable<IFullTextAnalyzer> BuiltinAnalyzers() => new IFullTextAnalyzer[]
    {
        new SimpleEnglishAnalyzer(),
    };
}

/// <summary>
/// Thrown when an FTS index references an analyzer name that isn't
/// registered in the current build. The message names the requested
/// analyzer and the registered alternatives so the operator can either
/// REINDEX with a registered analyzer or run a build that includes the
/// missing one.
/// </summary>
internal sealed class FtsAnalyzerNotFoundException : Exception
{
    internal string AnalyzerName { get; }

    internal FtsAnalyzerNotFoundException(string analyzerName, IEnumerable<string> registered)
        : base($"FTS analyzer '{analyzerName}' is not registered. Registered analyzers: " +
               $"{string.Join(", ", registered)}. REINDEX with a registered analyzer or " +
               $"use a build that includes '{analyzerName}'.")
    {
        AnalyzerName = analyzerName;
    }
}
