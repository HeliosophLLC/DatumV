using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_h5_group(path, group_path) → table</c>. Opens an HDF5 group
/// and yields a **single row** with one column per direct-child dataset.
/// Sub-groups and datasets whose dtype isn't supported in v1 are silently
/// skipped. The pivot-mode companion to <c>open_h5_dataset</c>: when a
/// group represents a logical record made of related datasets that you
/// want side-by-side (descriptions + bitmasks + scalar metadata, parallel
/// label arrays, etc.), this lifts them all into one row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shape preservation.</strong> Each child dataset's cell carries
/// the dataset's full shape:
/// </para>
/// <list type="bullet">
///   <item>Scalar (rank-0) → scalar cell of the dataset's element kind</item>
///   <item>1-D, length N → flat 1-D array cell</item>
///   <item>2-D, shape (R, C) → multi-dim cell with <c>FixedShape = [R, C]</c></item>
///   <item>N-D, shape (D₀, …, Dₙ₋₁) → multi-dim cell with <c>FixedShape = [D₀, …, Dₙ₋₁]</c></item>
/// </list>
/// <para>
/// <strong>Plan-time schema peek.</strong> Both arguments must be constants
/// at plan time. The validator opens the file, looks up the group, and
/// walks its direct children to produce a real <see cref="Schema"/> — so
/// <c>SELECT DQDescriptions, DQmask FROM open_h5_group(...)</c>
/// type-checks against the actual datasets. Non-constant arguments throw.
/// </para>
/// <para>
/// <strong>Direct children only.</strong> Recursive descent into sub-groups
/// is deliberately out of scope: the row width would explode for deep
/// trees, and the natural cross-group access pattern is composition with
/// <c>open_h5_meta</c> for discovery + multiple <c>open_h5_group</c> /
/// <c>open_h5_dataset</c> calls per record. Sub-group children appear in
/// the manifest emitted by <c>open_h5_meta</c>; they don't appear as
/// columns here.
/// </para>
/// </remarks>
public sealed class OpenH5GroupFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_h5_group";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an HDF5 group and yields a single row with one typed column per direct-child " +
        "dataset: open_h5_group(path, group_path). Each cell carries the full dataset shape " +
        "(scalar / 1-D array / multi-dim for 2-D+). Sub-groups and unsupported-dtype datasets " +
        "are silently skipped. Use for the 'group-as-record' pattern.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("group_path", DataKindMatcher.Exact(DataKind.String)),
            ],
            FixedOutputSchema: null),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        ValidateArgumentShape(argumentKinds);

        if (constantArguments[0] is not DataValue pathValue || pathValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be a constant STRING. " +
                "Inline the file path or pass it via a bound $parameter.");
        }
        if (constantArguments[1] is not DataValue groupValue || groupValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (group_path) must be a constant STRING.");
        }

        string path = pathValue.AsString(constantStore);
        string groupPath = groupValue.AsString(constantStore);

        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name, $"HDF5 file not found: '{path}'.");
        }

        using var file = H5File.OpenRead(path);
        if (!TryLookupGroup(file, groupPath, out IH5Group? group, out string? error))
        {
            throw new FunctionArgumentException(Name, error);
        }

        return BuildSchema(group);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 2)
        {
            throw new ArgumentException("open_h5_group requires 2 arguments: (path, group_path).");
        }

        string path = arguments[0].AsString();
        string groupPath = arguments[1].AsString();

        await foreach (RowBatch batch in StreamRowsAsync(path, groupPath, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        string groupPath,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        using var file = H5File.OpenRead(path);
        if (!TryLookupGroup(file, groupPath, out IH5Group? group, out string? error))
        {
            throw new InvalidDataException(error);
        }

        // Walk children once, collecting (name, dataset, type) for every
        // includable child. The Schema we build here must agree with the one
        // ValidateArguments produced, so the rules live in BuildIncludedChildren.
        List<IncludedChild> children = BuildIncludedChildren(group);
        if (children.Count == 0)
        {
            // No columns to emit. Yield nothing rather than an empty row —
            // an empty group is informationally indistinguishable from a
            // missing group at this layer, and downstream callers can use
            // open_h5_meta to disambiguate.
            yield break;
        }

        string[] columnNames = new string[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            columnNames[i] = children[i].Name;
        }
        ColumnLookup outputLookup = new(columnNames);

        await Task.Yield();
        RowBatch batch = context.RentRowBatch(outputLookup);
        DataValue[] row = context.Pool.RentDataValues(children.Count);

        for (int i = 0; i < children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncludedChild child = children[i];
            row[i] = BuildCell(child.Dataset, child.Type, batch.Arena);
        }

        batch.Add(row);
        yield return batch;
    }

    /// <summary>
    /// Reads <paramref name="dataset"/> in full and packs it as one
    /// <see cref="DataValue"/> respecting the dataset's rank:
    /// scalar → scalar cell, 1-D → flat array cell, ≥2-D → multi-dim
    /// cell with the dataset's full <see cref="Hdf5DatasetType.Dimensions"/>
    /// as the shape.
    /// </summary>
    private static DataValue BuildCell(IH5Dataset dataset, Hdf5DatasetType type, IValueStore arena)
    {
        System.Array flat = Hdf5DatasetReader.ReadFlat(dataset, type);

        if (type.IsScalar)
        {
            return Hdf5DatasetReader.ScalarAt(flat, 0, type.ElementKind, arena);
        }
        if (type.Dimensions.Count == 1)
        {
            return Hdf5DatasetReader.SliceArray(flat, 0, flat.Length, type.ElementKind, arena);
        }

        int[] shape = new int[type.Dimensions.Count];
        int totalElements = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            shape[i] = checked((int)type.Dimensions[i]);
            totalElements = checked(totalElements * shape[i]);
        }
        return Hdf5DatasetReader.SliceMultiDim(flat, 0, totalElements, shape, type.ElementKind, arena);
    }

    private void ValidateArgumentShape(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new FunctionArgumentException(Name,
                "requires 2 arguments: open_h5_group(path, group_path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name, "argument 1 (path) must be STRING.");
        }
        if (argumentKinds[1] != DataKind.String)
        {
            throw new FunctionArgumentException(Name, "argument 2 (group_path) must be STRING.");
        }
    }

    private static Schema BuildSchema(IH5Group group)
    {
        List<IncludedChild> children = BuildIncludedChildren(group);
        ColumnInfo[] columns = new ColumnInfo[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            IncludedChild child = children[i];
            Hdf5DatasetType type = child.Type;
            int rank = type.Dimensions.Count;

            ColumnInfo column;
            if (rank <= 1)
            {
                // Scalar (rank-0) → scalar column; 1-D → flat array column.
                column = new ColumnInfo(child.Name, type.ElementKind, nullable: false)
                {
                    IsArray = !type.IsScalar,
                };
            }
            else
            {
                // 2-D and higher → multi-dim cell carrying the full dataset shape.
                int[] fixedShape = new int[rank];
                for (int s = 0; s < rank; s++)
                {
                    fixedShape[s] = checked((int)type.Dimensions[s]);
                }
                column = new ColumnInfo(child.Name, type.ElementKind, nullable: false)
                {
                    IsArray = true,
                    IsMultiDim = true,
                    FixedShape = fixedShape,
                };
            }
            columns[i] = column;
        }
        return new Schema(columns);
    }

    /// <summary>
    /// Walks <paramref name="group"/>'s direct children, returning the
    /// includable dataset members in declaration order. Sub-groups and
    /// datasets whose dtype isn't supported in v1 are silently dropped.
    /// The same rules are used by both <see cref="BuildSchema"/> and
    /// <see cref="StreamRowsAsync"/> so the schema and row shape stay in
    /// lock-step.
    /// </summary>
    private static List<IncludedChild> BuildIncludedChildren(IH5Group group)
    {
        List<IncludedChild> result = [];
        foreach (IH5Object child in group.Children())
        {
            if (child is not IH5Dataset dataset) continue;
            Hdf5DatasetType type = Hdf5DatasetType.From(dataset.Type, dataset.Space);
            if (!type.IsSupported) continue;
            result.Add(new IncludedChild(child.Name, dataset, type));
        }
        return result;
    }

    private static bool TryLookupGroup(
        IH5Group root,
        string groupPath,
        [NotNullWhen(true)] out IH5Group? group,
        [NotNullWhen(false)] out string? error)
    {
        group = null;
        error = null;

        if (string.IsNullOrEmpty(groupPath) || groupPath == "/")
        {
            group = root;
            return true;
        }

        try
        {
            // PureHDF accepts both leading-slash and relative forms; strip the
            // leading slash to match its relative-path API.
            string normalised = groupPath.StartsWith('/') ? groupPath[1..] : groupPath;
            if (!root.LinkExists(normalised))
            {
                error = $"HDF5 path '{groupPath}' not found in the file.";
                return false;
            }
            object obj = root.Get(normalised);
            if (obj is not IH5Group g)
            {
                error = $"HDF5 path '{groupPath}' is a {obj.GetType().Name.ToLowerInvariant()}, not a group.";
                return false;
            }
            group = g;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to open HDF5 group '{groupPath}': {ex.Message}";
            return false;
        }
    }

    private readonly record struct IncludedChild(string Name, IH5Dataset Dataset, Hdf5DatasetType Type);
}
