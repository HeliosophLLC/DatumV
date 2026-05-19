namespace Heliosoph.DatumV.Inference.OnnxRuntime;

/// <summary>
/// Top-level metadata fields read directly from the ONNX protobuf file —
/// the bits ORT doesn't expose through <c>InferenceSession.ModelMetadata</c>
/// (ir_version, opset_import, full operator-type set) that the
/// introspection surface needs to answer "is this model new enough to load
/// here?" and "what does it actually do?" without forcing a session load.
/// </summary>
/// <param name="IrVersion">
/// The ONNX <c>ir_version</c> field. The ONNX IR version this file was
/// authored against — see https://github.com/onnx/onnx/blob/main/docs/IR.md
/// for the version-to-feature mapping. Roughly: IR 9 ⇄ ai.onnx opset 20,
/// IR 10 ⇄ opset 21, IR 11 ⇄ opset 22.
/// </param>
/// <param name="ProducerName">
/// Free-form producer-tool identifier (e.g. <c>"pytorch"</c>,
/// <c>"transformers.onnx"</c>, <c>"optimum"</c>). Empty when unset.
/// </param>
/// <param name="ProducerVersion">
/// Free-form producer-tool version string (e.g. <c>"2.5.1"</c>). Empty when
/// unset.
/// </param>
/// <param name="OpsetVersion">
/// Version of the default <c>ai.onnx</c> opset this file imports. Multiple
/// opsets can be declared (custom domains like <c>com.microsoft</c>); this
/// is the one for the empty / "ai.onnx" domain that nearly every model
/// uses. <c>-1</c> when no default opset was declared (malformed file).
/// </param>
/// <param name="RequiredOps">
/// Distinct operator types referenced by the model's graph (e.g.
/// <c>"Conv"</c>, <c>"MatMul"</c>, <c>"Reshape"</c>). Drawn from
/// <c>graph.node[].op_type</c>; sorted alphabetically for stable output.
/// </param>
public sealed record OnnxFileMetadata(
    long IrVersion,
    string ProducerName,
    string ProducerVersion,
    int OpsetVersion,
    IReadOnlyList<string> RequiredOps);

/// <summary>
/// Reads a minimal subset of an ONNX file's protobuf header. Only walks the
/// top-level <c>ModelProto</c> fields plus a one-level recursion into
/// <c>graph.node[].op_type</c>. Everything else is skipped via the standard
/// protobuf-wire skipping rules.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than pulled from a generated ONNX proto because:
/// (1) we only need ~5 fields out of the ~30 in ModelProto;
/// (2) adding the full ONNX proto + Google.Protobuf as a runtime dependency
///     for this is a heavy bring-in for the marginal value;
/// (3) the wire format is stable — protobuf field tags can only ever be
///     added, never reused.
/// </para>
/// <para>
/// Operates on a memory-mapped or read stream — does NOT load weights.
/// The weights live in the same protobuf as raw bytes; we skip them with
/// the length-delimited skip path, so reading is O(graph-shape), not
/// O(model-size). A 7B-parameter file inspects in milliseconds.
/// </para>
/// </remarks>
internal static class OnnxFileMetadataReader
{
    // ModelProto field numbers, from onnx.proto
    private const int FieldIrVersion = 1;
    private const int FieldProducerName = 2;
    private const int FieldProducerVersion = 3;
    private const int FieldGraph = 7;
    private const int FieldOpsetImport = 8;

    // OperatorSetIdProto field numbers
    private const int FieldOpsetDomain = 1;
    private const int FieldOpsetVersion = 2;

    // GraphProto field numbers
    private const int FieldGraphNode = 1;

    // NodeProto field numbers
    private const int FieldNodeOpType = 4;

    // Wire types
    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireFixed32 = 5;

    public static OnnxFileMetadata Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return ReadInternal(bytes);
    }

    private static OnnxFileMetadata ReadInternal(ReadOnlySpan<byte> bytes)
    {
        long irVersion = 0;
        string producerName = "";
        string producerVersion = "";
        int opsetVersion = -1;
        SortedSet<string> requiredOps = new(StringComparer.Ordinal);

        int pos = 0;
        while (pos < bytes.Length)
        {
            (int field, int wire, int after) = ReadTag(bytes, pos);
            pos = after;

            switch ((field, wire))
            {
                case (FieldIrVersion, WireVarint):
                    (long v, pos) = ReadVarintLong(bytes, pos);
                    irVersion = v;
                    break;
                case (FieldProducerName, WireLengthDelimited):
                    (string pn, pos) = ReadString(bytes, pos);
                    producerName = pn;
                    break;
                case (FieldProducerVersion, WireLengthDelimited):
                    (string pv, pos) = ReadString(bytes, pos);
                    producerVersion = pv;
                    break;
                case (FieldOpsetImport, WireLengthDelimited):
                    (int? opv, pos) = ReadOpsetImport(bytes, pos);
                    if (opv is int v2) opsetVersion = v2;
                    break;
                case (FieldGraph, WireLengthDelimited):
                    pos = ReadGraph(bytes, pos, requiredOps);
                    break;
                default:
                    pos = SkipField(bytes, pos, wire);
                    break;
            }
        }

        return new OnnxFileMetadata(
            IrVersion: irVersion,
            ProducerName: producerName,
            ProducerVersion: producerVersion,
            OpsetVersion: opsetVersion,
            RequiredOps: requiredOps.ToList());
    }

    /// <summary>
    /// OperatorSetIdProto: domain + version. We only care about the default
    /// (empty) "ai.onnx" domain — custom domain opsets (com.microsoft, etc.)
    /// are intentionally ignored.
    /// </summary>
    private static (int? Version, int NewPos) ReadOpsetImport(ReadOnlySpan<byte> bytes, int pos)
    {
        (int len, int afterLen) = ReadLength(bytes, pos);
        int end = afterLen + len;
        string domain = "";
        long version = -1;
        int p = afterLen;
        while (p < end)
        {
            (int field, int wire, int after) = ReadTag(bytes, p);
            p = after;
            switch ((field, wire))
            {
                case (FieldOpsetDomain, WireLengthDelimited):
                    (domain, p) = ReadString(bytes, p);
                    break;
                case (FieldOpsetVersion, WireVarint):
                    (version, p) = ReadVarintLong(bytes, p);
                    break;
                default:
                    p = SkipField(bytes, p, wire);
                    break;
            }
        }
        return (domain.Length == 0 || domain == "ai.onnx" ? (int)version : null, end);
    }

    /// <summary>
    /// GraphProto contains the operator nodes among many other things; we
    /// walk only NodeProto entries to harvest op_type. Initializer tensors
    /// (the weight blobs) are sibling fields that we cleanly skip.
    /// </summary>
    private static int ReadGraph(ReadOnlySpan<byte> bytes, int pos, SortedSet<string> ops)
    {
        (int len, int afterLen) = ReadLength(bytes, pos);
        int end = afterLen + len;
        int p = afterLen;
        while (p < end)
        {
            (int field, int wire, int after) = ReadTag(bytes, p);
            p = after;
            if (field == FieldGraphNode && wire == WireLengthDelimited)
            {
                p = ReadNodeForOpType(bytes, p, ops);
            }
            else
            {
                p = SkipField(bytes, p, wire);
            }
        }
        return end;
    }

    private static int ReadNodeForOpType(ReadOnlySpan<byte> bytes, int pos, SortedSet<string> ops)
    {
        (int len, int afterLen) = ReadLength(bytes, pos);
        int end = afterLen + len;
        int p = afterLen;
        while (p < end)
        {
            (int field, int wire, int after) = ReadTag(bytes, p);
            p = after;
            if (field == FieldNodeOpType && wire == WireLengthDelimited)
            {
                (string op, p) = ReadString(bytes, p);
                if (op.Length > 0) ops.Add(op);
            }
            else
            {
                p = SkipField(bytes, p, wire);
            }
        }
        return end;
    }

    // ─── Protobuf wire-format primitives ──────────────────────────────────────

    private static (int Field, int Wire, int NewPos) ReadTag(ReadOnlySpan<byte> bytes, int pos)
    {
        (ulong tag, int next) = ReadVarintUnsigned(bytes, pos);
        return ((int)(tag >> 3), (int)(tag & 0x7), next);
    }

    private static (ulong Value, int NewPos) ReadVarintUnsigned(ReadOnlySpan<byte> bytes, int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= bytes.Length)
            {
                throw new InvalidDataException("Truncated ONNX file: varint extends past EOF.");
            }
            byte b = bytes[pos++];
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) return (result, pos);
            shift += 7;
            if (shift >= 64)
            {
                throw new InvalidDataException("Malformed ONNX file: varint exceeds 64 bits.");
            }
        }
    }

    private static (long Value, int NewPos) ReadVarintLong(ReadOnlySpan<byte> bytes, int pos)
    {
        (ulong u, int p) = ReadVarintUnsigned(bytes, pos);
        return ((long)u, p);
    }

    private static (int Length, int NewPos) ReadLength(ReadOnlySpan<byte> bytes, int pos)
    {
        (ulong u, int p) = ReadVarintUnsigned(bytes, pos);
        if (u > int.MaxValue)
        {
            throw new InvalidDataException("Length-delimited field exceeds Int32.MaxValue.");
        }
        return ((int)u, p);
    }

    private static (string Value, int NewPos) ReadString(ReadOnlySpan<byte> bytes, int pos)
    {
        (int len, int after) = ReadLength(bytes, pos);
        if (after + len > bytes.Length)
        {
            throw new InvalidDataException("Truncated ONNX file: string field extends past EOF.");
        }
        string s = System.Text.Encoding.UTF8.GetString(bytes.Slice(after, len));
        return (s, after + len);
    }

    private static int SkipField(ReadOnlySpan<byte> bytes, int pos, int wire)
    {
        switch (wire)
        {
            case WireVarint:
                (_, pos) = ReadVarintUnsigned(bytes, pos);
                return pos;
            case WireFixed64:
                return pos + 8;
            case WireLengthDelimited:
                (int len, int after) = ReadLength(bytes, pos);
                return after + len;
            case WireFixed32:
                return pos + 4;
            default:
                throw new InvalidDataException($"Unknown protobuf wire type {wire} at offset {pos}.");
        }
    }
}
