// using System.Diagnostics.CodeAnalysis;

// namespace DatumIngest.Serialization.Parquet;

// /// <summary>
// /// Format handler for Apache Parquet files. Matches <c>.parquet</c> and <c>.pq</c> extensions.
// /// </summary>
// public sealed class ParquetFileFormat : IFileFormat
// {
//     /// <inheritdoc />
//     public string Name => "parquet";

//     /// <inheritdoc />
//     public bool CanHandle(
//         FileFormatDescriptor descriptor,
//         [NotNullWhen(true)] out IFormatDeserializer? deserializer)
//     {
//         string ext = Path.GetExtension(descriptor.FilePath);
//         if (ext.Equals(".parquet", StringComparison.OrdinalIgnoreCase) ||
//             ext.Equals(".pq", StringComparison.OrdinalIgnoreCase))
//         {
//             deserializer = new ParquetDeserializer(descriptor);
//             return true;
//         }

//         deserializer = null;
//         return false;
//     }
// }
