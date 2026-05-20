namespace Heliosoph.DatumV.Serialization.Csv;

/// <summary>
/// Detects the delimiter character from file content, options, or extension.
/// Operates on a seekable <see cref="Stream"/> and resets position after detection.
/// </summary>
internal static class DelimiterDetector
{
    private const int SampleLineCount = 20;
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// Detects the delimiter for the given descriptor and stream.
    /// Checks options first, then file extension, then content sniffing.
    /// Resets stream position to 0 after detection.
    /// </summary>
    public static char Detect(Stream stream, IReadOnlyDictionary<string, string> options, string filePath)
    {
        if (options.TryGetValue("delimiter", out string? delimiterValue) &&
            delimiterValue.Length > 0)
        {
            return delimiterValue[0];
        }

        string fileName = Path.GetFileName(filePath);
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
            extension = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

        if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            return '\t';

        char detected = DetectFromStream(stream);
        stream.Seek(0, SeekOrigin.Begin);
        return detected;
    }

    private static char DetectFromStream(Stream stream)
    {
        string[] lines = ReadSampleLines(stream);
        return DetectFromLines(lines);
    }

    internal static char DetectFromLines(string[] lines)
    {
        if (lines.Length == 0)
            return ',';

        char bestCandidate = ',';
        int bestColumnCount = 0;

        foreach (char candidate in Candidates)
        {
            int headerFieldCount = CountFields(lines[0], candidate);

            if (headerFieldCount < 2)
                continue;

            bool consistent = true;
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                if (CountFields(lines[lineIndex], candidate) != headerFieldCount)
                {
                    consistent = false;
                    break;
                }
            }

            if (!consistent)
                continue;

            if (headerFieldCount > bestColumnCount)
            {
                bestColumnCount = headerFieldCount;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private static int CountFields(string line, char delimiter)
    {
        int count = 1;
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else
                    inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }

    private static string[] ReadSampleLines(Stream stream)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        List<string> lines = new(SampleLineCount + 1);
        int dataLinesRead = 0;

        while (dataLinesRead <= SampleLineCount)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            if (line.Length == 0) continue;

            lines.Add(line);
            if (lines.Count > 1) dataLinesRead++;
        }

        return lines.ToArray();
    }
}
