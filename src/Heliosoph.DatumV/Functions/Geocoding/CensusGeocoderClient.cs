using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Functions.Geocoding;

/// <summary>One input record for the US Census batch geocoder: a caller-chosen
/// unique id plus the street / city / state / zip fields the service matches on.</summary>
public readonly record struct CensusAddressRecord(
    string Id, string Street, string City, string State, string Zip);

/// <summary>
/// One geocoder verdict. <see cref="Status"/> is the service's match status
/// (<c>Match</c> / <c>No_Match</c> / <c>Tie</c>); the remaining fields are populated
/// only for matches. Coordinates are WGS-84 decimal degrees.
/// </summary>
public sealed record CensusGeocodeResult(
    string Id,
    string Status,
    string? MatchType,
    string? MatchedAddress,
    double? Latitude,
    double? Longitude);

/// <summary>
/// Client for the US Census Bureau batch geocoder
/// (<c>geocoding.geo.census.gov/geocoder/locations/addressbatch</c>): free, keyless,
/// US-only. Submissions are chunked well under the service's 10,000-record batch
/// limit; each chunk is one multipart POST whose response is a headerless CSV of
/// verdicts.
/// </summary>
/// <remarks>
/// Addresses in the submitted batch leave the machine — callers surface that fact
/// to users (the SQL function name and docs carry it). The service's gateway
/// intermittently answers 5xx under load, so server-side failures are retried
/// with backoff before surfacing; exhausted retries, client errors (4xx), and
/// transport timeouts throw <see cref="RemoteServiceException"/>. Per-record
/// match failures are data (<c>No_Match</c> / <c>Tie</c> verdicts), not errors.
/// </remarks>
public class CensusGeocoderClient : IDisposable
{
    /// <summary>The service's documented per-request record limit.</summary>
    public const int MaxBatchSize = 10_000;

    /// <summary>
    /// Records per request. Deliberately far below <see cref="MaxBatchSize"/>:
    /// the service processes roughly 10 records/second, so full 10k batches run
    /// long past any reasonable timeout while 2k chunks finish in a few minutes.
    /// </summary>
    public const int ChunkSize = 2_000;

    /// <summary>Benchmark used when the caller doesn't specify one.</summary>
    public const string DefaultBenchmark = "Public_AR_Current";

    private const string EndpointUrl =
        "https://geocoding.geo.census.gov/geocoder/locations/addressbatch";

    // Sized for the observed ~10 records/second service throughput on ChunkSize
    // rows plus generous slack; keeps a hung connection from stalling a query
    // forever (the aggregate interface carries no CancellationToken to do it
    // sooner).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    // Backoff before the second and third attempt at a chunk. 5xx from the
    // gateway is typically a transient overload; a short pause is usually enough.
    private static readonly TimeSpan[] DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private static CensusGeocoderClient? _instance;

    /// <summary>
    /// Process-wide client used by SQL functions. Settable so tests can substitute
    /// a stubbed transport; production code never replaces it.
    /// </summary>
    public static CensusGeocoderClient Instance
    {
        get => _instance ??= new CensusGeocoderClient();
        set => _instance = value;
    }

    private readonly HttpClient _http;
    private readonly TimeSpan[] _retryDelays;

    /// <summary>Creates a client against the live service.</summary>
    public CensusGeocoderClient()
        : this(handler: null) { }

    /// <summary>Creates a client with a custom transport (test stubs).</summary>
    public CensusGeocoderClient(HttpMessageHandler? handler)
        : this(handler, DefaultRetryDelays) { }

    /// <summary>Test seam: custom transport plus custom (e.g. zero) retry backoff.</summary>
    internal CensusGeocoderClient(HttpMessageHandler? handler, TimeSpan[] retryDelays)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = RequestTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Heliosoph-DatumV/1.0");
        _retryDelays = retryDelays;
    }

    /// <summary>
    /// Geocodes <paramref name="records"/> in submission order, transparently
    /// splitting into <see cref="ChunkSize"/>-record chunks. Returns one verdict
    /// per submitted record; order follows the service's response order, so
    /// callers correlate by <see cref="CensusGeocodeResult.Id"/>, not position.
    /// </summary>
    public async Task<List<CensusGeocodeResult>> GeocodeAsync(
        IReadOnlyList<CensusAddressRecord> records, string benchmark)
    {
        List<CensusGeocodeResult> all = new(records.Count);
        for (int start = 0; start < records.Count; start += ChunkSize)
        {
            int count = Math.Min(ChunkSize, records.Count - start);
            string requestCsv = BuildRequestCsv(records, start, count);
            string responseCsv = await PostBatchWithRetryAsync(requestCsv, benchmark).ConfigureAwait(false);
            ParseResponseCsv(responseCsv, all);
        }
        return all;
    }

    /// <summary>
    /// Posts one chunk, retrying 5xx statuses and transport failures with backoff.
    /// 4xx statuses fail immediately — the request itself is wrong and a retry
    /// can't heal it. Timeouts also fail immediately: after
    /// <see cref="RequestTimeout"/> of silence the service is overloaded and
    /// another full-length wait would only stall the query further.
    /// </summary>
    private async Task<string> PostBatchWithRetryAsync(string requestCsv, string benchmark)
    {
        RemoteServiceException? lastFailure = null;
        for (int attempt = 0; attempt <= _retryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_retryDelays[attempt - 1]).ConfigureAwait(false);
            }
            try
            {
                return await PostBatchAsync(requestCsv, benchmark).ConfigureAwait(false);
            }
            catch (RemoteServiceException ex) when (ex.IsRetryable)
            {
                lastFailure = ex;
            }
        }
        throw new RemoteServiceException(
            $"{lastFailure!.Message} Giving up after {_retryDelays.Length + 1} attempts.",
            lastFailure.InnerException);
    }

    private async Task<string> PostBatchAsync(string requestCsv, string benchmark)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(benchmark), "benchmark");
        ByteArrayContent file = new(Encoding.UTF8.GetBytes(requestCsv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "addressFile", "addresses.csv");

        try
        {
            using HttpResponseMessage response =
                await _http.PostAsync(EndpointUrl, form).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorSnippetAsync(response).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                throw new RemoteServiceException(
                    $"The US Census geocoder returned HTTP {status} ({response.ReasonPhrase}).{detail}",
                    innerException: null,
                    isRetryable: status >= 500);
            }
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteServiceException(
                "Could not reach the US Census geocoder (geocoding.geo.census.gov). "
                + "Check the machine's internet connection and try again.", ex,
                isRetryable: true);
        }
        catch (TaskCanceledException ex)
        {
            throw new RemoteServiceException(
                $"The US Census geocoder did not respond within {RequestTimeout.TotalMinutes:0} minutes. "
                + "The service may be overloaded; try again later or submit fewer rows.", ex);
        }
    }

    /// <summary>
    /// Pulls a short plain-text snippet out of an error response body — the
    /// service explains failures in HTML (e.g. what file shapes it accepts), and
    /// losing that text turns a diagnosable error into a dead end.
    /// </summary>
    private static async Task<string> ReadErrorSnippetAsync(HttpResponseMessage response)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        StringBuilder text = new(Math.Min(body.Length, 240));
        bool inTag = false;
        bool lastWasSpace = true;
        foreach (char c in body)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (inTag) continue;
            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastWasSpace) continue;
            text.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
            if (text.Length >= 200) break;
        }
        string snippet = text.ToString().Trim();
        return snippet.Length == 0 ? string.Empty : $" Service response: {snippet}";
    }

    /// <summary>
    /// Builds the headerless request CSV the batch endpoint expects: one
    /// <c>id,street,city,state,zip</c> line per record. Fields are CSV-quoted when
    /// they contain commas or quotes; embedded line breaks are flattened to spaces
    /// (a record must stay on one line).
    /// </summary>
    internal static string BuildRequestCsv(
        IReadOnlyList<CensusAddressRecord> records, int start, int count)
    {
        StringBuilder sb = new(count * 64);
        for (int i = start; i < start + count; i++)
        {
            CensusAddressRecord r = records[i];
            AppendField(sb, r.Id);
            sb.Append(',');
            AppendField(sb, r.Street);
            sb.Append(',');
            AppendField(sb, r.City);
            sb.Append(',');
            AppendField(sb, r.State);
            sb.Append(',');
            AppendField(sb, r.Zip);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string value)
    {
        string flat = value.IndexOfAny(['\r', '\n']) >= 0
            ? value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            : value;
        if (flat.Contains(',') || flat.Contains('"'))
        {
            sb.Append('"').Append(flat.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            sb.Append(flat);
        }
    }

    /// <summary>
    /// Parses the service's headerless response CSV into verdicts. Row layout:
    /// <c>id, input address, status</c> for non-matches, extended with
    /// <c>match type, matched address, "lon,lat", tiger line id, side</c> for
    /// matches. The coordinate field is longitude-first — the opposite of the
    /// (lat, lon) convention — and is split here so callers never see it raw.
    /// </summary>
    internal static void ParseResponseCsv(string csv, List<CensusGeocodeResult> results)
    {
        int position = 0;
        List<string> fields = [];
        while (position < csv.Length)
        {
            fields.Clear();
            position = ReadCsvRecord(csv, position, fields);
            if (fields.Count == 0 || (fields.Count == 1 && fields[0].Length == 0))
            {
                continue; // blank line
            }
            if (fields.Count < 3)
            {
                throw new RemoteServiceException(
                    "The US Census geocoder returned a malformed response row "
                    + $"({fields.Count} fields; expected at least id, address, status).");
            }

            string id = fields[0];
            string status = fields[2];
            string? matchType = fields.Count > 3 && fields[3].Length > 0 ? fields[3] : null;
            string? matchedAddress = fields.Count > 4 && fields[4].Length > 0 ? fields[4] : null;
            double? lat = null;
            double? lon = null;
            if (fields.Count > 5 && fields[5].Length > 0)
            {
                (lon, lat) = ParseCoordinates(fields[5]);
            }
            results.Add(new CensusGeocodeResult(id, status, matchType, matchedAddress, lat, lon));
        }
    }

    private static (double Lon, double Lat) ParseCoordinates(string field)
    {
        int comma = field.IndexOf(',');
        if (comma <= 0
            || !double.TryParse(field.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
            || !double.TryParse(field.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
        {
            throw new RemoteServiceException(
                $"The US Census geocoder returned an unparseable coordinate field '{field}' "
                + "(expected \"longitude,latitude\").");
        }
        return (lon, lat);
    }

    /// <summary>
    /// Reads one CSV record starting at <paramref name="position"/> into
    /// <paramref name="fields"/>, honouring double-quoted fields with doubled-quote
    /// escapes (commas and line breaks inside quotes stay literal). Returns the
    /// position just past the record's terminating newline (or end of input).
    /// </summary>
    internal static int ReadCsvRecord(string csv, int position, List<string> fields)
    {
        StringBuilder field = new();
        bool inQuotes = false;
        while (position < csv.Length)
        {
            char c = csv[position];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (position + 1 < csv.Length && csv[position + 1] == '"')
                    {
                        field.Append('"');
                        position += 2;
                        continue;
                    }
                    inQuotes = false;
                    position++;
                    continue;
                }
                field.Append(c);
                position++;
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    position++;
                    continue;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    position++;
                    continue;
                case '\r':
                    position++;
                    if (position < csv.Length && csv[position] == '\n') position++;
                    fields.Add(field.ToString());
                    return position;
                case '\n':
                    fields.Add(field.ToString());
                    return position + 1;
                default:
                    field.Append(c);
                    position++;
                    continue;
            }
        }
        fields.Add(field.ToString());
        return position;
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
