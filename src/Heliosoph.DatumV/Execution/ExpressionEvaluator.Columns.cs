using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

public sealed partial class ExpressionEvaluator
{
    private DataValue EvaluateColumn(ColumnReference column, EvaluationFrame frame)
    {
        // Variable-first precedence (PG PL/pgSQL `use_variable` semantics): an
        // unqualified reference is resolved against the procedural variable
        // scope before the row schema. Qualified `t.col` references skip
        // this — variables are never schema-qualified.
        if (column.TableName is null
            && _variableScope.TryGet(column.ColumnName, out ValueRef variableValue))
        {
            // The scope holds a ValueRef; the DataValue path needs the value
            // anchored in frame.Target. ToDataValue handles every payload
            // shape — inline scalars pass through, managed payloads
            // (Materialized) write into frame.Target on demand, arena-backed
            // values stabilise across arenas.
            return variableValue.ToDataValue(frame.Target, variableValue.TypeId, frame.Types);
        }

        Row row = frame.Row;

        // For qualified references (table.column), try the full qualified name first,
        // then the unqualified column name.
        if (column.QualifiedName is not null)
        {
            if (row.TryGetValue(column.QualifiedName, out DataValue qualifiedValue))
            {
                return qualifiedValue;
            }
        }

        if (row.TryGetValue(column.ColumnName, out DataValue value))
        {
            return value;
        }

        // Fall back to the outer row for correlated subquery column resolution.
        if (frame.OuterRow is Row outerRow)
        {
            if (column.QualifiedName is not null &&
                outerRow.TryGetValue(column.QualifiedName, out DataValue outerQualifiedValue))
            {
                return outerQualifiedValue;
            }

            if (outerRow.TryGetValue(column.ColumnName, out DataValue outerValue))
            {
                return outerValue;
            }
        }

        // Struct-field access via dot-notation: `curr_depth.intrinsics`
        // where `curr_depth` is a LET-bound or DECLARE'd struct. PG-style
        // bracket access (`curr_depth['intrinsics']`) already works via the
        // IndexAccessExpression path; this branch makes the more natural
        // dot syntax work too. Two sources to check, in priority order:
        //   1. The augmented row — SELECT LETs land here.
        //   2. The procedural variable scope — DECLARE / FOR / lambda
        //      parameter bindings land here.
        // Row lookups above had a chance to claim `curr_depth.intrinsics`
        // as a literal column name (the QualifiedName path); only fall
        // through to struct-field access when no row column matched.
        if (column.TableName is not null)
        {
            DataValue maybeStruct = default;
            bool foundStruct = false;
            if (row.TryGetValue(column.TableName, out DataValue rowValue)
                && rowValue.Kind == DataKind.Struct
                && !rowValue.IsNull)
            {
                maybeStruct = rowValue;
                foundStruct = true;
            }
            else if (_variableScope.TryGet(column.TableName, out ValueRef varValue)
                && varValue.Kind == DataKind.Struct
                && !varValue.IsNull)
            {
                maybeStruct = varValue.ToDataValue(frame.Source, varValue.TypeId, frame.Types);
                foundStruct = true;
            }
            // Lateral correlation: the lifted LET binding sits on the
            // outer driving row, not the current row. Without this branch
            // a struct-valued LET referenced via dot notation in a
            // lateral function source argument (`unnest(s.arr)` where
            // `s = {arr: [...]}` is a LET) can't be resolved.
            else if (frame.OuterRow is Row outer
                && outer.TryGetValue(column.TableName, out DataValue outerStruct)
                && outerStruct.Kind == DataKind.Struct
                && !outerStruct.IsNull)
            {
                maybeStruct = outerStruct;
                foundStruct = true;
            }

            if (foundStruct)
            {
                DataValue[] fields = maybeStruct.AsStruct(frame.Source);
                int fieldIndex = ResolveStructFieldIndexByName(
                    maybeStruct.TypeId, column.ColumnName, frame);
                if (fieldIndex < 0 && _sourceSchema is not null)
                {
                    // Values from decode paths that don't stamp a runtime
                    // TypeId (e.g. index-scan seek sessions) still resolve
                    // when the schema declares the struct column's fields —
                    // field order in the serialized payload matches the
                    // declared order by construction.
                    IReadOnlyList<ColumnInfo>? schemaFields = FindStructColumnFields(
                        new ColumnReference(column.TableName), _sourceSchema);
                    if (schemaFields is not null)
                    {
                        for (int i = 0; i < schemaFields.Count; i++)
                        {
                            if (string.Equals(schemaFields[i].Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                            {
                                fieldIndex = i;
                                break;
                            }
                        }
                    }
                }
                if (fieldIndex >= 0 && fieldIndex < fields.Length)
                {
                    return fields[fieldIndex];
                }
                // Variable / row column exists and is a struct but the
                // field name didn't match. Surface a precise error rather
                // than the generic "not found in row" — the user clearly
                // meant a struct field lookup, just typed a wrong name.
                throw new InvalidOperationException(
                    $"Struct '{column.TableName}' has no field named '{column.ColumnName}'.");
            }
        }

        if (column.TableName is not null)
        {
            throw new InvalidOperationException(
                $"Column '{column.TableName}.{column.ColumnName}' not found in row.");
        }

        // Unqualified miss: the name is neither a declared variable in
        // scope nor a column in the current row. Variables and columns
        // share the same lookup surface (PG `use_variable` semantics) so
        // surfacing both possibilities steers the user past typos in
        // either direction.
        throw new InvalidOperationException(
            $"Name '{column.ColumnName}' is not a declared variable in scope and is not a column in the current row.");
    }

    /// <summary>
    /// Returns the positional index of a struct field by name, or <c>-1</c>
    /// when not found. Walks the per-query <see cref="TypeRegistry"/> when
    /// available; the registry's <see cref="TypeDescriptor.FindFieldIndex"/>
    /// does the case-insensitive lookup. Returns <c>-1</c> when no registry
    /// is reachable or when the value's <see cref="DataValue.TypeId"/> is
    /// unregistered — callers fall back to a clear "field not found"
    /// error rather than a position-mismatch hazard.
    /// </summary>
    private int ResolveStructFieldIndexByName(ushort typeId, string fieldName, EvaluationFrame frame)
    {
        TypeRegistry? registry = _typeRegistry ?? frame.Types;
        if (registry is null || typeId == 0) return -1;
        TypeDescriptor? desc = registry.GetDescriptor(typeId);
        return desc is null ? -1 : desc.FindFieldIndex(fieldName);
    }
}
