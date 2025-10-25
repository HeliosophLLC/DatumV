#if false
// STUB File to contain my findings on the information schemata tables. This is not intended to be a complete implementation, but rather a place to collect my thoughts and findings on the topic.

    /// <summary>
    /// Maps virtual schema names to their known table names and column schemas.
    /// Column schemas use the same (name → DataKind string) format as
    /// <see cref="_tableColumnTypes"/> for consistent downstream validation.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> VirtualSchemaColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["information_schema"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["tables"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = "String",
                    ["table_schema"] = "String",
                    ["table_name"] = "String",
                    ["table_type"] = "String",
                },
                ["columns"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = "String",
                    ["table_schema"] = "String",
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["ordinal_position"] = "Int32",
                    ["data_type"] = "String",
                    ["is_nullable"] = "String",
                },
                ["schemata"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["catalog_name"] = "String",
                    ["schema_name"] = "String",
                },
            },
            ["datum_catalog"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["providers"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["provider_name"] = "String",
                },
                ["functions"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["function_name"] = "String",
                    ["function_type"] = "String",
                    ["category"] = "String",
                    ["return_type"] = "String",
                    ["description"] = "String",
                    ["parameter_count"] = "Int32",
                    ["query_unit_cost"] = "Int32",
                },
                ["function_parameters"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["function_name"] = "String",
                    ["ordinal_position"] = "Int32",
                    ["parameter_name"] = "String",
                    ["data_type"] = "String",
                    ["is_optional"] = "String",
                },
                ["statistics"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["data_type"] = "String",
                    ["row_count"] = "Int64",
                    ["distinct_count"] = "Int64",
                    ["null_ratio"] = "Float64",
                    ["min_value"] = "String",
                    ["max_value"] = "String",
                    ["entropy"] = "Float64",
                    ["dominant_value_ratio"] = "Float64",
                    ["is_constant"] = "String",
                    ["column_role"] = "String",
                    ["top_value"] = "String",
                    ["top_value_frequency"] = "Int64",
                    ["mean"] = "Float64",
                    ["standard_deviation"] = "Float64",
                    ["skewness"] = "Float64",
                    ["kurtosis"] = "Float64",
                    ["p25"] = "Float64",
                    ["p50"] = "Float64",
                    ["p75"] = "Float64",
                    ["zero_ratio"] = "Float64",
                    ["outlier_ratio"] = "Float64",
                    ["integer_valued"] = "String",
                    ["min_length"] = "Int32",
                    ["max_length"] = "Int32",
                    ["true_ratio"] = "Float64",
                },
                ["indexes"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["index_type"] = "String",
                    ["entry_count"] = "Int64",
                    ["chunk_count"] = "Int32",
                    ["total_row_count"] = "Int64",
                },
                ["interactions"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_a"] = "String",
                    ["column_b"] = "String",
                    ["pearson"] = "Float64",
                    ["spearman"] = "Float64",
                    ["cramer_v"] = "Float64",
                    ["anova_f"] = "Float64",
                    ["mutual_information"] = "Float64",
                    ["theil_u_ab"] = "Float64",
                    ["theil_u_ba"] = "Float64",
                    ["missingness_correlation"] = "Float64",
                },
            },
        };

#endif