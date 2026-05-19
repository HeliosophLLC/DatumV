using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: sidecar-inspect <path>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  <path>  A .datum-blob sidecar file, OR a .datum file");
    Console.Error.WriteLine("          (the tool will resolve the companion sidecar).");
    return 1;
}

string requestedPath = args[0];
string sidecarPath = ResolveSidecarPath(requestedPath);

if (!File.Exists(sidecarPath))
{
    Console.Error.WriteLine($"Sidecar file not found: {sidecarPath}");
    return 1;
}

long sidecarSize = new FileInfo(sidecarPath).Length;
Console.WriteLine($"Sidecar:     {sidecarPath}");
Console.WriteLine($"Size:        {sidecarSize:N0} bytes ({FormatBytes(sidecarSize)})");
Console.WriteLine();

// Read the 32-byte header.
byte[] headerBuffer = new byte[SidecarConstants.HeaderSize];
using (FileStream sidecarStream = new(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    if (sidecarSize < SidecarConstants.HeaderSize)
    {
        Console.Error.WriteLine(
            $"Sidecar is shorter than the {SidecarConstants.HeaderSize}-byte header " +
            $"({sidecarSize} bytes). File is truncated or not a .datum-blob.");
        return 1;
    }
    sidecarStream.ReadExactly(headerBuffer);
}

ReadOnlySpan<byte> header = headerBuffer;
ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(header[0..8]);
uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
ulong storedHash = BinaryPrimitives.ReadUInt64LittleEndian(
    header.Slice(SidecarConstants.PayloadHashOffset, 8));

bool magicOk = magic == SidecarConstants.Magic;
bool versionOk = version == SidecarConstants.Version;

Console.WriteLine("Header:");
Console.WriteLine($"  Magic:       0x{magic:X16} {(magicOk ? "✓ DATUMBLB" : $"✗ expected 0x{SidecarConstants.Magic:X16}")}");
Console.WriteLine($"  Version:     {version} {(versionOk ? "✓" : $"✗ reader supports v{SidecarConstants.Version}")}");
Console.WriteLine($"  Fingerprint: 0x{fingerprint:X16}");
Console.WriteLine($"  Header size: {SidecarConstants.HeaderSize} bytes");
Console.WriteLine();

long payloadBytes = sidecarSize - SidecarConstants.HeaderSize;
Console.WriteLine("Payload:");
Console.WriteLine($"  Bytes:       {payloadBytes:N0} ({FormatBytes(payloadBytes)})");

if (storedHash == 0)
{
    Console.WriteLine($"  Hash:        0x{storedHash:X16} (legacy file — payload not hashed)");
}
else
{
    ulong actualHash = HashSidecarPayload(sidecarPath, payloadBytes);
    bool hashOk = actualHash == storedHash;
    Console.WriteLine($"  Hash:        0x{storedHash:X16} {(hashOk ? "✓ xxHash3 matches" : $"✗ payload corrupted (actual 0x{actualHash:X16})")}");
}
Console.WriteLine();

// Cross-check companion .datum file if it exists.
string datumPath = Path.ChangeExtension(sidecarPath, ".datum");
if (!File.Exists(datumPath))
{
    Console.WriteLine("Companion:");
    Console.WriteLine($"  Path:        {datumPath}");
    Console.WriteLine($"  Status:      not found — orphan sidecar (companion .datum is missing)");
    return magicOk && versionOk ? 0 : 2;
}

(bool hasSidecarFlag, string datumDiagnostic) = ReadDatumSidecarLink(datumPath);

Console.WriteLine("Companion:");
Console.WriteLine($"  Path:        {datumPath}");
Console.WriteLine($"  HasSidecarReferences flag: {(hasSidecarFlag ? "✓ set" : "✗ not set")}");

if (datumDiagnostic.Length > 0)
{
    Console.WriteLine($"  Note:        {datumDiagnostic}");
}

return magicOk && versionOk ? 0 : 2;

static string ResolveSidecarPath(string input)
{
    // Accept either a .datum or a .datum-blob path; always operate on the .datum-blob.
    if (input.EndsWith(SidecarConstants.FileExtension, StringComparison.OrdinalIgnoreCase))
    {
        return input;
    }
    return Path.ChangeExtension(input, SidecarConstants.FileExtension);
}

static (bool HasSidecarFlag, string Diagnostic) ReadDatumSidecarLink(string datumPath)
{
    using FileStream stream = new(datumPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    long length = stream.Length;

    if (length < DatumFormatV2.HeaderSize + DatumFormatV2.TailSize)
    {
        return (false, "companion .datum file is truncated");
    }

    // Header flags live at offset 6 (magic[4] + version[2] + flags[2] + ...).
    Span<byte> headerBuffer = stackalloc byte[DatumFormatV2.HeaderSize];
    stream.ReadExactly(headerBuffer);
    ushort flagsRaw = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[6..8]);
    DatumFileFlagsV2 flags = (DatumFileFlagsV2)flagsRaw;
    bool hasSidecarFlag = (flags & DatumFileFlagsV2.HasSidecarReferences) != 0;

    // v2 doesn't yet store the sidecar fingerprint in the .datum footer
    // (deferred follow-up in project_sidecar_integrity_hash.md). The
    // sidecar's own header carries the fingerprint and that's what
    // SidecarReadStore validates at open time.
    return (hasSidecarFlag,
        hasSidecarFlag
            ? "v2 .datum does not embed the sidecar fingerprint in its footer; the sidecar's own header is authoritative"
            : "companion .datum does not declare HasSidecarReferences");
}

static ulong HashSidecarPayload(string path, long payloadBytes)
{
    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    stream.Seek(SidecarConstants.HeaderSize, SeekOrigin.Begin);

    XxHash3 hasher = new();
    byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
    try
    {
        long remaining = payloadBytes;
        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, buffer.Length);
            int read = stream.Read(buffer, 0, take);
            if (read == 0) break;
            hasher.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }

    return hasher.GetCurrentHashAsUInt64();
}

static string FormatBytes(long bytes)
{
    const double KB = 1024.0;
    const double MB = KB * 1024.0;
    const double GB = MB * 1024.0;
    return bytes switch
    {
        >= (long)(GB * 0.95) => $"{bytes / GB:F2} GB",
        >= (long)(MB * 0.95) => $"{bytes / MB:F2} MB",
        >= (long)(KB * 0.95) => $"{bytes / KB:F2} KB",
        _ => $"{bytes} B",
    };
}
