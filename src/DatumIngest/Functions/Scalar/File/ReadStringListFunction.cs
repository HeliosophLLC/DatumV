using System.Collections.Concurrent;
using System.Text.Json;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// <c>read_string_list(path String) → Array&lt;String&gt;</c>. Reads a JSON
/// array of strings off disk and returns it as a typed Float-style
/// <c>Array&lt;String&gt;</c>. Designed for SQL-defined models that need to
/// look up labels (ImageNet class names, COCO labels, custom classifier
/// vocabularies) bundled alongside the ONNX file at install time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Path resolution.</strong> Three forms accepted (mirrors
/// <c>tokenizer.encode_bert</c>):
/// <list type="bullet">
///   <item><description><c>file://</c>-prefixed URI — used verbatim after
///     stripping the prefix.</description></item>
///   <item><description>OS-absolute path — used verbatim.</description></item>
///   <item><description>Relative path — resolved against the calling
///     model's <c>USING</c> directory (the model descriptor's resolved
///     USING path). Outside a CREATE MODEL body relative paths throw
///     a clear <see cref="FunctionArgumentException"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Caching.</strong> Process-wide by absolute path. Multi-row queries
/// re-resolve the same labels file once; subsequent reads return the cached
/// array reference. Cache entries never evict — labels files are tiny
/// (a few KB) and stable, so the residency cost is negligible.
/// </para>
/// <para>
/// <strong>File format.</strong> Strict JSON array of strings —
/// <c>["person", "bicycle", "car", ...]</c>. Non-array roots or
/// non-string elements throw <see cref="InvalidDataException"/>;
/// the schema is intentionally narrow because the typical caller is
/// indexing into the array by class id and a malformed entry would
/// surface as a confusing index error several rows in.
/// </para>
/// </remarks>
public sealed class ReadStringListFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "read_string_list";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Reads a JSON array of strings from disk and returns Array<String>. "
        + "Path may be a 'file://' URI, an absolute path, or — inside a CREATE MODEL body — "
        + "relative to the model's USING directory. Results are cached by absolute path "
        + "for the lifetime of the process.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ReadStringListFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef pathArg = arguments.Span[0];
        if (pathArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.String));
        }

        string absolutePath = ResolvePath(pathArg.AsString(), frame);
        string[] strings = Cache.GetOrAdd(absolutePath, LoadFromDisk);

        ValueRef[] elements = new ValueRef[strings.Length];
        for (int i = 0; i < strings.Length; i++)
        {
            elements[i] = ValueRef.FromString(strings[i]);
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.String, elements));
    }

    private static readonly ConcurrentDictionary<string, string[]> Cache = new(StringComparer.Ordinal);

    private static string ResolvePath(string path, EvaluationFrame frame)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.Path.GetFullPath(path["file://".Length..]);
        }
        if (System.IO.Path.IsPathRooted(path))
        {
            return System.IO.Path.GetFullPath(path);
        }
        if (frame.CurrentModel is { ResolvedUsingPath: { } resolved })
        {
            string? modelDir = System.IO.Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(modelDir))
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(modelDir, path));
            }
        }
        throw new FunctionArgumentException("read_string_list",
            $"'{path}' is a relative path but read_string_list was called outside a CREATE MODEL "
            + "body. Pass an absolute path, a 'file://'-prefixed URI, or call inside a model body "
            + "where the path resolves against the model's USING directory.");
    }

    private static string[] LoadFromDisk(string absolutePath)
    {
        if (!System.IO.File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"read_string_list: file '{absolutePath}' not found.", absolutePath);
        }

        using FileStream stream = System.IO.File.OpenRead(absolutePath);
        using JsonDocument doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"read_string_list: '{absolutePath}' is not a JSON array.");
        }

        List<string> result = new(doc.RootElement.GetArrayLength());
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"read_string_list: '{absolutePath}' element at index {result.Count} "
                    + "is not a string. The schema is strict — every element must be a JSON string.");
            }
            result.Add(element.GetString() ?? string.Empty);
        }
        return result.ToArray();
    }
}
