using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.BTree.Mutable;
using Heliosoph.DatumV.Indexing.Fts;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Statistics;

namespace Heliosoph.DatumV.Catalog.Providers;

public sealed partial class DatumFileTableProviderV2
{
    /// <summary>
    /// Discovers and opens every <c>.datum-bptree-{column}</c> file that lives
    /// alongside this provider's data file and matches a column in the live
    /// schema. Files that don't match a current column (DROPped column with
    /// stale tree, renamed column, etc.) are left on disk but not opened —
    /// REINDEX cleans them up.
    /// </summary>
    private void OpenColumnIndexes()
    {
        Schema schema = _snapshot.Schema;

        foreach (ColumnInfo column in schema.Columns)
        {
            string treePath = GetColumnIndexPath(_descriptor.FilePath, column.Name);

            if (!File.Exists(treePath))
            {
                continue;
            }

            try
            {
                MutableBPlusTree tree = MutableBPlusTree.Open(treePath);
                _columnTrees[column.Name] = tree;
                _columnIndexes[column.Name] = new MutableBPlusTreeColumnIndex(tree);
            }
            catch
            {
                // Silently skip a tree that won't open (torn write, version
                // mismatch); the column degrades to scan-based access until
                // REINDEX rebuilds it. Don't crash provider construction.
            }
        }
    }

    /// <summary>
    /// Returns the per-column acceleration B+Tree path companion for a given
    /// data file + column. Column name is sanitized so non-alphanumeric chars
    /// don't collide with the path separator.
    /// </summary>
    internal static string GetColumnIndexPath(string datumPath, string columnName)
    {
        string sanitized = SanitizeColumnNameForPath(columnName);
        return Path.ChangeExtension(datumPath, $".datum-bptree-{sanitized}");
    }

    private static string SanitizeColumnNameForPath(string columnName)
    {
        Span<char> buffer = stackalloc char[columnName.Length];
        for (int i = 0; i < columnName.Length; i++)
        {
            char c = columnName[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_';
        }
        return new string(buffer);
    }

    /// <inheritdoc />
    public bool TryGetColumnIndex(string columnName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Indexing.IColumnIndex? index)
    {
        if (_columnIndexes.TryGetValue(columnName, out MutableBPlusTreeColumnIndex? tree))
        {
            index = tree;
            return true;
        }
        index = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<Indexing.ICompositeIndex> GetCompositeIndexes()
    {
        // Snapshot the dictionaries under the dict lock so a concurrent
        // CREATE / DROP / UPDATE-rebuild can't tear the enumeration. Wrapping
        // adapters around the captured tree handles is safe outside the lock
        // — the adapters hold the handle, not the dict.
        lock (_compositeIndexSync)
        {
            if (_compositeIndexTrees.Count == 0) return Array.Empty<Indexing.ICompositeIndex>();

            Indexing.ICompositeIndex[] result = new Indexing.ICompositeIndex[_compositeIndexTrees.Count];
            int i = 0;
            foreach ((string name, Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree) in _compositeIndexTrees)
            {
                IndexDescriptor descriptor = _compositeIndexDescriptors[name];
                result[i++] = new MutableBPlusTreeBytesCompositeIndex(tree, descriptor);
            }
            return result;
        }
    }

    /// <summary>
    /// Closes every per-column tree and clears the dictionary. Called from
    /// REINDEX (before a rebuild rewrites the files) and from
    /// <see cref="Dispose"/>. The caller must hold the mutation lock when
    /// invoking this during a rebuild — concurrent readers that captured a
    /// <see cref="MutableBPlusTreeColumnIndex"/> reference before the close
    /// keep working through their stale reference for the duration of the
    /// rebuild window; any new TryGetColumnIndex call after close returns
    /// <c>false</c> until the next <see cref="OpenColumnIndexes"/>.
    /// </summary>
    private void CloseColumnIndexes()
    {
        foreach (MutableBPlusTree tree in _columnTrees.Values)
        {
            tree.Dispose();
        }
        _columnTrees.Clear();
        _columnIndexes.Clear();
    }

    /// <summary>
    /// Opens the <c>.datum-pkindex</c> sidecar when the schema has a PK
    /// and the file exists. Single-column PKs open the typed tree
    /// (<see cref="MutableBPlusTree"/>); composite PKs open the
    /// bytes-keyed tree (<see cref="Indexing.BTree.MutableBytes.MutableBPlusTreeBytes"/>).
    /// No-op for files without a PK index — those tables fall back to
    /// <c>InsertExecutor</c>'s scan-based PK check.
    /// </summary>
    private void TryOpenPrimaryKeyIndex()
    {
        IReadOnlyList<int> pkIndices = _snapshot.Schema.PrimaryKeyColumnIndices;
        if (pkIndices.Count == 0) return;

        string pkIndexPath = GetPrimaryKeyIndexPath(_descriptor.FilePath);
        if (!File.Exists(pkIndexPath)) return;

        _pkIndexBytes = Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Open(pkIndexPath);
        _pkColumnIndices = pkIndices.ToArray();
    }

    /// <summary>
    /// Returns the <c>.datum-pkindex</c> path companion for the given
    /// <c>.datum</c> file path.
    /// </summary>
    internal static string GetPrimaryKeyIndexPath(string datumPath) =>
        Path.ChangeExtension(datumPath, ".datum-pkindex");
}
