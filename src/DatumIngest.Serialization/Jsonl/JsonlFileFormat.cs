// using System.Diagnostics.CodeAnalysis;

// namespace DatumIngest.Serialization.Jsonl;

// /// <summary>
// /// Format handler for JSONL/NDJSON files. Matches <c>.jsonl</c> and <c>.ndjson</c> extensions.
// /// </summary>
// public sealed class JsonlFileFormat : IFileFormat
// {
//     /// <inheritdoc />
//     public string Name => "jsonl";

//     /// <inheritdoc />
//     public bool CanHandle(
//         FileFormatDescriptor descriptor,
//         [NotNullWhen(true)] out IFormatDeserializer? deserializer)
//     {
//         string ext = Path.GetExtension(descriptor.FilePath);
//         if (ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
//             ext.Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
//         {
//             deserializer = new JsonlDeserializer(descriptor);
//             return true;
//         }

//         deserializer = null;
//         return false;
//     }
// }
