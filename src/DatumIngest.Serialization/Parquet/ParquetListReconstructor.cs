// using DatumIngest.Pooling;
// using DatumIngest.Model;
// using Parquet.Data;

// namespace DatumIngest.Serialization.Parquet;

// /// <summary>
// /// Reconstructs per-row <see cref="DataValue"/> array values from flat Parquet list
// /// columns using repetition levels to determine list boundaries.
// /// </summary>
// internal static class ParquetListReconstructor
// {
//     /// <summary>
//     /// Reconstructs per-row array values from a Parquet list column.
//     /// The returned array is rented from the pool — the caller must return it
//     /// via <see cref="Pool.ReturnDataValues"/> after consumption.
//     /// </summary>
//     /// <param name="column">The Parquet data column with repetition levels.</param>
//     /// <param name="elementKind">The <see cref="DataKind"/> of list elements.</param>
//     /// <param name="rowCount">Total rows in the row group.</param>
//     /// <param name="store">Value store for string elements.</param>
//     /// <param name="pool">Pool for renting the result array.</param>
//     /// <returns>One <see cref="DataValue"/> per row (Array or NullArray). Pool-rented.</returns>
//     internal static DataValue[] Reconstruct(
//         DataColumn column, DataKind elementKind, long rowCount, IValueStore store, Pool pool)
//     {
//         DataValue[] result = pool.RentDataValues((int)rowCount);
//         int[]? repetitionLevels = column.RepetitionLevels;
//         Array data = column.Data;

//         if (repetitionLevels is null || data.Length == 0)
//         {
//             for (long i = 0; i < rowCount; i++)
//                 result[i] = DataValue.NullArray(elementKind);
//             return result;
//         }

//         int rowIndex = -1;
//         int avgElementsPerRow = rowCount > 0 ? (int)(repetitionLevels.Length / rowCount) : 4;
//         List<DataValue> currentElements = new(Math.Max(avgElementsPerRow, 4));

//         // Dispatch once on kind, then iterate with typed array access — no boxing.
//         ReconstructCore(data, repetitionLevels, elementKind, store, result, ref rowIndex, currentElements);

//         if (rowIndex >= 0 && rowIndex < rowCount)
//         {
//             result[rowIndex] = currentElements.Count > 0
//                 ? DataValue.FromArray(elementKind, currentElements, store)
//                 : DataValue.NullArray(elementKind);
//             rowIndex++;
//         }

//         while (rowIndex < rowCount)
//         {
//             result[rowIndex] = DataValue.NullArray(elementKind);
//             rowIndex++;
//         }

//         return result;
//     }

//     /// <summary>
//     /// Dispatches on element kind once, then iterates with typed array casts — no per-element boxing.
//     /// </summary>
//     private static void ReconstructCore(
//         Array data, int[] repetitionLevels, DataKind elementKind, IValueStore store,
//         DataValue[] result, ref int rowIndex, List<DataValue> currentElements)
//     {
//         switch (elementKind)
//         {
//             case DataKind.Int32 when data is int?[] ni32:
//                 Iterate(repetitionLevels, ni32, result, ref rowIndex, currentElements, elementKind, store,
//                     static (v) => v is int val ? DataValue.FromInt32(val) : default,
//                     static (v) => v.HasValue);
//                 break;
//             case DataKind.Int64 when data is long?[] ni64:
//                 Iterate(repetitionLevels, ni64, result, ref rowIndex, currentElements, elementKind, store,
//                     static (v) => v is long val ? DataValue.FromInt64(val) : default,
//                     static (v) => v.HasValue);
//                 break;
//             case DataKind.Float32 when data is float?[] nf32:
//                 Iterate(repetitionLevels, nf32, result, ref rowIndex, currentElements, elementKind, store,
//                     static (v) => v is float val ? DataValue.FromFloat32(val) : default,
//                     static (v) => v.HasValue);
//                 break;
//             case DataKind.Float64 when data is double?[] nf64:
//                 Iterate(repetitionLevels, nf64, result, ref rowIndex, currentElements, elementKind, store,
//                     static (v) => v is double val ? DataValue.FromFloat64(val) : default,
//                     static (v) => v.HasValue);
//                 break;
//             case DataKind.Boolean when data is bool?[] nb:
//                 Iterate(repetitionLevels, nb, result, ref rowIndex, currentElements, elementKind, store,
//                     static (v) => v is bool val ? DataValue.FromBoolean(val) : default,
//                     static (v) => v.HasValue);
//                 break;
//             case DataKind.String when data is string?[] ns:
//                 IterateStrings(repetitionLevels, ns, result, ref rowIndex, currentElements, elementKind, store);
//                 break;
//             default:
//                 // Fallback: boxing path for uncommon types.
//                 IterateFallback(repetitionLevels, data, result, ref rowIndex, currentElements, elementKind, store);
//                 break;
//         }
//     }

//     private static void Iterate<T>(
//         int[] repetitionLevels, T[] data,
//         DataValue[] result, ref int rowIndex, List<DataValue> currentElements,
//         DataKind elementKind, IValueStore store,
//         Func<T, DataValue> convert, Func<T, bool> hasValue)
//     {
//         for (int i = 0; i < repetitionLevels.Length; i++)
//         {
//             if (repetitionLevels[i] == 0)
//             {
//                 if (rowIndex >= 0)
//                 {
//                     result[rowIndex] = currentElements.Count > 0
//                         ? DataValue.FromArray(elementKind, currentElements, store)
//                         : DataValue.NullArray(elementKind);
//                     currentElements.Clear();
//                 }
//                 rowIndex++;
//             }

//             if (hasValue(data[i]))
//                 currentElements.Add(convert(data[i]));
//         }
//     }

//     private static void IterateStrings(
//         int[] repetitionLevels, string?[] data,
//         DataValue[] result, ref int rowIndex, List<DataValue> currentElements,
//         DataKind elementKind, IValueStore store)
//     {
//         for (int i = 0; i < repetitionLevels.Length; i++)
//         {
//             if (repetitionLevels[i] == 0)
//             {
//                 if (rowIndex >= 0)
//                 {
//                     result[rowIndex] = currentElements.Count > 0
//                         ? DataValue.FromArray(elementKind, currentElements, store)
//                         : DataValue.NullArray(elementKind);
//                     currentElements.Clear();
//                 }
//                 rowIndex++;
//             }

//             if (data[i] is string s)
//                 currentElements.Add(DataValue.FromString(s, store));
//         }
//     }

//     private static void IterateFallback(
//         int[] repetitionLevels, Array data,
//         DataValue[] result, ref int rowIndex, List<DataValue> currentElements,
//         DataKind elementKind, IValueStore store)
//     {
//         for (int i = 0; i < repetitionLevels.Length; i++)
//         {
//             if (repetitionLevels[i] == 0)
//             {
//                 if (rowIndex >= 0)
//                 {
//                     result[rowIndex] = currentElements.Count > 0
//                         ? DataValue.FromArray(elementKind, currentElements, store)
//                         : DataValue.NullArray(elementKind);
//                     currentElements.Clear();
//                 }
//                 rowIndex++;
//             }

//             object? element = data.GetValue(i);
//             if (element is not null)
//                 currentElements.Add(ParquetValueExtractor.ExtractElement(element, elementKind, store));
//         }
//     }
// }
