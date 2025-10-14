// using DatumIngest.Model;
// using Parquet.Schema;

// namespace DatumIngest.Serialization.Parquet;

// /// <summary>
// /// Maps Parquet schema fields to <see cref="DataKind"/> and builds projection metadata.
// /// Pure static methods, no state.
// /// </summary>
// internal static class ParquetSchemaMapper
// {
//     /// <summary>
//     /// Builds a <see cref="Schema"/> from the Parquet reader's schema fields.
//     /// </summary>
//     internal static Schema BuildSchema(IReadOnlyList<Field> fields)
//     {
//         List<ColumnInfo> columns = new(fields.Count);

//         foreach (Field field in fields)
//         {
//             if (field is ListField listField && listField.Item is DataField itemField)
//             {
//                 DataKind elementKind = DataValue.MapClrType(itemField.ClrType);
//                 columns.Add(new ColumnInfo(field.Name, DataKind.Array, nullable: true, arrayElementKind: elementKind));
//             }
//             else if (field is DataField dataField)
//             {
//                 DataKind kind = DataValue.MapClrType(dataField.ClrType);
//                 columns.Add(new ColumnInfo(field.Name, kind, nullable: dataField.IsNullable));
//             }
//         }

//         return new Schema(columns);
//     }

//     /// <summary>
//     /// Projection metadata for a set of Parquet columns. Parallel lists indexed by projected column ordinal.
//     /// </summary>
//     internal readonly record struct ProjectedColumns(
//         IReadOnlyList<DataField> DataFields,
//         IReadOnlyList<string> ColumnNames,
//         IReadOnlyList<DataKind> ColumnKinds,
//         IReadOnlyList<bool> IsListColumn,
//         IReadOnlyList<DataKind> ElementKinds)
//     {
//         public int Count => DataFields.Count;
//     }

//     /// <summary>
//     /// Builds projection metadata from the Parquet schema, handling both flat
//     /// <see cref="DataField"/> and <see cref="ListField"/> (array) columns.
//     /// </summary>
//     internal static ProjectedColumns BuildProjection(
//         IReadOnlyList<Field> schemaFields,
//         IReadOnlySet<string>? requiredColumns)
//     {
//         List<DataField> dataFields = new();
//         List<string> names = new();
//         List<DataKind> kinds = new();
//         List<bool> isList = new();
//         List<DataKind> elementKinds = new();

//         foreach (Field field in schemaFields)
//         {
//             if (requiredColumns is not null && !requiredColumns.Contains(field.Name))
//                 continue;

//             if (field is ListField listField && listField.Item is DataField itemField)
//             {
//                 dataFields.Add(itemField);
//                 names.Add(field.Name);
//                 kinds.Add(DataKind.Array);
//                 isList.Add(true);
//                 elementKinds.Add(DataValue.MapClrType(itemField.ClrType));
//             }
//             else if (field is DataField dataField)
//             {
//                 dataFields.Add(dataField);
//                 names.Add(field.Name);
//                 kinds.Add(DataValue.MapClrType(dataField.ClrType));
//                 isList.Add(false);
//                 elementKinds.Add(default);
//             }
//         }

//         return new(dataFields, names, kinds, isList, elementKinds);
//     }
// }
