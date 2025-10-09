using DatumIngest.Model;
using DatumIngest.Output.Writers;
using PureHDF;

namespace DatumIngest.Serialization.Hdf5;

/// <summary>
/// Maps HDF5 dataset types and ranks to <see cref="DataKind"/>.
/// </summary>
internal static class Hdf5SchemaMapper
{
    internal static DataKind InferDataKind(IH5Dataset dataset)
    {
        IH5DataType type = dataset.Type;
        byte rank = dataset.Space.Rank;

        if (type.Class == H5DataTypeClass.String || type.Class == H5DataTypeClass.VariableLength)
            return DataKind.String;

        if (rank == 2) return DataKind.Vector;
        if (rank == 3) return HasTensorKindAttribute(dataset) ? DataKind.Tensor : DataKind.Matrix;
        if (rank >= 4) return DataKind.Tensor;

        if (type.Class == H5DataTypeClass.FixedPoint)
        {
            return (type.Size, type.FixedPoint.IsSigned) switch
            {
                (1, false) => DataKind.UInt8,
                (1, true) => DataKind.Int8,
                (2, false) => DataKind.UInt16,
                (2, true) => DataKind.Int16,
                (4, false) => DataKind.UInt32,
                (4, true) => DataKind.Int32,
                (8, false) => DataKind.UInt64,
                (8, true) => DataKind.Int64,
                _ => DataKind.Int64,
            };
        }

        if (type.Class == H5DataTypeClass.FloatingPoint)
            return type.Size >= 8 ? DataKind.Float64 : DataKind.Float32;

        return DataKind.String;
    }

    internal static bool HasTensorKindAttribute(IH5Dataset dataset)
    {
        if (!dataset.AttributeExists(Hdf5OutputWriter.TensorKindAttributeName))
            return false;

        IH5Attribute attribute = dataset.Attribute(Hdf5OutputWriter.TensorKindAttributeName);
        string value = attribute.Read<string>();
        return string.Equals(value, Hdf5OutputWriter.TensorKindAttributeValue, StringComparison.Ordinal);
    }

    internal static Schema BuildSchema(List<Hdf5DatasetEntry> entries)
    {
        List<ColumnInfo> columns = new(entries.Count);
        foreach (Hdf5DatasetEntry entry in entries)
        {
            DataKind kind = InferDataKind(entry.Dataset);
            columns.Add(new ColumnInfo(entry.Path, kind, nullable: true));
        }
        return new Schema(columns);
    }
}
