using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;

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

(bool hasSidecarFlag, ulong? datumFingerprint, string datumDiagnostic) = ReadDatumSidecarLink(datumPath);

Console.WriteLine("Companion:");
Console.WriteLine($"  Path:        {datumPath}");
Console.WriteLine($"  HasSidecarBlobs flag: {(hasSidecarFlag ? "✓ set" : "✗ not set")}");

if (datumFingerprint is ulong dfp)
{
    bool match = dfp == fingerprint;
    Console.WriteLine($"  Fingerprint: 0x{dfp:X16} {(match ? "✓ matches sidecar" : "✗ mismatch — sidecar is stale or swapped")}");
}
else
{
    Console.WriteLine($"  Fingerprint: {datumDiagnostic}");
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

static (bool HasSidecarFlag, ulong? Fingerprint, string Diagnostic) ReadDatumSidecarLink(string datumPath)
{
    using FileStream stream = new(datumPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    long length = stream.Length;

    if (length < DatumFileConstants.HeaderSize + DatumFileConstants.TailSize)
    {
        return (false, null, "companion .datum file is truncated");
    }

    // Header flags live at offset 6 (magic[4] + version[2] + flags[2] + ...).
    Span<byte> headerBuffer = stackalloc byte[DatumFileConstants.HeaderSize];
    stream.ReadExactly(headerBuffer);
    ushort flagsRaw = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[6..8]);
    DatumFileFlags flags = (DatumFileFlags)flagsRaw;
    bool hasSidecarFlag = (flags & DatumFileFlags.HasSidecarBlobs) != 0;

    if (!hasSidecarFlag)
    {
        return (false, null, "companion .datum does not declare a sidecar — fingerprint not present in footer");
    }

    // The fingerprint is the 8 bytes immediately before the tail (footerByteLength + tailMagic = 8 bytes).
    long fingerprintOffset = length - DatumFileConstants.TailSize - 8;
    if (fingerprintOffset < DatumFileConstants.HeaderSize)
    {
        return (true, null, "companion .datum is too short to contain a footer fingerprint");
    }

    stream.Seek(fingerprintOffset, SeekOrigin.Begin);
    Span<byte> fpBuffer = stackalloc byte[8];
    stream.ReadExactly(fpBuffer);
    ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(fpBuffer);
    return (true, fingerprint, string.Empty);
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
