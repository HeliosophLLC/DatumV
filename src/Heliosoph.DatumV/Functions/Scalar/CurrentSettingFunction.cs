using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns the current value of a session setting as text (PG
/// <c>current_setting()</c>). Recognized settings: <c>timezone</c> /
/// <c>time_zone</c> (the session time zone's canonical id) and
/// <c>search_path</c> (comma-separated schema list). Unknown names throw.
/// </summary>
/// <remarks>
/// <para>
/// Reads the live catalog state at evaluation time — not a plan-time
/// snapshot — so <c>SET TIME ZONE …; SHOW timezone;</c> in one batch
/// reports the post-SET value. Deliberately <see cref="IsPure"/>-false:
/// the value can change between statements, so it must never fold into a
/// plan-time constant or collapse across common-subexpression
/// elimination.
/// </para>
/// </remarks>
public sealed class CurrentSettingFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "current_setting";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns the current value of a session setting as text. " +
        "Recognized settings: 'timezone' and 'search_path'.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("setting_name", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CurrentSettingFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        string settingName = arg.AsString();
        string value = settingName.ToLowerInvariant() switch
        {
            "timezone" or "time_zone" => frame.Context.Catalog.SessionTimeZone.Id,
            "search_path" => string.Join(", ", frame.Context.Catalog.SearchPath),
            _ => throw new SessionSettingException(
                $"current_setting: unrecognized configuration parameter '{settingName}'."),
        };
        return new ValueTask<ValueRef>(ValueRef.FromString(value));
    }

    /// <inheritdoc />
    public bool IsPure => false;
}
