using System.Text;
using System.Text.Json;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Web.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace DatumIngest.Web.Tests;

/// <summary>
/// End-to-end tests for the parameter-binding path through
/// <see cref="QueryStreamService"/>: a SQL script with <c>$name</c>
/// references plus a matching <see cref="ParameterValue"/> dictionary
/// should produce row events with the substituted values, and missing /
/// extra parameters should surface as inline NDJSON error events with
/// the offending parameter name.
/// </summary>
public sealed class QueryStreamServiceParameterTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly QueryStreamService _service;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public QueryStreamServiceParameterTests()
    {
        ServiceCollection services = new();
        services.AddDatumIngest();
        _services = services.BuildServiceProvider();
        Pool pool = _services.GetRequiredService<Pool>();
        TableCatalog catalog = new(pool);
        _service = new QueryStreamService(catalog);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task SelectWithInt32Parameter_SubstitutesAndReturnsResult()
    {
        Dictionary<string, ParameterValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["n"] = new InlineParameter(DataValue.FromInt32(41)),
        };
        IReadOnlyList<JsonDocument> events = await RunAsync("SELECT $n + 1", parameters);

        JsonDocument? row = FindEvent(events, "row");
        Assert.NotNull(row);
        JsonElement cells = row!.RootElement.GetProperty("cells");
        Assert.Equal(1, cells.GetArrayLength());
        // The value is rendered through WebCellFormatter; for an Int32 it
        // surfaces as a plain number under the `text` field. Don't assert
        // the exact wire shape — just that 42 appears somewhere in the
        // serialized row.
        string rendered = cells[0].GetRawText();
        Assert.Contains("42", rendered);
    }

    [Fact]
    public async Task SelectWithStringParameter_SubstitutesAndReturnsResult()
    {
        Dictionary<string, ParameterValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["greeting"] = new StringParameter("hello world"),
        };
        IReadOnlyList<JsonDocument> events = await RunAsync("SELECT $greeting", parameters);

        JsonDocument? row = FindEvent(events, "row");
        Assert.NotNull(row);
        string rendered = row!.RootElement.GetProperty("cells").GetRawText();
        Assert.Contains("hello world", rendered);
    }

    [Fact]
    public async Task ParameterReferencedButNotSupplied_EmitsErrorEvent()
    {
        Dictionary<string, ParameterValue> empty = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<JsonDocument> events = await RunAsync("SELECT $missing", empty);

        JsonDocument? err = FindEvent(events, "error");
        Assert.NotNull(err);
        string message = err!.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("missing", message, StringComparison.OrdinalIgnoreCase);
        // A `complete` event follows the inline error so the NDJSON parser
        // sees a clean terminator.
        Assert.NotNull(FindEvent(events, "complete"));
    }

    [Fact]
    public async Task ParameterSuppliedButNotReferenced_EmitsErrorEvent()
    {
        Dictionary<string, ParameterValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["unused"] = new InlineParameter(DataValue.FromInt32(1)),
        };
        IReadOnlyList<JsonDocument> events = await RunAsync("SELECT 1", parameters);

        JsonDocument? err = FindEvent(events, "error");
        Assert.NotNull(err);
        string message = err!.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("unused", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullParametersDictionary_SkipsBinderEntirely()
    {
        // Passing a null parameters dict preserves the pre-port behaviour —
        // SQL without any $ references runs unchanged.
        IReadOnlyList<JsonDocument> events = await RunAsync("SELECT 7", parameters: null);

        JsonDocument? row = FindEvent(events, "row");
        Assert.NotNull(row);
        string rendered = row!.RootElement.GetProperty("cells").GetRawText();
        Assert.Contains("7", rendered);
    }

    // ───────────────────── helpers ─────────────────────

    private async Task<IReadOnlyList<JsonDocument>> RunAsync(
        string sql,
        IReadOnlyDictionary<string, ParameterValue>? parameters)
    {
        using MemoryStream output = new();
        await _service.ExecuteAsync(
            sql,
            maxRows: 1000,
            trace: TraceOptions.Off,
            parameters,
            output,
            Json,
            CancellationToken.None);

        output.Position = 0;
        string text = Encoding.UTF8.GetString(output.ToArray());
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    private static JsonDocument? FindEvent(IReadOnlyList<JsonDocument> events, string type)
    {
        foreach (JsonDocument doc in events)
        {
            if (doc.RootElement.TryGetProperty("type", out JsonElement t)
                && t.GetString() == type)
            {
                return doc;
            }
        }
        return null;
    }
}
