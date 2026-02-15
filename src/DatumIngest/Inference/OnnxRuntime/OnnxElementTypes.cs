using DatumIngest.Model;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// Bidirectional mapping between engine-side <see cref="DataKind"/> and ONNX
/// Runtime's <see cref="TensorElementType"/>. Used at the inference-backend
/// boundary to translate tensor specs in both directions: when reading a
/// loaded session's input/output metadata, and when allocating input
/// tensors from engine-side data.
/// </summary>
/// <remarks>
/// The mapping is partial: ONNX has element types DatumIngest does not yet
/// model (Complex64, Complex128, BFloat16, sub-byte types). Unmapped types
/// raise an exception at the boundary so a model with an unsupported
/// signature fails fast rather than silently producing garbage.
/// </remarks>
internal static class OnnxElementTypes
{
    /// <summary>Converts an ORT element type to the engine's <see cref="DataKind"/>.</summary>
    public static DataKind ToDataKind(TensorElementType ortType) => ortType switch
    {
        TensorElementType.Float    => DataKind.Float32,
        TensorElementType.UInt8    => DataKind.UInt8,
        TensorElementType.Int8     => DataKind.Int8,
        TensorElementType.UInt16   => DataKind.UInt16,
        TensorElementType.Int16    => DataKind.Int16,
        TensorElementType.Int32    => DataKind.Int32,
        TensorElementType.Int64    => DataKind.Int64,
        TensorElementType.Bool     => DataKind.Boolean,
        TensorElementType.Float16  => DataKind.Float16,
        TensorElementType.Double   => DataKind.Float64,
        TensorElementType.UInt32   => DataKind.UInt32,
        TensorElementType.UInt64   => DataKind.UInt64,
        _ => throw new NotSupportedException(
            $"ONNX tensor element type {ortType} has no DatumIngest DataKind mapping. " +
            "Add a case here if a model needs this element type.")
    };

    /// <summary>Converts a <see cref="DataKind"/> to the ORT element type.</summary>
    public static TensorElementType ToOnnxElementType(DataKind kind) => kind switch
    {
        DataKind.Float32 => TensorElementType.Float,
        DataKind.UInt8   => TensorElementType.UInt8,
        DataKind.Int8    => TensorElementType.Int8,
        DataKind.UInt16  => TensorElementType.UInt16,
        DataKind.Int16   => TensorElementType.Int16,
        DataKind.Int32   => TensorElementType.Int32,
        DataKind.Int64   => TensorElementType.Int64,
        DataKind.Boolean => TensorElementType.Bool,
        DataKind.Float16 => TensorElementType.Float16,
        DataKind.Float64 => TensorElementType.Double,
        DataKind.UInt32  => TensorElementType.UInt32,
        DataKind.UInt64  => TensorElementType.UInt64,
        _ => throw new NotSupportedException(
            $"DataKind {kind} has no ONNX tensor element type. " +
            "Only scalar numeric kinds map to ONNX tensors; arrays, structs, and " +
            "media kinds (Image/Audio/Video/Json) belong outside the tensor signature.")
    };
}
