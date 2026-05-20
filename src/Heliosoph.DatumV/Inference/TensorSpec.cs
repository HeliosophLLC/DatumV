using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Inference;

/// <summary>
/// Describes one tensor in a model's input or output signature: name,
/// element type, and shape (with <see langword="null"/> dimensions for
/// dynamic axes). Returned from <see cref="IInferenceSession.Inputs"/> /
/// <see cref="IInferenceSession.Outputs"/> so callers can validate their
/// <see cref="TensorBag"/> contents at SQL-MODEL load time rather than
/// per-row.
/// </summary>
/// <param name="Name">
/// The tensor name as declared in the ONNX graph (e.g. <c>"input_ids"</c>,
/// <c>"pixel_values"</c>, <c>"last_hidden_state"</c>). The same name is used
/// as the key in <see cref="TensorBag"/>.
/// </param>
/// <param name="ElementKind">
/// Engine-side scalar element type. Maps 1:1 to ONNX / OpenVINO native
/// dtypes — <see cref="DataKind.Float32"/>, <see cref="DataKind.Float16"/>,
/// <see cref="DataKind.Int64"/>, etc.
/// </param>
/// <param name="Shape">
/// Dimensions. A <see langword="null"/> entry means "dynamic" (sequence
/// length, batch size, image resolution). Concrete inputs must agree with
/// the non-null entries; backends decide what to do with the null entries
/// at run time.
/// </param>
public sealed record TensorSpec(
    string Name,
    DataKind ElementKind,
    IReadOnlyList<int?> Shape);
