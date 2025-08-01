using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator for recursive Common Table Expressions (WITH RECURSIVE).
/// Executes the anchor member once, then iterates the recursive member using
/// the previous iteration's output as the working table until no new rows are
/// produced or <see cref="ExecutionContext.MaxRecursionDepth"/> is reached.
/// All accumulated rows (anchor + all iterations) are yielded.
/// </summary>
/// <remarks>
/// Recursive CTEs are always materialized — the full result set must be computed
/// before any row can be yielded to the outer query, because the recursive member
/// depends on the running accumulation.
/// </remarks>
internal sealed class RecursiveCommonTableExpressionOperator : IQueryOperator, IDisposable
{
    private readonly IQueryOperator _anchorOperator;
    private readonly string _name;
    private readonly IReadOnlyList<string>? _explicitColumnNames;

    /// <summary>
    /// Factory that produces the recursive member's operator tree. Called once per
    /// iteration with a <see cref="WorkingTableOperator"/> that replays the previous
    /// iteration's rows.
    /// </summary>
    private readonly Func<IQueryOperator, IQueryOperator> _recursiveMemberFactory;

    private List<Row>? _allRows;
    private bool _materialized;
    private string? _spillFilePath;

    /// <summary>
    /// Creates a recursive CTE operator.
    /// </summary>
    /// <param name="anchorOperator">The operator tree for the anchor (non-recursive) member.</param>
    /// <param name="recursiveMemberFactory">
    /// A factory that, given a working-table operator representing the previous iteration's
    /// output, returns the recursive member's operator tree. The working-table operator is
    /// substituted where the CTE name appears in the recursive member's FROM clause.
    /// </param>
    /// <param name="name">The CTE name.</param>
    /// <param name="explicitColumnNames">Optional explicit column names from the CTE definition.</param>
    public RecursiveCommonTableExpressionOperator(
        IQueryOperator anchorOperator,
        Func<IQueryOperator, IQueryOperator> recursiveMemberFactory,
        string name,
        IReadOnlyList<string>? explicitColumnNames = null)
    {
        _anchorOperator = anchorOperator;
        _recursiveMemberFactory = recursiveMemberFactory;
        _name = name;
        _explicitColumnNames = explicitColumnNames;
    }

    /// <summary>The CTE name.</summary>
    public string Name => _name;

    /// <summary>The anchor operator tree.</summary>
    public IQueryOperator AnchorOperator => _anchorOperator;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        if (!_materialized)
        {
            await MaterializeAsync(context).ConfigureAwait(false);
        }

        // Replay from disk if spilled, otherwise from memory.
        if (_spillFilePath is not null)
        {
            await foreach (Row row in ReplayFromDiskAsync(context.CancellationToken).ConfigureAwait(false))
            {
                yield return RenameColumnsIfNeeded(row);
            }
        }
        else if (_allRows is not null)
        {
            foreach (Row row in _allRows)
            {
                yield return RenameColumnsIfNeeded(row);
            }
        }
    }

    /// <summary>
    /// Executes the anchor member, then iterates the recursive member until fixpoint
    /// or the max recursion depth is reached.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        _allRows = new List<Row>();
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        BinaryWriter? spillWriter = null;
        bool schemaWritten = false;
        int maxDepth = context.MaxRecursionDepth;

        try
        {
            // Execute anchor member.
            List<Row> workingTable = new();
            await foreach (Row row in _anchorOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.QueryMeter?.ThrowIfExceeded();

                workingTable.Add(row);
                AddRow(row, estimator, memoryBudget, ref spillWriter, ref schemaWritten);
            }

            // Iterate recursive member.
            for (int depth = 0; depth < maxDepth; depth++)
            {
                if (workingTable.Count == 0)
                {
                    break;
                }

                WorkingTableOperator workingTableOperator = new(workingTable);
                IQueryOperator recursiveMember = _recursiveMemberFactory(workingTableOperator);

                List<Row> nextWorkingTable = new();
                await foreach (Row row in recursiveMember.ExecuteAsync(context).ConfigureAwait(false))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.QueryMeter?.ThrowIfExceeded();

                    nextWorkingTable.Add(row);
                    AddRow(row, estimator, memoryBudget, ref spillWriter, ref schemaWritten);
                }

                workingTable = nextWorkingTable;
            }

            if (workingTable.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Recursive CTE '{_name}' exceeded maximum recursion depth of {maxDepth}.");
            }
        }
        finally
        {
            if (spillWriter is not null)
            {
                spillWriter.Flush();
                spillWriter.Dispose();
            }
        }

        if (_spillFilePath is not null)
        {
            _allRows = null;
        }

        _materialized = true;
    }

    /// <summary>
    /// Adds a row to the accumulator (in-memory or spilled to disk).
    /// </summary>
    private void AddRow(
        Row row,
        MemoryEstimator? estimator,
        long? memoryBudget,
        ref BinaryWriter? spillWriter,
        ref bool schemaWritten)
    {
        if (_spillFilePath is not null)
        {
            if (!schemaWritten)
            {
                RowSerializer.WriteSchema(spillWriter!, row);
                schemaWritten = true;
            }

            RowSerializer.WriteRow(spillWriter!, row);
        }
        else
        {
            _allRows!.Add(row);

            if (estimator is not null)
            {
                if (estimator.ShouldSample())
                {
                    estimator.RecordSample(row);
                }

                estimator.IncrementRowCount();
                long estimatedMemory = estimator.EstimateTotalBytes();

                if (estimatedMemory > memoryBudget!.Value)
                {
                    SpillToDisk(ref spillWriter, ref schemaWritten);
                }
                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                {
                    estimator.EscalateToEveryRow();
                }
            }
        }
    }

    /// <summary>
    /// Transitions to spill mode, writing all buffered rows to a temp file.
    /// </summary>
    private void SpillToDisk(ref BinaryWriter? spillWriter, ref bool schemaWritten)
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum-rcte-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDirectory);
        _spillFilePath = Path.Combine(spillDirectory, "rcte.spill");

        FileStream fileStream = new(
            _spillFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        spillWriter = new BinaryWriter(fileStream);

        foreach (Row bufferedRow in _allRows!)
        {
            if (!schemaWritten)
            {
                RowSerializer.WriteSchema(spillWriter, bufferedRow);
                schemaWritten = true;
            }

            RowSerializer.WriteRow(spillWriter, bufferedRow);
        }

        _allRows!.Clear();
    }

    /// <summary>
    /// Replays rows from a spill file.
    /// </summary>
    private async IAsyncEnumerable<Row> ReplayFromDiskAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileStream fileStream = new(
            _spillFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);

        await using (fileStream.ConfigureAwait(false))
        {
            using BinaryReader reader = new(fileStream);

            RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);

            while (fileStream.Position < fileStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return RowSerializer.ReadRow(reader, names, nameIndex);
            }
        }
    }

    /// <summary>
    /// Renames the output row's columns if the CTE definition provides explicit column names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Row RenameColumnsIfNeeded(Row row)
    {
        if (_explicitColumnNames is null)
        {
            return row;
        }

        string[] renamedNames = new string[row.FieldCount];
        DataValue[] values = new DataValue[row.FieldCount];
        for (int index = 0; index < row.FieldCount; index++)
        {
            renamedNames[index] = index < _explicitColumnNames.Count
                ? _explicitColumnNames[index]
                : row.ColumnNames[index];
            values[index] = row[index];
        }

        return new Row(renamedNames, values);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_spillFilePath is not null)
        {
            string? directory = Path.GetDirectoryName(_spillFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup — OS handles temp files on shutdown.
                }
            }

            _spillFilePath = null;
        }
    }

    /// <summary>
    /// Simple operator that replays an in-memory list of rows.
    /// Used as the working table for the recursive member in each iteration.
    /// </summary>
    internal sealed class WorkingTableOperator : IQueryOperator
    {
        private readonly List<Row> _rows;

        /// <summary>
        /// Creates a working table operator from a snapshot of rows.
        /// </summary>
        /// <param name="rows">The rows to replay.</param>
        public WorkingTableOperator(List<Row> rows)
        {
            _rows = rows;
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators
        /// <inheritdoc/>
        public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }
        }
#pragma warning restore CS1998
    }
}
