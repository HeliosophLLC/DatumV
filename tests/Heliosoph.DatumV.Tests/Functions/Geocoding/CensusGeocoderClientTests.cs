using System.Net;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Geocoding;

namespace Heliosoph.DatumV.Tests.Functions.Geocoding;

/// <summary>
/// Tests for <see cref="CensusGeocoderClient"/> — the request-CSV formatter, the
/// response-CSV parser (including the service's longitude-first coordinate field),
/// 10,000-record chunking, and transport-failure mapping to
/// <see cref="RemoteServiceException"/>. All HTTP goes through a stub handler.
/// </summary>
public sealed class CensusGeocoderClientTests
{
    // ───────────────────────── request CSV ─────────────────────────

    [Fact]
    public void BuildRequestCsv_PlainFields_OnePerLine()
    {
        CensusAddressRecord[] records =
        [
            new("1", "1600 Pennsylvania Ave NW", "Washington", "DC", "20500"),
            new("2", "742 Evergreen Ter", "Springfield", "IL", "62704"),
        ];

        string csv = CensusGeocoderClient.BuildRequestCsv(records, 0, 2);

        Assert.Equal(
            "1,1600 Pennsylvania Ave NW,Washington,DC,20500\n"
            + "2,742 Evergreen Ter,Springfield,IL,62704\n",
            csv);
    }

    [Fact]
    public void BuildRequestCsv_CommaAndQuoteFields_AreQuoted()
    {
        CensusAddressRecord[] records =
        [
            new("1", "Suite 4, 100 Main St", "St. \"Loo\" Louis", "MO", "63101"),
        ];

        string csv = CensusGeocoderClient.BuildRequestCsv(records, 0, 1);

        Assert.Equal("1,\"Suite 4, 100 Main St\",\"St. \"\"Loo\"\" Louis\",MO,63101\n", csv);
    }

    [Fact]
    public void BuildRequestCsv_LineBreaks_FlattenToSpaces()
    {
        CensusAddressRecord[] records = [new("1", "100 Main St\r\nApt 2", "Springfield", "IL", "62704")];

        string csv = CensusGeocoderClient.BuildRequestCsv(records, 0, 1);

        Assert.Equal("1,100 Main St Apt 2,Springfield,IL,62704\n", csv);
    }

    [Fact]
    public void BuildRequestCsv_WindowedSlice_TakesOnlyRange()
    {
        CensusAddressRecord[] records =
        [
            new("1", "A St", "X", "IL", "1"),
            new("2", "B St", "X", "IL", "2"),
            new("3", "C St", "X", "IL", "3"),
        ];

        string csv = CensusGeocoderClient.BuildRequestCsv(records, 1, 1);

        Assert.Equal("2,B St,X,IL,2\n", csv);
    }

    // ───────────────────────── response CSV ─────────────────────────

    /// <summary>
    /// Response shaped like the live service's output: quoted fields, a full match
    /// row with the longitude-first coordinate pair, a bare No_Match row, and a
    /// Tie row.
    /// </summary>
    private const string GoldenResponse =
        "\"1\",\"1600 PENNSYLVANIA AVE NW, WASHINGTON, DC, 20500\",\"Match\",\"Exact\","
        + "\"1600 PENNSYLVANIA AVE NW, WASHINGTON, DC, 20502\",\"-77.03535,38.898754\",\"76225813\",\"L\"\r\n"
        + "\"2\",\"123 NOWHERE ST, SPRINGFIELD, XX, 00000\",\"No_Match\"\r\n"
        + "\"3\",\"742 EVERGREEN TER, SPRINGFIELD, IL, 62704\",\"Tie\"\r\n";

    [Fact]
    public void ParseResponseCsv_GoldenResponse_AllVerdictShapes()
    {
        List<CensusGeocodeResult> results = [];
        CensusGeocoderClient.ParseResponseCsv(GoldenResponse, results);

        Assert.Equal(3, results.Count);

        CensusGeocodeResult match = results[0];
        Assert.Equal("1", match.Id);
        Assert.Equal("Match", match.Status);
        Assert.Equal("Exact", match.MatchType);
        Assert.Equal("1600 PENNSYLVANIA AVE NW, WASHINGTON, DC, 20502", match.MatchedAddress);
        // The service's coordinate field is "lon,lat" — assert the split landed
        // the right way around (DC sits at ~38.9°N, -77°E).
        Assert.NotNull(match.Latitude);
        Assert.NotNull(match.Longitude);
        Assert.Equal(38.898754, match.Latitude!.Value, 6);
        Assert.Equal(-77.03535, match.Longitude!.Value, 6);

        CensusGeocodeResult noMatch = results[1];
        Assert.Equal("2", noMatch.Id);
        Assert.Equal("No_Match", noMatch.Status);
        Assert.Null(noMatch.MatchType);
        Assert.Null(noMatch.MatchedAddress);
        Assert.Null(noMatch.Latitude);
        Assert.Null(noMatch.Longitude);

        Assert.Equal("Tie", results[2].Status);
    }

    [Fact]
    public void ParseResponseCsv_BlankLines_AreSkipped()
    {
        List<CensusGeocodeResult> results = [];
        CensusGeocoderClient.ParseResponseCsv("\r\n\"1\",\"A, B, C, 1\",\"No_Match\"\r\n\r\n", results);

        Assert.Single(results);
        Assert.Equal("1", results[0].Id);
    }

    [Fact]
    public void ParseResponseCsv_TooFewFields_Throws()
    {
        List<CensusGeocodeResult> results = [];
        Assert.Throws<RemoteServiceException>(
            () => CensusGeocoderClient.ParseResponseCsv("\"1\",\"only two fields\"\r\n", results));
    }

    [Fact]
    public void ParseResponseCsv_UnparseableCoordinates_Throws()
    {
        string response = "\"1\",\"A, B, C, 1\",\"Match\",\"Exact\",\"A, B, C, 1\",\"garbage\",\"1\",\"L\"\r\n";
        List<CensusGeocodeResult> results = [];
        Assert.Throws<RemoteServiceException>(
            () => CensusGeocoderClient.ParseResponseCsv(response, results));
    }

    [Fact]
    public void ReadCsvRecord_DoubledQuotes_Unescape()
    {
        List<string> fields = [];
        CensusGeocoderClient.ReadCsvRecord("\"a\"\"b\",plain\n", 0, fields);

        Assert.Equal(["a\"b", "plain"], fields);
    }

    // ───────────────────────── transport ─────────────────────────

    /// <summary>
    /// Captures every request body and answers from a caller-supplied script.
    /// Multipart bodies are recorded verbatim (boundaries included) — assertions
    /// use Contains against distinctive payload substrings.
    /// </summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(RequestBodies.Count - 1);
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task GeocodeAsync_SendsBenchmarkAndAddressFile()
    {
        StubHandler handler = new(_ => Ok("\"1\",\"A ST, X, IL, 1\",\"No_Match\"\r\n"));
        using CensusGeocoderClient client = new(handler);

        List<CensusGeocodeResult> results = await client.GeocodeAsync(
            [new CensusAddressRecord("1", "A St", "X", "IL", "1")], "Public_AR_Census2020");

        Assert.Single(results);
        string body = Assert.Single(handler.RequestBodies);
        Assert.Contains("Public_AR_Census2020", body);
        Assert.Contains("1,A St,X,IL,1", body);
        Assert.Contains("addressFile", body);
    }

    [Fact]
    public async Task GeocodeAsync_OverChunkSize_SplitsRequests()
    {
        StubHandler handler = new(_ => Ok(string.Empty));
        using CensusGeocoderClient client = new(handler);

        CensusAddressRecord[] records = new CensusAddressRecord[CensusGeocoderClient.ChunkSize + 1];
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = new CensusAddressRecord($"{i + 1}", $"St {i + 1}", "X", "IL", "1");
        }

        await client.GeocodeAsync(records, CensusGeocoderClient.DefaultBenchmark);

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains($"{CensusGeocoderClient.ChunkSize},St {CensusGeocoderClient.ChunkSize},", handler.RequestBodies[0]);
        Assert.DoesNotContain($"{CensusGeocoderClient.ChunkSize + 1},St", handler.RequestBodies[0]);
        Assert.Contains($"{CensusGeocoderClient.ChunkSize + 1},St {CensusGeocoderClient.ChunkSize + 1},", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task GeocodeAsync_HttpError_ThrowsRemoteService_WithBodySnippet()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<p>down for maintenance</p>"),
        });
        using CensusGeocoderClient client = new(handler, retryDelays: []);

        RemoteServiceException ex = await Assert.ThrowsAsync<RemoteServiceException>(
            () => client.GeocodeAsync(
                [new CensusAddressRecord("1", "A St", "X", "IL", "1")],
                CensusGeocoderClient.DefaultBenchmark));
        Assert.Contains("503", ex.Message);
        Assert.Contains("down for maintenance", ex.Message);
    }

    [Fact]
    public async Task GeocodeAsync_NetworkFailure_ThrowsRemoteService()
    {
        ThrowingHandler handler = new();
        using CensusGeocoderClient client = new(handler, retryDelays: []);

        RemoteServiceException ex = await Assert.ThrowsAsync<RemoteServiceException>(
            () => client.GeocodeAsync(
                [new CensusAddressRecord("1", "A St", "X", "IL", "1")],
                CensusGeocoderClient.DefaultBenchmark));
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task GeocodeAsync_TransientServerError_RetriesAndSucceeds()
    {
        StubHandler handler = new(attempt => attempt == 0
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(string.Empty) }
            : Ok("\"1\",\"A ST, X, IL, 1\",\"No_Match\"\r\n"));
        using CensusGeocoderClient client = new(handler, retryDelays: [TimeSpan.Zero]);

        List<CensusGeocodeResult> results = await client.GeocodeAsync(
            [new CensusAddressRecord("1", "A St", "X", "IL", "1")],
            CensusGeocoderClient.DefaultBenchmark);

        Assert.Single(results);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task GeocodeAsync_RetriesExhausted_MentionsAttempts()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(string.Empty),
        });
        using CensusGeocoderClient client = new(handler, retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        RemoteServiceException ex = await Assert.ThrowsAsync<RemoteServiceException>(
            () => client.GeocodeAsync(
                [new CensusAddressRecord("1", "A St", "X", "IL", "1")],
                CensusGeocoderClient.DefaultBenchmark));
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("Giving up after 3 attempts", ex.Message);
    }

    [Fact]
    public async Task GeocodeAsync_ClientError_DoesNotRetry()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<p>Malformed input file</p>"),
        });
        using CensusGeocoderClient client = new(handler, retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        RemoteServiceException ex = await Assert.ThrowsAsync<RemoteServiceException>(
            () => client.GeocodeAsync(
                [new CensusAddressRecord("1", "A St", "X", "IL", "1")],
                CensusGeocoderClient.DefaultBenchmark));
        Assert.Single(handler.RequestBodies);
        Assert.Contains("Malformed input file", ex.Message);
        Assert.DoesNotContain("Giving up", ex.Message);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated DNS failure");
    }
}
