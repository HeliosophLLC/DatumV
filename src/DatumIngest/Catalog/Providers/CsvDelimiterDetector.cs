namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Sniffs the delimiter character from the first few lines of a CSV file.
/// Evaluates candidate delimiters by checking which one produces a consistent
/// column count greater than one across all sampled lines.
/// </summary>
internal static class CsvDelimiterDetector
{
    /// <summary>
    /// Number of data lines (after the header) to sample for detection.
    /// </summary>
    private const int SampleLineCount = 20;

    /// <summary>
    /// Candidate delimiters in priority order. When two candidates tie
    /// (same consistency and column count), the earlier one wins.
    /// </summary>
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// Detects the most likely delimiter for a CSV file by reading its
    /// first lines and scoring each candidate.
    /// </summary>
    /// <param name="filePath">Path to the CSV file.</param>
    /// <returns>The detected delimiter character, or comma as the fallback.</returns>
    public static char Detect(string filePath)
    {
        string[] lines = ReadSampleLines(filePath);
        return DetectFromLines(lines);
    }

    /// <summary>
    /// Detects the most likely delimiter by reading sample lines from a stream.
    /// Used when the source is compressed and the raw file bytes are not directly readable.
    /// </summary>
    /// <param name="stream">A readable (possibly decompressed) stream positioned at the start.</param>
    /// <returns>The detected delimiter character, or comma as the fallback.</returns>
    public static char Detect(Stream stream)
    {
        string[] lines = ReadSampleLines(stream);
        return DetectFromLines(lines);
    }

    /// <summary>
    /// Detects the most likely delimiter from pre-read lines.
    /// Exposed for testability without requiring file I/O.
    /// </summary>
    /// <param name="lines">
    /// The first lines of the file (header + data rows).
    /// Must contain at least one line (the header).
    /// </param>
    /// <returns>The detected delimiter character, or comma as the fallback.</returns>
    internal static char DetectFromLines(string[] lines)
    {
        if (lines.Length == 0)
        {
            return ',';
        }

        char bestCandidate = ',';
        int bestColumnCount = 0;

        foreach (char candidate in Candidates)
        {
            int headerFieldCount = CountFields(lines[0], candidate);

            // A useful delimiter must produce at least two columns.
            if (headerFieldCount < 2)
            {
                continue;
            }

            // Check consistency: every sampled data line must produce the same field count.
            bool consistent = true;
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                int fieldCount = CountFields(lines[lineIndex], candidate);
                if (fieldCount != headerFieldCount)
                {
                    consistent = false;
                    break;
                }
            }

            if (!consistent)
            {
                continue;
            }

            // Prefer the candidate that produces the most columns (more structure).
            // On a tie, the first candidate in priority order wins.
            if (headerFieldCount > bestColumnCount)
            {
                bestColumnCount = headerFieldCount;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Counts the number of fields a line would produce when split on the
    /// given delimiter, respecting RFC 4180 quoting rules.
    /// </summary>
    private static int CountFields(string line, char delimiter)
    {
        int count = 1;
        bool inQuotes = false;

        for (int position = 0; position < line.Length; position++)
        {
            char character = line[position];

            if (character == '"')
            {
                if (inQuotes && position + 1 < line.Length && line[position + 1] == '"')
                {
                    // Escaped quote — skip the pair.
                    position++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Reads the header line plus up to <see cref="SampleLineCount"/> data lines
    /// from the file, skipping empty lines.
    /// </summary>
    private static string[] ReadSampleLines(string filePath)
    {
        using StreamReader reader = new(filePath);
        return ReadSampleLines(reader);
    }

    /// <summary>
    /// Reads the header line plus up to <see cref="SampleLineCount"/> data lines
    /// from a stream, skipping empty lines.
    /// </summary>
    private static string[] ReadSampleLines(Stream stream)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        return ReadSampleLines(reader);
    }

    /// <summary>
    /// Reads the header line plus up to <see cref="SampleLineCount"/> data lines
    /// from a reader, skipping empty lines.
    /// </summary>
    private static string[] ReadSampleLines(StreamReader reader)
    {
        List<string> lines = new(SampleLineCount + 1);
        int dataLinesRead = 0;

        while (dataLinesRead <= SampleLineCount)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(line);
            // The first line is the header; subsequent ones are data lines.
            if (lines.Count > 1)
            {
                dataLinesRead++;
            }
        }

        return lines.ToArray();
    }
}
