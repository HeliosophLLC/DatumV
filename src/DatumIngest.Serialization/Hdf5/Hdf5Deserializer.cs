// using System.Runtime.CompilerServices;
// using DatumIngest.Model;
// using PureHDF;
// using PureHDF.VOL.Native;

// namespace DatumIngest.Serialization.Hdf5;

// /// <summary>
// /// Deserializes HDF5 files into <see cref="RowBatch"/> streams. Each 1-D dataset
// /// becomes a column. Multi-dimensional datasets are restored to Vector/Matrix/Tensor.
// /// </summary>
// public sealed class Hdf5Deserializer : IFormatDeserializer
// {
//     private const int DefaultBatchSize = 1024;

//     private readonly FileFormatDescriptor _descriptor;

//     /// <summary>Creates a deserializer for the given file descriptor.</summary>
//     public Hdf5Deserializer(FileFormatDescriptor descriptor)
//     {
//         _descriptor = descriptor;
//     }

//     /// <inheritdoc/>
//     public async IAsyncEnumerable<RowBatch> DeserializeAsync(
//         SerializationContext context,
//         [EnumeratorCancellation] CancellationToken cancellationToken = default)
//     {
//         // HDF5 requires a file path — PureHDF doesn't support stream-based reading.
//         using NativeFile file = H5File.OpenRead(_descriptor.FilePath);

//         List<Hdf5DatasetEntry> entries = Hdf5DatasetDiscovery.Discover(file);

//         if (entries.Count == 0)
//             yield break;

//         // Read all datasets into typed column arrays.
//         List<Hdf5ColumnData> columns = new(entries.Count);
//         long rowCount = 0;

//         foreach (Hdf5DatasetEntry entry in entries)
//         {
//             Hdf5ColumnData col = Hdf5ColumnReader.Read(entry);
//             columns.Add(col);
//             rowCount = rowCount == 0 ? col.RowCount : Math.Min(rowCount, col.RowCount);
//         }

//         // Build schema metadata once.
//         IReadOnlyList<string> names = columns.Select(c => c.Name).ToArray();
//         Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
//         for (int i = 0; i < names.Count; i++)
//             nameIndex[names[i]] = i;

//         RowBatch? batch = null;

//         for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
//         {
//             cancellationToken.ThrowIfCancellationRequested();

//             batch ??= context.Pool.RentRowBatch(DefaultBatchSize);
//             DataValue[] values = context.Pool.RentDataValues(columns.Count);
//             for (int col = 0; col < columns.Count; col++)
//             {
//                 values[col] = columns[col].GetValue(rowIndex, batch.Arena);
//             }

//             batch.Add(new Row(names, values, nameIndex));

//             if (batch.IsFull)
//             {
//                 yield return batch;
//                 batch = null;
//             }
//         }

//         if (batch is not null)
//         {
//             yield return batch;
//         }

//         await Task.CompletedTask;
//     }
// }
