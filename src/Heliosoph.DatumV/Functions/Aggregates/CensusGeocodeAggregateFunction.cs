using System.Globalization;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Geocoding;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>census_geocode_agg(id, street, city, state, zip [, options STRUCT]) →
/// Array&lt;Struct{id, status, match_type, matched_address, lat, lon}&gt;</c> —
/// batch-geocodes the group's US street addresses through the US Census Bureau
/// geocoder. One result element per input row, keyed by the caller-supplied
/// unique <c>id</c>; unnest the array and join back on <c>id</c> to attach
/// coordinates to the source rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>External service.</strong> Every accumulated address is sent to
/// <c>geocoding.geo.census.gov</c> (free, keyless, US-only) when the group
/// finalizes. Match quality is returned as data: <c>status</c> is
/// <c>Match</c> / <c>No_Match</c> / <c>Tie</c>, and <c>lat</c> / <c>lon</c> /
/// <c>match_type</c> / <c>matched_address</c> are NULL for non-matches. Geocode
/// once into a table (CTAS) rather than re-querying — the service is rate-limited
/// and results don't change.
/// </para>
/// <para>
/// <strong>Arguments.</strong> <c>id</c> is any integer (returned as Int64) or a
/// string, must be non-NULL and unique within the group. The four address parts
/// are strings; NULLs are sent as empty fields (the service matches on whatever
/// is present — street plus either city+state or zip generally suffices). The
/// optional options struct supports <c>benchmark</c> (string, default
/// <c>Public_AR_Current</c>), captured from the first row.
/// </para>
/// <para>
/// <strong>Memory.</strong> The accumulator holds the group's address strings in
/// managed memory; the only arena writes are the final result values. Merge
/// concatenates record lists, so parallel hash-aggregate merging is supported.
/// Requests are chunked (the service processes ~10 records/second) and transient
/// server-side failures are retried with backoff. Groups with zero rows return
/// NULL.
/// </para>
/// </remarks>
public sealed class CensusGeocodeAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "census_geocode_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Batch-geocodes the group's US street addresses via the US Census Bureau geocoder "
        + "(sends addresses to geocoding.geo.census.gov): census_geocode_agg(id, street, city, state, zip "
        + "[, {benchmark}]) → Array<Struct{id, status, match_type, matched_address, lat, lon}>. "
        + "Unnest the result and join back on id.";

    private static readonly ParameterSpec[] AddressParameters =
    [
        new ParameterSpec("street", DataKindMatcher.Exact(DataKind.String)),
        new ParameterSpec("city", DataKindMatcher.Exact(DataKind.String)),
        new ParameterSpec("state", DataKindMatcher.Exact(DataKind.String)),
        new ParameterSpec("zip", DataKindMatcher.Exact(DataKind.String)),
    ];

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } = BuildSignatures();

    private static FunctionSignatureVariant[] BuildSignatures()
    {
        ParameterSpec intId = new("id", DataKindMatcher.Family(DataKindFamily.IntegerFamily));
        ParameterSpec stringId = new("id", DataKindMatcher.Exact(DataKind.String));
        ParameterSpec options = new("options", DataKindMatcher.Exact(DataKind.Struct));
        ReturnTypeRule returnRule = ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct));

        return
        [
            new FunctionSignatureVariant(
                Parameters: [intId, .. AddressParameters],
                VariadicTrailing: null,
                ReturnType: returnRule),
            new FunctionSignatureVariant(
                Parameters: [intId, .. AddressParameters, options],
                VariadicTrailing: null,
                ReturnType: returnRule),
            new FunctionSignatureVariant(
                Parameters: [stringId, .. AddressParameters],
                VariadicTrailing: null,
                ReturnType: returnRule),
            new FunctionSignatureVariant(
                Parameters: [stringId, .. AddressParameters, options],
                VariadicTrailing: null,
                ReturnType: returnRule),
        ];
    }

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (5 or 6))
        {
            throw new ArgumentException(
                "census_geocode_agg() requires five or six arguments: "
                + "id, street, city, state, zip, and an optional options struct.");
        }
        if (argumentKinds[0] != DataKind.String && !DataKindFamily.IntegerFamily.Contains(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"census_geocode_agg() first argument (id) must be an integer or STRING, got {argumentKinds[0]}.");
        }
        for (int i = 1; i <= 4; i++)
        {
            if (argumentKinds[i] != DataKind.String)
            {
                throw new ArgumentException(
                    $"census_geocode_agg() argument {i} ({AddressParameters[i - 1].Name}) must be STRING, "
                    + $"got {argumentKinds[i]}.");
            }
        }
        if (argumentKinds.Length == 6 && argumentKinds[5] != DataKind.Struct)
        {
            throw new ArgumentException(
                $"census_geocode_agg() sixth argument (options) must be a struct, got {argumentKinds[5]}.");
        }
        return DataKind.Struct;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } =
        ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct));

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Collects the group's address rows as managed strings (arena-backed inputs
    /// are materialized at accumulate time — the source arena recycles across
    /// batches); the geocoder round-trip runs once at
    /// <see cref="IAggregateAccumulator.ResultAsync"/>.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<CensusAddressRecord> _records = [];
        private readonly HashSet<string> _ids = [];
        private bool _idIsString;
        private bool _idKindCaptured;
        private string _benchmark = CensusGeocoderClient.DefaultBenchmark;
        private bool _optionsCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments.Length == 6 && !_optionsCaptured && !arguments[5].IsNull)
            {
                CaptureOptions(arguments[5], frame);
            }

            if (arguments[0].IsNull)
            {
                throw new FunctionArgumentException(Name,
                    "id must not be NULL — it keys the geocoder's response back to the input rows.");
            }

            string id;
            if (arguments[0].Kind == DataKind.String)
            {
                id = arguments[0].AsString(frame.Source, frame.SidecarRegistry);
                _idIsString = true;
            }
            else if (arguments[0].TryToInt64(out long idValue))
            {
                id = idValue.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                throw new FunctionArgumentException(Name,
                    $"could not read an id from a {arguments[0].Kind} value.");
            }
            _idKindCaptured = true;

            if (!_ids.Add(id))
            {
                throw new FunctionArgumentException(Name,
                    $"duplicate id '{id}' — ids must be unique within the group so results "
                    + "can be joined back to input rows.");
            }

            _records.Add(new CensusAddressRecord(
                id,
                ReadAddressPart(arguments[1], frame),
                ReadAddressPart(arguments[2], frame),
                ReadAddressPart(arguments[3], frame),
                ReadAddressPart(arguments[4], frame)));
        }

        private static string ReadAddressPart(in DataValue value, in InvocationFrame frame) =>
            value.IsNull ? string.Empty : value.AsString(frame.Source, frame.SidecarRegistry);

        private void CaptureOptions(in DataValue optionsArg, in InvocationFrame frame)
        {
            _optionsCaptured = true;

            TypeDescriptor? descriptor = frame.Types?.GetDescriptor(optionsArg.TypeId);
            if (descriptor?.Fields is null)
            {
                throw new FunctionArgumentException(Name,
                    "options struct has no registered field descriptors; expected a struct literal "
                    + "with any of: benchmark.");
            }

            DataValue[] fields = optionsArg.AsStruct(frame.Source);
            for (int i = 0; i < descriptor.Fields.Count && i < fields.Length; i++)
            {
                if (fields[i].IsNull) continue;

                switch (descriptor.Fields[i].Name.ToLowerInvariant())
                {
                    case "benchmark":
                        if (fields[i].Kind != DataKind.String)
                        {
                            throw new FunctionArgumentException(Name,
                                $"benchmark option must be a string, got {fields[i].Kind}.");
                        }
                        _benchmark = fields[i].AsString(frame.Source, frame.SidecarRegistry);
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            if (o._optionsCaptured && !_optionsCaptured)
            {
                _benchmark = o._benchmark;
                _optionsCaptured = true;
            }
            if (o._idKindCaptured && !_idKindCaptured)
            {
                _idIsString = o._idIsString;
                _idKindCaptured = true;
            }
            foreach (CensusAddressRecord record in o._records)
            {
                if (!_ids.Add(record.Id))
                {
                    throw new FunctionArgumentException(Name,
                        $"duplicate id '{record.Id}' — ids must be unique within the group so results "
                        + "can be joined back to input rows.");
                }
                _records.Add(record);
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_records.Count == 0)
            {
                return DataValue.NullArrayOf(DataKind.Struct);
            }

            List<CensusGeocodeResult> verdicts =
                await CensusGeocoderClient.Instance.GeocodeAsync(_records, _benchmark).ConfigureAwait(false);

            Dictionary<string, CensusGeocodeResult> byId = new(verdicts.Count, StringComparer.Ordinal);
            foreach (CensusGeocodeResult verdict in verdicts)
            {
                byId.TryAdd(verdict.Id, verdict);
            }

            ushort elementTypeId = TypeRegistry.NoType;
            if (frame.Types is { } types)
            {
                int idTypeId = _idIsString
                    ? types.InternScalarType(DataKind.String)
                    : types.InternScalarType(DataKind.Int64);
                int stringTypeId = types.InternScalarType(DataKind.String);
                int float64TypeId = types.InternScalarType(DataKind.Float64);
                elementTypeId = (ushort)types.InternStructType(
                [
                    new StructFieldDescriptor("id", idTypeId),
                    new StructFieldDescriptor("status", stringTypeId),
                    new StructFieldDescriptor("match_type", stringTypeId),
                    new StructFieldDescriptor("matched_address", stringTypeId),
                    new StructFieldDescriptor("lat", float64TypeId),
                    new StructFieldDescriptor("lon", float64TypeId),
                ]);
            }

            DataValue[][] rows = new DataValue[_records.Count][];
            for (int i = 0; i < _records.Count; i++)
            {
                string id = _records[i].Id;
                if (!byId.TryGetValue(id, out CensusGeocodeResult? verdict))
                {
                    throw new RemoteServiceException(
                        $"The US Census geocoder response did not include record id '{id}'. "
                        + "The service may have rejected the batch; try again.");
                }

                rows[i] =
                [
                    _idIsString
                        ? DataValue.FromString(id, frame.Target)
                        : DataValue.FromInt64(long.Parse(id, CultureInfo.InvariantCulture)),
                    DataValue.FromString(verdict.Status, frame.Target),
                    verdict.MatchType is { } matchType
                        ? DataValue.FromString(matchType, frame.Target)
                        : DataValue.Null(DataKind.String),
                    verdict.MatchedAddress is { } matched
                        ? DataValue.FromString(matched, frame.Target)
                        : DataValue.Null(DataKind.String),
                    verdict.Latitude is { } lat
                        ? DataValue.FromFloat64(lat)
                        : DataValue.Null(DataKind.Float64),
                    verdict.Longitude is { } lon
                        ? DataValue.FromFloat64(lon)
                        : DataValue.Null(DataKind.Float64),
                ];
            }

            return DataValue.FromStructArray(rows, frame.Target, elementTypeId);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _records.Clear();
            _ids.Clear();
            _idIsString = false;
            _idKindCaptured = false;
            _benchmark = CensusGeocoderClient.DefaultBenchmark;
            _optionsCaptured = false;
        }
    }
}
