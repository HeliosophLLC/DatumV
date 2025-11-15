namespace DatumIngest.Manifest;

/// <summary>
/// Canonical lists of SQL keywords, type names, date-part names, and built-in
/// function names recognized by the DatumIngest dialect. Used as a single source
/// of truth for Monaco's <c>MonarchGrammarFactory</c>, the catalog-driven
/// <c>LanguageServerManifest</c> builder, and any future tooling that needs to
/// surface the dialect surface area without re-parsing tokens.
/// </summary>
/// <remarks>
/// The shell's <c>ShellHighlighter</c> deliberately does not consume these lists —
/// it switches on parser <c>SqlToken</c> values directly, which is the
/// authoritative source for what the tokenizer recognizes. These string lists
/// exist for surfaces (Monarch, manifest) that need names without running the
/// tokenizer.
/// </remarks>
public static class SqlKeywordRegistry
{
    /// <summary>
    /// SQL clause and operator keywords. <c>TRUE</c>, <c>FALSE</c>, and
    /// <c>NULL</c> are intentionally excluded — they live in
    /// <see cref="BoolNullKeywords"/> so themes can color them distinctly.
    /// </summary>
    public static IReadOnlyList<string> ClauseKeywords { get; } =
    [
        // Core DML and clause keywords
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "LATERAL", "APPLY", "ON", "WHERE", "AND", "OR",
        "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE", "IS",
        "AS", "SHARD", "GROUP", "HAVING", "QUALIFY", "ORDER", "BY", "ASC",
        "DESC", "LIMIT", "OFFSET", "CAST", "EXTRACT", "AT", "TIME", "ZONE",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP",

        // Conditional expressions
        "CASE", "WHEN", "THEN", "ELSE", "END",

        // Window function keywords
        "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING",
        "FOLLOWING", "CURRENT",

        // Modifiers
        "EXISTS", "DISTINCT", "IGNORE", "RESPECT", "NULLS",

        // Common Table Expressions
        "WITH", "RECURSIVE", "MATERIALIZED",

        // Set operations
        "UNION", "ALL", "INTERSECT", "EXCEPT",

        // DatumIngest extensions
        "LET", "SCAN", "INIT", "PIVOT", "UNPIVOT", "FOR", "INCLUDE",

        // ASSERT / DEFINE clause keywords
        "ASSERT", "DEFINE", "MESSAGE", "FAIL", "WARN", "SKIP", "ABORT",

        // DDL keywords
        "CREATE", "TABLE", "TEMP", "TEMPORARY", "DROP", "ALTER", "ADD",
        "COLUMN", "DEFAULT", "PRIMARY", "KEY", "IF", "INDEX",
        "ANALYZE",

        // DML keywords
        "INSERT", "VALUES", "UPDATE", "SET", "DELETE",
    ];

    /// <summary>Boolean and null literal keywords.</summary>
    public static IReadOnlyList<string> BoolNullKeywords { get; } =
        ["TRUE", "FALSE", "NULL"];

    /// <summary>
    /// Date-part field names used with <c>EXTRACT(field FROM source)</c> and
    /// <c>date_part('field', source)</c>. Names that overlap with SQL keywords
    /// or built-in functions (e.g. <c>YEAR</c>, <c>MONTH</c>) are excluded —
    /// they already get keyword or function coloring via earlier case rules.
    /// </summary>
    public static IReadOnlyList<string> DatePartKeywords { get; } =
    [
        "DOW", "DOY", "ISODOW", "ISOYEAR",
        "EPOCH", "JULIAN",
        "CENTURY", "DECADE", "MILLENNIUM",
        "MICROSECOND", "MILLISECOND",
        "TIMEZONE", "TIMEZONE_HOUR", "TIMEZONE_MINUTE",
    ];

    /// <summary>
    /// Column data type names. These are tokenized as <c>type.identifier</c>
    /// in Monarch so editor themes can color them distinctly from keywords
    /// and plain identifiers.
    /// </summary>
    public static IReadOnlyList<string> TypeKeywords { get; } =
    [
        "Unknown",
        "Type",
        "Boolean",
        "UInt8", "UInt16", "UInt32", "UInt64",
        "Int8", "Int16", "Int32", "Int64",
        "Float32", "Float64",
        "Date", "Time", "DateTime", "Duration",
        "String", "JsonValue", "Uuid",
        "UInt8Array", "Image",
        "Vector", "Matrix", "Tensor", "Array", "Struct",
    ];

    /// <summary>
    /// Built-in function names. Names that overlap with SQL keywords (e.g.
    /// <c>LEFT</c>, <c>RIGHT</c>, <c>CAST</c>, <c>RANGE</c>) are intentionally
    /// excluded — Monarch's <c>@keywords</c> case takes precedence so they
    /// will be colored as keywords regardless.
    /// </summary>
    public static IReadOnlyList<string> BuiltinFunctions { get; } =
    [
        // Aggregate functions
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "VARIANCE", "VAR_SAMP", "VAR_POP",
        "STDDEV", "STDDEV_SAMP", "STDDEV_POP",
        "MEDIAN", "PERCENTILE_CONT", "PERCENTILE_DISC", "MODE",
        "CORR", "COVAR_POP", "COVAR_SAMP",
        "APPROX_MEDIAN", "APPROX_PERCENTILE",
        "STRING_AGG", "ARRAY_AGG", "ARG_MAX", "ARG_MIN",

        // Window functions
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",

        // Table-valued functions
        "UNNEST",

        // String functions
        "len", "length", "char_length", "character_length",
        "mid", "substring", "substr", "overlay", "upper", "lower",
        "trim", "ltrim", "rtrim", "btrim",
        "contains", "starts_with", "ends_with", "position", "strpos", "replace",
        "concat", "concat_ws", "repeat", "reverse",
        "lpad", "rpad", "regexp_extract", "regexp_replace",
        "regexp_count", "regexp_like", "regexp_match",
        "regexp_substr", "regexp_instr",
        "word_count", "split_part", "initcap", "translate",
        "ascii", "chr", "octet_length", "bit_length",
        "format", "string_to_array", "regexp_split_to_array",
        "to_hex", "to_bin", "to_oct", "to_ascii", "unistr", "casefold", "normalize",
        "quote_ident", "quote_literal", "quote_nullable", "parse_ident",
        "get_filename", "get_file_extension", "get_path",

        // Date/Time functions
        "now", "year", "month", "day", "hour", "minute", "second",
        "quarter", "dayofweek", "dayofyear",
        "make_date", "make_timestamp", "make_time", "current_time",
        "transaction_timestamp", "statement_timestamp", "clock_timestamp", "timeofday",
        "date_diff", "date_add", "date_trunc", "date_bucket", "date_bin",
        "date_span", "date_offset", "time_diff",
        "strftime", "is_date",

        // Duration functions
        "make_duration", "duration_seconds", "duration_minutes",
        "duration_hours", "duration_days",

        // Type conversion / introspection functions
        "to_epoch", "date_part", "cyclical_encode", "typeof", "can_cast", "try_cast",

        // JSON functions
        "json_value", "json_query", "json_exists", "json_array_length",

        // Math — Arithmetic & Basics
        "abs", "sign", "negate", "mod",
        "add", "subtract", "multiply", "divide",

        // Math — Powers/Roots/Logs
        "sqrt", "cbrt", "square", "exp", "exp2",
        "ln", "log2", "log10", "pow", "log",

        // Math — Trigonometric & Hyperbolic
        "sin", "cos", "tan", "asin", "acos", "atan", "atan2",
        "sinh", "cosh", "tanh", "degrees", "radians",
        "pi", "euler",

        // Math — Rounding & Quantization
        "ceil", "floor", "truncate", "round",
        "quantize", "bucketize", "clip",

        // Math — ML Activations
        "sigmoid", "relu", "selu", "gelu", "swish",
        "softplus", "softsign", "mish",
        "hard_sigmoid", "hard_swish", "leaky_relu", "elu",
        "softmax", "log_softmax", "l2_normalize",

        // Math — Vector Reductions
        "vec_sum", "vec_mean", "vec_min", "vec_max",
        "vec_std", "vec_var", "vec_median",
        "vec_argmin", "vec_argmax", "vec_norm",
        "vec_count_nonzero", "vec_any", "vec_all", "vec_product",

        // Tensor introspection
        "rdim", "shape",

        // Vector/Tensor manipulation
        "vec", "tensor", "vec_slice", "vec_concat",
        "vec_reverse", "vec_sort", "vec_unique",
        "vec_flatten", "vec_pad", "vec_repeat",
        "linspace", "arange",

        // Distance/Similarity
        "cosine_similarity", "euclidean_distance",
        "manhattan_distance", "dot", "hamming_distance",

        // Utility & Conditional
        "nullif", "coalesce", "greatest", "least",
        "is_nan", "is_finite", "is_even", "is_odd",
        "if_null", "iif", "choose",

        // Random & Sampling
        "random", "hash_split", "random_int", "random_range",
        "random_normal", "random_boolean",
        "random_truncated_normal", "random_log_normal",
        "random_exponential", "random_beta",
        "random_poisson", "random_categorical",
        "random_vector", "random_normal_vector",
        "random_permutation", "random_choice",

        // Numeric/Array
        "min_max_normalize", "clamp", "denormalize", "reshape",

        // Array functions
        "array", "array_length", "array_get",
        "array_contains", "array_position", "array_join",
        "array_concat", "array_slice", "array_sort",
        "array_reverse", "array_distinct",
        "array_min", "array_max", "array_sum", "array_avg",
        "array_transform", "array_filter",

        // Byte functions
        "bytes", "bytes_concat", "bytes_slice",

        // Categorical/Encoding
        "one_hot", "one_hot_unk",
        "label_encode", "label_encode_unk", "hash_encode",

        // Hash functions
        "md5", "md5_bytes", "sha256", "sha512", "crc32",

        // Encoding functions
        "base64_encode", "base64_decode",
        "hex_encode", "hex_decode",

        // UUID functions
        "uuidv4", "gen_random_uuid", "uuidv7", "is_uuid",
        "uuid_str", "uuid_bytes", "uuid_extract_version", "uuid_extract_timestamp",

        // Image — Metadata
        "width", "height", "channels", "pixel_count", "dimensions",

        // Image — Loading & Decode
        "load_image", "image_to_bytes",
        "image_to_tensor_hwc", "image_to_tensor_chw",

        // Image — Analysis
        "image_brightness_mean", "image_brightness_std",
        "image_brightness_histogram",
        "detect_blur", "compression_artifact_score",
        "image_pixel_mean", "image_pixel_std",

        // Image — Transforms
        "resize", "crop", "grayscale", "rotate",
        "noise", "blur", "brighten", "darken", "sobel",
        "resize_and_crop", "affine_transform",
        "elastic_deform", "perspective_warp",

        // Image — Hashing
        "perceptual_hash",
    ];
}
