using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// 1-D list builders for top-level <c>Array&lt;T&gt;</c> columns. Each
/// produces an Arrow <see cref="Apache.Arrow.ListArray"/> with the
/// element-type matching the engine's per-element kind. Element kinds
/// supported in v1 mirror <c>OpenArrowFunction</c>'s reader set:
/// Boolean, Int8/16/32/64, UInt8/16/32/64, Float32/64, String.
/// </summary>
/// <remarks>
/// Implementation lives in <c>ArrowListBuilders.Impl.cs</c>; this entry
/// point exists so other files in the namespace can reference the
/// factory without dragging the per-element-kind switch into the call
/// site.
/// </remarks>
internal static class ArrowListBuilders
{
    public static IArrowColumnBuilder Create(DataKind elementKind, SidecarRegistry? sidecarRegistry)
    {
        return elementKind switch
        {
            DataKind.Boolean => new ArrowListOfBooleanBuilder(),
            DataKind.Int8 => new ArrowListOfInt8Builder(),
            DataKind.UInt8 => new ArrowListOfUInt8Builder(),
            DataKind.Int16 => new ArrowListOfInt16Builder(),
            DataKind.UInt16 => new ArrowListOfUInt16Builder(),
            DataKind.Int32 => new ArrowListOfInt32Builder(),
            DataKind.UInt32 => new ArrowListOfUInt32Builder(),
            DataKind.Int64 => new ArrowListOfInt64Builder(),
            DataKind.UInt64 => new ArrowListOfUInt64Builder(),
            DataKind.Float32 => new ArrowListOfFloat32Builder(),
            DataKind.Float64 => new ArrowListOfFloat64Builder(),
            DataKind.String => new ArrowListOfStringBuilder(sidecarRegistry),
            _ => throw new ExportPlanException(
                $"COPY TO arrow: Array<{elementKind}> has no ListArray writer in v1. " +
                "Supported element kinds: Boolean, Int8/16/32/64, UInt8/16/32/64, " +
                "Float32/64, String. Export to parquet for the broader set."),
        };
    }
}
