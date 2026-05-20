using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2.Decoding;

/// <summary>
/// Per-column page decoder for the v2 columnar format. One instance per
/// (reader, page) pair. Constructed over the mmap'd page bytes; provides
/// random-access reads via <see cref="ReadValue"/>.
/// </summary>
internal interface IPageDecoderV2
{
    /// <summary>The number of rows captured in this page.</summary>
    int RowCount { get; }

    /// <summary>
    /// Reads the value at <paramref name="rowIndex"/>. Returns a typed
    /// null when the row's null bit is set. For
    /// <see cref="EncoderKind.VariableSlot"/> sidecar-pointer rows, the
    /// returned <see cref="DataValue"/> carries
    /// <see cref="Model.DataValue.IsInSidecar"/> = <see langword="true"/>;
    /// the caller resolves the bytes via the per-query
    /// <see cref="DatumFile.Sidecar.SidecarRegistry"/> using the
    /// configured <c>storeId</c>.
    /// </summary>
    DataValue ReadValue(int rowIndex);
}
